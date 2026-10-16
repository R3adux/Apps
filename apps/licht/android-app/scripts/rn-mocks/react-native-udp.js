// react-native-udp mirrors Node's dgram API.
// PROBE_NO_MULTICAST=1 drops mDNS (multicast), like the Android emulator behind its NAT.
const dgram = globalThis.__probeRequire('node:dgram');
const noMulticast = process.env.PROBE_NO_MULTICAST === '1';
const isMulticast = (host) => typeof host === 'string' && (host.startsWith('224.') || host.toLowerCase().startsWith('ff0'));

const udp = {
  createSocket(...args) {
    const socket = dgram.createSocket(...args);
    if (noMulticast) {
      socket.addMembership = () => {};
      const send = socket.send.bind(socket);
      socket.send = (...a) => {
        const host = a.find((x, i) => i > 0 && typeof x === 'string');
        if (isMulticast(host)) {
          a.find((x) => typeof x === 'function')?.();
          return;
        }
        send(...a);
      };
    }
    return socket;
  },
};
module.exports = udp;
module.exports.default = udp;
