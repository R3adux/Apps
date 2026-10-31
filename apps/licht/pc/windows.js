// Windows helpers for Licht.exe: start with Windows (registry), log file, first-start hint, unpacking the
// window program, and a last-resort message box. Only what Windows brings along – no downloads,
// no connections outside this PC, and never a web browser.

import { execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  createWriteStream, existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import { format } from 'node:util';

const RUN_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run';
const MAX_LOG_BYTES = 1024 * 1024;

const run = (file, args) => new Promise((resolve) => {
  execFile(file, args, { windowsHide: true }, (error, stdout) => resolve({ ok: !error, stdout: String(stdout ?? '') }));
});

/**
 * Starting Licht together with Windows, via the per-user "Run" registry key.
 * The entry always points to this Licht.exe: an entry for a Licht.exe elsewhere (an older copy) is
 * corrected by repair(), so Windows never starts an old version at login.
 */
export function autostart({ name = 'Licht', command = `"${process.execPath}" --autostart` } = {}) {
  const read = async () => {
    const { ok, stdout } = await run('reg.exe', ['query', RUN_KEY, '/v', name]);
    if (!ok) return undefined;
    const line = stdout.split(/\r?\n/).find((l) => l.trim().startsWith(name));
    return line?.replace(/^\s*\S+\s+REG_\w+\s+/, '').trim();
  };
  return {
    async get() {
      return (await read()) !== undefined;
    },
    async set(enabled) {
      const args = enabled
        ? ['add', RUN_KEY, '/v', name, '/t', 'REG_SZ', '/d', command, '/f']
        : ['delete', RUN_KEY, '/v', name, '/f'];
      await run('reg.exe', args);
      return this.get();
    },
    /** If the entry exists but points elsewhere, points it to this Licht.exe. Returns whether it is on. */
    async repair() {
      const current = await read();
      if (current === undefined) return false;
      if (current.toLowerCase() !== command.toLowerCase()) {
        console.log(`Autostart zeigte auf ${current} – jetzt auf diese Licht.exe`);
        return this.set(true);
      }
      return true;
    },
  };
}

/**
 * A Windows message box, only for when even the window program cannot run (it shows all other messages).
 * Licht.exe has no console window to print errors to.
 */
export function showError(title, message) {
  const quote = (text) => `'${String(text).replace(/'/g, "''")}'`;
  const script = 'Add-Type -AssemblyName System.Windows.Forms; '
    + `[void][System.Windows.Forms.MessageBox]::Show(${quote(message)}, ${quote(title)}, 'OK', 'Error')`;
  const encoded = Buffer.from(script, 'utf16le').toString('base64');
  return new Promise((resolve) => {
    const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
      '-WindowStyle', 'Hidden', '-EncodedCommand', encoded], { windowsHide: true, stdio: 'ignore' });
    child.on('exit', resolve);
    child.on('error', resolve);
  });
}

/**
 * Writes the window program (embedded in Licht.exe) to <dataDir>/fenster/<version>/ and returns the
 * path of LichtFenster.exe. Files already there and unchanged are kept; older versions are removed
 * (if one still runs, next time). files: { 'LichtFenster.exe': Buffer, … }
 */
export function installWindowFiles(dataDir, files) {
  const names = Object.keys(files).sort();
  const hash = createHash('sha256');
  for (const name of names) hash.update(name).update(files[name]);
  const version = hash.digest('hex').slice(0, 12);
  const base = join(dataDir, 'fenster');
  const dir = join(base, version);
  for (const name of names) {
    const target = join(dir, name);
    if (existsSync(target) && readFileSync(target).equals(files[name])) continue;
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(`${target}.neu`, files[name]);
    renameSync(`${target}.neu`, target);
  }
  for (const old of readdirSync(base)) {
    if (old === version) continue;
    try {
      rmSync(join(base, old), { recursive: true, force: true });
    } catch {
      // still in use by a Licht that is ending right now
    }
  }
  return join(dir, 'LichtFenster.exe');
}

/**
 * Sends console output to <dataDir>/licht.log (Licht.exe has no console window).
 * The log is kept small: when it grows over 1 MB it is moved to licht.old.log.
 */
export function logToFile(dataDir) {
  mkdirSync(dataDir, { recursive: true });
  const file = join(dataDir, 'licht.log');
  try {
    if (statSync(file).size > MAX_LOG_BYTES) renameSync(file, join(dataDir, 'licht.old.log'));
  } catch {
    // no log yet
  }
  const stream = createWriteStream(file, { flags: 'a' });
  for (const level of ['log', 'info', 'warn', 'error', 'debug']) {
    console[level] = (...args) => {
      stream.write(`${new Date().toISOString()} ${level.toUpperCase().padEnd(5)} ${format(...args)}\n`);
    };
  }
  return file;
}

/** True only the first time (per data folder) – for a one-time hint. */
export function firstRun(dataDir) {
  const marker = join(dataDir, '.licht-gestartet');
  if (existsSync(marker)) return false;
  try {
    mkdirSync(dataDir, { recursive: true });
    writeFileSync(marker, new Date().toISOString());
  } catch {
    // not important
  }
  return true;
}
