// Windows desktop integration of Licht.exe (window program, autostart, log file).
// The real window program and autostart need Windows; they run in the Windows build (GitHub Actions).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { autostart, firstRun, installDesktopFiles, logToFile, startDesktop } from '../server/desktop.js';
import { createHttpServer } from '../server/http.js';

const windows = process.platform === 'win32';

const until = (check, ms = 5000) => new Promise((resolve, reject) => {
  const started = Date.now();
  const timer = setInterval(() => {
    if (check()) { clearInterval(timer); resolve(); } else if (Date.now() - started > ms) {
      clearInterval(timer); reject(new Error('timed out'));
    }
  }, 10);
});

test('window program files are unpacked once, old versions removed', () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-fenster-'));
  try {
    const v1 = { 'LichtFenster.exe': Buffer.from('one'), 'runtimes/win-x64/native/WebView2Loader.dll': Buffer.from('L') };
    const exe1 = installDesktopFiles(dir, v1);
    assert.ok(exe1.endsWith('LichtFenster.exe'));
    assert.equal(readFileSync(exe1, 'utf8'), 'one');
    assert.equal(readFileSync(join(dirname(exe1), 'runtimes', 'win-x64', 'native', 'WebView2Loader.dll'), 'utf8'), 'L');
    assert.equal(installDesktopFiles(dir, v1), exe1); // same version: same place
    writeFileSync(exe1, 'damaged');
    installDesktopFiles(dir, v1);
    assert.equal(readFileSync(exe1, 'utf8'), 'one'); // repaired
    const exe2 = installDesktopFiles(dir, { ...v1, 'LichtFenster.exe': Buffer.from('two') });
    assert.notEqual(exe2, exe1);
    assert.equal(existsSync(exe1), false); // the old version is gone
    assert.deepEqual(readdirSync(join(dir, 'fenster')).length, 1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

const fake = fileURLToPath(new URL('./support/fake-desktop.mjs', import.meta.url));

function startFake(options, env = {}) {
  const got = [];
  const logs = [];
  const saved = console.log;
  console.log = (...args) => logs.push(args.join(' '));
  Object.assign(process.env, env);
  const shell = startDesktop(process.execPath, { url: 'http://localhost:1', dataDir: '/x', ...options }, {
    ready: () => got.push('ready'),
    loaded: (detail) => got.push(`loaded ${detail}`),
    quit: () => got.push('quit'),
    openBrowser: () => got.push('browser'),
    autostart: (on) => got.push(`autostart ${on}`),
    exit: (code) => got.push(`exit ${code}`),
  }, { prefixArgs: [fake] });
  for (const key of Object.keys(env)) delete process.env[key];
  return { shell, got, logs, restore: () => { console.log = saved; } };
}

test('Licht talks with the window program', async () => {
  const { shell, got, logs, restore } = startFake({ autostartEnabled: true, show: true });
  try {
    await until(() => got.includes('loaded 5 fake'));
    assert.deepEqual(got, ['ready', 'loaded 5 fake']);
    assert.ok(logs.some((l) => l.includes('--url http://localhost:1 --data /x --autostart on --show')));
    assert.equal(shell.show(), true);
    await until(() => got.includes('loaded 1 fake'));
    shell.setAutostart(false);
    await until(() => logs.includes('autostart:off'));
    await shell.stop(); // ends properly: no 'exit' report
    assert.equal(shell.alive, false);
    assert.equal(shell.show(), false);
    assert.equal(got.some((g) => g.startsWith('exit')), false);
  } finally {
    restore();
    await shell.stop();
  }
});

test('menu clicks in the window program reach Licht', async () => {
  const { shell, got, restore } = startFake({ autostartEnabled: true }, { FAKE_DESKTOP_MENU: '1' });
  try {
    await until(() => got.includes('quit'));
    assert.deepEqual(got, ['ready', 'browser', 'autostart false', 'quit']);
  } finally {
    restore();
    await shell.stop();
  }
});

test('a crashed window program is reported', async () => {
  const { shell, got, restore } = startFake({}, { FAKE_DESKTOP_CRASH: '1' });
  try {
    await until(() => got.includes('exit 3'));
    assert.equal(shell.alive, false);
    assert.equal(shell.show(), false); // Licht then opens the browser
  } finally {
    restore();
  }
});

test('a missing window program is reported', async () => {
  const got = [];
  const saved = { warn: console.warn, error: console.error };
  console.warn = () => {};
  console.error = () => {};
  try {
    const shell = startDesktop('/nicht/da/LichtFenster.exe', { url: 'http://localhost:1', dataDir: '/x' }, { exit: (code) => got.push(code) });
    await until(() => got.length === 1);
    assert.equal(shell.show(), false);
  } finally {
    Object.assign(console, saved);
  }
});

const builtWindow = fileURLToPath(new URL('../desktop/bin/Release/net48/LichtFenster.exe', import.meta.url));

test('the real Licht window opens and shows the Licht page', {
  skip: (!windows && 'Windows only') || (!existsSync(builtWindow) && 'window program not built (dotnet build desktop)'),
  timeout: 90000,
}, async () => {
  const hub = { lights: () => [], subscribe: () => () => {}, wake() {} };
  const server = createHttpServer(hub, { webDir: fileURLToPath(new URL('../web/', import.meta.url)), log: {} });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const dir = mkdtempSync(join(tmpdir(), 'licht-window-'));
  try {
    const child = spawn(builtWindow, ['--url', `http://localhost:${server.address().port}`, '--data', dir, '--selftest'],
      { stdio: ['pipe', 'pipe', 'inherit'] });
    let out = '';
    child.stdout.on('data', (d) => { out += d; });
    const code = await new Promise((resolve) => child.on('close', resolve));
    console.log(out.trim());
    assert.match(out, /selftest:ok \d+ WebView2 /);
    assert.equal(code, 0);
  } finally {
    server.close();
    try {
      rmSync(dir, { recursive: true, force: true });
    } catch {
      // WebView2 may still hold files for a moment
    }
  }
});

test('autostart can be switched on and off', { skip: !windows && 'Windows only' }, async () => {
  const entry = autostart({ name: `LichtTest${process.pid}`, command: '"C:\\Licht\\Licht.exe" --autostart' });
  try {
    assert.equal(await entry.get(), false);
    assert.equal(await entry.set(true), true);
    assert.equal(await entry.get(), true);
    assert.equal(await entry.set(false), false);
  } finally {
    await entry.set(false);
  }
});

test('hint only on the very first start', () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-desktop-'));
  try {
    assert.equal(firstRun(dir), true);
    assert.equal(firstRun(dir), false);
    assert.ok(existsSync(join(dir, '.licht-gestartet')));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('messages go to licht.log, a big log is moved aside', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-log-'));
  const saved = { ...console };
  try {
    writeFileSync(join(dir, 'licht.log'), 'x'.repeat(1024 * 1024 + 1));
    const file = logToFile(dir);
    console.log('Hallo', { a: 1 });
    console.error('Fehler');
    await new Promise((r) => setTimeout(r, 100));
    const log = readFileSync(file, 'utf8');
    assert.match(log, /LOG   Hallo \{ a: 1 \}/);
    assert.match(log, /ERROR Fehler/);
    assert.ok(existsSync(join(dir, 'licht.old.log')));
  } finally {
    Object.assign(console, saved);
    try {
      rmSync(dir, { recursive: true, force: true });
    } catch {
      // Windows: the log file is still open
    }
  }
});
