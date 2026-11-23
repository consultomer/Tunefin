import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { config } from './config.js';

export class YtDlpError extends Error {
  constructor(
    message: string,
    readonly exitCode: number | null,
    readonly stderr: string,
  ) {
    super(message);
  }
}

/** Thrown when a request names a Jellyfin account that has no YouTube cookies stored. */
export class NoAccountError extends Error {}

/** Account keys are Jellyfin user ids in "N" format (32 hex chars). */
export function isValidAccount(account: string): boolean {
  return /^[0-9a-f]{32}$/.test(account);
}

/** True if the Jellyfin plugin has stored YouTube cookies for this account. */
export function hasAccount(account: string): boolean {
  return isValidAccount(account) && existsSync(path.join(config.userCookiesDir, `${account}.cookies.txt`));
}

/**
 * yt-dlp writes refreshed cookies back to the --cookies file. Work on a private copy per account so
 * the source (owned by the Jellyfin plugin, mounted read-only) is never modified. The copy is
 * refreshed whenever the user uploads new cookies (source mtime changes).
 */
const working = new Map<string, { mtimeMs: number; file: string }>();
function cookiesPath(account?: string): string | undefined {
  let src: string | undefined;
  if (account) {
    if (!isValidAccount(account)) throw new NoAccountError('invalid account');
    src = path.join(config.userCookiesDir, `${account}.cookies.txt`);
  } else {
    src = config.cookiesFile;
  }
  if (!src) return undefined;

  let mtimeMs: number;
  try {
    mtimeMs = statSync(src).mtimeMs;
  } catch {
    if (account) throw new NoAccountError('this user has not connected a YouTube account');
    return undefined;
  }

  const key = account ?? '';
  const cached = working.get(key);
  if (cached && cached.mtimeMs === mtimeMs) return cached.file;
  // Private dir (not sticky /tmp, where protected_regular blocks rewriting foreign-owned files),
  // and read+write instead of copy so the copy is owned by this process.
  const file = cached?.file ?? path.join(mkdtempSync(path.join(os.tmpdir(), 'ytm-cookies-')), 'cookies.txt');
  writeFileSync(file, readFileSync(src), { mode: 0o600 });
  working.set(key, { mtimeMs, file });
  return file;
}

/** Arguments shared by every yt-dlp invocation. */
export function baseArgs(account?: string): string[] {
  const args = ['--ignore-config', '--no-warnings', '--no-playlist', '--no-progress', '--js-runtimes', config.ytDlpJsRuntime];
  const cookies = cookiesPath(account);
  if (cookies) args.push('--cookies', cookies);
  return args;
}

/**
 * Run yt-dlp with an argv array (never a shell string, so user-provided
 * metadata can't inject commands). Resolves with stdout.
 */
export function runYtDlp(args: string[], timeoutMs: number, account?: string): Promise<string> {
  let fullArgs: string[];
  try {
    fullArgs = [...baseArgs(account), ...args];
  } catch (err) {
    return Promise.reject(err);
  }
  return new Promise((resolve, reject) => {
    const child = spawn(config.ytDlpPath, fullArgs, { stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8').on('data', (d: string) => (stdout += d));
    child.stderr.setEncoding('utf8').on('data', (d: string) => {
      stderr += d;
      if (stderr.length > 64_000) stderr = stderr.slice(-32_000);
    });

    const timer = setTimeout(() => {
      child.kill('SIGKILL');
      reject(new YtDlpError(`yt-dlp timed out after ${timeoutMs} ms`, null, stderr));
    }, timeoutMs);

    child.on('error', (err) => {
      clearTimeout(timer);
      reject(new YtDlpError(`failed to start yt-dlp: ${err.message}`, null, ''));
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code === 0) resolve(stdout);
      else {
        const lastError = stderr.split('\n').filter((l) => l.includes('ERROR')).pop() ?? stderr.trim().split('\n').pop() ?? '';
        reject(new YtDlpError(`yt-dlp exited with code ${code}: ${lastError}`, code, stderr));
      }
    });
  });
}

/** YouTube video IDs are exactly 11 chars of [A-Za-z0-9_-]. */
export function isValidVideoId(id: string): boolean {
  return /^[A-Za-z0-9_-]{11}$/.test(id);
}

export function watchUrl(videoId: string): string {
  return `https://music.youtube.com/watch?v=${videoId}`;
}
