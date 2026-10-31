// Builds Licht as a single executable (Node.js "single executable application").
//
//   node scripts/build-exe.mjs            → dist/Licht.exe on Windows, dist/licht elsewhere
//   node scripts/build-exe.mjs --bundle   → only dist/licht.cjs (bundled app, runs with `node`)
//
// The executable is a copy of the running node binary with the bundled app injected,
// so build on the platform you want to run it on (CI builds the Windows .exe).
// On Windows the window program (pc/fenster/, needs the .NET SDK) is built and embedded too
// (elsewhere with --window, for checking).

import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const dist = join(root, 'dist');
const onlyBundle = process.argv.includes('--bundle');
const VERSION = '2.0.0';

rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });

// The window program (Licht window + tray icon, see pc/fenster/). Built here so one command builds everything;
// its files go into Licht.exe and are unpacked on first start (pc/windows.js installWindowFiles).
const isWindows = process.platform === 'win32';
const windowProject = join(root, 'pc', 'fenster', 'LichtFenster');
const windowOut = join(windowProject, 'bin', 'Release', 'net48');
let windowFiles = [];
if (!onlyBundle && (isWindows || process.argv.includes('--window'))) {
  rmSync(windowOut, { recursive: true, force: true });
  execFileSync('dotnet', ['build', windowProject, '-c', 'Release', '-nologo', '-v', 'quiet'], { stdio: 'inherit' });
  windowFiles = readdirSync(windowOut).filter((name) => /\.(exe|dll|config)$/i.test(name)).sort();
  for (const name of ['LichtFenster.exe', 'LichtFenster.exe.config', 'Licht.Fenster.Core.dll']) {
    if (!windowFiles.includes(name)) throw new Error(`Window program incomplete: ${name} missing`);
  }
  console.log(`Fenster-Programm: ${windowFiles.join(', ')}`);
}

const bundle = join(dist, 'licht.cjs');
await esbuild.build({
  entryPoints: [join(root, 'pc/main.js')],
  bundle: true,
  platform: 'node',
  target: 'node22',
  format: 'cjs',
  outfile: bundle,
  external: ['@matter/nodejs-ble', 'bun:sqlite'], // Bluetooth (native) and Bun-only code, not used by the .exe
  // matter.js reads import.meta.url in a few places; give it a sensible value in CommonJS.
  define: { 'import.meta.url': 'importMetaUrl', LICHT_WINDOW_FILES: JSON.stringify(windowFiles) },
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
  assets: Object.fromEntries(windowFiles.map((name) => [`fenster/${name}`, join(windowOut, name)])),
}));
execFileSync(process.execPath, ['--experimental-sea-config', seaConfig], { stdio: 'inherit' });

const exe = join(dist, isWindows ? 'Licht.exe' : 'licht');
copyFileSync(process.execPath, exe);

// Windows: lamp icon and file description instead of Node's (must happen before injecting).
if (isWindows) {
  const { rcedit } = await import('rcedit');
  const icon = join(windowProject, 'licht.ico'); // the same icon as the window and the tray
  await rcedit(exe, {
    icon,
    'file-version': VERSION,
    'product-version': VERSION,
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

// Windows: mark the program as a window program ("GUI subsystem") instead of a console
// program, so no black console window opens. Licht shows its tray icon and window instead.
if (isWindows) {
  const image = readFileSync(exe);
  const pe = image.readUInt32LE(0x3c);
  if (image.toString('latin1', pe, pe + 4) !== 'PE\0\0') throw new Error('Licht.exe: PE header not found');
  const subsystem = pe + 4 + 20 + 68; // optional header, same offset for PE32 and PE32+
  if (image.readUInt16LE(subsystem) !== 3) throw new Error('Licht.exe: unexpected subsystem'); // 3 = console
  image.writeUInt16LE(2, subsystem); // 2 = IMAGE_SUBSYSTEM_WINDOWS_GUI
  writeFileSync(exe, image);
}
console.log(`Fertig: ${exe}`);
