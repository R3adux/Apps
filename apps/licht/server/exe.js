// Entry point of Licht.exe: everything (incl. the web app and the window program) in one file.
//
// On Windows Licht.exe has no console window. It runs in the background with an icon in the
// notification area (tray) and shows Licht in a small window of its own (../desktop/, Edge WebView2);
// without WebView2 the browser is used instead. Click on the icon = open Licht, right-click = more.
// Started by Windows at login (--autostart) it only shows the icon.
// Messages go to <data folder>/licht.log, errors at startup are shown as a message box.
//
// Environment (mainly for tests): LICHT_NO_BROWSER=1 (open nothing at start), LICHT_NO_TRAY=1,
// PORT, LICHT_DATA.

/* global LICHT_DESKTOP_FILES */
import { getAsset, isSea } from 'node:sea';
import webFiles from 'licht:web-files';
import { defaults, startLicht } from './app.js';
import {
  autostart, firstRun, installDesktopFiles, logToFile, openBrowser, showError, startDesktop,
} from './desktop.js';

const windows = process.platform === 'win32';
const atLogin = process.argv.includes('--autostart');
const quietStart = atLogin || process.env.LICHT_NO_BROWSER === '1';
const { port, dataDir } = defaults();
const url = `http://localhost:${port}`;
const MAX_RESTARTS = 3;

let openedAt = 0;
const browser = () => {
  if (process.env.LICHT_NO_BROWSER === '1' || Date.now() - openedAt < 2000) return; // double click: one tab
  openedAt = Date.now();
  openBrowser(url);
};

async function fail(message) {
  console.error(message);
  if (windows) await showError('Licht', message);
  process.exit(1);
}

// The window program's files, embedded into Licht.exe by scripts/build-exe.mjs.
function desktopFiles() {
  const names = typeof LICHT_DESKTOP_FILES === 'undefined' ? [] : LICHT_DESKTOP_FILES;
  if (!names.length || !isSea()) return undefined;
  return Object.fromEntries(names.map((name) => [name, Buffer.from(getAsset(`fenster/${name}`))]));
}

// Licht already runs (second double click): ask it to show its window.
async function showRunning() {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/api/show`, { method: 'POST', signal: AbortSignal.timeout(3000) });
    return res.ok;
  } catch {
    return false;
  }
}

async function main() {
  if (windows) logToFile(dataDir);
  console.log(`Licht startet${atLogin ? ' (Autostart)' : ''}`);
  process.on('uncaughtException', (err) => console.error('Unerwarteter Fehler:', err));
  process.on('unhandledRejection', (err) => console.error('Unerwarteter Fehler:', err));

  const startup = windows ? autostart() : undefined;
  let licht;
  let shell; // the window program (tray icon + Licht window)
  let restarts = 0;
  let stopping = false;

  const stop = async () => {
    if (stopping) return;
    stopping = true;
    console.log('Licht wird beendet');
    // Both at once: also when Windows shuts down, the lamp data is closed properly in time.
    await Promise.all([shell?.stop(), licht?.close()]);
    process.exit(0);
  };

  const show = () => {
    if (!shell?.show()) browser();
  };

  const setAutostart = async (enabled) => {
    const now = await startup.set(enabled);
    shell?.setAutostart(now);
    console.log(`Mit Windows starten: ${now ? 'an' : 'aus'}`);
    return now;
  };

  const launchShell = async (files, showWindow) => {
    let exe;
    try {
      exe = installDesktopFiles(dataDir, files);
    } catch (err) {
      console.error('Fenster-Programm nicht installierbar:', err);
      if (showWindow) browser();
      return;
    }
    shell = startDesktop(exe, {
      url,
      dataDir,
      autostartEnabled: await startup.get(),
      show: showWindow,
      hello: !showWindow && firstRun(dataDir),
    }, {
      ready: () => console.log('Tray-Symbol bereit'),
      openBrowser: browser,
      quit: stop,
      autostart: (enabled) => setAutostart(enabled).catch((err) => console.error('Autostart:', err)),
      loaded: (detail) => console.log(`App-Fenster geladen (${detail})`),
      unavailable: () => console.log('App-Fenster nicht möglich – Licht öffnet im Browser'),
      exit: () => {
        // Crashed or could not start: try again (a few times), so the tray icon comes back.
        if (stopping) return;
        if (restarts++ < MAX_RESTARTS) setTimeout(() => launchShell(files, false), 1000);
        else console.error('Fenster-Programm läuft nicht – Licht bleibt im Browser erreichbar');
      },
    });
  };

  const desktop = startup ? {
    quit: stop,
    show,
    autostart: { get: () => startup.get(), set: setAutostart },
  } : undefined;

  // Show the window as soon as the page can load; the lamps are loaded meanwhile.
  let shellStarted;
  const listening = () => {
    if (!windows || process.env.LICHT_NO_TRAY === '1') return;
    const files = desktopFiles();
    if (files) shellStarted = launchShell(files, !quietStart);
  };

  try {
    licht = await startLicht({ web: { webFiles }, desktop, listening });
  } catch (err) {
    if (err.code === 'EADDRINUSE') {
      // Most likely Licht is already running: bring its window to the front.
      console.log('Licht läuft bereits');
      if (!atLogin && !(await showRunning())) browser();
      setTimeout(() => process.exit(0), 500);
      return;
    }
    await shell?.stop();
    await fail(`Licht konnte nicht starten:\n\n${err.message ?? err}`);
    return;
  }

  console.log(`Licht läuft: ${licht.urls.join(', ')}`);
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.on(signal, stop);
  await shellStarted;
  if (!shell && !quietStart) browser(); // no window program (other systems, or not available)
  if (!windows) console.log(`Öffne im Browser: ${licht.urls.join('  oder  ')}`);
}

main();
