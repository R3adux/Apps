// Starts the Licht hub: Matter controller + web server. Used by main.js (npm start)
// and exe.js (Licht.exe).

import { homedir, networkInterfaces } from 'node:os';
import { join } from 'node:path';
import { Environment, Logger, singleton } from '@matter/main';
import { Ble } from '@matter/main/protocol';
import { LightHub } from '../core/hub.js';
import { createHttpServer } from './http.js';

export const defaults = () => ({
  port: Number(process.env.PORT ?? 8080),
  dataDir: process.env.LICHT_DATA ?? join(homedir(), '.licht'),
  ble: process.env.LICHT_BLE === '1',
  logLevel: process.env.LICHT_LOG ?? 'error', // 'info' or 'debug' for troubleshooting
});

async function enableBle(environment) {
  try {
    const { NodeJsBle } = await import('@matter/nodejs-ble');
    Ble.get = singleton(() => new NodeJsBle({ environment }));
    return true;
  } catch (err) {
    console.warn('Bluetooth nicht verfügbar (dafür einmalig: npm install @matter/nodejs-ble):', err.message);
    return false;
  }
}

// Fails with code EADDRINUSE when the port is taken (e.g. Licht is already running).
function listen(server, port) {
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, () => {
      server.off('error', reject);
      resolve();
    });
  });
}

/**
 * web: { webDir } or { webFiles }. Returns { urls, dataDir, ble, close() }.
 */
export async function startLicht({ web, ...options } = {}) {
  const { port, dataDir, ble: wantBle, logLevel } = { ...defaults(), ...options };
  Logger.level = logLevel;
  const environment = Environment.default;
  environment.vars.set('storage.path', dataDir);
  const ble = wantBle ? await enableBle(environment) : false;

  const hub = new LightHub({ environment });
  const server = createHttpServer(hub, { ...web, ble });
  await listen(server, port); // before starting Matter, so a second instance stops right away
  await hub.start();

  const urls = [
    `http://localhost:${port}`,
    ...Object.values(networkInterfaces())
      .flat()
      .filter((a) => a && a.family === 'IPv4' && !a.internal)
      .map((a) => `http://${a.address}:${port}`),
  ];
  let closed = false;
  return {
    urls,
    dataDir,
    ble,
    async close() {
      if (closed) return;
      closed = true;
      server.close();
      await hub.close();
    },
  };
}
