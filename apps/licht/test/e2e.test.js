// End to end: a simulated Matter lamp, two Licht hubs (like the PC and the phone),
// commissioning, control, sharing and removing – all over the real Matter protocol.
import './support/no-ipv6.cjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Environment, Logger } from '@matter/main';
import { LightHub } from '../core/hub.js';
import { startVirtualLight, waitFor } from './support/harness.js';

Logger.level = 'error';

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

    // Share with the phone hub (Matter multi-admin): one lamp → QR code text plus the 11 digits.
    const shared = await pc.share([id]);
    assert.match(shared.manual, /^\d{11}$/);
    assert.match(shared.code, /^MT:[0-9A-Z.-]+$/);
    assert.deepEqual([shared.lamps, shared.skipped, shared.validMinutes], [['Wohnzimmer'], [], 15]);
    const phone = new LightHub({ id: 'phone', label: 'Licht Handy' });
    hubs.push(phone);
    await phone.start();
    const phoneId = await phone.commission({ code: shared.manual, ip: address });
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

// One QR code for all lamps: a second Licht (e.g. another phone) adds them all without setting them up.
test('all lamps shared with one code are added by another Licht in one go', { timeout: 240_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-shareall-'));
  const ports = [5300, 5400].map((base) => base + Math.floor(Math.random() * 90));
  const lamps = ports.map((port, i) => startVirtualLight(join(dir, `lamp${i}`), port));
  const hubs = [];
  try {
    const codes = await Promise.all(lamps.map((l) => l.ready));
    Environment.default.vars.set('storage.path', join(dir, 'hubs'));
    const pc = new LightHub({ id: 'pc', label: 'Licht PC' });
    hubs.push(pc);
    await pc.start();
    const ids = [];
    for (const [i, port] of ports.entries()) ids.push(await pc.commission({ code: codes[i], ip: `127.0.0.1:${port}` }));
    await pc.rename(ids[0], 'Flur');
    await pc.rename(ids[1], 'Küche');

    const shared = await pc.share();
    assert.equal(shared.manual, undefined);
    assert.deepEqual([...shared.lamps].sort(), ['Flur', 'Küche']);
    assert.equal(shared.code.split('*').length, 2);

    const phone = new LightHub({ id: 'phone2', label: 'Licht Handy 2' });
    hubs.push(phone);
    await phone.start();
    const progress = [];
    phone.subscribe((e) => e.type === 'adding' && progress.push(e.done ? 'done' : `${e.index + 1}/${e.total}`));
    const t = Date.now();
    const result = await phone.add({ code: shared.code });
    console.log(`TIMING share all: 2 lamps added in ${Date.now() - t} ms`);
    assert.equal(result.added.length, 2);
    assert.deepEqual([result.present, result.failed], [0, []]);
    assert.deepEqual(progress, ['1/2', '2/2', 'done']);
    assert.deepEqual(phone.lights().map((l) => l.name), ['Flur', 'Küche']); // names come from the lamps
    for (const light of phone.lights()) {
      await phone.update(light.id, { on: true });
      await phone.update(light.id, { on: false });
    }
    await waitFor(() => lamps.every((l) => l.state.onOff === false), 'both lamps switched by the second Licht');

    // The same code again: the lamps are recognised as already there (no second setup).
    const again = await phone.add({ code: (await pc.share()).code });
    assert.deepEqual([again.added, again.present], [[], 2]);
    assert.equal(phone.lights().length, 2);
  } finally {
    for (const hub of hubs) await hub.close().catch(() => {});
    for (const l of lamps) l.child.kill('SIGKILL');
    rmSync(dir, { recursive: true, force: true });
  }
});
