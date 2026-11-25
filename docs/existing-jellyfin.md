# Add Tunefin to an existing Jellyfin

You keep your Jellyfin container and its config. You add:

1. **`ytm-sidecar`**: a small service that searches, streams and downloads from YouTube Music.
   It runs from a ready-made image (`ghcr.io/consultomer/tunefin-sidecar`), so you don't clone or
   build anything.
2. **One extra folder** (`YouTube`) mounted into both Jellyfin and the sidecar.
3. **The Tunefin plugin**, installed from Jellyfin's plugin catalog.

> Requirements: Jellyfin **12.0 or newer** in Docker. For other versions see
> [Other Jellyfin versions](../README.md#other-jellyfin-versions).

---

## Step 1: Find three things about your setup

| What | How to find it | Example |
|---|---|---|
| **Plugins folder** on the host | The host folder mounted at `/config` in Jellyfin, plus `/plugins`. It contains a `configurations/` folder. For the linuxserver image it's `<config>/data/plugins`. The sidecar reads users' cookies from it | `/srv/jellyfin/config/plugins` |
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

## Step 3: Add the sidecar

Pick **A** if Jellyfin is in your own `docker-compose.yml`, or **B** to run this repo's add-on
file next to it.

### Option A: paste into your own docker-compose.yml (recommended)

Add this service next to your Jellyfin service:

```yaml
services:
  # ...your jellyfin service...

  ytm-sidecar:
    image: ghcr.io/consultomer/tunefin-sidecar:latest
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
```

Start only the new service. This doesn't touch your other containers:

```bash
docker compose up -d --no-deps ytm-sidecar
```

### Option B: use docker-compose.addon.yml

```bash
mkdir tunefin && cd tunefin
curl -fsSLO https://raw.githubusercontent.com/consultomer/Tunefin/main/docker-compose.addon.yml
curl -fsSL  https://raw.githubusercontent.com/consultomer/Tunefin/main/.env.example -o .env
# edit .env: PUID, PGID, JELLYFIN_PLUGINS_PATH, YOUTUBE_PATH, JELLYFIN_NETWORK
docker compose -f docker-compose.addon.yml up -d
```

If Jellyfin uses `network_mode: host`, there's no shared network. Instead, publish the
sidecar's port (`ports: ["127.0.0.1:8787:8787"]`) and set **Sidecar URL** to
`http://127.0.0.1:8787` in the plugin settings.

## Step 4: Install the plugin

1. Recreate Jellyfin so the Step 2 volume is applied:
   ```bash
   docker compose up -d jellyfin        # skip if you mounted the folder already
   ```
2. In Jellyfin, open **Dashboard → Plugins → Repositories**, click **+**, name it *Tunefin* and
   paste:
   ```
   https://consultomer.github.io/Tunefin/manifest.json
   ```
3. Open **Catalog**, choose **Tunefin** and click **Install**.
4. Restart Jellyfin once to load it: `docker restart <jellyfin>`.

Check *Dashboard → Plugins*: **Tunefin** should be *Active*. Open its settings and check that:
- **Sidecar URL** is `http://ytm-sidecar:8787` (the default).
- **YouTube folder, as seen by Jellyfin** matches your Step 2 path (default `/media/YouTube`).
- **Test connection** returns songs.

## Step 5: Connect YouTube

Each user opens `https://<your-jellyfin>/YouTube` while signed in to Jellyfin Web. See
[Connect your YouTube account](../README.md#connect-your-youtube-account).

The first user to connect gets a private **Liked – <name>** library. The plugin then hides each
user's library from everyone else by adjusting library access. Users who had "access all
libraries" are switched to an explicit list that keeps including new libraries.

---

## Updating

- **Plugin:** update it under *Dashboard → Plugins* when Jellyfin offers a new version, then
  restart Jellyfin.
- **Sidecar:** pull the new image. Do this too when YouTube playback breaks: the image is rebuilt
  every week with the latest yt-dlp.
  ```bash
  docker compose pull ytm-sidecar && docker compose up -d --no-deps ytm-sidecar
  ```

### Moving from the old `ytm-plugin` installer

Earlier versions of this guide used a `ytm-plugin` container that built the plugin into
`<plugins>/YouTubeMusic_1.0.0.0/`. To switch to the catalog:

1. Remove the `ytm-plugin` service from your compose file, and change the sidecar's `build:` line
   to `image: ghcr.io/consultomer/tunefin-sidecar:latest`. Then run
   `docker compose up -d --remove-orphans ytm-sidecar`.
2. Install Tunefin from the catalog (Step 4).
3. Delete `<plugins>/YouTubeMusic_1.0.0.0/` and restart Jellyfin.

Your settings (`configurations/Jellyfin.Plugin.YouTubeMusic.xml`), users' cookies and liked songs
stay where they are.

## Uninstalling

1. Uninstall Tunefin under *Dashboard → Plugins*, remove the `ytm-sidecar` service, and delete
   `<plugins>/configurations/Jellyfin.Plugin.YouTubeMusic.xml`.
2. Restart Jellyfin, then delete the *Liked – …* libraries if you don't want them any more.
   Users' saved cookies are in `<plugins>/Jellyfin.Plugin.YouTubeMusic/users/`; delete that folder too.
