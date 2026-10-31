// Licht on the PC (Licht.exe without its Windows window): one instance only, taking over from older
// versions, the conversation with the window program, and the whole start-up with a stand-in window.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createBridge } from '../pc/bridge.js';
import { claimInstance, stopOlderVersion } from '../pc/instance.js';
import { removeStaleLocks, waitForLockOwners } from '../pc/locks.js';
import { installWindowFiles } from '../pc/windows.js';

const until = async (check, what, ms = 15_000) => {
  const end = Date.now() + ms;
  for (;;) {
    const value = check();
    if (value) return value;
    if (Date.now() > end) throw new Error(`timed out: ${what}`);
    await new Promise((r) => setTimeout(r, 50));
  }
};

test('only one Licht: a second start reaches the first one', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-instance-'));
  const first = await claimInstance(dir);
  try {
    assert.equal(first.first, true);
    const received = [];
    first.onMessage((m) => received.push(m));
    const second = await claimInstance(dir);
    assert.equal(second.first, false);
    await second.send({ t: 'show' });
    await until(() => received.length, 'show message');
    assert.deepEqual(received, [{ t: 'show' }]);
    // Another data folder is another Licht (tests run several side by side).
    const other = await claimInstance(join(dir, 'other'));
    assert.equal(other.first, true);
    await other.close();
  } finally {
    await first.close();
    rmSync(dir, { recursive: true, force: true });
  }
});

test('a socket left behind by a crashed Licht does not block the next start', { skip: process.platform === 'win32' }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-stale-'));
  const { instancePath } = await import('../pc/instance.js');
  writeFileSync(instancePath(dir), ''); // a leftover file, nobody listening
  const instance = await claimInstance(dir);
  assert.equal(instance.first, true);
  await instance.close();
  rmSync(dir, { recursive: true, force: true });
});

function fakeOldLicht({ canQuit = true, isLicht = true } = {}) {
  let quitAsked = false;
  const server = createServer((req, res) => {
    if (req.url === '/api/info') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(isLicht ? { ble: false } : { name: 'something else' }));
    } else if (req.url === '/api/quit' && req.method === 'POST' && canQuit) {
      quitAsked = true;
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end('{"ok":true}');
      setTimeout(() => server.close(), 300);
      server.closeAllConnections?.();
    } else {
      res.writeHead(404);
      res.end('{}');
    }
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({
    port: server.address().port, server, quitAsked: () => quitAsked,
  })));
}

test('an older Licht with web server is asked to quit; other programs are left alone', async () => {
  assert.equal(await stopOlderVersion({ port: 9 }), 'none'); // nothing there
  const old = await fakeOldLicht();
  assert.equal(await stopOlderVersion({ port: old.port }), 'stopped');
  assert.equal(old.quitAsked(), true);
  const veryOld = await fakeOldLicht({ canQuit: false });
  assert.equal(await stopOlderVersion({ port: veryOld.port, wait: 500 }), 'stuck');
  veryOld.server.close();
  const other = await fakeOldLicht({ isLicht: false });
  assert.equal(await stopOlderVersion({ port: other.port }), 'none');
  other.server.close();
});

test('lock files: waits for their owner, then removes them', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-locks-'));
  try {
    // A lock of a process that is gone is not waited for.
    writeFileSync(join(dir, 'matter.lock'), '999999');
    let t = Date.now();
    await waitForLockOwners(dir, 3000);
    assert.ok(Date.now() - t < 1000);
    // A lock of a running process: waited for until it ends.
    const owner = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 700)']);
    writeFileSync(join(dir, 'matter.pid'), String(owner.pid));
    t = Date.now();
    await waitForLockOwners(dir, 5000);
    assert.ok(Date.now() - t >= 500, 'waited for the owner');
    await removeStaleLocks(dir);
    assert.equal(existsSync(join(dir, 'matter.lock')), false);
    assert.equal(existsSync(join(dir, 'matter.pid')), false);
    await removeStaleLocks(join(dir, 'missing')); // no error for a missing folder
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('window program files are unpacked once, old versions removed', () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-fenster-'));
  try {
    const v1 = { 'LichtFenster.exe': Buffer.from('one'), 'de/LichtFenster.resources.dll': Buffer.from('R') };
    const exe1 = installWindowFiles(dir, v1);
    assert.ok(exe1.endsWith('LichtFenster.exe'));
    assert.equal(readFileSync(exe1, 'utf8'), 'one');
    assert.equal(installWindowFiles(dir, v1), exe1); // same version: same place
    writeFileSync(exe1, 'damaged');
    installWindowFiles(dir, v1);
    assert.equal(readFileSync(exe1, 'utf8'), 'one'); // repaired
    const exe2 = installWindowFiles(dir, { ...v1, 'LichtFenster.exe': Buffer.from('two') });
    assert.notEqual(exe2, exe1);
    assert.equal(existsSync(exe1), false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

function fakeHub() {
  const listeners = new Set();
  const light = { id: '7', name: 'Flur', on: false };
  return {
    lights: () => [light],
    light: (id) => (id === '7' ? light : undefined),
    subscribe: (fn) => (listeners.add(fn), () => listeners.delete(fn)),
    emit: (event) => listeners.forEach((fn) => fn(event)),
    async update(id) {
      if (id !== '7') throw Object.assign(new Error('Lampe nicht gefunden.'), { status: 404 });
      return light;
    },
  };
}

test('bridge: calls wait for the lamps, replies and events reach the window', async () => {
  const sent = [];
  let quit = 0;
  let autostartOn = false;
  const bridge = createBridge({
    autostart: { get: async () => autostartOn, set: async (on) => (autostartOn = on) },
    quit: () => { quit++; },
    hint: true,
  });
  bridge.setSend((m) => sent.push(m));
  await bridge.handle({ t: 'ready' });
  assert.deepEqual(sent.shift(), { t: 'hello', autostart: false, hint: true });

  // A call before the lamps are loaded waits for them.
  const early = bridge.handle({ t: 'call', id: 1, method: 'lights', args: [] });
  await new Promise((r) => setTimeout(r, 50));
  assert.equal(sent.length, 0);
  const hub = fakeHub();
  bridge.attachHub(hub);
  await early;
  assert.deepEqual(sent.splice(0), [
    { t: 'lights', lights: hub.lights() },
    { t: 'reply', id: 1, ok: true, result: hub.lights() },
  ]);

  await bridge.handle({ t: 'call', id: 2, method: 'update', args: ['8', { on: true }] });
  assert.deepEqual(sent.shift(), { t: 'reply', id: 2, ok: false, error: 'Lampe nicht gefunden.', status: 404 });
  await bridge.handle({ t: 'call', id: 3, method: 'autostart', args: [true] });
  assert.deepEqual(sent.shift(), { t: 'reply', id: 3, ok: true, result: true });
  await bridge.handle({ t: 'call', id: 4, method: 'quit', args: [] });
  assert.deepEqual(sent.shift(), { t: 'reply', id: 4, ok: true, result: { ok: true } });
  await new Promise((r) => setTimeout(r, 100));
  assert.equal(quit, 1);

  hub.emit({ type: 'light', light: hub.light('7') });
  hub.emit({ type: 'adding', index: 0, total: 2 });
  hub.emit({ type: 'lights' });
  assert.deepEqual(sent.splice(0), [
    { t: 'light', light: hub.light('7') },
    { t: 'adding', index: 0, total: 2 },
    { t: 'lights', lights: hub.lights() },
  ]);

  // A restarted window program gets everything again.
  await bridge.handle({ t: 'ready' });
  assert.deepEqual(sent.splice(0), [{ t: 'hello', autostart: true, hint: true }, { t: 'lights', lights: hub.lights() }]);
});

test('bridge: if Licht cannot start, the window is told and calls fail clearly', async () => {
  const sent = [];
  const bridge = createBridge({ quit() {} });
  bridge.setSend((m) => sent.push(m));
  bridge.hubFailed(new Error('Storage is locked'));
  await bridge.handle({ t: 'call', id: 1, method: 'lights', args: [] });
  await bridge.handle({ t: 'ready' });
  assert.deepEqual(sent, [
    { t: 'fatal', title: 'Licht konnte nicht starten', text: 'Storage is locked' },
    { t: 'reply', id: 1, ok: false, error: 'Storage is locked', status: 500 },
    { t: 'hello', autostart: false, hint: false },
    { t: 'fatal', title: 'Licht konnte nicht starten', text: 'Storage is locked' },
  ]);
});

const main = fileURLToPath(new URL('../pc/main.js', import.meta.url));
const fakeWindow = fileURLToPath(new URL('./support/fake-window.mjs', import.meta.url));
const noIpv6 = fileURLToPath(new URL('./support/no-ipv6.cjs', import.meta.url));
const readLog = (file) => (existsSync(file) ? readFileSync(file, 'utf8').trim().split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []);

function startLicht(dir, extraEnv = {}, args = []) {
  const child = spawn(process.execPath, [main, ...args], {
    env: {
      ...process.env,
      NODE_OPTIONS: `--require ${noIpv6}`, // like the other tests: this test machine has no IPv6
      LICHT_DATA: dir,
      LICHT_WINDOW: fakeWindow,
      FAKE_WINDOW_LOG: join(dir, 'window.log'),
      ...extraEnv,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let output = '';
  child.stdout.on('data', (d) => { output += d; });
  child.stderr.on('data', (d) => { output += d; });
  const exited = new Promise((resolve) => child.on('exit', resolve));
  return { child, exited, output: () => output };
}

test('Licht.exe start-up: window first, lamps, second start, quit – no web server', { timeout: 120_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-pc-'));
  const log = join(dir, 'window.log');
  const licht = startLicht(dir);
  try {
    const entries = () => readLog(log);
    await until(() => entries().some((e) => e.t === 'reply' && e.id === 3), 'replies to the window', 60_000);
    const all = entries();
    assert.deepEqual(all[0], { t: 'started', args: ['--data', dir, '--show'] });
    assert.deepEqual(all.find((e) => e.t === 'hello'), { t: 'hello', autostart: false, hint: true });
    assert.deepEqual(all.find((e) => e.t === 'lights'), { t: 'lights', lights: [] });
    assert.deepEqual(all.find((e) => e.id === 1), { t: 'reply', id: 1, ok: true, result: [] });
    assert.deepEqual(all.find((e) => e.id === 2), { t: 'reply', id: 2, ok: true, result: false });
    assert.equal(all.find((e) => e.id === 3).status, 404);

    // A second start shows the window of the running one and ends right away.
    const second = startLicht(dir);
    assert.equal(await second.exited, 0);
    await until(() => entries().some((e) => e.t === 'show'), 'show request');

    // "Beenden" in the window program ends Licht.
    writeFileSync(`${log}.quit`, '');
    assert.equal(await licht.exited, 0);
    assert.doesNotMatch(licht.output(), /http:\/\//); // no web server address anywhere
  } finally {
    licht.child.kill('SIGKILL');
    rmSync(dir, { recursive: true, force: true });
  }
});

test('Licht.exe --quit ends a running Licht (and does nothing when none runs)', { timeout: 120_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-quit-'));
  const log = join(dir, 'window.log');
  try {
    const nothing = startLicht(dir, {}, ['--quit']);
    assert.equal(await nothing.exited, 0);
    assert.equal(readLog(log).length, 0); // no window was started

    const licht = startLicht(dir);
    try {
      await until(() => readLog(log).some((e) => e.t === 'reply' && e.id === 3), 'Licht running', 60_000);
      const quit = startLicht(dir, {}, ['--quit']);
      assert.equal(await quit.exited, 0);
      assert.equal(await licht.exited, 0);
      assert.ok(readLog(log).some((e) => e.t === 'quit'), 'the window program was told to end');
      assert.match(licht.output(), /--quit/);
    } finally {
      licht.child.kill('SIGKILL');
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('a crashed window program is started again', { timeout: 60_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-crash-'));
  const log = join(dir, 'window.log');
  const licht = startLicht(dir, { FAKE_WINDOW_CRASH: '1' }, ['--autostart']);
  try {
    await until(() => readLog(log).filter((e) => e.t === 'started').length >= 3, 'three starts', 30_000);
    const starts = readLog(log).filter((e) => e.t === 'started');
    assert.deepEqual(starts[0].args, ['--data', dir]); // at login: no window, only the tray icon
  } finally {
    licht.child.kill('SIGKILL');
    await licht.exited;
    rmSync(dir, { recursive: true, force: true });
  }
});
