// Windows desktop integration for Licht.exe: the window program (tray icon + Licht window, see
// ../desktop/), autostart via reg.exe, message box via PowerShell, browser via cmd.
// No downloads, no connections outside this PC.

import { exec, execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  createWriteStream, existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import { createInterface } from 'node:readline';
import { format } from 'node:util';

const RUN_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run';
const MAX_LOG_BYTES = 1024 * 1024;

const run = (file, args) => new Promise((resolve) => {
  execFile(file, args, { windowsHide: true }, (error, stdout) => resolve({ ok: !error, stdout: String(stdout ?? '') }));
});

const encodePowerShell = (script) => Buffer.from(script, 'utf16le').toString('base64');
const powershellArgs = (script) => [
  '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
  '-EncodedCommand', encodePowerShell(script),
];

/** Starting Licht together with Windows, via the per-user "Run" registry key. */
export function autostart({ name = 'Licht', command = `"${process.execPath}" --autostart` } = {}) {
  return {
    async get() {
      const { ok, stdout } = await run('reg.exe', ['query', RUN_KEY, '/v', name]);
      return ok && stdout.includes(name);
    },
    async set(enabled) {
      const args = enabled
        ? ['add', RUN_KEY, '/v', name, '/t', 'REG_SZ', '/d', command, '/f']
        : ['delete', RUN_KEY, '/v', name, '/f'];
      await run('reg.exe', args);
      return this.get();
    },
  };
}

export function openBrowser(url) {
  const command = process.platform === 'win32' ? `start "" "${url}"`
    : process.platform === 'darwin' ? `open "${url}"` : `xdg-open "${url}"`;
  exec(command, { windowsHide: true }, () => {});
}

/** A Windows message box – Licht.exe has no console window to print errors to. */
export function showError(title, message) {
  const quote = (text) => `'${String(text).replace(/'/g, "''")}'`;
  const script = 'Add-Type -AssemblyName System.Windows.Forms; '
    + `[void][System.Windows.Forms.MessageBox]::Show(${quote(message)}, ${quote(title)}, 'OK', 'Error')`;
  return new Promise((resolve) => {
    const child = spawn('powershell.exe', powershellArgs(script), { windowsHide: true, stdio: 'ignore' });
    child.on('exit', resolve);
    child.on('error', resolve);
  });
}

/**
 * Writes the window program (LichtFenster.exe and its few files, embedded in Licht.exe) to
 * <dataDir>/fenster/<version>/ and returns the path of LichtFenster.exe. Files already there and
 * unchanged are kept; older versions are removed (if one still runs, next time).
 * files: { 'LichtFenster.exe': Buffer, … }
 */
export function installDesktopFiles(dataDir, files) {
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
 * Starts the window program: tray icon + Licht window. It ends together with Licht.
 * options: { url, dataDir, autostartEnabled, show, hello }
 * handlers: { ready(), openBrowser(), quit(), autostart(enabled), loaded(detail), unavailable(), exit(code) }
 * Returns { show(), setAutostart(enabled), stop(), alive }; show() is false when the window program
 * is not running (then Licht opens the browser instead).
 */
export function startDesktop(exe, options, handlers, { prefixArgs = [] } = {}) {
  const args = [...prefixArgs, '--url', options.url, '--data', options.dataDir,
    '--autostart', options.autostartEnabled ? 'on' : 'off'];
  if (options.show) args.push('--show');
  if (options.hello) args.push('--hello');
  let child;
  let alive = false;
  try {
    // Not windowsHide: that would also hide the Licht window the first time it opens.
    child = spawn(exe, args, { stdio: ['pipe', 'pipe', 'pipe'] });
    alive = true;
  } catch (err) {
    console.error('Fenster-Programm startet nicht:', err);
    queueMicrotask(() => handlers.exit?.(null));
    return { show: () => false, setAutostart() {}, async stop() {}, alive: false };
  }
  const write = (line) => {
    if (alive && child.stdin.writable) child.stdin.write(`${line}\n`);
    return alive;
  };
  const exited = new Promise((resolve) => child.on('close', resolve));
  let stopping = false;
  child.on('error', (err) => console.error('Fenster-Programm:', err.message));
  // 'close' also follows a failed start ('exit' does not).
  child.on('close', (code) => {
    alive = false;
    if (!stopping) {
      console.warn(`Fenster-Programm beendet (${code})`);
      handlers.exit?.(code);
    }
  });
  child.stdin.on('error', () => {}); // the program ended; noticed via 'close'
  child.stderr.on('data', (data) => console.error('Fenster:', String(data).trim()));
  createInterface({ input: child.stdout }).on('line', (line) => {
    const [command, ...rest] = line.trim().split(' ');
    const detail = rest.join(' ');
    if (command === 'ready') handlers.ready?.();
    else if (command === 'open-browser') handlers.openBrowser?.();
    else if (command === 'quit') handlers.quit?.();
    else if (command === 'autostart:on') handlers.autostart?.(true);
    else if (command === 'autostart:off') handlers.autostart?.(false);
    else if (command === 'window:loaded') handlers.loaded?.(detail);
    else if (command === 'window:unavailable') handlers.unavailable?.();
    else if (command.startsWith('log:')) console.log(line.trim().slice(4));
  });
  return {
    get alive() {
      return alive;
    },
    show: () => write('show'),
    setAutostart: (enabled) => write(enabled ? 'autostart:on' : 'autostart:off'),
    /** Ends the program properly (so its tray icon disappears at once), at most after 2 s. */
    async stop() {
      if (!alive) return;
      stopping = true;
      write('quit');
      const timer = setTimeout(() => child.kill(), 2000);
      await exited;
      clearTimeout(timer);
    },
  };
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
    writeFileSync(marker, new Date().toISOString());
  } catch {
    // not important
  }
  return true;
}
