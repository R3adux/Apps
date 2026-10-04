// npm run probe – bundle scripts/probe-entry.js with Metro and run it in Node.
import { execFileSync } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const out = join(mkdtempSync(join(tmpdir(), 'licht-probe-')), 'probe.js');
execFileSync('npx', ['react-native', 'bundle', '--config', 'scripts/metro.probe.config.js', '--platform', 'android',
  '--dev', 'true', '--minify', 'false', '--entry-file', 'scripts/probe-entry.js', '--bundle-output', out], { stdio: 'inherit' });
globalThis.__DEV__ = true;
globalThis.window = globalThis;
createRequire(import.meta.url)(out);
