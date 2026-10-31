import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parsePairingCodes, shareCode } from '../core/pairing.js';
import { cameraProblem, qrMatrix, qrSvg, readQr } from '../web/qr.js';

// Draws the modules like a screen would (with the white quiet zone) and reads them back with the scanner.
function photo(rows, scale = 4) {
  const quiet = 4;
  const size = (rows.length + 2 * quiet) * scale;
  const rgba = new Uint8ClampedArray(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const my = Math.floor(y / scale) - quiet;
      const mx = Math.floor(x / scale) - quiet;
      const dark = rows[my]?.[mx] === true;
      rgba.set(dark ? [0, 0, 0, 255] : [255, 255, 255, 255], (y * size + x) * 4);
    }
  }
  return { rgba, size };
}

const lamp = 'MT:Y.K90AFN00KA0648G00';

test('share codes become QR codes the app can read back – one lamp and ten', () => {
  const one = shareCode([{ qr: lamp, address: { ip: '192.168.178.42', port: 5540 } }]);
  const ten = shareCode(Array.from({ length: 10 }, (_, i) => ({ qr: lamp, address: { ip: `192.168.178.${40 + i}`, port: 5540 } })));
  for (const code of [lamp, one, ten]) {
    const rows = qrMatrix(code);
    const { rgba, size } = photo(rows);
    const read = readQr(rgba, size, size);
    assert.equal(read, code);
    assert.equal(parsePairingCodes(read).length, code.split('*').length);
  }
  assert.ok(qrMatrix(ten).length <= 109, 'ten lamps still fit an easily scanned code');
});

test('the drawn QR code is black on white with a quiet zone', () => {
  const svg = qrSvg(lamp);
  const size = qrMatrix(lamp).length + 8;
  assert.match(svg, new RegExp(`viewBox="0 0 ${size} ${size}"`));
  assert.match(svg, /<rect width="\d+" height="\d+" fill="#fff"\/><path d="M\d/);
  assert.match(svg, /shape-rendering="crispEdges"/);
});

test('camera problems are explained in plain words', () => {
  assert.match(cameraProblem({ name: 'NotAllowedError' }), /Berechtigungen → Kamera/);
  assert.match(cameraProblem({ name: 'NotFoundError' }), /Keine Kamera/);
  assert.match(cameraProblem(new Error('x')), /Code stattdessen ein/);
});
