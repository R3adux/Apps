import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatAddress, parseAddress, parsePairingCode, parsePairingCodes, shareCode } from '../core/pairing.js';

test('manual code with or without dashes', () => {
  const expected = { passcode: 20202021, identifierData: { shortDiscriminator: 15 } };
  assert.deepEqual(parsePairingCode('34970112332'), expected);
  assert.deepEqual(parsePairingCode(' 3497-011-2332 '), expected);
  assert.deepEqual(parsePairingCode('3497 011 2332'), expected);
});

test('QR code text', () => {
  assert.deepEqual(parsePairingCode('MT:Y.K90AFN00KA0648G00'), {
    passcode: 20202021, identifierData: { longDiscriminator: 3840 }, address: undefined,
  });
  assert.equal(parsePairingCode('mt:y.k90afn00ka0648g00').passcode, 20202021); // typed in lower case
});

test('invalid codes give a German error', () => {
  assert.throws(() => parsePairingCode('1234'), /11 oder 21 Ziffern/);
  assert.throws(() => parsePairingCode('34970112331'), /ungültig/);
  assert.throws(() => parsePairingCode(''), /11 oder 21 Ziffern/);
  assert.throws(() => parsePairingCode('MT:KAPUTT'), (err) => err.status === 400 && /QR-Code ist ungültig/.test(err.message));
});

test('addresses: IPv4 and IPv6, with and without port', () => {
  assert.deepEqual(parseAddress('192.168.0.136'), { ip: '192.168.0.136', port: 5540, type: 'udp' });
  assert.deepEqual(parseAddress(' 192.168.0.136:5541 '), { ip: '192.168.0.136', port: 5541, type: 'udp' });
  assert.deepEqual(parseAddress('[fe80::1]:5540'), { ip: 'fe80::1', port: 5540, type: 'udp' });
  for (const bad of ['', 'lampe', '192.168.0', '192.168.0.1:0', '192.168.0.1:70000', 'fe80::1']) {
    assert.equal(parseAddress(bad), undefined, bad);
  }
  assert.equal(formatAddress({ ip: '192.168.0.136', port: 5540 }), '192.168.0.136:5540');
  assert.equal(formatAddress({ ip: 'fe80::1', port: 5540 }), '[fe80::1]:5540');
});

test('several lamps in one QR code, with their addresses', () => {
  const one = 'MT:Y.K90AFN00KA0648G00'; // passcode 20202021, discriminator 3840
  const two = shareCode([{ qr: 'MT:Y.K90AFN00KA0648G00' }]).replace(/^MT:/, 'MT:'); // unchanged without address
  assert.equal(two, one);
  const code = shareCode([
    { qr: one, address: { ip: '192.168.0.136', port: 5540 } },
    { qr: one, address: { ip: 'fe80::1', port: 5541 } },
    { qr: one },
  ]);
  assert.match(code, /^MT:[0-9A-Z.-]+\*[0-9A-Z.-]+\*[0-9A-Z.-]+$/); // QR alphanumeric characters only
  const lamps = parsePairingCodes(code);
  assert.equal(lamps.length, 3);
  assert.deepEqual(lamps.map((l) => l.passcode), [20202021, 20202021, 20202021]);
  assert.deepEqual(lamps.map((l) => l.identifierData.longDiscriminator), [3840, 3840, 3840]);
  assert.deepEqual(lamps[0].address, { ip: '192.168.0.136', port: 5540, type: 'udp' });
  assert.deepEqual(lamps[1].address, { ip: 'fe80::1', port: 5541, type: 'udp' });
  assert.equal(lamps[2].address, undefined);
  assert.throws(() => shareCode([]), /Keine Lampe/);
});

test('reads QR codes exactly like the Matter spec (500 random codes, compared with matter.js)', async () => {
  const { QrPairingCodeCodec } = await import('@matter/main/types');
  let seed = 7;
  const random = (max) => { seed = (seed * 1103515245 + 12345) % 2 ** 31; return seed % max; };
  for (let i = 0; i < 500; i++) {
    const passcode = 1 + random(99999997);
    if ([11111111, 22222222, 33333333, 44444444, 55555555, 66666666, 77777777, 88888888, 12345678, 87654321]
      .includes(passcode)) continue; // not allowed by Matter
    const payload = {
      version: 0, vendorId: random(65536), productId: random(65536), flowType: random(3),
      discoveryCapabilities: random(256), discriminator: random(4096), passcode,
    };
    const [lamp] = parsePairingCodes(QrPairingCodeCodec.encode([payload]));
    assert.equal(lamp.passcode, payload.passcode);
    assert.equal(lamp.identifierData.longDiscriminator, payload.discriminator);
  }
});
