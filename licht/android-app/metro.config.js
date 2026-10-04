const path = require('path');
const { getDefaultConfig, mergeConfig } = require('@react-native/metro-config');

const MATTER = /^@(matter|project-chip)\//;
// "static-load": matter.js variant without dynamic import(), which Metro cannot bundle.
const MATTER_CONDITIONS = ['static-load', 'react-native', 'import', 'default'];

// The app shares the Matter logic with the PC hub (../core). All packages, also those
// imported from ../core, must come from this app's node_modules.
const config = {
  watchFolders: [path.resolve(__dirname, '../core')],
  resolver: {
    unstable_enablePackageExports: true,
    nodeModulesPaths: [path.resolve(__dirname, 'node_modules')],
    disableHierarchicalLookup: true,
    // matter.js ships ESM and CommonJS builds. Always use the ESM build, otherwise both end
    // up in the bundle and their singletons (environment, timers) get out of sync.
    resolveRequest(context, moduleName, platform) {
      const fromMatter = /[\\/]node_modules[\\/]@(matter|project-chip)[\\/]/.test(context.originModulePath);
      if (MATTER.test(moduleName) || (fromMatter && moduleName.startsWith('#'))) {
        return context.resolveRequest(
          { ...context, unstable_conditionNames: MATTER_CONDITIONS, isESMImport: true },
          moduleName,
          platform,
        );
      }
      return context.resolveRequest(context, moduleName, platform);
    },
  },
};

module.exports = mergeConfig(getDefaultConfig(__dirname), config);
