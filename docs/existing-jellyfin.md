# Add Tunefin to an existing Jellyfin

You keep your Jellyfin container and its config. You add:

1. **`ytm-sidecar`**: a small service that searches, streams and downloads from YouTube Music.
2. **`ytm-plugin`**: a one-shot container that builds the plugin and copies it into your Jellyfin's
   `plugins` folder, then exits.
3. **One extra folder** (`YouTube`) mounted into both Jellyfin and the sidecar.

Nothing needs to be stopped. Jellyfin only needs **one restart** at the end to load the plugin.

> Requirements: Jellyfin **12.4** in Docker. For other versions see
> [Other Jellyfin versions](../README.md#other-jellyfin-versions).

---

## Step 1: Find three things about your setup

| What | How to find it | Example |
|---|---|---|
| **Plugins folder** on the host | The host folder mounted at `/config` in Jellyfin, plus `/plugins`. It contains a `configurations/` folder. For the linuxserver image it's `<config>/data/plugins` | `/srv/jellyfin/config/plugins` |
| **Docker network** of Jellyfin | `docker inspect <jellyfin> -f '{{json .NetworkSettings.Networks}}'` | `jellyfin_default` |
| **User** Jellyfin runs as | `docker inspect <jellyfin> -f '{{.Config.User}}'` (empty = root `0:0`). For linuxserver it's your `PUID`/`PGID` | `1000:1000` |

Also choose a host folder for the liked songs, for example `/srv/media/YouTube`. It must **not** be
inside one of your existing libraries.

## Step 2: Mount the YouTube folder into Jellyfin

Add one volume to your Jellyfin service (the in-container path is up to you, `/media/YouTube` is
the default):

```yaml
services:
  jellyfin:
    volumes:
      # ...your existing volumes...
      - /srv/media/YouTube:/media/YouTube
```

Create the folder and give it to Jellyfin's user:

```bash
sudo mkdir -p /srv/media/YouTube && sudo chown 1000:1000 /srv/media/YouTube   # use your PUID:PGID
```

The new mount is applied when Jellyfin is recreated in Step 4.

## Step 3: Add the sidecar and the plugin installer

Pick **A** if Jellyfin is in your own `docker-compose.yml`, or **B** to run this repo's add-on
file next to it.

### Option A: paste into your own docker-compose.yml (recommended)

Clone this repo next to your compose file (`git clone … Tunefin`), then add:

```yaml
services:
  # ...your jellyfin service...

  ytm-sidecar:
    build: ./Tunefin/ytm-sidecar
    image: ytm-sidecar:latest
    container_name: ytm-sidecar
    restart: unless-stopped
    user: "1000:1000"                  # SAME user as Jellyfin (root = "0:0")
    environment:
      YOUTUBE_DIR: /youtube
      USER_COOKIES_DIR: /user-cookies
      MAX_CONCURRENT_DOWNLOADS: "10"
    volumes:
      - /srv/media/YouTube:/youtube    # the folder from Step 2 (one mount: hardlinks)
      - /srv/jellyfin/config/plugins/Jellyfin.Plugin.YouTubeMusic/users:/user-cookies:ro
    # must share a network with Jellyfin; if Jellyfin uses a custom network, add it here:
    # networks: [your_network]

  ytm-plugin:
    build: ./Tunefin/Jellyfin.Plugin.YouTubeMusic
    image: jellyfin-ytm-plugin-installer:latest
    restart: "no"
    network_mode: none
    environment:
      SIDECAR_URL: http://ytm-sidecar:8787
      LIBRARY_ROOT_PATH: /media/YouTube   # where JELLYFIN sees the folder from Step 2
    volumes:
      - /srv/jellyfin/config/plugins:/plugins
```

Start only the new services. This doesn't touch your other containers:

```bash
docker compose up -d --build --no-deps ytm-sidecar
docker compose up --build --no-deps ytm-plugin      # prints "YouTube Music plugin installed…" and exits
```

### Option B: use docker-compose.addon.yml

```bash
git clone https://github.com/consultomer/Tunefin.git && cd Tunefin
cp .env.example .env
# edit .env: PUID, PGID, JELLYFIN_PLUGINS_PATH, YOUTUBE_PATH, JELLYFIN_NETWORK (and LIBRARY_ROOT_PATH if not /media/YouTube)
docker compose -f docker-compose.addon.yml up -d --build
```

If Jellyfin uses `network_mode: host`, there's no shared network. Instead, publish the
sidecar's port (`ports: ["127.0.0.1:8787:8787"]`) and set **Sidecar URL** to
`http://127.0.0.1:8787` in the plugin settings.

## Step 4: Restart Jellyfin once

Recreate it, so the Step 2 volume is applied and the plugin is loaded:

```bash
docker compose up -d jellyfin        # or: docker restart <jellyfin> if you mounted the folder already
```

Check *Dashboard → Plugins*: **YouTube Music** should be *Active*. Open its settings and check
that:
- **Sidecar URL** is `http://ytm-sidecar:8787`.
- **YouTube folder, as seen by Jellyfin** matches your Step 2 path.
- **Test connection** returns songs.

## Step 5: Connect YouTube

Each user opens `https://<your-jellyfin>/YouTube` while signed in to Jellyfin Web. See
[Connect your YouTube account](../README.md#connect-your-youtube-account).

The first user to connect gets a private **Liked – <name>** library. The plugin then hides each
user's library from everyone else by adjusting library access. Users who had "access all
libraries" are switched to an explicit list that keeps including new libraries.

---

## Updating

```bash
cd Tunefin && git pull && cd ..
docker compose up -d --build --no-deps ytm-sidecar
docker compose up --build --no-deps ytm-plugin
docker restart <jellyfin>
```

The installer swaps the DLL atomically, so it's safe while Jellyfin is running. The new version
loads on the restart.

## Uninstalling

1. Remove the `ytm-sidecar` and `ytm-plugin` services, and delete
   `<plugins>/YouTubeMusic_*` and `<plugins>/configurations/Jellyfin.Plugin.YouTubeMusic.xml`.
2. Restart Jellyfin, then delete the *Liked – …* libraries if you don't want them any more.
   Users' saved cookies are in `<plugins>/Jellyfin.Plugin.YouTubeMusic/users/`; delete that folder too.
