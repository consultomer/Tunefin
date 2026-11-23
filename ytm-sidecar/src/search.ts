import { Innertube } from 'youtubei.js';
import { config } from './config.js';
import { TtlCache } from './ttlCache.js';

export interface SearchResult {
  videoId: string;
  title: string;
  artist: string;
  album: string | null;
  durationSeconds: number | null;
  thumbnailUrl: string | null;
}

let clientPromise: Promise<Innertube> | null = null;
let clientCreatedAt = 0;
const CLIENT_MAX_AGE_MS = 6 * 3600 * 1000;

/** Lazily create (and periodically refresh) the InnerTube session. */
function client(): Promise<Innertube> {
  if (!clientPromise || Date.now() - clientCreatedAt > CLIENT_MAX_AGE_MS) {
    clientCreatedAt = Date.now();
    clientPromise = Innertube.create({ retrieve_player: false, generate_session_locally: true }).catch((err) => {
      clientPromise = null;
      throw err;
    });
  }
  return clientPromise;
}

/** YTM thumbnails come as `...=w60-h60-l90-rj`; ask for a cover-sized square instead. */
function upscaleThumbnail(url: string | undefined): string | null {
  if (!url) return null;
  return url.replace(/=w\d+-h\d+/, '=w544-h544');
}

interface RawSong {
  id?: string;
  item_type?: string;
  title?: string;
  artists?: { name: string }[];
  author?: { name: string };
  album?: { name?: string };
  duration?: { seconds?: number };
  thumbnails?: { url: string; width: number }[];
}

function mapSong(raw: RawSong): SearchResult | null {
  if (!raw.id || !raw.title) return null;
  const artists = raw.artists?.map((a) => a.name).filter(Boolean) ?? [];
  const thumbs = [...(raw.thumbnails ?? [])].sort((a, b) => b.width - a.width);
  return {
    videoId: raw.id,
    title: raw.title,
    artist: artists.length > 0 ? artists.join(', ') : (raw.author?.name ?? 'Unknown Artist'),
    album: raw.album?.name ?? null,
    durationSeconds: raw.duration?.seconds ?? null,
    thumbnailUrl: upscaleThumbnail(thumbs[0]?.url),
  };
}

const cache = new TtlCache<SearchResult[]>(config.searchCacheTtlMs, 500);

export async function searchSongs(query: string, limit: number): Promise<SearchResult[]> {
  const key = `${limit}|${query.trim().toLowerCase()}`;
  const all = await cache.getOrLoad(key, async () => {
    const yt = await client();
    const results: SearchResult[] = [];
    // Search and SearchContinuation share the shape we need but not a common type.
    type Page = { contents?: unknown; has_continuation: boolean; getContinuation(): Promise<Page> };
    let page = (await yt.music.search(query, { type: 'song' })) as unknown as Page;
    // First page usually has ~20 songs; follow continuations until we have enough.
    for (let i = 0; i < 3; i++) {
      // First page: contents = MusicShelf[]; continuation: contents = MusicShelf.
      const raw = page.contents as { contents?: RawSong[] } | { contents?: RawSong[] }[] | undefined;
      const shelf = Array.isArray(raw) ? raw : raw ? [raw] : [];
      for (const section of shelf) {
        for (const item of section.contents ?? []) {
          if (item.item_type && item.item_type !== 'song') continue;
          const mapped = mapSong(item);
          if (mapped && !results.some((r) => r.videoId === mapped.videoId)) results.push(mapped);
        }
      }
      if (results.length >= limit || !page.has_continuation) break;
      page = await page.getContinuation();
    }
    return results;
  });
  return all.slice(0, limit);
}
