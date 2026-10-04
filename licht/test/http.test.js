import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { createHttpServer } from '../server/http.js';

const light = { id: '7', name: 'Flur', online: true, on: false, mode: 'white', brightness: 50, temperature: 50, color: { h: 0, s: 100 }, supports: {} };
const calls = [];
const listeners = new Set();
const hub = {
  lights: () => [light],
  light: (id) => (id === '7' ? light : undefined),
  async update(id, change) {
    calls.push(['update', id, change]);
    if (id !== '7') throw Object.assign(new Error('Lampe nicht gefunden.'), { status: 404 });
    return { ...light, ...change };
  },
  async commission(opts) { calls.push(['commission', opts]); return '7'; },
  async rename(id, name) { calls.push(['rename', id, name]); },
  async share(id) { calls.push(['share', id]); return { code: '12345678901', validMinutes: 15 }; },
  async remove(id) { calls.push(['remove', id]); },
  subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
};

let server;
let base;
before(async () => {
  server = createHttpServer(hub, { webDir: fileURLToPath(new URL('../web/', import.meta.url)), log: {} });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  base = `http://127.0.0.1:${server.address().port}`;
});
after(() => server.close());

const req = (path, method = 'GET', body) =>
  fetch(base + path, { method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body && JSON.stringify(body) });

test('lists lights', async () => {
  assert.deepEqual(await (await req('/api/lights')).json(), { lights: [light] });
});

test('changes a light', async () => {
  const res = await req('/api/lights/7', 'POST', { on: true });
  assert.equal(res.status, 200);
  assert.equal((await res.json()).light.on, true);
  assert.deepEqual(calls.at(-1), ['update', '7', { on: true }]);
});

test('hub errors keep their status and message', async () => {
  const res = await req('/api/lights/8', 'POST', { on: true });
  assert.equal(res.status, 404);
  assert.deepEqual(await res.json(), { error: 'Lampe nicht gefunden.' });
});

test('adds, renames, shares and removes', async () => {
  assert.equal((await req('/api/lights', 'POST', { code: '3497-011-2332', ip: '', wifi: { ssid: '' } })).status, 201);
  assert.deepEqual(calls.at(-1), ['commission', { code: '3497-011-2332', ip: undefined, wifi: undefined }]);
  await req('/api/lights/7/name', 'POST', { name: 'Küche' });
  assert.deepEqual(calls.at(-1), ['rename', '7', 'Küche']);
  assert.deepEqual(await (await req('/api/lights/7/share', 'POST')).json(), { code: '12345678901', validMinutes: 15 });
  await req('/api/lights/7', 'DELETE');
  assert.deepEqual(calls.at(-1), ['remove', '7']);
});

test('rejects broken JSON', async () => {
  const res = await fetch(`${base}/api/lights/7`, { method: 'POST', body: '{nope' });
  assert.equal(res.status, 400);
});

test('serves the web app and blocks path traversal', async () => {
  const res = await req('/');
  assert.match(res.headers.get('content-type'), /text\/html/);
  assert.match(await res.text(), /<title>Licht<\/title>/);
  assert.equal((await req('/app.js')).headers.get('content-type'), 'text/javascript; charset=utf-8');
  assert.notEqual((await req('/..%2fpackage.json')).status, 200);
  assert.equal((await req('/nope.txt')).status, 404);
});

test('streams live updates', async () => {
  const controller = new AbortController();
  const res = await fetch(`${base}/api/events`, { signal: controller.signal });
  const reader = res.body.getReader();
  const first = new TextDecoder().decode((await reader.read()).value);
  assert.match(first, /"type":"lights"/);
  for (const fn of listeners) fn({ type: 'light', light: { ...light, on: true } });
  const second = new TextDecoder().decode((await reader.read()).value);
  assert.match(second, /"type":"light".*"on":true/);
  controller.abort();
});
