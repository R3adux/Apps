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
import { startVirtualLight, stopVirtualLight, waitFor } from './support/harness.js';

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
    await stopVirtualLight(lamp);
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
    await stopVirtualLight(lamp);
    rmSync(dir, { recursive: true, force: true });
  }
});

test('a lamp switched off and on at the wall reacts again right away', { timeout: 180_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-powercut-'));
  const port = 5500 + Math.floor(Math.random() * 90);
  let lamp = startVirtualLight(dir, port);
  let hub;
  try {
    const code = await lamp.ready;
    Environment.default.vars.set('storage.path', join(dir, 'hub'));
    hub = new LightHub({ id: 'powercut' });
    await hub.start();
    const id = await hub.commission({ code, ip: `127.0.0.1:${port}` });
    await waitFor(() => hub.light(id)?.online, 'connected');

    // Power cut: the lamp restarts and forgets its connections; Licht has not noticed yet.
    await stopVirtualLight(lamp);
    lamp = startVirtualLight(dir, port);
    await lamp.ready;

    const t = Date.now();
    await hub.update(id, { on: true, brightness: 80, mode: 'white', temperature: 0 });
    await waitFor(() => lamp.state.onOff === true, 'lamp on');
    const ms = Date.now() - t;
    console.log(`TIMING powercut command ${ms} ms`);
    assert.ok(ms < 6000, `command after power cut took ${ms} ms`);
  } finally {
    await hub?.close().catch(() => {});
    await stopVirtualLight(lamp);
    rmSync(dir, { recursive: true, force: true });
  }
});

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
    await stopVirtualLight(lamp);
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
    await stopVirtualLight(lamp);
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

    await stopVirtualLight(lamp);
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
    await stopVirtualLight(lamp);
    rmSync(dir, { recursive: true, force: true });
  }
});
