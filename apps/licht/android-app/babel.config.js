const matter = /node_modules[\\/]@(matter|project-chip)[\\/]/;

module.exports = {
  overrides: [
    { exclude: matter, presets: ['module:@react-native/babel-preset'] },
    {
      // matter.js has a module exporting "Commands", which React Native's view-config codegen
      // would mistake for a native component. matter.js has no native components.
      include: matter,
      presets: [['module:@react-native/babel-preset', { disableStaticViewConfigsCodegen: true }]],
    },
  ],
  // matter.js uses static class blocks, which Hermes does not support natively.
  plugins: ['@babel/plugin-transform-class-static-block'],
};
