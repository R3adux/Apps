// Makes desktop/licht.ico (tray, window and Licht.exe icon) from web/icon.svg:
// small sizes as 32-bit bitmaps (shown correctly everywhere), 256 px as PNG.
// Needs Playwright with Chromium (e.g. npx playwright install chromium); run: node scripts/make-icon.mjs
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const pw = await import('playwright');
const svg = readFileSync(`${root}web/icon.svg`, 'utf8');
const sizes = [16, 20, 24, 32, 40, 48, 64, 256];
const browser = await pw.chromium.launch();
const page = await browser.newPage();
await page.setContent('<html><body></body></html>');
const images = [];
for (const s of sizes) {
  const result = await page.evaluate(async ({ svg, s }) => {
    const img = new Image();
    img.src = 'data:image/svg+xml;base64,' + btoa(svg);
    await img.decode();
    const c = document.createElement('canvas'); c.width = s; c.height = s;
    const ctx = c.getContext('2d'); ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(img, 0, 0, s, s);
    const data = Array.from(ctx.getImageData(0, 0, s, s).data);
    const png = c.toDataURL('image/png').split(',')[1];
    return { data, png };
  }, { svg, s });
  if (s >= 256) images.push(Buffer.from(result.png, 'base64'));
  else {
    const px = result.data;
    const header = Buffer.alloc(40);
    header.writeUInt32LE(40, 0); header.writeInt32LE(s, 4); header.writeInt32LE(s * 2, 8);
    header.writeUInt16LE(1, 12); header.writeUInt16LE(32, 14);
    const xor = Buffer.alloc(s * s * 4);
    for (let y = 0; y < s; y++) for (let x = 0; x < s; x++) {
      const src = ((s - 1 - y) * s + x) * 4, dst = (y * s + x) * 4;
      xor[dst] = px[src + 2]; xor[dst + 1] = px[src + 1]; xor[dst + 2] = px[src]; xor[dst + 3] = px[src + 3];
    }
    const rowBytes = Math.ceil(s / 32) * 4;
    const and = Buffer.alloc(rowBytes * s); // all 0: alpha channel decides
    images.push(Buffer.concat([header, xor, and]));
  }
}
await browser.close();
const head = Buffer.alloc(6); head.writeUInt16LE(1, 2); head.writeUInt16LE(sizes.length, 4);
let offset = 6 + 16 * sizes.length;
const dir = sizes.map((s, i) => {
  const e = Buffer.alloc(16);
  e[0] = s >= 256 ? 0 : s; e[1] = s >= 256 ? 0 : s;
  e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6); e.writeUInt32LE(images[i].length, 8); e.writeUInt32LE(offset, 12);
  offset += images[i].length; return e;
});
const ico = Buffer.concat([head, ...dir, ...images]);
writeFileSync(`${root}desktop/licht.ico`, ico);
console.log('ico bytes', ico.length);
