// The phone app in the background: Licht sleeps (no subscriptions, so the lamps do not wake the phone),
// a command still switches at once, and coming back connects everything again.
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

test('in the background Licht sleeps, a command still switches at once, back in front all is connected', { timeout: 120_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-sleep-'));
  const port = 5300 + Math.floor(Math.random() * 90);
  const lamp = startVirtualLight(dir, port);
  const hub = new LightHub({ id: 'phone-sleep', label: 'Licht Handy' });
  try {
    const code = await lamp.ready;
    Environment.default.vars.set('storage.path', join(dir, 'hub'));
    await hub.start();
    const id = await hub.commission({ code, ip: `127.0.0.1:${port}` });
    await waitFor(() => hub.light(id).status === 'online', 'connected');

    // A quick switch to another app: nothing changes.
    hub.pause();
    assert.equal(hub.paused, true);
    hub.resume();
    assert.deepEqual([hub.paused, hub.asleep], [false, false]);

    // Longer in the background: asleep, the lamp is no longer subscribed.
    hub.pause({ sleepAfter: 0 });
    await waitFor(() => hub.asleep && hub.light(id).status !== 'online', 'asleep');

    // A command while asleep (like a tap on the widget): switches at once, without subscribing again.
    const started = Date.now();
    await hub.update(id, { on: true });
    const ms = Date.now() - started;
    await waitFor(() => lamp.state.onOff === true, 'lamp on');
    assert.ok(ms < 2000, `command took ${ms} ms`);
    await new Promise((r) => setTimeout(r, 1500));
    assert.notEqual(hub.light(id).status, 'online', 'no subscription while asleep');

    // The measurement shown in the app: time to reach each lamp from sleep, with no connection left.
    const [result] = await hub.measureFromSleep();
    assert.equal(typeof result.ms, 'number', JSON.stringify(result));
    console.log(`TIMING from sleep (no connection left): ${result.ms} ms; command while asleep: ${ms} ms`);

    // Back in front: connected again, live updates flow.
    hub.resume();
    await waitFor(() => hub.light(id).status === 'online', 'connected again');
    await hub.update(id, { on: false });
    await waitFor(() => hub.light(id).on === false && lamp.state.onOff === false, 'off');
  } finally {
    await hub.close().catch(() => {});
    lamp.child.kill('SIGKILL');
    rmSync(dir, { recursive: true, force: true });
  }
});
