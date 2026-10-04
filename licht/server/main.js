#!/usr/bin/env node
// Licht hub (npm start): controls Matter lamps in the home network and serves the web app.
//
// Settings (environment variables):
//   PORT       web port (default 8080)
//   LICHT_DATA where pairing data is stored (default ~/.licht)
//   LICHT_BLE  1 = allow setting up factory-new lamps via Bluetooth (needs a Bluetooth adapter)
//   LICHT_LOG  log level, e.g. info or debug

import { fileURLToPath } from 'node:url';
import { startLicht } from './app.js';

const licht = await startLicht({ web: { webDir: fileURLToPath(new URL('../web/', import.meta.url)) } });
console.log(`Licht läuft. Öffne im Browser: ${licht.urls.join('  oder  ')}`);
console.log(`Daten: ${licht.dataDir}${licht.ble ? ' · Bluetooth an' : ''}`);

async function stop() {
  await licht.close();
  process.exit(0);
}
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
