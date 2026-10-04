// Bundles scripts/probe-entry.js like the app (same transforms and resolution rules), but
// without React Native's startup code and with Node's built-in modules passed through,
// so the result runs in Node. See scripts/probe.mjs.
const { builtinModules } = require('node:module');
const { mkdirSync, writeFileSync } = require('node:fs');
const path = require('node:path');
const base = require('../metro.config.js');

// Native React Native modules → Node-backed stand-ins (only used by probe-rn-entry.js).
const MOCKS = {
  'react-native': 'react-native.js',
  'react-native/Libraries/Utilities/PolyfillFunctions': 'polyfill-functions.js',
  'react-native-udp': 'react-native-udp.js',
  'react-native-tcp-socket': 'react-native-tcp-socket.js',
  '@react-native-community/netinfo': 'netinfo.js',
  'react-native-quick-crypto': 'quick-crypto.js',
  '@craftzdog/react-native-buffer': 'buffer.js',
  '@react-native-async-storage/async-storage': 'async-storage.js',
  'react-native-ble-plx': 'ble-plx.js',
  'react-native-polyfill-globals/src/encoding': 'empty-polyfill.js',
};

const shims = path.join(__dirname, '.probe-shims');
mkdirSync(shims, { recursive: true });
const names = [...new Set([...builtinModules, 'sqlite', 'test'].map((m) => m.replace(/^node:/, '')))];
const builtins = new Set(names.flatMap((m) => [m, `node:${m}`]));
const shimFile = (name) => path.join(shims, `${name.replace(/\//g, '_')}.js`);
// Created up front: Metro only knows files that exist when it scans the project.
for (const name of names) {
  writeFileSync(shimFile(name), `module.exports = globalThis.__probeRequire(${JSON.stringify(`node:${name}`)});\n`);
}

module.exports = {
  ...base,
  projectRoot: path.resolve(__dirname, '..'),
  resolver: {
    ...base.resolver,
    resolveRequest(context, moduleName, platform) {
      if (moduleName.startsWith('bun:')) return { type: 'empty' };
      if (MOCKS[moduleName]) return { type: 'sourceFile', filePath: path.join(__dirname, 'rn-mocks', MOCKS[moduleName]) };
      if (builtins.has(moduleName)) {
        return { type: 'sourceFile', filePath: shimFile(moduleName.replace(/^node:/, '')) };
      }
      return base.resolver.resolveRequest(context, moduleName, platform);
    },
  },
  watchFolders: [...base.watchFolders, shims],
  serializer: { ...base.serializer, getModulesRunBeforeMainModule: () => [], getPolyfills: () => [] },
};
