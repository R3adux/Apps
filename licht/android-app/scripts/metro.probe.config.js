// Bundles scripts/probe-entry.js like the app (same transforms and resolution rules), but
// without React Native's startup code and with Node's built-in modules passed through,
// so the result runs in Node. See scripts/probe.mjs.
const { builtinModules } = require('node:module');
const { mkdirSync, writeFileSync } = require('node:fs');
const path = require('node:path');
const base = require('../metro.config.js');

const shims = path.join(__dirname, '.probe-shims');
mkdirSync(shims, { recursive: true });
const builtins = new Set(builtinModules.flatMap((m) => [m, `node:${m}`]).concat(['node:sqlite', 'node:test']));

module.exports = {
  ...base,
  projectRoot: path.resolve(__dirname, '..'),
  resolver: {
    ...base.resolver,
    resolveRequest(context, moduleName, platform) {
      if (moduleName.startsWith('bun:')) return { type: 'empty' };
      if (builtins.has(moduleName)) {
        const name = moduleName.replace(/^node:/, '');
        const filePath = path.join(shims, `${name.replace(/\//g, '_')}.js`);
        writeFileSync(filePath, `module.exports = globalThis.__probeRequire(${JSON.stringify(`node:${name}`)});\n`);
        return { type: 'sourceFile', filePath };
      }
      return base.resolver.resolveRequest(context, moduleName, platform);
    },
  },
  watchFolders: [...base.watchFolders, shims],
  serializer: { ...base.serializer, getModulesRunBeforeMainModule: () => [], getPolyfills: () => [] },
};
