// Windows desktop integration of Licht.exe (tray icon, autostart, log file).
// Tray and autostart need Windows; they run in the Windows build (GitHub Actions).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { autostart, firstRun, logToFile, startTray } from '../server/desktop.js';

const windows = process.platform === 'win32';

test('tray icon starts (exactly as Licht.exe starts it)', { skip: !windows && 'Windows only', timeout: 60000 }, async () => {
  const script = readFileSync(new URL('../server/tray.ps1', import.meta.url), 'utf8');
  process.env.LICHT_TRAY_SELFTEST = '1';
  try {
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('tray icon did not report ready')), 45000);
      startTray(script, {
        autostartEnabled: true,
        hello: true,
        ready: () => { clearTimeout(timer); resolve(); },
        open() {}, quit() {}, autostart() {},
      });
    });
  } finally {
    delete process.env.LICHT_TRAY_SELFTEST;
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
