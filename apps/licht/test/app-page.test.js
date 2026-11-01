// The phone app's page in a real browser (Chrome, headless), with a simulated camera that films a QR code:
// sharing shows a readable QR code, scanning one adds all its lamps, the camera is off afterwards.
// Without the app the page uses made-up lamps (web/api.js DemoApi), so no phone and no lamp is needed.
// Needs Chrome: CHROME_PATH, or a usual install; skipped when there is none.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { shareCode } from '../core/pairing.js';
import { qrMatrix, readQr } from '../web/qr.js';
import { buildPage } from '../scripts/build-page.mjs';

const chrome = [process.env.CHROME_PATH, '/opt/pw-browsers/chromium', '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser']
  .find((path) => path && existsSync(path));
let playwright;
try {
  playwright = await import('playwright-core');
} catch {
  playwright = undefined;
}
const skip = !chrome || !playwright ? 'Chrome or playwright-core not available' : false;

// A camera picture (YUV4MPEG2, grey) with a QR code in the middle – Chrome plays it as the camera.
function cameraFilm(file, text) {
  const W = 640;
  const H = 480;
  const scale = 7;
  const rows = qrMatrix(text);
  const side = (rows.length + 8) * scale;
  const left = Math.floor((W - side) / 2);
  const top = Math.floor((H - side) / 2);
  const y = Buffer.alloc(W * H, 190);
  for (let py = 0; py < side; py++) {
    for (let px = 0; px < side; px++) {
      const dark = rows[Math.floor(py / scale) - 4]?.[Math.floor(px / scale) - 4] === true;
      y[(top + py) * W + left + px] = dark ? 25 : 240;
    }
  }
  const colour = Buffer.alloc((W / 2) * (H / 2), 128);
  const frame = Buffer.concat([Buffer.from('FRAME\n'), y, colour, colour]);
  writeFileSync(file, Buffer.concat([Buffer.from(`YUV4MPEG2 W${W} H${H} F10:1 Ip A1:1 C420jpeg\n`), frame, frame]));
}

// The drawn QR code (SVG path of the page) back into modules, then read like a camera would.
function readDrawnQr(svg) {
  const size = Number(/viewBox="0 0 (\d+) /.exec(svg)[1]);
  const rows = Array.from({ length: size }, () => Array(size).fill(false));
  for (const [, x, y, w] of svg.matchAll(/M(\d+) (\d+)h(\d+)v1h-\d+z/g)) {
    for (let i = 0; i < Number(w); i++) rows[Number(y)][Number(x) + i] = true;
  }
  const scale = 4;
  const pixels = size * scale;
  const rgba = new Uint8ClampedArray(pixels * pixels * 4);
  for (let py = 0; py < pixels; py++) {
    for (let px = 0; px < pixels; px++) {
      const v = rows[Math.floor(py / scale)][Math.floor(px / scale)] ? 0 : 255;
      rgba.set([v, v, v, 255], (py * pixels + px) * 4);
    }
  }
  return readQr(rgba, pixels, pixels);
}

test('phone app: share as QR code, add all lamps by scanning one, remove an old connected app', { skip, timeout: 120_000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'licht-page-'));
  const lamp = 'MT:Y.K90AFN00KA0648G00';
  const shared = shareCode([
    { qr: lamp, address: { ip: '192.168.178.42', port: 5540 } },
    { qr: lamp, address: { ip: '192.168.178.43', port: 5540 } },
  ]);
  cameraFilm(join(dir, 'camera.y4m'), shared);
  const html = await buildPage();
  const server = createServer((req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end(html);
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const browser = await playwright.chromium.launch({
    executablePath: chrome,
    args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-video-capture=${join(dir, 'camera.y4m')}`],
  });
  try {
    const page = await browser.newPage({ viewport: { width: 400, height: 800 } });
    const errors = [];
    page.on('pageerror', (err) => errors.push(String(err)));
    // localhost: a secure page, like the app's – the camera is only available there.
    await page.goto(`http://localhost:${server.address().port}/`);
    await page.waitForSelector('.light');
    const lampsBefore = await page.locator('.light').count();

    // Sharing all lamps: a QR code that reads back as the code Licht made.
    await page.click('#menu-btn');
    await page.click('[data-action="share-all"]');
    await page.waitForSelector('#share-qr svg', { timeout: 10_000 });
    const svg = await page.locator('#share-qr').innerHTML();
    const drawn = readDrawnQr(svg);
    assert.match(drawn, /^MT:[^*]+\*[^*]+\*[^*]+$/); // the demo's three reachable lamps
    assert.match(await page.locator('#share-skipped').innerText(), /Balkon/);
    await page.click('#share-dialog .btn.primary');

    // Adding by scanning: the camera films the code of two lamps.
    await page.click('#menu-btn');
    await page.click('#menu [data-action="add"]');
    await page.click('#scan-btn');
    await page.waitForSelector('#toast:not([hidden])', { timeout: 20_000 });
    assert.equal(await page.locator('#toast').innerText(), '2 Lampen hinzugefügt.');
    assert.equal(await page.locator('.light').count(), lampsBefore + 2);
    assert.equal(await page.evaluate(() => document.querySelector('#scan-video').srcObject), null); // camera off
    assert.equal(await page.locator('#add-dialog').evaluate((d) => d.open), false);

    // Connected apps of a lamp: the list, and an old entry removed (frees a place for another device).
    await page.locator('.light-more').first().click();
    await page.click('#light-menu [data-action="connections"]');
    await page.waitForSelector('#connections-list li');
    assert.equal(await page.locator('#connections-list li').count(), 5);
    assert.match(await page.locator('#connections-summary').innerText(), /^5 von 5 Plätzen belegt\. Die Lampe ist voll/);
    assert.equal(await page.locator('#connections-list li.own button').count(), 0); // Licht itself stays
    await page.locator('#connections-list li', { hasText: 'Licht Handy 08E1' }).locator('button').click();
    await page.click('#ask-ok');
    await page.waitForFunction(() => document.querySelectorAll('#connections-list li').length === 4, null, { timeout: 10_000 });
    assert.match(await page.locator('#connections-summary').innerText(), /^4 von 5 Plätzen belegt\.$/);
    assert.match(await page.locator('#toast').innerText(), /„Licht Handy 08E1“ wurde von „.+“ entfernt\./);
    assert.deepEqual(errors, []);
  } finally {
    await browser.close();
    server.close();
    rmSync(dir, { recursive: true, force: true });
  }
});
