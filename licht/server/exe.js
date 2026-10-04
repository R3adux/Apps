// Entry point of Licht.exe: everything (incl. the web app) in one file.
// Starts the hub and opens the app in the default browser.

import { exec } from 'node:child_process';
import webFiles from 'licht:web-files';
import { defaults, startLicht } from './app.js';

const openBrowser = (url) => {
  if (process.env.LICHT_NO_BROWSER === '1') return;
  const command = process.platform === 'win32' ? `start "" "${url}"`
    : process.platform === 'darwin' ? `open "${url}"` : `xdg-open "${url}"`;
  exec(command, () => {});
};

const waitForEnter = () => new Promise((resolve) => {
  process.stdin.resume();
  process.stdin.once('data', resolve);
});

async function main() {
  console.log('Licht – lokale Lampensteuerung\n');
  try {
    const licht = await startLicht({ web: { webFiles } });
    console.log(`Läuft. Öffne im Browser:\n  ${licht.urls.join('\n  ')}\n`);
    console.log('Auf dem Handy (gleiches WLAN) die zweite Adresse öffnen.');
    console.log('Dieses Fenster offen lassen – Schließen beendet Licht.\n');
    openBrowser(licht.urls[0]);
    const stop = async () => {
      await licht.close();
      process.exit(0);
    };
    process.on('SIGINT', stop);
    process.on('SIGTERM', stop);
  } catch (err) {
    if (err.code === 'EADDRINUSE') {
      // Most likely Licht is already running: just show it.
      const url = `http://localhost:${defaults().port}`;
      console.log(`Licht läuft bereits. Öffne ${url}`);
      openBrowser(url);
      setTimeout(() => process.exit(0), 1500);
    } else {
      console.error('Licht konnte nicht starten:', err.message ?? err);
      console.error('\nEnter drücken zum Schließen.');
      await waitForEnter();
      process.exit(1);
    }
  }
}

main();
