// The phone app's page (web/) as one self-contained HTML text: script, styles and icon inside.
// Used by the Android build (android-app/scripts/build-web.mjs) and the page test (test/app-page.test.js).
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';

const web = join(dirname(fileURLToPath(import.meta.url)), '..', 'web');

export async function buildPage() {
  const { outputFiles } = await esbuild.build({
    entryPoints: [join(web, 'app.js')],
    bundle: true,
    format: 'iife',
    target: 'chrome100',
    minify: true,
    write: false,
    logLevel: 'warning',
  });
  const script = outputFiles[0].text.replace(/<\/script/gi, '<\\/script');
  const css = readFileSync(join(web, 'style.css'), 'utf8');
  const icon = `data:image/svg+xml;base64,${readFileSync(join(web, 'icon.svg')).toString('base64')}`;
  return readFileSync(join(web, 'index.html'), 'utf8')
    .replace('<link rel="icon" href="icon.svg" type="image/svg+xml">', `<link rel="icon" href="${icon}">`)
    .replace('<link rel="stylesheet" href="style.css">', () => `<style>${css}</style>`)
    .replace('<script type="module" src="app.js"></script>', '')
    .replace('</body>', () => `<script>${script}</script>\n</body>`);
}
