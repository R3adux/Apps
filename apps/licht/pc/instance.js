// Only one Licht at a time. The first one listens on a named pipe (Windows) or socket (elsewhere);
// a second start finds it there, asks it to show its window and ends. Nothing listens on the network.
//
// Older Licht versions (before October 2026) ran a web server on port 8080 instead. A new Licht asks
// such a one to quit (its /api/quit, from this computer only) before starting – otherwise both would
// use the same lamp data.

import { createHash } from 'node:crypto';
import { rmSync } from 'node:fs';
import { createConnection, createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createInterface } from 'node:readline';

const LEGACY_PORT = 8080;

/** Pipe / socket name for a data folder: one Licht per user (and per data folder, e.g. in tests). */
export function instancePath(dataDir) {
  const id = createHash('sha256').update(dataDir.toLowerCase()).digest('hex').slice(0, 16);
  return process.platform === 'win32' ? `\\\\.\\pipe\\licht-${id}` : join(tmpdir(), `licht-${id}.sock`);
}

function connect(path) {
  return new Promise((resolve) => {
    const socket = createConnection(path);
    socket.once('connect', () => resolve(socket));
    socket.once('error', () => resolve(undefined));
  });
}

/**
 * Becomes the one running Licht, or finds the one already running.
 * Returns { first: true, onMessage(handler), close() } or { first: false, send(message) }.
 * Messages are JSON objects, one per line, e.g. { t: 'show' }.
 */
export async function claimInstance(dataDir) {
  const path = instancePath(dataDir);
  for (let attempt = 0; attempt < 3; attempt++) {
    const socket = await connect(path);
    if (socket) {
      return {
        first: false,
        send: (message) => new Promise((resolve) => socket.end(`${JSON.stringify(message)}\n`, resolve)),
      };
    }
    // Nobody there. A socket file left behind by a crashed Licht (not on Windows) is removed first.
    if (process.platform !== 'win32') rmSync(path, { force: true });
    const handlers = new Set();
    const server = createServer((client) => {
      client.on('error', () => {});
      createInterface({ input: client }).on('line', (line) => {
        let message;
        try {
          message = JSON.parse(line);
        } catch {
          return;
        }
        for (const handler of handlers) handler(message);
      });
    });
    const listening = await new Promise((resolve) => {
      server.once('error', (err) => resolve(err));
      server.listen(path, () => resolve(true));
    });
    if (listening === true) {
      server.unref(); // does not keep Licht running by itself
      return {
        first: true,
        onMessage: (handler) => handlers.add(handler),
        close: () => new Promise((resolve) => server.close(() => resolve())),
      };
    }
    if (listening.code !== 'EADDRINUSE') throw listening;
    // Another Licht started at the same moment: try to reach it.
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error('Licht läuft schon, ist aber nicht erreichbar.');
}

async function fetchJson(url, options = {}, timeout = 1000) {
  try {
    const res = await fetch(url, { ...options, signal: AbortSignal.timeout(timeout) });
    return { status: res.status, body: await res.json().catch(() => undefined) };
  } catch {
    return undefined;
  }
}

/**
 * An older Licht version (with web server) running on this computer is asked to quit.
 * Returns 'none' (none running), 'stopped', or 'stuck' (it could not be stopped: a very old version
 * without "quit" – the user has to close it). Another program on that port is left alone.
 */
export async function stopOlderVersion({ port = LEGACY_PORT, wait = 15_000 } = {}) {
  const base = `http://127.0.0.1:${port}`;
  const info = await fetchJson(`${base}/api/info`);
  if (!info || info.status !== 200 || typeof info.body?.ble !== 'boolean') return 'none';
  console.log('Eine ältere Licht-Version läuft noch – sie wird beendet');
  const quit = await fetchJson(`${base}/api/quit`, { method: 'POST' }, 3000);
  if (quit?.status !== 200) return 'stuck';
  const end = Date.now() + wait;
  while (Date.now() < end) {
    await new Promise((r) => setTimeout(r, 300));
    if (!(await fetchJson(`${base}/api/info`, {}, 500))) return 'stopped';
  }
  return 'stuck';
}
