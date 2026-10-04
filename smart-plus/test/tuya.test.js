import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, createHmac } from 'node:crypto';
import { TuyaClient, canonicalPath, signRequest } from '../src/tuya.js';

test('canonicalPath sorts query keys and drops undefined values', () => {
  assert.equal(canonicalPath('/v1.0/token', { grant_type: 1 }), '/v1.0/token?grant_type=1');
  assert.equal(canonicalPath('/x', { size: 100, last_row_key: undefined, a: 'b' }), '/x?a=b&size=100');
  assert.equal(canonicalPath('/x'), '/x');
});

test('signRequest follows the Tuya HMAC-SHA256 scheme', async () => {
  const body = '{"commands":[{"code":"switch_led","value":true}]}';
  const sign = await signRequest({
    clientId: 'cid', secret: 'sec', accessToken: 'tok', t: '1700000000000',
    method: 'post', url: '/v1.0/iot-03/devices/abc/commands', body,
  });
  const contentHash = createHash('sha256').update(body).digest('hex');
  const stringToSign = `POST\n${contentHash}\n\n/v1.0/iot-03/devices/abc/commands`;
  const expected = createHmac('sha256', 'sec')
    .update(`cidtok1700000000000${stringToSign}`).digest('hex').toUpperCase();
  assert.equal(sign, expected);
});

function fakeTuya(handler) {
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ url, ...init });
    return { json: async () => handler(url, init, calls.length) };
  };
  return { calls, fetchImpl };
}

test('client fetches a token once, reuses it and signs business calls with it', async () => {
  const { calls, fetchImpl } = fakeTuya((url) =>
    url.includes('/token')
      ? { success: true, result: { access_token: 'T1', expire_time: 7200 } }
      : { success: true, result: [{ code: 'switch_led', value: true }] });
  const client = new TuyaClient({ endpoint: 'https://example.test/', clientId: 'cid', secret: 's', fetchImpl, now: () => 1000 });
  await client.status('dev1');
  await client.status('dev1');
  assert.equal(calls.length, 3);
  assert.equal(calls[0].url, 'https://example.test/v1.0/token?grant_type=1');
  assert.equal(calls[0].headers.access_token, undefined);
  assert.equal(calls[1].headers.access_token, 'T1');
  assert.equal(calls[1].headers.sign_method, 'HMAC-SHA256');
  assert.match(calls[1].headers.sign, /^[0-9A-F]{64}$/);
});

test('client renews an invalid token and retries once', async () => {
  let tokens = 0;
  const { calls, fetchImpl } = fakeTuya((url, init) => {
    if (url.includes('/token')) return { success: true, result: { access_token: `T${++tokens}`, expire_time: 7200 } };
    if (init.headers.access_token === 'T1') return { success: false, code: 1010, msg: 'token invalid' };
    return { success: true, result: true };
  });
  const client = new TuyaClient({ endpoint: 'https://e.test', clientId: 'c', secret: 's', fetchImpl });
  assert.equal(await client.sendCommands('d', [{ code: 'switch_led', value: false }]), true);
  assert.equal(tokens, 2);
  assert.equal(calls.at(-1).body, '{"commands":[{"code":"switch_led","value":false}]}');
});

test('listDevices follows pagination', async () => {
  const { fetchImpl } = fakeTuya((url) => {
    if (url.includes('/token')) return { success: true, result: { access_token: 'T', expire_time: 7200 } };
    if (!url.includes('last_row_key')) return { success: true, result: { devices: [{ id: 'a' }], has_more: true, last_row_key: 'k1' } };
    return { success: true, result: { devices: [{ id: 'b' }], has_more: false } };
  });
  const client = new TuyaClient({ endpoint: 'https://e.test', clientId: 'c', secret: 's', fetchImpl });
  assert.deepEqual((await client.listDevices()).map((d) => d.id), ['a', 'b']);
});

test('other Tuya errors are thrown', async () => {
  const { fetchImpl } = fakeTuya((url) =>
    url.includes('/token')
      ? { success: true, result: { access_token: 'T', expire_time: 7200 } }
      : { success: false, code: 2008, msg: 'command or value not support' });
  const client = new TuyaClient({ endpoint: 'https://e.test', clientId: 'c', secret: 's', fetchImpl });
  await assert.rejects(client.status('d'), { code: 2008 });
});
