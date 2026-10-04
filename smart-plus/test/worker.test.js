import { test, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import worker, { resetState } from '../src/worker.js';

const env = {
  APP_PASSWORD: 'geheim',
  TUYA_CLIENT_ID: 'cid',
  TUYA_CLIENT_SECRET: 'secret',
  TUYA_ENDPOINT: 'https://tuya.test',
  ASSETS: { fetch: async () => new Response('asset') },
};

let sent;
const realFetch = globalThis.fetch;

beforeEach(() => {
  resetState();
  sent = [];
  globalThis.fetch = async (url, init) => {
    const ok = (result) => new Response(JSON.stringify({ success: true, result }));
    if (url.includes('/v1.0/token')) return ok({ access_token: 'T', expire_time: 7200 });
    if (url.includes('/associated-users/devices')) {
      return ok({
        has_more: false,
        devices: [
          { id: 'b', name: 'Wohnzimmer', category: 'dj', online: true, status: [{ code: 'switch_led', value: true }, { code: 'bright_value_v2', value: 500 }] },
          { id: 'a', name: 'Bad', category: 'dj', online: true, status: [{ code: 'switch_led', value: false }] },
          { id: 'c', name: 'Rauchmelder', category: 'ywbj', online: true, status: [{ code: 'smoke_sensor_status', value: 'normal' }] },
        ],
      });
    }
    if (url.includes('/specification')) return new Response(JSON.stringify({ success: false, code: 1106, msg: 'permission deny' }));
    if (url.endsWith('/status')) return ok([{ code: 'switch_led', value: true }, { code: 'bright_value_v2', value: 10 }]);
    if (url.endsWith('/commands')) {
      sent.push(JSON.parse(init.body));
      return ok(true);
    }
    throw new Error(`unexpected ${url}`);
  };
});

afterEach(() => {
  globalThis.fetch = realFetch;
});

const call = (path, { method = 'GET', password = 'geheim', body } = {}) =>
  worker.fetch(new Request(`https://licht.test${path}`, {
    method,
    headers: password ? { Authorization: `Bearer ${password}` } : {},
    body: body && JSON.stringify(body),
  }), env);

test('rejects requests without the right password', async () => {
  assert.equal((await call('/api/lights', { password: 'falsch' })).status, 401);
  assert.equal((await call('/api/lights', { password: null })).status, 401);
});

test('refuses to run without APP_PASSWORD', async () => {
  const res = await worker.fetch(new Request('https://licht.test/api/lights'), { ...env, APP_PASSWORD: '' });
  assert.equal(res.status, 500);
});

test('lists only lights, sorted by name', async () => {
  const { lights } = await (await call('/api/lights')).json();
  assert.deepEqual(lights.map((l) => [l.id, l.on, l.brightness]), [['a', false, 100], ['b', true, 50]]);
});

test('sends commands for a light', async () => {
  const res = await call('/api/lights/b', { method: 'POST', body: { on: true, mode: 'white', brightness: 100 } });
  assert.equal(res.status, 200);
  assert.deepEqual(sent, [{ commands: [
    { code: 'switch_led', value: true },
    { code: 'bright_value_v2', value: 1000 },
  ] }]);
});

test('serves the web app for other paths', async () => {
  assert.equal(await (await call('/')).text(), 'asset');
});
