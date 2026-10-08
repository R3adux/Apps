// Like @react-native-community/netinfo on a phone in Wi-Fi: one IPv4 address.
const os = globalThis.__probeRequire('node:os');
const wifi = Object.values(os.networkInterfaces()).flat().find((a) => a && a.family === 'IPv4' && !a.internal);
const netinfo = {
  configure() {},
  fetch: async (type) => (type === 'wifi' && wifi
    ? { type: 'wifi', isConnected: true, details: { ipAddress: wifi.address, subnet: wifi.netmask } }
    : { type, isConnected: false, details: null }),
};
module.exports = netinfo;
module.exports.default = netinfo;
