// Helpers for end-to-end tests: a simulated Matter lamp in its own process.
import { spawn } from 'node:child_process';
import { join } from 'node:path';
import { createInterface } from 'node:readline';

export async function waitFor(check, what, timeout = 15_000) {
  const end = Date.now() + timeout;
  for (;;) {
    const result = check();
    if (result) return result;
    if (Date.now() > end) throw new Error(`Timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 100));
  }
}

export function startVirtualLight(dir, port) {
  const child = spawn(process.execPath, [new URL('./virtual-light.mjs', import.meta.url).pathname], {
    env: { ...process.env, STORE: join(dir, 'lamp'), PORT: String(port) },
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  const lamp = { child, state: {}, ready: null };
  lamp.ready = new Promise((resolve, reject) => {
    child.on('exit', (code) => reject(new Error(`virtual light exited with ${code}`)));
    createInterface({ input: child.stdout }).on('line', (line) => {
      const msg = JSON.parse(line);
      if (msg.ready) resolve(msg.code);
      if (msg.attr) lamp.state[msg.attr] = msg.value;
    });
  });
  return lamp;
}

/** Stops a simulated lamp and waits until its process is gone. */
export function stopVirtualLight(lamp) {
  if (lamp.child.exitCode !== null) return Promise.resolve();
  return new Promise((resolve) => {
    lamp.child.once('exit', resolve);
    lamp.child.kill();
  });
}
