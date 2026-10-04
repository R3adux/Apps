#!/usr/bin/env node
// Licht hub: controls Matter lamps in the home network and serves the web app.
//
// Settings (environment variables):
//   PORT       web port (default 8080)
//   LICHT_DATA where pairing data is stored (default ~/.licht)
//   LICHT_BLE  1 = allow setting up factory-new lamps via Bluetooth (needs a Bluetooth adapter)

import { homedir, networkInterfaces } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Environment, Logger, singleton } from '@matter/main';
import { Ble } from '@matter/main/protocol';
import { LightHub } from '../core/hub.js';
import { createHttpServer } from './http.js';

const port = Number(process.env.PORT ?? 8080);
const dataDir = process.env.LICHT_DATA ?? join(homedir(), '.licht');
const wantBle = process.env.LICHT_BLE === '1';

Logger.level = process.env.LICHT_LOG ?? 'error'; // 'info' or 'debug' for troubleshooting
const environment = Environment.default;
environment.vars.set('storage.path', dataDir);

let ble = false;
if (wantBle) {
  try {
    const { NodeJsBle } = await import('@matter/nodejs-ble');
    Ble.get = singleton(() => new NodeJsBle({ environment }));
    ble = true;
  } catch (err) {
    console.warn('Bluetooth nicht verfügbar (npm install @matter/nodejs-ble?):', err.message);
  }
}

const hub = new LightHub({ environment });
await hub.start();

const webDir = fileURLToPath(new URL('../web/', import.meta.url));
const server = createHttpServer(hub, { webDir, ble });
server.listen(port, () => {
  const addresses = Object.values(networkInterfaces())
    .flat()
    .filter((a) => a && a.family === 'IPv4' && !a.internal)
    .map((a) => `http://${a.address}:${port}`);
  console.log(`Licht läuft. Öffne im Browser: ${[`http://localhost:${port}`, ...addresses].join('  oder  ')}`);
  console.log(`Daten: ${dataDir}${ble ? ' · Bluetooth an' : ''}`);
});

let stopping = false;
async function stop() {
  if (stopping) return;
  stopping = true;
  server.close();
  await hub.close();
  process.exit(0);
}
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
