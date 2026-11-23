function int(name: string, fallback: number): number {
  const raw = process.env[name];
  if (raw === undefined || raw === '') return fallback;
  const n = Number.parseInt(raw, 10);
  if (Number.isNaN(n)) throw new Error(`Environment variable ${name} must be an integer, got "${raw}"`);
  return n;
}

export const config = {
  host: process.env.HOST ?? '0.0.0.0',
  port: int('PORT', 8787),
  /** Root of the Jellyfin music library as seen by this container. */
  musicDir: process.env.MUSIC_DIR ?? '/music',
  /** Optional folder under MUSIC_DIR for downloads, e.g. "Liked". */
  downloadSubdir: process.env.DOWNLOAD_SUBDIR ?? '',
  /** "artist-album" = <Artist>/<Album>/<Title>, "flat" = "<Artist> - <Title>" in one folder. */
  downloadLayout: (process.env.DOWNLOAD_LAYOUT ?? 'artist-album') as 'artist-album' | 'flat',
  /** Per-user libraries: <youtubeDir>/<user>/..., single copies in <youtubeDir>/.store (same filesystem: hardlinks). */
  youtubeDir: process.env.YOUTUBE_DIR ?? '/youtube',
  ytDlpPath: process.env.YTDLP_PATH ?? 'yt-dlp',
  /** yt-dlp needs a JS runtime for YouTube signature solving; node is always available here. */
  ytDlpJsRuntime: process.env.YTDLP_JS_RUNTIME ?? 'node',
  /**
   * Per-user YouTube cookies: <userCookiesDir>/<account>.cookies.txt, written by the Jellyfin plugin
   * when a user connects their account. Requests name the account with ?account= / "account".
   */
  userCookiesDir: process.env.USER_COOKIES_DIR ?? '/user-cookies',
  /** Optional server-wide cookies file, used only for requests that don't name an account. */
  cookiesFile: process.env.YTDLP_COOKIES_FILE || undefined,
  /** Format selector used for both streaming and downloading. m4a first for widest client support (iOS). */
  audioFormat: process.env.AUDIO_FORMAT ?? 'bestaudio[ext=m4a]/bestaudio[acodec=opus]/bestaudio',
  /** googlevideo URLs live ~6h; cache well below that. */
  streamCacheTtlMs: int('STREAM_CACHE_TTL_SECONDS', 3600) * 1000,
  searchCacheTtlMs: int('SEARCH_CACHE_TTL_SECONDS', 600) * 1000,
  maxSearchLimit: int('MAX_SEARCH_LIMIT', 50),
  maxConcurrentDownloads: int('MAX_CONCURRENT_DOWNLOADS', 10),
  downloadTimeoutMs: int('DOWNLOAD_TIMEOUT_SECONDS', 600) * 1000,
  resolveTimeoutMs: int('RESOLVE_TIMEOUT_SECONDS', 45) * 1000,
  /** Optional shared secret; when set, every request except /health must send `X-Api-Key`. */
  apiKey: process.env.API_KEY || undefined,
  logLevel: process.env.LOG_LEVEL ?? 'info',
};
