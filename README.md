# Tunefin: YouTube Music for Jellyfin

![Jellyfin](https://img.shields.io/badge/Jellyfin-12.4-blue) ![.NET](https://img.shields.io/badge/.NET-10-purple) ![Node](https://img.shields.io/badge/Node-20+-green) ![Docker](https://img.shields.io/badge/Docker-Compose-blue) ![License](https://img.shields.io/badge/License-MIT-green)

Tunefin brings **YouTube Music** into **Jellyfin** and **Finamp**. Search YouTube from the normal search box, play instantly, and like a song to save it (tagged, with cover art) into your own private "Liked" library.

## Table of Contents
- [Introduction](#introduction)
- [Features](#features)
- [Technologies Used](#technologies-used)
- [How It Works](#how-it-works)
- [Project Structure](#project-structure)
- [Installation](#installation)
  - [Requirements](#requirements)
  - [Install from the Plugin Repository](#install-from-the-plugin-repository)
  - [New Install (Jellyfin Included)](#new-install-jellyfin-included)
  - [Existing Jellyfin Installation](#existing-jellyfin-installation)
  - [Jellyfin Without Containers](#jellyfin-without-containers)
- [Connect Your YouTube Account](#connect-your-youtube-account)
- [Usage](#usage)
- [Configuration](#configuration)
  - [Plugin Settings](#plugin-settings)
  - [Sidecar Environment Variables](#sidecar-environment-variables)
- [Updating](#updating)
- [Troubleshooting](#troubleshooting)
- [Other Jellyfin Versions](#other-jellyfin-versions)
- [Disclaimer](#disclaimer)
- [Contributing](#contributing)
- [License](#license)
- [Contact](#contact)

## Introduction

Tunefin is a Jellyfin plugin plus a small companion service (the *sidecar*) that lets you search and play YouTube Music right inside Jellyfin and Finamp, and keep the songs you like. Each user connects their own YouTube account, likes stay separate, and a song liked by several people is stored only once. Users who don't connect YouTube just get normal Jellyfin. Everything runs with Docker Compose.

## Features
- 🔎 **Search:** type in the normal Jellyfin/Finamp search box and get YouTube Music songs after your local results.
- ▶️ **Play:** stream any result immediately, with seeking. Works in Jellyfin Web, the Jellyfin apps and Finamp.
- ♥ **Like:** the song is downloaded (tagged, with cover art) into **your own private library**, *Liked – &lt;your name&gt;*. From there it's a normal local track: add it to playlists or download it for offline use.
- 📥 **Import your YouTube Music likes:** your "Liked music" list is downloaded into the same library and kept in sync.
- 👥 **Multi-user:** every user connects **their own** YouTube account. Users never see each other's liked songs, and shared songs are stored **once** on disk (hardlinks).

## Technologies Used
- **Plugin:** C#, .NET 10, Jellyfin 12.4 plugin API
- **Sidecar:** Node.js, TypeScript, Fastify, [yt-dlp](https://github.com/yt-dlp/yt-dlp), ffmpeg, [youtubei.js](https://github.com/LuanRT/YouTube.js)
- **Clients:** Jellyfin Web, Jellyfin Android/iOS, Finamp
- **Deployment:** Docker Compose

## How It Works

```
 Finamp / Jellyfin Web ──► Jellyfin + Tunefin plugin ────────────────► ytm-sidecar (Node + yt-dlp + ffmpeg)
                            • adds YouTube songs to search results     GET  /search
                            • plays them through the sidecar ───────►  GET  /stream/:id   (range proxy)
                            • Likes → download to your private library ─►  POST /download     (one copy per song)
                            • imports your YouTube Music likes ─────►  POST /likes
                            └──────── shared "YouTube" folder ◄──────┘
```

<details>
<summary>Details for the curious</summary>

- **Search:** an ASP.NET action filter adds YouTube Music matches to Jellyfin's `/Items?searchTerm=…` responses, because Jellyfin channels can't receive search text. Matches you already have locally are left out.
- **Playback:** the songs are items of a *YouTube Music* channel. Jellyfin and Finamp play `/stream/:id` from the sidecar, which proxies googlevideo with Range support. Finamp's direct-play endpoint (`/Items/{id}/File`) is intercepted for these items. Every stream uses the requesting user's own cookies.
- **Likes:** a `UserDataSaved` listener sends ♥'d YouTube items to the sidecar. `/download` stores one copy per video in `.store/<videoId>.m4a` and hardlinks it as `<user>/<Artist> - <Title>.m4a`.
- **Privacy:** the plugin creates a *Liked – &lt;user&gt;* library per user and removes it from every other user's library access. Users with "access all libraries" are switched to an explicit list, and new libraries keep getting added to it. A Jellyfin admin can always change these permissions.
- **Likes import:** the sidecar reads the user's "Liked music" playlist with youtubei.js and their cookies. The plugin skips songs the user already has, including ones in their other libraries with the same title and artist. The import only adds songs: unliking on YouTube doesn't delete anything.

</details>

## Project Structure

```
Tunefin/
├── Jellyfin.Plugin.YouTubeMusic/   # Jellyfin plugin (.NET 10); its Dockerfile builds and installs it
│   ├── Api/                        # Account and plugin API controllers
│   ├── Channel/                    # "YouTube Music" channel (playback)
│   ├── Configuration/              # Settings page and /YouTube account page
│   ├── Search/                     # Search injection and Finamp file-endpoint filters
│   ├── Sidecar/                    # HTTP client for the sidecar
│   └── Users/                      # Per-user libraries, likes sync and import task
├── ytm-sidecar/                    # Fastify + TypeScript service (yt-dlp, ffmpeg)
│   └── src/                        # search, stream, download, likes, library
├── docs/
│   ├── existing-jellyfin.md        # Guide for adding Tunefin to an existing Jellyfin container
│   └── native-jellyfin.md          # Guide for a Jellyfin installed without containers
├── docker-compose.yml              # Full stack: Jellyfin + sidecar + plugin installer
├── docker-compose.addon.yml        # Sidecar + plugin installer for an existing Jellyfin
├── build-plugin.sh                 # Builds the plugin DLL into dist/ without Compose
├── branding/                       # Logo source (make_logo.py) and rendered PNGs
├── pages/                          # Plugin-repository site published to GitHub Pages
├── .github/workflows/release.yml   # Tag v* → GitHub release + manifest.json on gh-pages
└── .env.example                    # Ports, folders and user
```

## Installation

### Requirements
- Docker with Compose v2 (or, for a Jellyfin without containers, Node 20+, ffmpeg and yt-dlp: see [Jellyfin Without Containers](#jellyfin-without-containers))
- Jellyfin **12.4** (the full stack below installs it for you). For other versions see [Other Jellyfin Versions](#other-jellyfin-versions).

### Install from the Plugin Repository

The plugin can be installed straight from Jellyfin. Add this repository under **Dashboard → Plugins → Repositories**:

```
https://consultomer.github.io/Tunefin/manifest.json
```

Then install **Tunefin** from the **Catalog**, restart Jellyfin, and set **Sidecar URL** and **YouTube folder** in the plugin settings. You still need to run the sidecar: follow the sidecar steps in [Existing Jellyfin Installation](#existing-jellyfin-installation) or [Jellyfin Without Containers](#jellyfin-without-containers), and skip the plugin-install steps.

### New Install (Jellyfin Included)

1. **Clone the repository:**
   ```bash
   git clone https://github.com/consultomer/Tunefin.git
   cd Tunefin
   ```
2. **Configure the environment (optional):** ports, folders and user.
   ```bash
   cp .env.example .env
   ```
3. **Build and start the stack** (the first build takes a few minutes):
   ```bash
   docker compose up -d --build
   ```
4. **Finish the Jellyfin setup:** open `http://<server>:8096` and complete the setup wizard. If you have music, put it in `./data/music` and add `/media/Music` as a **Music** library.
5. **Connect YouTube:** each user follows [Connect Your YouTube Account](#connect-your-youtube-account).
6. **Search** for a song in Jellyfin or Finamp. 🎉

Folders created under `./data/`:

| Folder | What |
|---|---|
| `data/jellyfin/` | Jellyfin config and cache, including the plugin and users' saved cookies |
| `data/music/` | Your own music (optional) |
| `data/youtube/` | Liked songs: one folder per user, and `.store/` with the single real copy of each song |

### Existing Jellyfin Installation

See **[docs/existing-jellyfin.md](docs/existing-jellyfin.md)**. In short, you add two containers (the sidecar and a one-shot plugin installer) to your setup and mount one extra folder into Jellyfin. You don't need to rebuild or reconfigure Jellyfin.

### Jellyfin Without Containers

If Jellyfin is installed directly on the machine (a distro package, a seedbox or a shared host), see **[docs/native-jellyfin.md](docs/native-jellyfin.md)**. You build the plugin once and copy it into Jellyfin's `plugins` folder, then run the sidecar as a normal Node process (systemd, or `screen` + cron) as the same user as Jellyfin.

## Connect Your YouTube Account

Every user does this once, for their own account. Until they do, Jellyfin works as usual for them: no YouTube results and no downloads.

1. Sign in to **Jellyfin Web** in your browser.
2. Open **`http://<server>:8096/YouTube`** (or your Jellyfin address + `/YouTube`).
3. On a computer, open a **private/incognito** window and sign in at [music.youtube.com](https://music.youtube.com) with your account.
4. Export the cookies for `youtube.com` in **Netscape / cookies.txt** format, for example with the *Get cookies.txt LOCALLY* extension (allow it in private windows).
5. Paste the contents on the `/YouTube` page (or pick the file) and click **Save & import**.
6. Close the private window **without signing out**, so the exported login keeps working.

The page shows how many liked songs were found, and the import starts in the background (about 1 GB per 200 songs). The import runs again at every Jellyfin start and every 6 hours, or when you click **Import now**. **Disconnect** deletes your cookies.

> 🔐 Cookies are a login. They are stored on the Jellyfin server (`plugins/Jellyfin.Plugin.YouTubeMusic/users/`, mode 600) and only used for that user. Only connect accounts on a server you trust.

## Usage

**Jellyfin Web / Android / iOS:** search as usual. YouTube songs appear after your local results, with the album shown as `<Album> · YouTube`. Tap to play, and tap ♥ to keep the song.

**Finamp:** Finamp searches only the tab you're on.
- **Tracks** tab: YouTube songs appear directly.
- **Albums** tab (Finamp's default): you get one **"YouTube: &lt;search&gt;"** album. Open it to see the matches in YouTube's order.

Liked songs appear in your **Liked – &lt;name&gt;** library within about 30 seconds and are marked as favorites. Un-heart a song to remove it from your library. The file is deleted when no user has it any more.

## Configuration

### Plugin Settings

*Dashboard → Plugins → Tunefin*

| Setting | Default | Notes |
|---|---|---|
| Sidecar URL | `http://ytm-sidecar:8787` | How Jellyfin reaches the sidecar |
| Sidecar API key | – | Optional shared secret (same value as the sidecar's `API_KEY`) |
| Download songs when liked | on | |
| Show YouTube matches in search | on | |
| YouTube matches per search | 5 | |
| Parallel downloads during a likes import | 10 | Keep at or below `MAX_CONCURRENT_DOWNLOADS` |
| YouTube folder, as seen by Jellyfin | `/media/YouTube` | Must be the folder the sidecar mounts as `/youtube` |

The same page lists every user's connection status and has **Import now** and **Re-apply privacy** buttons.

### Sidecar Environment Variables

| Variable | Default | Purpose |
|---|---|---|
| `YOUTUBE_DIR` | `/youtube` | Per-user folders + `.store` (must be **one** mount so hardlinks work) |
| `USER_COOKIES_DIR` | `/user-cookies` | Users' cookies, written by the plugin. Mount read-only |
| `MAX_CONCURRENT_DOWNLOADS` | `10` | Parallel yt-dlp downloads. Lower it on small servers |
| `API_KEY` | – | If set, every request except `/health` needs `X-Api-Key` |
| `AUDIO_FORMAT` | `bestaudio[ext=m4a]/bestaudio[acodec=opus]/bestaudio` | yt-dlp format selector (m4a first for iOS) |
| `STREAM_CACHE_TTL_SECONDS` | `3600` | Stream-URL cache |
| `SEARCH_CACHE_TTL_SECONDS` | `600` | Search cache |
| `DOWNLOAD_TIMEOUT_SECONDS` | `600` | Kill a stuck download |
| `YTDLP_COOKIES_FILE` | – | Optional server-wide cookies, only for requests that don't name a user |
| `LOG_LEVEL` | `info` | |

## Releasing

Push a version tag. The **Release** workflow builds the plugin, attaches the zip to a GitHub release and adds the version to `manifest.json` on the `gh-pages` branch:

```bash
git tag -a v1.0.1 -m "What changed in this version" && git push origin v1.0.1
```

## Updating

```bash
git pull
docker compose up -d --build        # rebuilds the sidecar and reinstalls the plugin
docker compose restart jellyfin     # loads the new plugin version
```

When YouTube changes something and playback breaks, rebuild the sidecar to get the latest yt-dlp:

```bash
docker compose build --no-cache ytm-sidecar && docker compose up -d ytm-sidecar
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| No YouTube results in search | The user hasn't connected YouTube at `/YouTube` |
| Plugin missing in the Dashboard | `docker compose logs ytm-plugin`, then restart Jellyfin |
| "Could not use these cookies" | Export again from a fresh private window. The export must include `SAPISID` (be signed in) |
| Playback/downloads fail, sidecar log says *confirm you're not a bot* or *format is not available* | That user's cookies expired. Reconnect at `/YouTube` |
| Song liked but never appears | Check `docker compose logs ytm-sidecar`, and that the YouTube folder is mounted into Jellyfin at the path in the plugin settings |
| `Permission denied` in the sidecar log | Jellyfin and the sidecar must run as the same `PUID:PGID`, which must own `data/youtube` |
| Server slow during an import | Lower *Parallel downloads* in the plugin settings and `MAX_CONCURRENT_DOWNLOADS` |

Useful logs: `docker compose logs -f ytm-sidecar` and Jellyfin's *Dashboard → Logs*.

## Other Jellyfin Versions

The plugin is compiled against Jellyfin **12.4** (.NET 10). For another version, change `JellyfinVersion` (and `TargetFramework` if needed) in `Jellyfin.Plugin.YouTubeMusic/Jellyfin.Plugin.YouTubeMusic.csproj`, plus `targetAbi` in `meta.json`, then rebuild. `curl http://<server>:8096/System/Info/Public` shows your version.

## Disclaimer

Tunefin is not affiliated with YouTube, Google or Jellyfin. It uses yt-dlp and unofficial YouTube APIs, which can break at any time. Downloading content may be against YouTube's Terms of Service in your country. Use it for personal use with content you have the right to access.

## Contributing
Contributions are welcome! Please fork the repository and create a pull request with your changes. Ensure that your code adheres to the project's coding standards and passes all tests.

## License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Contact
For any questions or support, please contact:

- **Omer Abdulrehman**
- **Email:** [consultomer@gmail.com](mailto:consultomer@gmail.com)
- **LinkedIn:** [Omer Abdulrehman](https://www.linkedin.com/in/omerarehman/)
