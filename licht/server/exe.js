// Entry point of Licht.exe: everything (incl. the web app) in one file.
//
// On Windows Licht.exe has no console window. It runs in the background with an icon in the
// notification area (tray): click = open the app, right-click = autostart / quit.
// Started by Windows at login (--autostart) it does not open the browser.
// Messages go to <data folder>/licht.log, errors at startup are shown as a message box.
//
// Environment (mainly for tests): LICHT_NO_BROWSER=1, LICHT_NO_TRAY=1, PORT, LICHT_DATA.

import webFiles from 'licht:web-files';
import trayScript from './tray.ps1';
import { defaults, startLicht } from './app.js';
import { autostart, firstRun, logToFile, openBrowser, showError, startTray } from './desktop.js';

const windows = process.platform === 'win32';
const atLogin = process.argv.includes('--autostart');
const { port, dataDir } = defaults();
const url = `http://localhost:${port}`;

let openedAt = 0;
const open = () => {
  if (process.env.LICHT_NO_BROWSER === '1' || Date.now() - openedAt < 2000) return; // double click: one tab
  openedAt = Date.now();
  openBrowser(url);
};

async function fail(message) {
  console.error(message);
  if (windows) await showError('Licht', message);
  process.exit(1);
}

async function main() {
  if (windows) logToFile(dataDir);
  console.log(`Licht startet${atLogin ? ' (Autostart)' : ''}`);
  process.on('uncaughtException', (err) => console.error('Unerwarteter Fehler:', err));
  process.on('unhandledRejection', (err) => console.error('Unerwarteter Fehler:', err));

  const startup = windows ? autostart() : undefined;
  let licht;
  let tray;
  let stopping = false;
  const stop = async () => {
    if (stopping) return;
    stopping = true;
    console.log('Licht wird beendet');
    tray?.stop();
    await licht?.close();
    process.exit(0);
  };

  try {
    licht = await startLicht({
      web: { webFiles },
      desktop: startup ? { quit: stop, autostart: startup } : undefined,
    });
  } catch (err) {
    if (err.code === 'EADDRINUSE') {
      // Most likely Licht is already running: just show it.
      console.log('Licht läuft bereits');
      if (!atLogin) open();
      setTimeout(() => process.exit(0), 1000);
      return;
    }
    await fail(`Licht konnte nicht starten:\n\n${err.message ?? err}`);
    return;
  }

  console.log(`Licht läuft: ${licht.urls.join(', ')}`);
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.on(signal, stop);

  if (windows && process.env.LICHT_NO_TRAY !== '1') {
    tray = startTray(trayScript, {
      autostartEnabled: await startup.get(),
      hello: firstRun(dataDir),
      ready: () => console.log('Tray-Symbol bereit'),
      open,
      quit: stop,
      autostart: (enabled) => startup.set(enabled).catch((err) => console.error('Autostart:', err)),
    });
  }
  if (!windows) {
    console.log(`Öffne im Browser: ${licht.urls.join('  oder  ')}`);
  }
  if (!atLogin) open();
}

main();
