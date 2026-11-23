/**
 * Per-user liked-song libraries without duplicate files.
 *
 *   <youtubeDir>/.store/<videoId>.m4a          one real copy per song (tagged, cover art)
 *   <youtubeDir>/<user>/<Artist> - <Title>.m4a  hardlink to the store file, one per user who liked it
 *   <youtubeDir>/<user>/.ytm-library.json       { videoId: fileName } manifest
 *
 * Hardlinks share the same data on disk, so a song liked by three users uses the space of one.
 * Each user folder is a separate Jellyfin library that only that user can see.
 */
import { link, mkdir, readFile, rename, stat, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { config } from './config.js';
import { buildDownloadArgs, findExisting, sanitizeSegment, withSlot, type DownloadRequest } from './download.js';
import { runYtDlp, watchUrl } from './ytdlp.js';

const storeDir = () => path.join(config.youtubeDir, '.store');
const tempDir = () => path.join(config.youtubeDir, '.ytm-tmp');
const MANIFEST = '.ytm-library.json';

export interface LinkResult {
  status: 'downloaded' | 'linked' | 'exists';
  filePath: string;
}

export function userFolderName(user: string): string {
  const name = sanitizeSegment(user, '');
  if (!name || name.startsWith('.')) throw new Error('invalid user folder name');
  return name;
}

// ---- manifest (serialised per user) ------------------------------------------
const locks = new Map<string, Promise<unknown>>();
function withUserLock<T>(user: string, fn: () => Promise<T>): Promise<T> {
  const prev = locks.get(user) ?? Promise.resolve();
  const next = prev.then(fn, fn);
  locks.set(user, next.catch(() => undefined));
  return next;
}

async function readManifest(userDir: string): Promise<Record<string, string>> {
  try {
    return JSON.parse(await readFile(path.join(userDir, MANIFEST), 'utf8')) as Record<string, string>;
  } catch {
    return {};
  }
}

async function writeManifest(userDir: string, manifest: Record<string, string>): Promise<void> {
  const file = path.join(userDir, MANIFEST);
  await writeFile(file + '.tmp', JSON.stringify(manifest, null, 1));
  await rename(file + '.tmp', file);
}

const exists = (p: string) => stat(p).then(() => true, () => false);

// ---- store: one download per videoId, shared by everyone ------------------------
const storing = new Map<string, Promise<{ file: string; downloaded: boolean }>>();

function ensureStored(req: DownloadRequest): Promise<{ file: string; downloaded: boolean }> {
  const pending = storing.get(req.videoId);
  if (pending) return pending;
  const p = (async () => {
    const dir = storeDir();
    const hit = await findExisting(dir, req.videoId);
    if (hit) return { file: hit, downloaded: false };
    return withSlot(async () => {
      const raced = await findExisting(dir, req.videoId);
      if (raced) return { file: raced, downloaded: false };
      await mkdir(dir, { recursive: true });
      await mkdir(tempDir(), { recursive: true });
      const out = await runYtDlp(
        buildDownloadArgs(req, path.join(dir, req.videoId), tempDir(), watchUrl(req.videoId)),
        config.downloadTimeoutMs,
        req.account,
      );
      const file = out.trim().split('\n').filter(Boolean).pop() ?? (await findExisting(dir, req.videoId));
      if (!file) throw new Error('yt-dlp finished but the output file could not be located');
      return { file, downloaded: true };
    });
  })().finally(() => storing.delete(req.videoId));
  storing.set(req.videoId, p);
  return p;
}

export function activeStoreDownloads(): string[] {
  return [...storing.keys()];
}

// ---- per-user operations ------------------------------------------------------
export async function addToUser(user: string, req: DownloadRequest): Promise<LinkResult> {
  const folder = userFolderName(user);
  const userDir = path.join(config.youtubeDir, folder);

  // Fast path: already in this user's library.
  const before = await readManifest(userDir);
  const known = before[req.videoId];
  if (known && (await exists(path.join(userDir, known)))) {
    return { status: 'exists', filePath: path.join(userDir, known) };
  }

  const stored = await ensureStored(req);

  return withUserLock(folder, async () => {
    await mkdir(userDir, { recursive: true });
    const manifest = await readManifest(userDir);
    const current = manifest[req.videoId];
    if (current && (await exists(path.join(userDir, current)))) {
      return { status: 'exists' as const, filePath: path.join(userDir, current) };
    }

    const ext = path.extname(stored.file);
    const mainArtist = req.artist.split(',')[0]!.trim();
    let base = sanitizeSegment(`${mainArtist} - ${req.title}`, req.videoId);
    // Two different songs with the same artist/title: keep both.
    if (await exists(path.join(userDir, base + ext))) base = sanitizeSegment(`${base} [${req.videoId}]`, req.videoId);
    const fileName = base + ext;

    await link(stored.file, path.join(userDir, fileName));
    manifest[req.videoId] = fileName;
    await writeManifest(userDir, manifest);
    return { status: stored.downloaded ? ('downloaded' as const) : ('linked' as const), filePath: path.join(userDir, fileName) };
  });
}

export async function removeFromUser(user: string, videoId: string): Promise<boolean> {
  const folder = userFolderName(user);
  const userDir = path.join(config.youtubeDir, folder);
  return withUserLock(folder, async () => {
    const manifest = await readManifest(userDir);
    const fileName = manifest[videoId];
    if (!fileName) return false;
    await unlink(path.join(userDir, fileName)).catch(() => undefined);
    delete manifest[videoId];
    await writeManifest(userDir, manifest);

    // Nobody else links to it any more -> free the space.
    const stored = await findExisting(storeDir(), videoId);
    if (stored && (await stat(stored)).nlink <= 1) await unlink(stored).catch(() => undefined);
    return true;
  });
}

export async function listUser(user: string): Promise<string[]> {
  const userDir = path.join(config.youtubeDir, userFolderName(user));
  return Object.keys(await readManifest(userDir));
}
