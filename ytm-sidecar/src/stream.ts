import { config } from './config.js';
import { TtlCache } from './ttlCache.js';
import { runYtDlp, watchUrl } from './ytdlp.js';

export interface ResolvedStream {
  url: string;
  ext: string;
  acodec: string;
  contentType: string;
  /** Unix seconds, parsed from the googlevideo `expire` param when present. */
  expiresAt: number | null;
}

const CONTENT_TYPES: Record<string, string> = {
  m4a: 'audio/mp4',
  mp4: 'audio/mp4',
  webm: 'audio/webm',
  opus: 'audio/ogg',
  ogg: 'audio/ogg',
  mp3: 'audio/mpeg',
};

const cache = new TtlCache<ResolvedStream>(config.streamCacheTtlMs, 2000);

async function resolve(videoId: string, account?: string): Promise<ResolvedStream> {
  // Equivalent to `yt-dlp -f bestaudio -g`, but also prints the container so we can set Content-Type.
  const out = await runYtDlp(
    ['-f', config.audioFormat, '--skip-download', '--print', '%(ext)s\t%(acodec)s\t%(url)s', watchUrl(videoId)],
    config.resolveTimeoutMs,
    account,
  );
  const line = out.trim().split('\n').pop() ?? '';
  const [ext = '', acodec = '', url = ''] = line.split('\t');
  if (!url.startsWith('http')) throw new Error(`yt-dlp returned no stream URL for ${videoId}`);
  const expire = Number(new URL(url).searchParams.get('expire'));
  return {
    url,
    ext,
    acodec,
    contentType: CONTENT_TYPES[ext] ?? 'application/octet-stream',
    expiresAt: Number.isFinite(expire) && expire > 0 ? expire : null,
  };
}

/** Stream URLs aren't tied to the account, so the cache is shared; `account` only picks the cookies used to resolve. */
export function getStream(videoId: string, account?: string): Promise<ResolvedStream> {
  return cache.getOrLoad(
    videoId,
    () => resolve(videoId, account),
    // Never cache past the URL's own expiry (minus a safety margin).
    (s) => (s.expiresAt ? Math.min(config.streamCacheTtlMs, s.expiresAt * 1000 - Date.now() - 5 * 60_000) : config.streamCacheTtlMs),
  );
}

export function invalidateStream(videoId: string): void {
  cache.delete(videoId);
}
