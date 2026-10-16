import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { removeStaleLocks } from '../server/app.js';

test('removes leftover matter.js lock files, keeps the data', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-locks-'));
  mkdirSync(join(dir, 'licht', 'sub'), { recursive: true });
  for (const f of ['matter.lock', 'matter.pid']) {
    writeFileSync(join(dir, 'licht', f), '1180 abc');
    writeFileSync(join(dir, 'licht', 'sub', f), '1180 abc');
  }
  writeFileSync(join(dir, 'licht', 'fabric.json'), '{}');
  await removeStaleLocks(dir);
  assert.equal(existsSync(join(dir, 'licht', 'matter.lock')), false);
  assert.equal(existsSync(join(dir, 'licht', 'sub', 'matter.pid')), false);
  assert.equal(existsSync(join(dir, 'licht', 'fabric.json')), true);
  await removeStaleLocks(join(dir, 'missing')); // no error for a missing folder
});
