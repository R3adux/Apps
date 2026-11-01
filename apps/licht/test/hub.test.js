import { test } from 'node:test';
import assert from 'node:assert/strict';
import { COMMISSIONING_TIMEOUT, commissioningError, connectionName } from '../core/hub.js';

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

test('a lamp that takes no further app says so in German, with what to do', () => {
  // The text matter.js uses (seen with a real SMART+ A60 that already had 5 apps).
  const full = commissioningError(new Error(
    'Commissioned fabrics (5) exceed supported fabrics (5). Please remove some fabrics before commissioning.'));
  assert.equal(full.status, 409);
  assert.equal(full.lampFull, true);
  assert.match(full.message, /schon mit 5 Apps verbunden/);
  assert.match(full.message, /„Verbundene Apps“/);
  const named = commissioningError(Object.assign(new Error('x'), { name: 'MaximumCommissionedFabricsReachedError' }));
  assert.equal(named.lampFull, true);
});

test('connected apps get a readable name', () => {
  assert.equal(connectionName({ label: 'Licht Handy 3F2A', vendorId: 0xfff1 }), 'Licht Handy 3F2A');
  assert.equal(connectionName({ label: '', vendorId: 0x6006 }), 'Google');
  assert.match(connectionName({ label: '', vendorId: 0xfff1 }), /ohne Namen/);
  assert.match(connectionName({ label: '', vendorId: 4489 }), /Hersteller 4489/);
});
