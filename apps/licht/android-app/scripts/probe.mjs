// npm run probe – see scripts/probe-entry.js.
// Needs the PC hub's dependencies for the simulated lamp: (cd .. && npm ci).
import { execFileSync, spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createInterface } from 'node:readline';

const require = createRequire(import.meta.url);
const dir = mkdtempSync(join(tmpdir(), 'licht-probe-'));
// node platform (default) or the app's react-native platform with stand-ins (--rn)
const entry = process.argv.includes('--rn') ? 'scripts/probe-rn-entry.js' : 'scripts/probe-entry.js';
const out = join(dir, 'probe.js');
execFileSync('npx', ['react-native', 'bundle', '--config', 'scripts/metro.probe.config.js', '--platform', 'android',
  '--dev', 'false', '--minify', 'false', '--entry-file', entry, '--bundle-output', out], { stdio: 'inherit' });

// Simulated lamp (from the PC hub's tests)
const port = 5600 + Math.floor(Math.random() * 300);
const lamp = spawn(process.execPath, ['../test/support/virtual-light.mjs'], {
  env: { ...process.env, STORE: join(dir, 'lamp'), PORT: String(port) },
  stdio: ['ignore', 'pipe', 'inherit'],
});
const code = await new Promise((resolve, reject) => {
  lamp.on('exit', (c) => reject(new Error(`simulated lamp exited (${c})`)));
  createInterface({ input: lamp.stdout }).on('line', (line) => {
    const msg = JSON.parse(line);
    if (msg.ready) resolve(msg.code);
  });
});
process.on('exit', () => lamp.kill());
console.log('probe: simulated lamp ready on port', port);

// What Hermes does not have (PROBE_KEEP_GLOBALS=1 to keep them, for narrowing down problems)
if (!process.env.PROBE_KEEP_GLOBALS) {
  delete globalThis.FinalizationRegistry;
  delete globalThis.DOMException;
  delete globalThis.SuppressedError;
  delete AbortSignal.timeout;
  delete AbortSignal.any;
}

require('../../test/support/no-ipv6.cjs'); // only active on machines without IPv6
Object.assign(process.env, { PROBE_CODE: code, PROBE_IP: `127.0.0.1:${port}`, PROBE_STORE: join(dir, 'hub') });
globalThis.__probeRequire = require;
globalThis.__DEV__ = false;
require(out);
