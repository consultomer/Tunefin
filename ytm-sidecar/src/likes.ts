/**
 * Reads a user's YouTube Music "Liked songs" (playlist LM) using their browser cookies.
 */
import { Innertube } from 'youtubei.js';
import type { SearchResult } from './search.js';

/** Turn a Netscape cookies.txt (or a raw "a=b; c=d" header) into a Cookie header for youtube.com. */
export function cookieHeader(input: string): string {
  const text = input.trim();
  if (!text.includes('\t')) {
    if (/^[^=;\s]+=/.test(text)) return text; // already a header
    throw new Error('unrecognised cookie format (expected a Netscape cookies.txt export)');
  }
  const pairs = new Map<string, string>();
  for (const raw of text.split(/\r?\n/)) {
    if (!raw.trim() || (raw.startsWith('#') && !raw.startsWith('#HttpOnly_'))) continue;
    const f = raw.replace(/^#HttpOnly_/, '').split('\t');
    if (f.length < 7) continue;
    const domain = f[0]!.replace(/^\./, '');
    if (domain !== 'youtube.com' && !domain.endsWith('.youtube.com')) continue;
    pairs.set(f[5]!, f[6]!.trim());
  }
  if (!pairs.has('SAPISID') && !pairs.has('__Secure-3PAPISID')) {
    throw new Error('cookies are not from a logged-in youtube.com session (SAPISID missing)');
  }
  return [...pairs].map(([k, v]) => `${k}=${v}`).join('; ');
}

interface RawItem {
  id?: string;
  title?: string;
  artists?: { name: string }[];
  author?: { name: string };
  album?: { name?: string };
  duration?: { seconds?: number };
  thumbnails?: { url: string; width: number }[];
}

type Page = { items?: unknown; contents?: unknown; has_continuation?: boolean; getContinuation(): Promise<Page> };

export async function getLikedSongs(cookies: string, max = 5000): Promise<SearchResult[]> {
  const yt = await Innertube.create({ cookie: cookieHeader(cookies), retrieve_player: false });
  if (!yt.session.logged_in) throw new Error('YouTube did not accept these cookies (not logged in) - export them again');

  const out: SearchResult[] = [];
  const seen = new Set<string>();
  let page = (await yt.music.getPlaylist('LM')) as unknown as Page;
  for (let guard = 0; guard < 200; guard++) {
    const raw = (page.items ?? page.contents ?? []) as Iterable<RawItem>;
    for (const it of raw) {
      if (!it.id || !it.title || seen.has(it.id)) continue;
      seen.add(it.id);
      const artists = it.artists?.map((a) => a.name).filter(Boolean) ?? [];
      const thumbs = [...(it.thumbnails ?? [])].sort((a, b) => b.width - a.width);
      out.push({
        videoId: it.id,
        title: it.title,
        artist: artists.length ? artists.join(', ') : (it.author?.name ?? 'Unknown Artist'),
        album: it.album?.name ?? null,
        durationSeconds: it.duration?.seconds ?? null,
        thumbnailUrl: thumbs[0]?.url?.replace(/=w\d+-h\d+/, '=w544-h544') ?? null,
      });
    }
    if (out.length >= max || !page.has_continuation) break;
    page = await page.getContinuation();
  }
  return out;
}
