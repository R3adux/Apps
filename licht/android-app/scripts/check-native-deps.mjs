// Android only builds native code of packages listed in package.json. A native module that
// is only a dependency of a dependency is missing at runtime ("… could not be found").
import { existsSync, readFileSync, readdirSync } from 'node:fs';

const app = JSON.parse(readFileSync('package.json', 'utf8'));
const direct = new Set(Object.keys(app.dependencies));
const packages = readdirSync('node_modules').flatMap((d) =>
  d.startsWith('@') ? readdirSync(`node_modules/${d}`).map((x) => `${d}/${x}`) : [d]);
const missing = packages.filter((p) => existsSync(`node_modules/${p}/android`) && !direct.has(p));
if (missing.length) {
  console.error(`Native modules missing in package.json dependencies: ${missing.join(', ')}`);
  process.exit(1);
}
console.log('Native modules OK');
