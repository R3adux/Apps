// Starts the Licht hub: Matter controller + web server. Used by main.js (npm start)
// and exe.js (Licht.exe).

import { readdir, rm } from 'node:fs/promises';
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

// matter.js marks its data folders with lock files holding the process id. When Licht is ended
// abruptly (closing the console window on Windows), they stay behind, and Windows soon reuses
// the process id – matter.js then refuses to start ("Storage is locked by another process").
// Only called once this instance owns the web port, i.e. no other Licht is running.
const NOT_MATTER = new Set(['WebView2', 'fenster', 'node_modules']); // window data and programs (Licht.exe)
export async function removeStaleLocks(dir, depth = 3) {
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    if (NOT_MATTER.has(entry.name)) continue;
    const path = join(dir, entry.name);
    if (entry.isDirectory() && depth > 0) await removeStaleLocks(path, depth - 1);
    else if (entry.name === 'matter.lock' || entry.name === 'matter.pid') await rm(path, { force: true });
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
 * web: { webDir } or { webFiles }; desktop: see createHttpServer;
 * listening(): called as soon as the web page can be loaded (before the lamps are connected).
 * Returns { urls, dataDir, ble, close() }.
 */
export async function startLicht({ web, listening, ...options } = {}) {
  const { port, dataDir, ble: wantBle, logLevel } = { ...defaults(), ...options };
  Logger.level = logLevel;
  const environment = Environment.default;
  environment.vars.set('storage.path', dataDir);
  const ble = wantBle ? await enableBle(environment) : false;

  const hub = new LightHub({ environment });
  // Lamp requests wait until the stored lamps are loaded (the page may already be open by then).
  let started;
  const ready = new Promise((resolve, reject) => { started = { resolve, reject }; });
  ready.catch(() => {}); // a failed start is reported by startLicht itself
  const server = createHttpServer(hub, { ...web, ble, desktop: options.desktop, ready });
  await listen(server, port); // before starting Matter, so a second instance stops right away
  listening?.();
  try {
    await removeStaleLocks(dataDir);
    await hub.start();
    started.resolve();
  } catch (err) {
    started.reject(err);
    server.close();
    throw err;
  }

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
