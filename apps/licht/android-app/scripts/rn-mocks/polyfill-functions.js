module.exports = {
  polyfillGlobal(name, getValue) {
    if (!(name in globalThis)) globalThis[name] = getValue();
  },
};
