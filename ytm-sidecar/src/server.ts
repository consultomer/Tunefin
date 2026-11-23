import { Readable } from 'node:stream';
import Fastify, { type FastifyReply, type FastifyRequest } from 'fastify';
import { config } from './config.js';
import { activeDownloads, downloadTrack } from './download.js';
import { activeStoreDownloads, addToUser, listUser, removeFromUser } from './library.js';
import { getLikedSongs } from './likes.js';
import { searchSongs } from './search.js';
import { getStream, invalidateStream } from './stream.js';
import { NoAccountError, YtDlpError, hasAccount, isValidAccount, isValidVideoId } from './ytdlp.js';

const app = Fastify({
  logger: { level: config.logLevel },
  // Downloads can take a while; Jellyfin waits for the response.
  requestTimeout: 0,
});

// ---- optional API key --------------------------------------------------------
if (config.apiKey) {
  app.addHook('onRequest', async (req, reply) => {
    if (req.url === '/health') return;
    if (req.headers['x-api-key'] !== config.apiKey) {
      return reply.code(401).send({ error: 'unauthorized' });
    }
  });
}

app.get('/health', async () => ({ status: 'ok', activeDownloads: [...activeDownloads(), ...activeStoreDownloads()] }));

// ---- GET /search -------------------------------------------------------------
app.get<{ Querystring: { q?: string; limit?: string } }>('/search', async (req, reply) => {
  const q = (req.query.q ?? '').trim();
  if (!q) return reply.code(400).send({ error: 'query parameter "q" is required' });
  const limit = Math.min(Math.max(Number.parseInt(req.query.limit ?? '15', 10) || 15, 1), config.maxSearchLimit);
  try {
    return await searchSongs(q, limit);
  } catch (err) {
    req.log.error({ err }, 'search failed');
    return reply.code(502).send({ error: 'search failed', detail: (err as Error).message });
  }
});

// ---- GET/HEAD /stream/:videoId (range-aware proxy) ---------------------------
const FORWARDED_HEADERS = ['content-type', 'content-length', 'content-range', 'accept-ranges', 'last-modified', 'etag'];

const UPSTREAM_RETRY_DELAYS_MS = [750, 1500, 2500, 3500, 1000];
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** Poll a freshly resolved URL with a 1-byte range request until googlevideo accepts it. */
async function warmUp(url: string): Promise<void> {
  for (const delay of [0, ...UPSTREAM_RETRY_DELAYS_MS]) {
    await sleep(delay);
    try {
      const res = await fetch(url, { headers: { 'user-agent': 'Mozilla/5.0', range: 'bytes=0-0' } });
      await res.body?.cancel();
      if (res.ok || res.status === 206) return;
    } catch {
      /* ignore */
    }
  }
}

async function fetchUpstream(videoId: string, account: string | undefined, req: FastifyRequest, signal: AbortSignal) {
  const stream = await getStream(videoId, account);
  const headers: Record<string, string> = { 'user-agent': 'Mozilla/5.0' };
  // googlevideo throttles un-ranged requests to a crawl, so always ask for a range upstream.
  headers.range = req.headers.range ?? 'bytes=0-';
  const res = await fetch(stream.url, { method: req.method === 'HEAD' ? 'HEAD' : 'GET', headers, signal });
  return { stream, res };
}

type StreamRequest = FastifyRequest<{ Params: { videoId: string }; Querystring: { account?: string } }>;

async function handleStream(req: StreamRequest, reply: FastifyReply) {
  const { videoId } = req.params;
  const { account } = req.query;
  if (!isValidVideoId(videoId)) return reply.code(400).send({ error: 'invalid videoId' });
  if (account !== undefined && !isValidAccount(account)) return reply.code(400).send({ error: 'invalid account' });
  if (account !== undefined && !hasAccount(account)) return reply.code(403).send({ error: 'this user has not connected a YouTube account' });

  const abort = new AbortController();
  req.raw.on('close', () => abort.abort());

  try {
    // Freshly issued googlevideo URLs often answer 403 for a few seconds before they "activate",
    // so retry the same URL with backoff; only re-resolve (new URL) as a last resort.
    let { stream, res } = await fetchUpstream(videoId, account, req, abort.signal);
    for (let attempt = 0; (res.status === 403 || res.status === 410) && attempt < UPSTREAM_RETRY_DELAYS_MS.length; attempt++) {
      await res.body?.cancel();
      await sleep(UPSTREAM_RETRY_DELAYS_MS[attempt]!);
      if (abort.signal.aborted) return reply;
      if (attempt === UPSTREAM_RETRY_DELAYS_MS.length - 1) invalidateStream(videoId);
      ({ stream, res } = await fetchUpstream(videoId, account, req, abort.signal));
    }

    if (!res.ok && res.status !== 206) {
      await res.body?.cancel();
      req.log.warn({ videoId, status: res.status }, 'upstream refused stream');
      return reply.code(502).send({ error: 'upstream error', status: res.status });
    }

    // Client didn't ask for a range: answer a plain 200 with the full length.
    const clientWantedRange = Boolean(req.headers.range);
    reply.code(!clientWantedRange && res.status === 206 ? 200 : res.status);
    for (const h of FORWARDED_HEADERS) {
      if (!clientWantedRange && h === 'content-range') continue;
      const v = res.headers.get(h);
      if (v) reply.header(h, v);
    }
    // googlevideo answers with a generic type; give clients the real container.
    reply.header('content-type', stream.contentType);
    reply.header('accept-ranges', 'bytes');
    reply.header('cache-control', 'no-store');

    if (req.method === 'HEAD' || !res.body) return reply.send();
    return reply.send(Readable.fromWeb(res.body as import('node:stream/web').ReadableStream));
  } catch (err) {
    if (abort.signal.aborted) return reply; // client went away mid-seek; nothing to do
    if (err instanceof NoAccountError) return reply.code(403).send({ error: err.message });
    req.log.error({ err, videoId }, 'stream failed');
    const status = err instanceof YtDlpError ? 502 : 500;
    return reply.code(status).send({ error: 'stream failed', detail: (err as Error).message });
  }
}

// Explicit HEAD (instead of Fastify's auto HEAD, which would run GET and pull the whole file upstream).
app.route({ method: ['GET', 'HEAD'], url: '/stream/:videoId', exposeHeadRoute: false, handler: handleStream });

// ---- GET /resolve/:videoId (container/codec info; also warms the stream cache) ----
app.get<{ Params: { videoId: string }; Querystring: { account?: string } }>('/resolve/:videoId', async (req, reply) => {
  const { videoId } = req.params;
  const { account } = req.query;
  if (!isValidVideoId(videoId)) return reply.code(400).send({ error: 'invalid videoId' });
  if (account !== undefined && !isValidAccount(account)) return reply.code(400).send({ error: 'invalid account' });
  if (account !== undefined && !hasAccount(account)) return reply.code(403).send({ error: 'this user has not connected a YouTube account' });
  try {
    const s = await getStream(videoId, account);
    void warmUp(s.url); // background: by the time the player asks for /stream it's usually live
    return { videoId, ext: s.ext, acodec: s.acodec, contentType: s.contentType, expiresAt: s.expiresAt };
  } catch (err) {
    if (err instanceof NoAccountError) return reply.code(403).send({ error: err.message });
    req.log.error({ err, videoId }, 'resolve failed');
    return reply.code(502).send({ error: 'resolve failed', detail: (err as Error).message });
  }
});

// ---- POST /download ----------------------------------------------------------
interface DownloadBody {
  videoId: string;
  title: string;
  artist: string;
  album?: string | null;
  /** Jellyfin user folder; when set, the song goes to that user's private library (shared store + hardlink). */
  user?: string;
  /** Jellyfin user id ("N" format) whose YouTube cookies are used. */
  account?: string;
}

app.post<{ Body: DownloadBody }>(
  '/download',
  {
    schema: {
      body: {
        type: 'object',
        required: ['videoId', 'title', 'artist'],
        properties: {
          videoId: { type: 'string' },
          title: { type: 'string', minLength: 1, maxLength: 500 },
          artist: { type: 'string', minLength: 1, maxLength: 500 },
          album: { type: ['string', 'null'], maxLength: 500 },
          user: { type: 'string', minLength: 1, maxLength: 200 },
          account: { type: 'string', pattern: '^[0-9a-f]{32}$' },
        },
      },
    },
  },
  async (req, reply) => {
    const body = req.body;
    if (!isValidVideoId(body.videoId)) return reply.code(400).send({ error: 'invalid videoId' });
    if (body.account !== undefined && !hasAccount(body.account)) return reply.code(403).send({ error: 'this user has not connected a YouTube account' });
    try {
      const started = Date.now();
      if (body.user) {
        const r = await addToUser(body.user, body);
        req.log.info({ videoId: body.videoId, user: body.user, ...r, ms: Date.now() - started }, 'download complete');
        return { status: 'downloaded', filePath: r.filePath, alreadyExisted: r.status === 'exists', storeHit: r.status === 'linked' };
      }
      const result = await downloadTrack(body);
      req.log.info({ videoId: body.videoId, ...result, ms: Date.now() - started }, 'download complete');
      return { status: 'downloaded', filePath: result.filePath, alreadyExisted: result.status === 'exists' };
    } catch (err) {
      if (err instanceof NoAccountError) return reply.code(403).send({ error: err.message });
      req.log.error({ err, videoId: body.videoId }, 'download failed');
      return reply.code(502).send({ error: 'download failed', detail: (err as Error).message });
    }
  },
);

// ---- per-user library -------------------------------------------------------
app.get<{ Params: { user: string } }>('/library/:user', async (req, reply) => {
  try {
    return { videoIds: await listUser(req.params.user) };
  } catch (err) {
    return reply.code(400).send({ error: (err as Error).message });
  }
});

app.delete<{ Params: { user: string; videoId: string } }>('/library/:user/:videoId', async (req, reply) => {
  if (!isValidVideoId(req.params.videoId)) return reply.code(400).send({ error: 'invalid videoId' });
  try {
    const removed = await removeFromUser(req.params.user, req.params.videoId);
    req.log.info({ user: req.params.user, videoId: req.params.videoId, removed }, 'removed from user library');
    return { removed };
  } catch (err) {
    return reply.code(400).send({ error: (err as Error).message });
  }
});

// ---- POST /likes: a user's YouTube Music "Liked songs" ---------------------------
app.post<{ Body: { cookies: string } }>(
  '/likes',
  { schema: { body: { type: 'object', required: ['cookies'], properties: { cookies: { type: 'string', minLength: 10, maxLength: 1_000_000 } } } } },
  async (req, reply) => {
    try {
      const songs = await getLikedSongs(req.body.cookies);
      req.log.info({ count: songs.length }, 'read liked songs');
      return songs;
    } catch (err) {
      // never log the cookies themselves
      req.log.warn({ msg2: (err as Error).message }, 'reading liked songs failed');
      return reply.code(400).send({ error: (err as Error).message });
    }
  },
);

// ---- lifecycle ---------------------------------------------------------------
for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.on(signal, () => {
    app.log.info(`${signal} received, shutting down`);
    app.close().then(() => process.exit(0));
  });
}

app.listen({ host: config.host, port: config.port }).catch((err) => {
  app.log.error(err);
  process.exit(1);
});
