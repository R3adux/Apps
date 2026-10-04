// react-native-udp mirrors Node's dgram API.
const dgram = globalThis.__probeRequire('node:dgram');
const udp = { createSocket: (...args) => dgram.createSocket(...args) };
module.exports = udp;
module.exports.default = udp;
