// Starts the Matter part of Licht on the PC: lamp data in the data folder, and the hub.
// Loaded by main.js only after the window is already on screen – this is where matter.js is loaded.

import { Environment, Logger } from '@matter/main';
import { LightHub } from '../core/hub.js';
import { dataDirectory, logLevel as defaultLogLevel } from './config.js';
import { removeStaleLocks } from './locks.js';

/** Starts the hub. Returns { hub, close() }. */
export async function startHost(options = {}) {
  const { dataDir = dataDirectory(), logLevel = defaultLogLevel() } = options;
  Logger.level = logLevel;
  const environment = Environment.default;
  environment.vars.set('storage.path', dataDir);
  await removeStaleLocks(dataDir);
  const hub = new LightHub({ environment, label: 'Licht PC' });
  await hub.start();
  let closed = false;
  return {
    hub,
    async close() {
      if (closed) return;
      closed = true;
      await hub.close();
    },
  };
}
