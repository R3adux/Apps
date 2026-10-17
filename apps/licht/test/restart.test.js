// Startup and reconnection: what happens when Licht starts while a lamp is switched off,
// and when the lamp comes back. Licht must show the lamp right away (from stored data),
// answer quickly when the lamp is unreachable, and find it again by itself.
import './support/no-ipv6.cjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Environment, Logger } from '@matter/main';
import { LightHub } from '../core/hub.js';
import { cutPower, startVirtualLight, waitFor } from './support/harness.js';

Logger.level = 'error';

test('starts instantly with the lamp off and reconnects when it is back', { timeout: 180_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-restart-'));
  const port = 5300 + Math.floor(Math.random() * 200);
  let lamp = startVirtualLight(dir, port);
  const hubs = [];
  try {
    const code = await lamp.ready;
    Environment.default.vars.set('storage.path', join(dir, 'hub'));

    // Set up as usual.
    const first = new LightHub({ id: 'restart' });
    hubs.push(first);
    await first.start();
    const id = await first.commission({ code, ip: `127.0.0.1:${port}` });
    await first.update(id, { on: true, mode: 'colour', brightness: 50, color: { h: 120, s: 100 } });
    await waitFor(() => lamp.state.onOff === true && lamp.state.colorMode === 0, 'lamp on in colour');
    // Give the lamp's report time to arrive, so it is stored like during normal use.
    await new Promise((r) => setTimeout(r, 1500));
    await first.close();

    // The lamp is switched off (e.g. at the wall switch), then Licht starts again.
    await cutPower(lamp);
    const hub = new LightHub({ id: 'restart' });
    hubs.push(hub);
    let t = Date.now();
    await hub.start();
    const startMs = Date.now() - t;
    assert.ok(startMs < 3000, `start took ${startMs} ms`);

    // The lamp is shown immediately with its last known state, marked as not connected.
    const [shown] = hub.lights();
    assert.ok(shown, 'lamp listed right after start');
    assert.equal(shown.id, id);
    assert.equal(shown.name, 'Virtuelle A60');
    assert.equal(shown.on, true);
    assert.equal(shown.mode, 'colour');
    assert.equal(shown.online, false);
    assert.equal(shown.status, 'connecting');

    // A command to the switched-off lamp fails quickly instead of hanging.
    t = Date.now();
    await assert.rejects(hub.update(id, { on: false }), (err) => err.status === 503);
    const failMs = Date.now() - t;
    assert.ok(failMs < 10_000, `command failed after ${failMs} ms`);

    // The lamp comes back. Licht finds it again by itself, without the user doing anything.
    lamp = startVirtualLight(dir, port);
    await lamp.ready;
    t = Date.now();
    await waitFor(() => hub.light(id)?.online, 'lamp online again', 60_000);
    const backMs = Date.now() - t;
    assert.ok(backMs < 45_000, `reconnect took ${backMs} ms`);

    // And reacts immediately.
    t = Date.now();
    await hub.update(id, { on: false });
    await waitFor(() => lamp.state.onOff === false, 'lamp off');
    assert.ok(Date.now() - t < 2000, 'command after reconnect is fast');
  } finally {
    for (const hub of hubs) await hub.close().catch(() => {});
    await cutPower(lamp);
    rmSync(dir, { recursive: true, force: true });
  }
});

// A real power cut: the lamp vanishes without closing its connection, and after the restart it no
// longer knows the old one – Licht must notice that at once instead of sending into the void.
for (const resume of [true, false]) {
  test(`a lamp switched off and on at the wall reacts to the first tap (${resume ? 'resumes' : 'no resume'})`, {
    timeout: 180_000,
  }, async () => {
    const dir = mkdtempSync(join(tmpdir(), 'licht-powercut-'));
    const port = 5500 + Math.floor(Math.random() * 90);
    let lamp = startVirtualLight(dir, port, { resume });
    let hub;
    try {
      const code = await lamp.ready;
      Environment.default.vars.set('storage.path', join(dir, 'hub'));
      hub = new LightHub({ id: 'powercut' });
      await hub.start();
      const id = await hub.commission({ code, ip: `127.0.0.1:${port}` });
      await waitFor(() => hub.light(id)?.online, 'connected');

      await cutPower(lamp);
      lamp = startVirtualLight(dir, port, { resume });
      await lamp.ready;
      await new Promise((r) => setTimeout(r, 500)); // the user taps right after the lamp is back

      const t = Date.now();
      await hub.update(id, { on: true, brightness: 80, mode: 'white', temperature: 0 });
      await waitFor(() => lamp.state.onOff === true, 'lamp on');
      const ms = Date.now() - t;
      await waitFor(() => hub.light(id).status === 'online', 'shown as connected', 5000);
      const onlineMs = Date.now() - t;
      console.log(`TIMING powercut (${resume ? 'resume' : 'no resume'}) first tap ${ms} ms, online after ${onlineMs} ms`);
      assert.ok(ms < 3000, `first tap after power cut took ${ms} ms`);
    } finally {
      await hub?.close().catch(() => {});
      await cutPower(lamp);
      rmSync(dir, { recursive: true, force: true });
    }
  });
}

test('a command sent while the lamp is still starting up goes through', { timeout: 180_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-booting-'));
  const port = 5600 + Math.floor(Math.random() * 90);
  let lamp = startVirtualLight(dir, port);
  let hub;
  try {
    const code = await lamp.ready;
    Environment.default.vars.set('storage.path', join(dir, 'hub'));
    hub = new LightHub({ id: 'booting' });
    await hub.start();
    const id = await hub.commission({ code, ip: `127.0.0.1:${port}` });
    await waitFor(() => hub.light(id)?.online, 'connected');

    // Switched off at the wall; the user switches it on again and taps the app immediately.
    await cutPower(lamp);
    const t = Date.now();
    const command = hub.update(id, { on: true });
    await new Promise((r) => setTimeout(r, 1000));
    lamp = startVirtualLight(dir, port);
    await command;
    await waitFor(() => lamp.state.onOff === true, 'lamp on');
    const ms = Date.now() - t;
    console.log(`TIMING booting command ${ms} ms`);
  } finally {
    await hub?.close().catch(() => {});
    await cutPower(lamp); // cleaning up: no need to wait for the lamp's goodbye
    rmSync(dir, { recursive: true, force: true });
  }
});

test('notices by itself when a lamp goes away and comes back', { timeout: 300_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-away-'));
  const port = 5700 + Math.floor(Math.random() * 90);
  let lamp = startVirtualLight(dir, port);
  let hub;
  try {
    const code = await lamp.ready;
    Environment.default.vars.set('storage.path', join(dir, 'hub'));
    hub = new LightHub({ id: 'away' });
    await hub.start();
    const id = await hub.commission({ code, ip: `127.0.0.1:${port}` });
    await waitFor(() => hub.light(id)?.online, 'connected');

    await cutPower(lamp);
    let t = Date.now();
    await waitFor(() => hub.light(id)?.online === false, 'lamp shown as away', 120_000);
    const awayMs = Date.now() - t;

    lamp = startVirtualLight(dir, port);
    await lamp.ready;
    t = Date.now();
    await waitFor(() => hub.light(id)?.online, 'lamp back', 60_000);
    const backMs = Date.now() - t;
    console.log(`TIMING away noticed after ${awayMs} ms, back after ${backMs} ms`);
    assert.ok(awayMs < 40_000, `noticed after ${awayMs} ms`);
    assert.ok(backMs < 30_000, `back after ${backMs} ms`);
  } finally {
    await hub?.close().catch(() => {});
    await cutPower(lamp); // cleaning up: no need to wait for the lamp's goodbye
    rmSync(dir, { recursive: true, force: true });
  }
});

// The phone app calls refresh() when it comes back from the background.
test('refresh: answering lamps stay connected, a silent one is reconnected', { timeout: 180_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-refresh-'));
  const port = 5800 + Math.floor(Math.random() * 90);
  const lamp = startVirtualLight(dir, port);
  let hub;
  try {
    const code = await lamp.ready;
    Environment.default.vars.set('storage.path', join(dir, 'hub'));
    hub = new LightHub({ id: 'refresh' });
    await hub.start();
    const id = await hub.commission({ code, ip: `127.0.0.1:${port}` });
    await waitFor(() => hub.light(id)?.status === 'online', 'connected');

    // Lamp answers: no reconnect (the status never leaves 'online'), commands stay fast.
    const statuses = [];
    const unsubscribe = hub.subscribe(() => statuses.push(hub.light(id).status));
    hub.refresh();
    let t = Date.now();
    await hub.update(id, { on: true });
    const commandMs = Date.now() - t;
    await new Promise((r) => setTimeout(r, 2000));
    unsubscribe();
    assert.deepEqual(statuses.filter((s) => s !== 'online'), []);
    assert.ok(commandMs < 1000, `command after refresh took ${commandMs} ms`);

    // Lamp silent (phone was away, lamp frozen): refresh notices it within the probe time.
    lamp.child.kill('SIGSTOP');
    t = Date.now();
    hub.refresh();
    await waitFor(() => hub.light(id).status !== 'online', 'silent lamp noticed', 15_000);
    const noticedMs = Date.now() - t;
    lamp.child.kill('SIGCONT');
    t = Date.now();
    await waitFor(() => hub.light(id).status === 'online', 'lamp back', 30_000);
    const backMs = Date.now() - t;
    console.log(`TIMING refresh: command ${commandMs} ms, silent noticed ${noticedMs} ms, back ${backMs} ms`);
    assert.ok(noticedMs < 8000, `noticed after ${noticedMs} ms`);
  } finally {
    lamp.child.kill('SIGCONT');
    await hub?.close().catch(() => {});
    await cutPower(lamp);
    rmSync(dir, { recursive: true, force: true });
  }
});

// matter.js backs off its connection attempts up to 2 minutes apart while a lamp is away.
test('a lamp that was off for a while reacts at once when it is back', { timeout: 240_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-longoff-'));
  const port = 5900 + Math.floor(Math.random() * 90);
  let lamp = startVirtualLight(dir, port);
  let hub;
  try {
    const code = await lamp.ready;
    Environment.default.vars.set('storage.path', join(dir, 'hub'));
    hub = new LightHub({ id: 'longoff' });
    await hub.start();
    const id = await hub.commission({ code, ip: `127.0.0.1:${port}` });
    await waitFor(() => hub.light(id)?.online, 'connected');

    await cutPower(lamp);
    await new Promise((r) => setTimeout(r, 90_000));
    lamp = startVirtualLight(dir, port);
    await lamp.ready;
    await new Promise((r) => setTimeout(r, 2000));

    const t = Date.now();
    await hub.update(id, { on: true });
    await waitFor(() => lamp.state.onOff === true, 'lamp on');
    const ms = Date.now() - t;
    console.log(`TIMING after 90 s off: first tap ${ms} ms`);
    assert.ok(ms < 3000, `first tap took ${ms} ms`);
  } finally {
    await hub?.close().catch(() => {});
    await cutPower(lamp); // cleaning up: no need to wait for the lamp's goodbye
    rmSync(dir, { recursive: true, force: true });
  }
});

// The user is told a tap did not work – then it must not happen later behind their back.
test('a command reported as failed is not carried out later', { timeout: 240_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-ghost-'));
  const port = 6500 + Math.floor(Math.random() * 90);
  let lamp = startVirtualLight(dir, port);
  let hub;
  try {
    const code = await lamp.ready;
    Environment.default.vars.set('storage.path', join(dir, 'hub'));
    hub = new LightHub({ id: 'ghost' });
    await hub.start();
    const id = await hub.commission({ code, ip: `127.0.0.1:${port}` });
    await waitFor(() => hub.light(id)?.online, 'connected');
    await hub.update(id, { on: true });
    await waitFor(() => lamp.state.onOff === true, 'lamp on');
    await hub.update(id, { on: false });
    await waitFor(() => lamp.state.onOff === false, 'lamp off');
    await new Promise((r) => setTimeout(r, 1500)); // stored by the lamp

    await cutPower(lamp);
    await assert.rejects(hub.update(id, { on: true }), (err) => err.status === 503);

    lamp = startVirtualLight(dir, port);
    await lamp.ready;
    await waitFor(() => hub.light(id).status === 'online', 'connected again', 60_000);
    await new Promise((r) => setTimeout(r, 15_000));
    assert.notEqual(lamp.state.onOff, true, 'the failed "on" was carried out later');
  } finally {
    await hub?.close().catch(() => {});
    await cutPower(lamp); // cleaning up: no need to wait for the lamp's goodbye
    rmSync(dir, { recursive: true, force: true });
  }
});
