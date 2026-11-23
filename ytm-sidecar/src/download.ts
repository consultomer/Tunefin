import { mkdir, readdir } from 'node:fs/promises';
import path from 'node:path';
import { config } from './config.js';
import { runYtDlp, watchUrl } from './ytdlp.js';

export interface DownloadRequest {
  videoId: string;
  /** Jellyfin account whose YouTube cookies are used. */
  account?: string;
  title: string;
  artist: string;
  album?: string | null;
}

export interface DownloadResult {
  status: 'downloaded' | 'exists';
  filePath: string;
}

const AUDIO_EXTS = ['.m4a', '.opus', '.ogg', '.webm', '.mp3', '.flac'];
const MAX_SEGMENT_BYTES = 150;

/** Make a string safe to use as a single path segment on Linux, Windows (SMB) and macOS. */
export function sanitizeSegment(input: string | null | undefined, fallback: string): string {
  let s = (input ?? '')
    .normalize('NFC')
    .replace(/[\u0000-\u001f\u007f]/g, '')
    .replace(/[<>:"/\\|?*]/g, '_')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/^[.\s]+|[.\s]+$/g, '');
  if (/^(con|prn|aux|nul|com\d|lpt\d)$/i.test(s)) s = `_${s}`;
  // Truncate on a code-point boundary to stay under filesystem byte limits.
  while (Buffer.byteLength(s, 'utf8') > MAX_SEGMENT_BYTES) s = [...s].slice(0, -1).join('').trimEnd();
  return s.length > 0 ? s : fallback;
}

/** Literal text inside a yt-dlp output template must have `%` doubled. */
const escapeTemplate = (s: string) => s.replace(/%/g, '%%');

/** Metadata values must be single-line for --parse-metadata. */
const oneLine = (s: string) => s.replace(/[\r\n]+/g, ' ').trim();

/**
 * Build a `--parse-metadata FROM:TO` pair that sets `meta_<field>` to a literal value.
 * FROM is wrapped in brackets so yt-dlp always treats it as a template (a bare word
 * would be interpreted as a field name), then the regex strips the brackets again.
 */
function setMeta(field: string, value: string): string[] {
  // `:` separates FROM and TO, so literal colons ("Title: Remix") must be escaped as `\:`.
  const literal = escapeTemplate(oneLine(value)).replace(/:/g, '\\:');
  return ['--parse-metadata', `[${literal}]:^\\[(?P<meta_${field}>.*)\\]$`];
}

/** Clear a metadata field (YouTube descriptions/comments are noise in a music library). */
function clearMeta(field: string): string[] {
  return ['--parse-metadata', `:(?P<meta_${field}>)`];
}

/** yt-dlp argv for a download; `outputBase` is the final path without extension. Exported for tests. */
export function buildDownloadArgs(req: DownloadRequest, outputBase: string, tempDir: string, source: string): string[] {
  return [
    '-f', config.audioFormat,
    // Extract audio without re-encoding: m4a stays m4a, webm/opus is remuxed to .opus.
    '-x',
    '--paths', `temp:${tempDir}`,
    '-o', escapeTemplate(outputBase) + '.%(ext)s',
    '--embed-thumbnail',
    '--convert-thumbnails', 'jpg',
    // Crop YouTube's 16:9 thumbnails to a square cover.
    '--postprocessor-args',
    "ThumbnailsConvertor+ffmpeg_o:-c:v mjpeg -qmin 1 -qscale:v 2 -vf crop=\"'if(gt(ih,iw),iw,ih)':'if(gt(iw,ih),ih,iw)'\"",
    '--add-metadata',
    ...setMeta('title', req.title),
    ...setMeta('artist', req.artist),
    ...setMeta('album_artist', req.artist.split(',')[0]!.trim()),
    ...(req.album ? setMeta('album', req.album) : setMeta('album', 'Singles')),
    ...clearMeta('description'),
    ...clearMeta('synopsis'),
    ...clearMeta('comment'),
    ...clearMeta('purl'),
    '--no-overwrites',
    '--no-simulate',
    '--print', 'after_move:filepath',
    source,
  ];
}

export async function findExisting(dir: string, baseName: string): Promise<string | null> {
  try {
    const files = await readdir(dir);
    const hit = files.find((f) => {
      const ext = path.extname(f).toLowerCase();
      return AUDIO_EXTS.includes(ext) && f.slice(0, -ext.length) === baseName;
    });
    return hit ? path.join(dir, hit) : null;
  } catch {
    return null;
  }
}

// ---- simple concurrency limiter -------------------------------------------
let running = 0;
const waiters: (() => void)[] = [];
export async function withSlot<T>(fn: () => Promise<T>): Promise<T> {
  if (running >= config.maxConcurrentDownloads) await new Promise<void>((r) => waiters.push(r));
  running++;
  try {
    return await fn();
  } finally {
    running--;
    waiters.shift()?.();
  }
}

// ---- dedupe: one in-flight download per videoId ----------------------------
const inFlight = new Map<string, Promise<DownloadResult>>();

export function activeDownloads(): string[] {
  return [...inFlight.keys()];
}

export function downloadTrack(req: DownloadRequest): Promise<DownloadResult> {
  const existing = inFlight.get(req.videoId);
  if (existing) return existing;
  const p = doDownload(req).finally(() => inFlight.delete(req.videoId));
  inFlight.set(req.videoId, p);
  return p;
}

async function doDownload(req: DownloadRequest): Promise<DownloadResult> {
  const root = config.downloadSubdir ? path.join(config.musicDir, sanitizeSegment(config.downloadSubdir, 'Liked')) : config.musicDir;
  let dir: string;
  let title: string;
  if (config.downloadLayout === 'flat') {
    // <root>/<Main Artist> - <Title>.m4a  (all artists/album are still in the tags)
    const mainArtist = req.artist.split(',')[0]!.trim();
    dir = root;
    title = sanitizeSegment(`${mainArtist} - ${req.title}`, req.videoId);
  } else {
    // <root>/<Artist>/<Album>/<Title>.m4a
    dir = path.join(root, sanitizeSegment(req.artist, 'Unknown Artist'), sanitizeSegment(req.album, 'Singles'));
    title = sanitizeSegment(req.title, req.videoId);
  }

  // Guard against anything escaping the music root (sanitizeSegment should already prevent it).
  const resolved = path.resolve(dir);
  if (resolved !== path.resolve(config.musicDir) && !resolved.startsWith(path.resolve(config.musicDir) + path.sep)) {
    throw new Error('refusing to write outside the music directory');
  }

  const already = await findExisting(dir, title);
  if (already) return { status: 'exists', filePath: already };

  return withSlot(async () => {
    // Re-check: an identical request may have finished while we waited for a slot.
    const raced = await findExisting(dir, title);
    if (raced) return { status: 'exists', filePath: raced };

    await mkdir(dir, { recursive: true });
    const tempDir = path.join(config.musicDir, '.ytm-tmp');
    await mkdir(tempDir, { recursive: true });

    const args = buildDownloadArgs(req, path.join(dir, title), tempDir, watchUrl(req.videoId));

    const out = await runYtDlp(args, config.downloadTimeoutMs, req.account);
    const filePath = out.trim().split('\n').filter(Boolean).pop() ?? (await findExisting(dir, title));
    if (!filePath) throw new Error('yt-dlp finished but the output file could not be located');
    return { status: 'downloaded', filePath };
  });
}
