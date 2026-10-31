// The calls the windows (Windows app, Android app) make to the hub: argument checks and results.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { callHub } from '../core/api.js';

function fakeHub() {
  const calls = [];
  const light = { id: '7', name: 'Flur', on: false };
  return {
    calls,
    lights: () => [light],
    light: (id) => (id === '7' ? light : undefined),
    async update(id, change) { calls.push(['update', id, change]); return light; },
    async add(options) { calls.push(['add', options]); return { added: ['7'], present: 0, failed: [] }; },
    async rename(id, name) { calls.push(['rename', id, name]); },
    async share(ids) { calls.push(['share', ids]); return { code: 'MT:X' }; },
    async remove(id) { calls.push(['remove', id]); return { localOnly: true }; },
    async discover() { return [{ name: 'A', shortDiscriminator: 15, discriminator: 3840 }, { name: 'B', shortDiscriminator: 1, discriminator: 300 }]; },
    wake() { calls.push(['wake']); },
    refresh() { calls.push(['refresh']); },
  };
}

test('calls reach the hub with checked arguments', async () => {
  const hub = fakeHub();
  assert.deepEqual(await callHub(hub, 'lights'), [hub.light('7')]);
  assert.deepEqual(await callHub(hub, 'update', [7, { on: true }]), { light: hub.light('7') });
  const added = await callHub(hub, 'add', [{ code: '34970112332', ip: '', wifi: { ssid: '' } }]);
  assert.deepEqual(added, { added: ['7'], present: 0, failed: [], lights: [hub.light('7')] });
  await callHub(hub, 'rename', ['7', 'Küche']);
  assert.deepEqual(await callHub(hub, 'remove', ['7']), { ok: true, localOnly: true });
  await callHub(hub, 'share', []);
  await callHub(hub, 'share', ['7']);
  await callHub(hub, 'share', [['7', 8]]);
  await callHub(hub, 'wake');
  await callHub(hub, 'refresh');
  assert.deepEqual(hub.calls, [
    ['update', '7', { on: true }],
    ['add', { code: '34970112332', ip: undefined, wifi: undefined }],
    ['rename', '7', 'Küche'],
    ['remove', '7'],
    ['share', undefined], ['share', ['7']], ['share', ['7', '8']],
    ['wake'], ['refresh'],
  ]);
});

test('bad calls are refused with a status', async () => {
  const hub = fakeHub();
  await assert.rejects(callHub(hub, 'update', [{}, { on: true }]), (err) => err.status === 400);
  await assert.rejects(callHub(hub, 'gibtsnicht'), (err) => err.status === 400 && /Unbekannte Funktion/.test(err.message));
});

test('network search marks the lamps a code belongs to', async () => {
  const hub = fakeHub();
  const devices = await callHub(hub, 'discover', ['34970112332']); // short discriminator 15
  assert.deepEqual(devices.map((d) => d.matchesCode), [true, false]);
  assert.deepEqual((await callHub(hub, 'discover', [])).map((d) => d.matchesCode), [undefined, undefined]);
  assert.deepEqual((await callHub(hub, 'discover', ['quatsch'])).map((d) => d.matchesCode), [undefined, undefined]);
});
