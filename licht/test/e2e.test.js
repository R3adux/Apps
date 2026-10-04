// End to end: a simulated Matter lamp, two Licht hubs (like the PC and the phone),
// commissioning, control, sharing and removing – all over the real Matter protocol.
import './support/no-ipv6.cjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { Environment, Logger } from '@matter/main';
import { LightHub } from '../core/hub.js';

Logger.level = 'error';

async function waitFor(check, what, timeout = 15_000) {
  const end = Date.now() + timeout;
  for (;;) {
    const result = check();
    if (result) return result;
    if (Date.now() > end) throw new Error(`Timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 100));
  }
}

function startVirtualLight(dir, port) {
  const child = spawn(process.execPath, [new URL('./support/virtual-light.mjs', import.meta.url).pathname], {
    env: { ...process.env, STORE: join(dir, 'lamp'), PORT: String(port) },
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  const lamp = { child, state: {}, ready: null };
  lamp.ready = new Promise((resolve, reject) => {
    child.on('exit', (code) => reject(new Error(`virtual light exited with ${code}`)));
    createInterface({ input: child.stdout }).on('line', (line) => {
      const msg = JSON.parse(line);
      if (msg.ready) resolve(msg.code);
      if (msg.attr) lamp.state[msg.attr] = msg.value;
    });
  });
  return lamp;
}

test('commission, control, share and remove a lamp', { timeout: 180_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-e2e-'));
  const port = 5600 + Math.floor(Math.random() * 300);
  const lamp = startVirtualLight(dir, port);
  const hubs = [];
  try {
    const code = await lamp.ready;
    const address = `127.0.0.1:${port}`;
    Environment.default.vars.set('storage.path', join(dir, 'hubs'));

    // PC hub adds the lamp with the code printed on it.
    const pc = new LightHub({ id: 'pc', label: 'Licht PC' });
    hubs.push(pc);
    await pc.start();
    assert.ok(Array.isArray(await pc.discover(1)));
    const id = await pc.commission({ code, ip: address });
    assert.equal(pc.lights().length, 1);
    assert.equal(pc.light(id).name, 'Virtuelle A60');
    assert.equal(pc.light(id).online, true);
    assert.deepEqual(pc.light(id).supports, { brightness: true, temperature: true, color: true });

    // Colour
    await pc.update(id, { on: true, mode: 'colour', brightness: 50, color: { h: 240, s: 100 } });
    await waitFor(() => lamp.state.onOff === true && lamp.state.colorMode === 0 && lamp.state.currentHue === 169, 'colour on lamp');
    assert.equal(lamp.state.currentLevel, 126); // 50 %
    assert.equal(pc.light(id).mode, 'colour');

    // White
    await pc.update(id, { mode: 'white', brightness: 100, temperature: 100 });
    await waitFor(() => lamp.state.colorMode === 2 && lamp.state.colorTemperatureMireds === 153, 'cold white on lamp');

    // The name is stored on the lamp itself.
    await pc.rename(id, 'Wohnzimmer');
    assert.equal(pc.light(id).name, 'Wohnzimmer');

    // Share with the phone hub (Matter multi-admin).
    const shared = await pc.share(id);
    assert.match(shared.code, /^\d{11}$/);
    const phone = new LightHub({ id: 'phone', label: 'Licht Handy' });
    hubs.push(phone);
    await phone.start();
    const phoneId = await phone.commission({ code: shared.code, ip: address });
    assert.equal(phone.light(phoneId).name, 'Wohnzimmer');

    // Phone switches off → lamp off → PC sees it via its subscription.
    await phone.update(phoneId, { on: false });
    await waitFor(() => lamp.state.onOff === false, 'lamp off');
    await waitFor(() => pc.light(id).on === false, 'PC sees lamp off');

    // PC removes itself; the phone keeps working.
    await pc.remove(id);
    assert.equal(pc.lights().length, 0);
    await phone.update(phoneId, { on: true });
    await waitFor(() => lamp.state.onOff === true, 'lamp on again via phone');

    // A wrong code is rejected with a readable message.
    await assert.rejects(pc.commission({ code: '1234' }), /11 oder 21 Ziffern/);
  } finally {
    for (const hub of hubs) await hub.close().catch(() => {});
    lamp.child.kill();
    rmSync(dir, { recursive: true, force: true });
  }
});
