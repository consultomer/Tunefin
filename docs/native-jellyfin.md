# Add Tunefin to a Jellyfin that isn't in a container

Use this guide when Jellyfin is installed directly on the machine: a distro package, the
`jellyfin` binary, a seedbox or shared host. You don't need Docker Compose. The containers in the
other guides only do two things, and both can run directly on the host:

1. **Plugin**: build the DLL once and copy it into Jellyfin's `plugins` folder.
2. **Sidecar**: a small Node service that runs `yt-dlp` and `ffmpeg`. You run it as a normal
   process next to Jellyfin.

When nothing runs in a container, the plugin and the sidecar see the **same paths**. You don't
have to translate folders between `/youtube` and `/media/YouTube`: you use the real folder
everywhere.

> Requirements: Jellyfin **12.4**. For other versions see
> [Other Jellyfin versions](../README.md#other-jellyfin-versions).
> Check it with `curl -s http://127.0.0.1:8096/System/Info/Public`.

---

## Step 1: Find your setup

| What | How to find it | Common values |
|---|---|---|
| **User** Jellyfin runs as | `ps -o user= -C jellyfin` | `jellyfin` (distro package), or your own user |
| **Plugins folder** | The `plugins` folder in Jellyfin's data directory. It contains a `configurations/` folder. *Dashboard → Paths* shows the data path | `/var/lib/jellyfin/plugins` (Debian/Ubuntu package), `~/.local/share/jellyfin/plugins`, `~/.config/jellyfin/data/plugins` |
| **Free port** for the sidecar | `ss -ltn \| grep 8787` (no output means it's free) | `8787` |

Then choose a folder for liked songs, for example `/srv/media/YouTube` or `~/YouTube`:

- It must **not** be inside one of your existing libraries. Otherwise every user's private
  *Liked* songs show up in the shared library.
- It must be on **one filesystem**, since the sidecar hardlinks songs from `.store/` into each
  user's folder. Avoid FUSE or rclone mounts.
- The Jellyfin user must own it:

```bash
sudo mkdir -p /srv/media/YouTube && sudo chown jellyfin:jellyfin /srv/media/YouTube
```

The rest of this guide uses these placeholders. Replace them with your values:

```bash
PLUGINS=/var/lib/jellyfin/plugins      # Jellyfin's plugins folder
YOUTUBE=/srv/media/YouTube             # the liked-songs folder
TUNEFIN=/opt/tunefin                   # where you cloned this repo
```

## Step 2: Install the tools the sidecar needs

| Tool | Install |
|---|---|
| Node **20+** | Your distro, or [nodejs.org](https://nodejs.org) / `nvm` |
| ffmpeg | `sudo apt install ffmpeg` (or your distro's package) |
| yt-dlp | `python3 -m pip install --user -U "yt-dlp[default]"` or `pipx install "yt-dlp[default]"` |

Install yt-dlp with the `[default]` extras, which add the YouTube JS-challenge solver and Opus
cover art. Distro packages of yt-dlp are usually too old for YouTube.

If you install as a user without root, yt-dlp ends up in `~/.local/bin`. That folder is often
missing from `PATH` for cron and services, so set `YTDLP_PATH` to the full path in Step 5.

## Step 3: Build and install the plugin

The plugin needs the **.NET 10 SDK**. `build-plugin.sh` uses a local `dotnet` if you have
version 10. Otherwise it builds in a throwaway Docker or Podman container. This is only a build
tool: Jellyfin and the sidecar still run without containers.

```bash
git clone https://github.com/consultomer/Tunefin.git "$TUNEFIN" && cd "$TUNEFIN"
./build-plugin.sh                      # → dist/YouTubeMusic_<version>/
```

Copy it into Jellyfin's plugins folder as the Jellyfin user:

```bash
sudo -u jellyfin mkdir -p "$PLUGINS/configurations"
sudo -u jellyfin cp -r dist/YouTubeMusic_* "$PLUGINS/"
```

On a host where Jellyfin runs as your own user, drop `sudo -u jellyfin`.

Then create the plugin config, so it points at the native sidecar and the real folder. Generate a
random API key first, then use the same key for the sidecar in Step 5:

```bash
API_KEY=$(openssl rand -hex 24); echo "$API_KEY"    # keep this

sudo -u jellyfin tee "$PLUGINS/configurations/Jellyfin.Plugin.YouTubeMusic.xml" >/dev/null <<XML
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <SidecarUrl>http://127.0.0.1:8787</SidecarUrl>
  <SidecarApiKey>${API_KEY}</SidecarApiKey>
  <EnableAutoDownloadOnFavorite>true</EnableAutoDownloadOnFavorite>
  <TriggerLibraryScanAfterDownload>true</TriggerLibraryScanAfterDownload>
  <SearchResultLimit>10</SearchResultLimit>
  <LibraryRootPath>${YOUTUBE}</LibraryRootPath>
  <SavedSearches />
</PluginConfiguration>
XML
```

You can also skip this file and set **Sidecar URL**, **Sidecar API key** and **YouTube folder**
in the plugin settings after Step 6.

> **Use the API key on shared machines.** Any user on the host can connect to `127.0.0.1`.
> Without a key, they could use your users' YouTube logins through the sidecar.

## Step 4: Build the sidecar

```bash
cd "$TUNEFIN/ytm-sidecar"
npm ci && npm run build && npm prune --omit=dev
```

Check it as the Jellyfin user (it must be able to write to `$YOUTUBE`):

```bash
sudo -u jellyfin env HOST=127.0.0.1 PORT=8787 YOUTUBE_DIR="$YOUTUBE" \
  USER_COOKIES_DIR="$PLUGINS/Jellyfin.Plugin.YouTubeMusic/users" API_KEY="$API_KEY" \
  node dist/server.js
# in a second terminal:
curl -s http://127.0.0.1:8787/health                                  # → {"status":"ok",...}
curl -s -H "X-Api-Key: $API_KEY" 'http://127.0.0.1:8787/search?q=daft%20punk' | head -c 300
```

Stop it with Ctrl-C once it works.

## Step 5: Run the sidecar in the background

The sidecar must run as the **same user as Jellyfin**. Jellyfin then has access to the files it
downloads, and the sidecar can read the cookies the plugin saves.

| Setting | Value |
|---|---|
| `HOST` | `127.0.0.1`, so the sidecar is only reachable from this machine |
| `PORT` | `8787` (or the free port from Step 1, and update **Sidecar URL** to match) |
| `YOUTUBE_DIR` | `$YOUTUBE` |
| `USER_COOKIES_DIR` | `$PLUGINS/Jellyfin.Plugin.YouTubeMusic/users` |
| `API_KEY` | The key from Step 3 |
| `YTDLP_PATH` | Full path to `yt-dlp` if it isn't on the service's `PATH` |

All variables are listed under [Sidecar environment variables](../README.md#sidecar-environment-variables).

### Option A: systemd (most Linux servers)

`/etc/systemd/system/tunefin-sidecar.service`:

```ini
[Unit]
Description=Tunefin YouTube Music sidecar
After=network-online.target
Wants=network-online.target

[Service]
User=jellyfin
Group=jellyfin
WorkingDirectory=/opt/tunefin/ytm-sidecar
Environment=NODE_ENV=production HOST=127.0.0.1 PORT=8787
Environment=YOUTUBE_DIR=/srv/media/YouTube
Environment=USER_COOKIES_DIR=/var/lib/jellyfin/plugins/Jellyfin.Plugin.YouTubeMusic/users
Environment=API_KEY=<your key>
# Environment=YTDLP_PATH=/home/jellyfin/.local/bin/yt-dlp
ExecStart=/usr/bin/node dist/server.js
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

Because the file contains the API key, run `sudo chmod 600` on it, then:

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now tunefin-sidecar
journalctl -u tunefin-sidecar -f        # logs
```

If you don't have root but your user has systemd, put the unit in
`~/.config/systemd/user/`, remove `User=`/`Group=`, change `WantedBy=` to `default.target`, and
use `systemctl --user` instead. Run `loginctl enable-linger $USER` so it keeps running after you
log out.

### Option B: no systemd (seedboxes and shared hosts)

Put the settings in a start script kept **outside** the repo, so `git pull` never touches your
key. For example `~/tunefin-sidecar.sh`:

```bash
#!/usr/bin/env bash
export NODE_ENV=production HOST=127.0.0.1 PORT=8787
export YOUTUBE_DIR="$HOME/YouTube"
export USER_COOKIES_DIR="$HOME/.config/jellyfin/data/plugins/Jellyfin.Plugin.YouTubeMusic/users"
export API_KEY="<your key>"
export PATH="$HOME/.local/bin:$PATH"        # yt-dlp from pip --user
cd "$HOME/tunefin/ytm-sidecar"
exec node dist/server.js >> "$HOME/tunefin-sidecar.log" 2>&1
```

```bash
chmod 700 ~/tunefin-sidecar.sh
screen -dmS tunefin ~/tunefin-sidecar.sh            # start (or use tmux)
```

To start it after a reboot, add it to your crontab (`crontab -e`), or to the autostart script
your host already uses:

```cron
@reboot screen -dmS tunefin $HOME/tunefin-sidecar.sh
```

Stop it with `screen -S tunefin -X quit`. The log is in `~/tunefin-sidecar.log`.

## Step 6: Restart Jellyfin once

Restart Jellyfin with `sudo systemctl restart jellyfin`, your host's control panel, or however
you normally start it. This loads the plugin.

Check *Dashboard → Plugins*: **Tunefin** should be *Active*. Open its settings and check that:
- **Sidecar URL** is `http://127.0.0.1:8787`.
- **Sidecar API key** is set.
- **YouTube folder, as seen by Jellyfin** is your real folder (`$YOUTUBE`).
- **Test connection** returns songs.

## Step 7: Connect YouTube

Each user opens `https://<your-jellyfin>/YouTube` while signed in to Jellyfin Web. See
[Connect your YouTube account](../README.md#connect-your-youtube-account).

---

## Updating

```bash
cd "$TUNEFIN" && git pull
./build-plugin.sh
sudo -u jellyfin cp dist/YouTubeMusic_*/Jellyfin.Plugin.YouTubeMusic.dll "$PLUGINS"/YouTubeMusic_*/
(cd ytm-sidecar && npm ci && npm run build && npm prune --omit=dev)
sudo systemctl restart tunefin-sidecar      # or: screen -S tunefin -X quit; screen -dmS tunefin ~/tunefin-sidecar.sh
```

Then restart Jellyfin to load the new plugin version. Copy only the DLL: Jellyfin rewrites
`meta.json` itself.

If YouTube playback breaks, update yt-dlp first:
`python3 -m pip install --user -U "yt-dlp[default]"` (or `pipx upgrade yt-dlp`).

## Troubleshooting

| Symptom | Fix |
|---|---|
| Plugin missing or *Not supported* | Jellyfin isn't 12.4, or the files went to the wrong `plugins` folder. Check *Dashboard → Paths* |
| Test connection fails | Is the sidecar running (`curl http://127.0.0.1:8787/health`)? Do **Sidecar URL** and the port match? Is the API key the same in both places? |
| `401` from the sidecar | The plugin's **Sidecar API key** doesn't match the sidecar's `API_KEY` |
| `spawn yt-dlp ENOENT` in the sidecar log | yt-dlp isn't on the service's `PATH`. Set `YTDLP_PATH` |
| `Permission denied` | The sidecar doesn't run as the Jellyfin user, or that user doesn't own `$YOUTUBE` |
| Song liked but never appears | **YouTube folder** in the plugin settings must be exactly `YOUTUBE_DIR` |
| `EXDEV` / hardlink errors | `$YOUTUBE` spans several filesystems or is a FUSE mount. Move it to one local disk |

## Uninstalling

1. Stop and remove the sidecar (`sudo systemctl disable --now tunefin-sidecar` and delete the
   unit, or remove the `screen`/cron lines).
2. Delete `$PLUGINS/YouTubeMusic_*`, `$PLUGINS/configurations/Jellyfin.Plugin.YouTubeMusic.xml`
   and `$PLUGINS/Jellyfin.Plugin.YouTubeMusic/`, which holds the users' saved cookies.
3. Restart Jellyfin, then delete the *Liked – …* libraries and `$YOUTUBE` if you don't want them
   any more.
