// Windows helpers of Licht.exe: start with Windows, log file, first-start hint.
// The registry part needs Windows; it runs in the Windows build (GitHub Actions).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { autostart, firstRun, logToFile } from '../pc/windows.js';

const windows = process.platform === 'win32';

test('start with Windows: on, off, and an entry of another Licht.exe is corrected', { skip: !windows && 'Windows only' }, async () => {
  const name = `LichtTest${process.pid}`;
  const entry = autostart({ name, command: '"C:\\Licht\\Licht.exe" --autostart' });
  const elsewhere = autostart({ name, command: '"D:\\Alt\\Licht.exe" --autostart' });
  try {
    assert.equal(await entry.get(), false);
    assert.equal(await entry.repair(), false); // nothing to repair when off
    assert.equal(await elsewhere.set(true), true);
    assert.equal(await entry.repair(), true); // pointed to the old copy: now to this one
    assert.equal(await elsewhere.repair(), true);
    assert.equal(await entry.set(false), false);
  } finally {
    await entry.set(false);
  }
});

test('hint only on the very first start', () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-first-'));
  try {
    assert.equal(firstRun(join(dir, 'new')), true); // also creates the folder
    assert.equal(firstRun(join(dir, 'new')), false);
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
