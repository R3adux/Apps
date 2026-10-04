// Bundles scripts/probe-entry.js like the app (same resolution rules), but without React
// Native's startup code, so the result runs in Node. Catches bundling/init problems in
// matter.js quickly – without building an APK.
const base = require('../metro.config.js');

module.exports = {
  ...base,
  projectRoot: require('path').resolve(__dirname, '..'),
  serializer: { ...base.serializer, getModulesRunBeforeMainModule: () => [], getPolyfills: () => [] },
};
