const path = require('path');
const { getDefaultConfig, mergeConfig } = require('@react-native/metro-config');

// The app shares the Matter logic with the PC hub (../core). All packages, also those
// imported from ../core, must come from this app's node_modules.
const config = {
  watchFolders: [path.resolve(__dirname, '../core')],
  resolver: {
    unstable_enablePackageExports: true,
    // "static-load": matter.js variant without dynamic import(), which Metro cannot bundle.
    unstable_conditionNames: ['static-load', 'react-native', 'import', 'require', 'default'],
    nodeModulesPaths: [path.resolve(__dirname, 'node_modules')],
    disableHierarchicalLookup: true,
  },
};

module.exports = mergeConfig(getDefaultConfig(__dirname), config);
