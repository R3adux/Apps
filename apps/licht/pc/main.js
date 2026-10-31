// Entry point of Licht.exe: the lamps (matter.js) and the window program LichtFenster.exe, which shows the
// Licht window and the tray icon (pc/fenster/). Everything in one file, nothing to install.
//
// No web server and no browser: the window talks to Licht over its standard input/output (bridge.js),
// a second start of Licht.exe reaches the running one over a named pipe (instance.js).
// Started by Windows at login (--autostart) only the tray icon appears. `Licht.exe --quit` ends a running Licht.
// Messages go to <data folder>/licht.log.

/* global LICHT_WINDOW_FILES */
import { rmSync } from 'node:fs';
import { join } from 'node:path';
import { getAsset, isSea } from 'node:sea';
import { createBridge } from './bridge.js';
import { dataDirectory } from './config.js';
import { claimInstance, stopOlderVersion } from './instance.js';
import { waitForLockOwners } from './locks.js';
import { startWindow } from './window.js';
import { autostart, firstRun, installWindowFiles, logToFile, showError } from './windows.js';

const windows = process.platform === 'win32';
const atLogin = process.argv.includes('--autostart');
const quitRunning = process.argv.includes('--quit');
const dataDir = dataDirectory();
// After a crash the window program is started again, waiting a little longer each time.
const RESTART_DELAYS_MS = [300, 1000, 2000, 5000, 10_000, 30_000];
const QUICK_FAILURES_BEFORE_MESSAGE = 4;

// The window program: built into Licht.exe (scripts/build-exe.mjs), or given by LICHT_WINDOW (tests).
function windowCommand() {
  const given = process.env.LICHT_WINDOW;
  if (given) return /\.[cm]?js$/i.test(given) ? { file: process.execPath, args: [given] } : { file: given, args: [] };
  const names = typeof LICHT_WINDOW_FILES === 'undefined' ? [] : LICHT_WINDOW_FILES;
  if (!names.length || !isSea()) return undefined;
  const files = Object.fromEntries(names.map((name) => [name, Buffer.from(getAsset(`fenster/${name}`))]));
  return { file: installWindowFiles(dataDir, files), args: [] };
}

async function main() {
  if (windows) logToFile(dataDir);
  console.log(`Licht startet${atLogin ? ' (Autostart)' : ''}`);
  process.on('uncaughtException', (err) => console.error('Unerwarteter Fehler:', err));
  process.on('unhandledRejection', (err) => console.error('Unerwarteter Fehler:', err));

  // Only one Licht: a second start shows the window of the running one and ends.
  const instance = await claimInstance(dataDir);
  if (!instance.first) {
    console.log(quitRunning ? 'Licht wird beendet (--quit)' : 'Licht läuft bereits');
    if (quitRunning) await instance.send({ t: 'quit' });
    else if (!atLogin) await instance.send({ t: 'show' });
    process.exit(0);
  }
  if (quitRunning) {
    await instance.close(); // nothing to end
    process.exit(0);
  }

  const startup = windows ? autostart() : undefined;
  let host;
  let windowProgram;
  let stopping = false;
  const stop = async () => {
    if (stopping) return;
    stopping = true;
    console.log('Licht wird beendet');
    // Both at once: also when Windows shuts down, the lamp data is closed properly in time.
    await Promise.all([windowProgram?.stop(), host?.close()]);
    await instance.close();
    process.exit(0);
  };
  const bridge = createBridge({ autostart: startup, quit: stop, hint: firstRun(dataDir) });

  // The window first – it shows the lamps as last known right away, while matter.js is still loading.
  let command;
  try {
    command = windowCommand();
  } catch (err) {
    console.error('Fenster-Programm nicht installierbar:', err);
  }
  if (!command) {
    await showError('Licht', 'Das Licht-Fenster fehlt in dieser Licht.exe. Bitte Licht.exe neu herunterladen.');
    process.exit(1);
  }
  let pendingShow = false;
  let restarts = 0;
  let quickFailures = 0;
  let messageShown = false;
  const launch = (show) => {
    const startedAt = Date.now();
    const args = [...command.args, '--data', dataDir];
    if (show) args.push('--show');
    windowProgram = startWindow(command.file, args, {
      onMessage: (message) => {
        if (message.t === 'ready') {
          quickFailures = 0;
          if (pendingShow) {
            pendingShow = false;
            windowProgram.send({ t: 'show' });
          }
        }
        bridge.handle(message);
      },
      onExit: async () => {
        if (stopping) return;
        if (Date.now() - startedAt > 60_000) restarts = 0; // ran fine for a while: a new problem
        if (Date.now() - startedAt < 5000 && ++quickFailures >= QUICK_FAILURES_BEFORE_MESSAGE && !messageShown) {
          messageShown = true;
          await showError('Licht', 'Das Licht-Fenster startet nicht. Licht versucht es weiter.\n\n'
            + `Details stehen in ${join(dataDir, 'licht.log')}.`);
        }
        const delay = RESTART_DELAYS_MS[Math.min(restarts++, RESTART_DELAYS_MS.length - 1)];
        setTimeout(() => launch(false), delay);
      },
    });
    bridge.setSend((message) => windowProgram.send(message));
  };
  launch(!atLogin);

  instance.onMessage((message) => {
    if (message.t === 'quit') {
      console.log('Licht soll beendet werden (zweiter Start mit --quit)');
      stop();
      return;
    }
    if (message.t !== 'show') return;
    console.log('Licht wurde erneut gestartet – Fenster nach vorn');
    if (!windowProgram?.send({ t: 'show' })) pendingShow = true;
  });
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.on(signal, stop);

  // Housekeeping: the autostart entry points to this Licht.exe; data of the old browser window goes.
  startup?.repair().catch((err) => console.error('Autostart:', err));
  try {
    rmSync(join(dataDir, 'WebView2'), { recursive: true, force: true });
    rmSync(join(dataDir, 'fenster.txt'), { force: true });
  } catch {
    // only tidying up
  }

  // An older Licht (with web server) must not run at the same time: it uses the same lamp data.
  let older = await stopOlderVersion();
  if (older === 'stuck') {
    bridge.notify({
      t: 'message', kind: 'error', title: 'Ältere Licht-Version läuft noch',
      text: 'Bitte die ältere Licht-Version beenden (ihr Fenster schließen oder im Symbol unten rechts „Beenden“). '
        + 'Licht startet danach von selbst.',
    });
    while (older === 'stuck' && !stopping) {
      await new Promise((r) => setTimeout(r, 2000));
      older = await stopOlderVersion({ wait: 0 }) === 'none' ? 'stopped' : 'stuck';
    }
  }
  if (older === 'stopped') await waitForLockOwners(dataDir);

  try {
    const { startHost } = await import('./host.js');
    host = await startHost({ dataDir });
    bridge.attachHub(host.hub);
    console.log(`Licht läuft (${host.hub.lights().length} Lampen)`);
  } catch (err) {
    console.error('Licht konnte nicht starten:', err);
    bridge.hubFailed(err);
  }
}

main();
