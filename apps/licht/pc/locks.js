// matter.js marks its data folders with lock files holding the process id (matter.lock, matter.pid).
// No matter.js here: main.js uses this before loading it.

import { readFile, readdir, rm } from 'node:fs/promises';
import { join } from 'node:path';

const NOT_MATTER = new Set(['fenster', 'WebView2', 'node_modules']); // window program and old window data

async function lockFiles(dir, depth = 3) {
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  const found = [];
  for (const entry of entries) {
    if (NOT_MATTER.has(entry.name)) continue;
    const path = join(dir, entry.name);
    if (entry.isDirectory() && depth > 0) found.push(...await lockFiles(path, depth - 1));
    else if (entry.name === 'matter.lock' || entry.name === 'matter.pid') found.push(path);
  }
  return found;
}

const isRunning = (pid) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    return err.code === 'EPERM';
  }
};

/**
 * Waits (at most timeoutMs) until the processes named in the lock files have ended – used right after
 * an older Licht was asked to quit: it may still be closing its lamp data.
 */
export async function waitForLockOwners(dir, timeoutMs = 10_000) {
  const pids = new Set();
  for (const file of await lockFiles(dir)) {
    const pid = Number.parseInt(String(await readFile(file, 'utf8').catch(() => '')).trim(), 10);
    if (pid > 0 && pid !== process.pid) pids.add(pid);
  }
  const end = Date.now() + timeoutMs;
  while ([...pids].some(isRunning) && Date.now() < end) await new Promise((r) => setTimeout(r, 200));
}

// When Licht is ended abruptly (e.g. Windows shutting down), the lock files stay behind, and Windows soon
// reuses the process id – matter.js then refuses to start ("Storage is locked by another process").
// Only called once this Licht is the only one (see instance.js).
export async function removeStaleLocks(dir) {
  for (const file of await lockFiles(dir)) await rm(file, { force: true });
}

