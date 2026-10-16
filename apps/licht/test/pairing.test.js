import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parsePairingCode } from '../core/pairing.js';

test('manual code with or without dashes', () => {
  const expected = { passcode: 20202021, identifierData: { shortDiscriminator: 15 } };
  assert.deepEqual(parsePairingCode('34970112332'), expected);
  assert.deepEqual(parsePairingCode(' 3497-011-2332 '), expected);
  assert.deepEqual(parsePairingCode('3497 011 2332'), expected);
});

test('QR code text', () => {
  assert.deepEqual(parsePairingCode('MT:Y.K90AFN00KA0648G00'), { passcode: 20202021, identifierData: { longDiscriminator: 3840 } });
});

test('invalid codes give a German error', () => {
  assert.throws(() => parsePairingCode('1234'), /11 oder 21 Ziffern/);
  assert.throws(() => parsePairingCode('34970112331'), /ungültig/);
  assert.throws(() => parsePairingCode(''), /11 oder 21 Ziffern/);
});
