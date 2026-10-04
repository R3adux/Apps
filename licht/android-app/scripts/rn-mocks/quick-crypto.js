// react-native-quick-crypto implements Node's crypto API plus WebCrypto parts.
const crypto = globalThis.__probeRequire('node:crypto');
const quick = Object.assign({}, crypto, {
  subtle: crypto.webcrypto.subtle,
  getRandomValues: (array) => crypto.webcrypto.getRandomValues(array),
});
module.exports = quick;
module.exports.default = quick;
module.exports.install = () => {};
