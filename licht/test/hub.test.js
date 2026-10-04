import { test } from 'node:test';
import assert from 'node:assert/strict';
import { COMMISSIONING_TIMEOUT, commissioningError } from '../core/hub.js';

test('commissioning waits long enough (matter.js durations are milliseconds)', () => {
  // Regression: a timeout of "60" meant 60 ms and aborted every search for a lamp.
  assert.ok(COMMISSIONING_TIMEOUT >= 60_000, `timeout is ${COMMISSIONING_TIMEOUT} ms`);
});

test('commissioning errors become German hints', () => {
  const notFound = commissioningError(new Error('[discovery] discovery of node with discriminator 9 failed: No commissionable device was discovered'));
  assert.match(notFound.message, /^Lampe nicht gefunden/);
  assert.equal(notFound.status, 502);

  const timedOut = commissioningError(new Error('failed: No device could be commissioned (1 attempt(s) started of 1 discovered, all canceled or timed out)'));
  assert.match(timedOut.message, /nicht rechtzeitig geantwortet/);

  const rejected = commissioningError(new Error('failed: No device could be commissioned (1 of 1 started attempt(s) failed, 1 discovered)'));
  assert.match(rejected.message, /lehnt den Code ab/);

  const silent = commissioningError(new Error('[peer-communication] Could not connect to device'));
  assert.match(silent.message, /nicht im Kopplungsmodus/);

  const other = new Error('something else');
  assert.equal(commissioningError(other), other);
});
