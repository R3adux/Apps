// HTTP API + web app for the hub. Meant for the home network only.
//
//   GET    /api/lights               all lamps
//   POST   /api/lights               add a lamp      { code, ip?, wifi?: { ssid, password } }
//   POST   /api/lights/:id           change a lamp   { on?, mode?, brightness?, temperature?, color? }
//   POST   /api/lights/:id/name      rename          { name }
//   POST   /api/lights/:id/share     setup code for another app (e.g. the phone)
//   DELETE /api/lights/:id           remove
//   GET    /api/events               live updates (Server-Sent Events)
//   GET    /api/discover             Matter devices ready to be added right now
//   GET    /api/info                 { ble }

import { createServer } from 'node:http';
import { parsePairingCode } from '../core/pairing.js';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize, resolve, sep } from 'node:path';

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.webmanifest': 'application/manifest+json',
  '.json': 'application/json',
};
const MAX_BODY = 16 * 1024;

const send = (res, status, data) => {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(data));
};

async function readJson(req) {
  let size = 0;
  const chunks = [];
  for await (const chunk of req) {
    size += chunk.length;
    if (size > MAX_BODY) throw Object.assign(new Error('Anfrage zu groß.'), { status: 413 });
    chunks.push(chunk);
  }
  if (!chunks.length) return {};
  try {
    const value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (value && typeof value === 'object') return value;
  } catch {
    // fall through
  }
  throw Object.assign(new Error('Ungültige Anfrage.'), { status: 400 });
}

function events(hub, req, res) {
  res.writeHead(200, {
    'Content-Type': 'text/event-stream; charset=utf-8',
    'Cache-Control': 'no-store',
    Connection: 'keep-alive',
  });
  const write = (event) => res.write(`data: ${JSON.stringify(event)}\n\n`);
  write({ type: 'lights', lights: hub.lights() });
  const unsubscribe = hub.subscribe((event) =>
    write(event.type === 'lights' ? { type: 'lights', lights: hub.lights() } : event));
  const keepAlive = setInterval(() => res.write(': ping\n\n'), 25_000);
  req.on('close', () => {
    clearInterval(keepAlive);
    unsubscribe();
  });
}

// Lists lamps ready to be added; with ?code=… marks the ones that code belongs to.
async function discover(hub, req) {
  const code = new URL(req.url, 'http://localhost').searchParams.get('code');
  let ids;
  try {
    ids = code ? parsePairingCode(code).identifierData : undefined;
  } catch {
    ids = undefined;
  }
  return (await hub.discover()).map((device) => ({
    ...device,
    matchesCode: ids
      ? ids.shortDiscriminator === device.shortDiscriminator || ids.longDiscriminator === device.discriminator
      : undefined,
  }));
}

async function api(hub, req, res, path, options) {
  const { method } = req;
  if (path === '/api/info' && method === 'GET') return send(res, 200, { ble: Boolean(options.ble) });
  if (path === '/api/events' && method === 'GET') return events(hub, req, res);
  if (path === '/api/lights' && method === 'GET') return send(res, 200, { lights: hub.lights() });
  if (path === '/api/discover' && method === 'GET') return send(res, 200, { devices: await discover(hub, req) });
  if (path === '/api/lights' && method === 'POST') {
    const { code, ip, wifi } = await readJson(req);
    const id = await hub.commission({ code, ip: ip || undefined, wifi: wifi?.ssid ? wifi : undefined });
    return send(res, 201, { light: hub.light(id) });
  }

  const match = path.match(/^\/api\/lights\/(\d+)(?:\/(name|share))?$/);
  if (!match) return send(res, 404, { error: 'Nicht gefunden.' });
  const [, id, action] = match;
  if (!action && method === 'POST') return send(res, 200, { light: await hub.update(id, await readJson(req)) });
  if (!action && method === 'DELETE') {
    await hub.remove(id);
    return send(res, 200, { ok: true });
  }
  if (action === 'name' && method === 'POST') {
    await hub.rename(id, (await readJson(req)).name);
    return send(res, 200, { light: hub.light(id) });
  }
  if (action === 'share' && method === 'POST') return send(res, 200, await hub.share(id));
  return send(res, 405, { error: 'Methode nicht erlaubt.' });
}

// Serves the web app from a folder, or from memory (files embedded into Licht.exe).
async function staticFile(res, web, path) {
  const name = path === '/' ? '/index.html' : decodeURIComponent(path);
  let body;
  if (web.files) {
    body = web.files[name];
  } else {
    const file = normalize(join(web.dir, name));
    if (!file.startsWith(web.dir + sep)) return send(res, 403, { error: 'Verboten.' });
    body = await readFile(file).catch(() => undefined);
  }
  if (body === undefined) return send(res, 404, { error: 'Nicht gefunden.' });
  res.writeHead(200, {
    'Content-Type': TYPES[extname(name)] ?? 'application/octet-stream',
    'Cache-Control': 'no-cache',
  });
  res.end(body);
}

/** webDir: folder with the web app, or webFiles: { '/index.html': Buffer, … } */
export function createHttpServer(hub, { webDir, webFiles, ble = false, log = console } = {}) {
  const web = webFiles ? { files: webFiles } : { dir: resolve(webDir) };
  return createServer(async (req, res) => {
    const { pathname } = new URL(req.url, 'http://localhost');
    try {
      if (pathname.startsWith('/api/')) await api(hub, req, res, pathname, { ble });
      else if (req.method === 'GET' || req.method === 'HEAD') await staticFile(res, web, pathname);
      else send(res, 405, { error: 'Methode nicht erlaubt.' });
    } catch (err) {
      const status = err.status ?? 500;
      if (status >= 500) log.error?.(`${req.method} ${pathname}:`, err);
      if (!res.headersSent) send(res, status, { error: err.message || 'Interner Fehler.' });
      else res.end();
    }
  });
}
