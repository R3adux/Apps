// Starts the window program LichtFenster.exe (tray icon + Licht window, see pc/fenster/) and talks to it:
// JSON, one object per line, over its standard input/output. The messages are described in bridge.js.

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

/**
 * command: the program; args: its arguments. onMessage(message) for each message from it;
 * onExit(code) when it ended without being asked to (crash, or it could not start).
 * Returns { alive, send(message) → bool, stop() }.
 */
export function startWindow(command, args, { onMessage, onExit }) {
  let child;
  try {
    // Not windowsHide: that would also hide the Licht window the first time it opens.
    child = spawn(command, args, { stdio: ['pipe', 'pipe', 'pipe'] });
  } catch (err) {
    console.error('Fenster-Programm startet nicht:', err);
    queueMicrotask(() => onExit?.(null));
    return { alive: false, send: () => false, async stop() {} };
  }
  let alive = true;
  let stopping = false;
  const closed = new Promise((resolve) => child.on('close', resolve));
  child.on('error', (err) => console.error('Fenster-Programm:', err.message));
  // 'close' also follows a failed start ('exit' does not).
  child.on('close', (code) => {
    alive = false;
    if (!stopping) {
      console.warn(`Fenster-Programm beendet (${code})`);
      onExit?.(code);
    }
  });
  child.stdin.on('error', () => {}); // the program ended; noticed via 'close'
  child.stderr.on('data', (data) => console.error('Fenster:', String(data).trim()));
  createInterface({ input: child.stdout }).on('line', (line) => {
    if (!line.trim()) return;
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      console.warn('Fenster: unlesbare Nachricht', line.slice(0, 200));
      return;
    }
    try {
      onMessage(message);
    } catch (err) {
      console.error('Fenster: Nachricht nicht verarbeitet', err);
    }
  });
  return {
    get alive() {
      return alive;
    },
    send(message) {
      if (!alive || !child.stdin.writable) return false;
      child.stdin.write(`${JSON.stringify(message)}\n`);
      return true;
    },
    /** Ends the program properly (so its tray icon disappears at once), at most after 2 s. */
    async stop() {
      if (!alive) return;
      stopping = true;
      this.send({ t: 'quit' });
      const timer = setTimeout(() => child.kill(), 2000);
      await closed;
      clearTimeout(timer);
    },
  };
}
