import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { request as httpRequest } from 'node:http';
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
  wake() { calls.push(['wake']); },
  async discover() { return [{ name: 'Lampe', discriminator: 3840, shortDiscriminator: 15, mode: 1, addresses: ['10.0.0.5:5540'] }]; },
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

test('network search marks devices matching the entered code', async () => {
  const match = await (await req('/api/discover?code=3497-011-2332')).json();
  assert.equal(match.devices[0].matchesCode, true);
  const other = await (await req('/api/discover?code=' + encodeURIComponent('MT:Y.K90AFN00KA0648G00'))).json();
  assert.equal(other.devices[0].matchesCode, true);
  const none = await (await req('/api/discover')).json();
  assert.equal(none.devices[0].matchesCode, undefined);
});

test('wake asks the hub to reconnect', async () => {
  assert.equal((await req('/api/wake', 'POST')).status, 200);
  assert.deepEqual(calls.at(-1), ['wake']);
});

test('rejects API calls from other web sites', async () => {
  const res = await fetch(`${base}/api/lights/7`, {
    method: 'POST', headers: { Origin: 'https://evil.example', 'Content-Type': 'text/plain' }, body: '{"on":true}',
  });
  assert.equal(res.status, 403);
  const own = await fetch(`${base}/api/lights`, { headers: { Origin: base } });
  assert.equal(own.status, 200);
});

test('PC functions (quit, autostart) only for this computer', async () => {
  let quit = 0;
  let autostart = false;
  const desktop = { quit: () => { quit++; }, autostart: { get: async () => autostart, set: async (on) => (autostart = on) } };
  const pc = createHttpServer(hub, { webDir: fileURLToPath(new URL('../web/', import.meta.url)), desktop, log: {} });
  await new Promise((r) => pc.listen(0, '127.0.0.1', r));
  const url = `http://127.0.0.1:${pc.address().port}`;
  try {
    assert.deepEqual((await (await fetch(`${url}/api/info`)).json()).desktop, { autostart: false });
    const set = await fetch(`${url}/api/autostart`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"enabled":true}' });
    assert.deepEqual(await set.json(), { autostart: true });
    assert.equal((await fetch(`${url}/api/quit`, { method: 'POST' })).status, 200);
    await new Promise((r) => setTimeout(r, 200));
    assert.equal(quit, 1);
    // A web page under another name that points to this computer gets nothing.
    const status = await new Promise((resolve, reject) => {
      httpRequest(`${url}/api/quit`, { method: 'POST', headers: { Host: `rebind.example:${pc.address().port}` } }, (res) => {
        res.resume();
        resolve(res.statusCode);
      }).on('error', reject).end();
    });
    assert.equal(status, 404);
    assert.equal(quit, 1);
  } finally {
    pc.close();
  }
  // The normal server (no desktop) has none of this.
  assert.equal((await (await req('/api/info')).json()).desktop, undefined);
  assert.equal((await req('/api/quit', 'POST')).status, 404);
});
