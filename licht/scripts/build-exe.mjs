// Builds Licht as a single executable (Node.js "single executable application").
//
//   node scripts/build-exe.mjs            → dist/Licht.exe on Windows, dist/licht elsewhere
//   node scripts/build-exe.mjs --bundle   → only dist/licht.cjs (bundled app, runs with `node`)
//
// The executable is a copy of the running node binary with the bundled app injected,
// so build on the platform you want to run it on (CI builds the Windows .exe).

import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const dist = join(root, 'dist');
const onlyBundle = process.argv.includes('--bundle');

rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });

// The web app becomes part of the bundle: { '/index.html': Buffer, … }
const webDir = join(root, 'web');
const webFiles = Object.fromEntries(
  readdirSync(webDir).map((name) => [`/${name}`, readFileSync(join(webDir, name)).toString('base64')]),
);
const webFilesPlugin = {
  name: 'licht-web-files',
  setup(build) {
    build.onResolve({ filter: /^licht:web-files$/ }, () => ({ path: 'web-files', namespace: 'licht' }));
    build.onLoad({ filter: /.*/, namespace: 'licht' }, () => ({
      contents: `const files = ${JSON.stringify(webFiles)};
        for (const key of Object.keys(files)) files[key] = Buffer.from(files[key], 'base64');
        export default files;`,
      loader: 'js',
    }));
  },
};

const bundle = join(dist, 'licht.cjs');
await esbuild.build({
  entryPoints: [join(root, 'server/exe.js')],
  bundle: true,
  platform: 'node',
  target: 'node22',
  format: 'cjs',
  outfile: bundle,
  external: ['@matter/nodejs-ble', 'bun:sqlite'], // Bluetooth (native) and Bun-only code, not used by the .exe
  plugins: [webFilesPlugin],
  // matter.js reads import.meta.url in a few places; give it a sensible value in CommonJS.
  define: { 'import.meta.url': 'importMetaUrl' },
  banner: { js: 'const importMetaUrl = require("node:url").pathToFileURL(__filename).href;' },
  logLevel: 'warning',
});
console.log(`Bundle: ${bundle}`);
if (onlyBundle) process.exit(0);

const blob = join(dist, 'licht.blob');
const seaConfig = join(dist, 'sea-config.json');
writeFileSync(seaConfig, JSON.stringify({
  main: bundle,
  output: blob,
  disableExperimentalSEAWarning: true,
  useCodeCache: false,
  useSnapshot: false,
}));
execFileSync(process.execPath, ['--experimental-sea-config', seaConfig], { stdio: 'inherit' });

const isWindows = process.platform === 'win32';
const exe = join(dist, isWindows ? 'Licht.exe' : 'licht');
copyFileSync(process.execPath, exe);

// Windows: lamp icon and file description instead of Node's (must happen before injecting).
if (isWindows) {
  const { default: pngToIco } = await import('png-to-ico');
  const { rcedit } = await import('rcedit');
  const icon = join(dist, 'licht.ico');
  writeFileSync(icon, await pngToIco(join(root, 'scripts', 'icon-256.png')));
  const version = '1.0.0';
  await rcedit(exe, {
    icon,
    'file-version': version,
    'product-version': version,
    'version-string': {
      FileDescription: 'Licht – lokale Lampensteuerung',
      ProductName: 'Licht',
      OriginalFilename: 'Licht.exe',
      InternalName: 'Licht',
      CompanyName: '',
      LegalCopyright: '',
    },
  });
}
const postject = join(root, 'node_modules', 'postject', 'dist', 'cli.js');
const args = [postject, exe, 'NODE_SEA_BLOB', blob, '--sentinel-fuse', 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2'];
if (process.platform === 'darwin') args.push('--macho-segment-name', 'NODE_SEA');
execFileSync(process.execPath, args, { stdio: 'inherit' });
console.log(`Fertig: ${exe}`);
