// Windows desktop integration for Licht.exe – only with what Windows brings along:
// reg.exe (autostart), PowerShell (tray icon, message box), cmd (open the browser).
// No extra programs, no downloads, no connections outside this PC.

import { exec, execFile, spawn } from 'node:child_process';
import { createWriteStream, existsSync, mkdirSync, renameSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
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
 * Tray icon (notification area). handlers: { ready(), open(), quit(), autostart(enabled) }.
 * Returns { stop() }, or undefined when PowerShell is not available.
 */
export function startTray(script, { autostartEnabled, hello, ...handlers }) {
  let child;
  try {
    child = spawn('powershell.exe', powershellArgs(script), {
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
      env: {
        ...process.env,
        LICHT_EXE: process.execPath,
        LICHT_PID: String(process.pid),
        LICHT_AUTOSTART: autostartEnabled ? '1' : '0',
        LICHT_HELLO: hello ? '1' : '0',
      },
    });
  } catch (err) {
    console.error('Tray icon could not start:', err);
    return undefined;
  }
  child.on('error', (err) => console.error('Tray icon could not start:', err));
  child.stderr.on('data', (data) => {
    // PowerShell also reports progress on stderr (as "#< CLIXML"); only real errors matter.
    const text = String(data).replace(/#< CLIXML/g, '').trim();
    if (text && !(text.startsWith('<Objs') && !text.includes('S="Error"'))) console.error('Tray:', text);
  });
  child.on('exit', (code) => console.warn(`Tray icon ended (${code})`));
  createInterface({ input: child.stdout }).on('line', (line) => {
    const command = line.trim();
    if (command === 'ready') handlers.ready?.();
    else if (command === 'open') handlers.open();
    else if (command === 'quit') handlers.quit();
    else if (command === 'autostart:on') handlers.autostart(true);
    else if (command === 'autostart:off') handlers.autostart(false);
  });
  return {
    stop() {
      if (child.exitCode === null) child.kill();
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
