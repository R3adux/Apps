// Test helper for machines without IPv6 (like some CI containers): turns udp6 sockets
// into inert fakes so matter.js runs on its IPv4 paths. Does nothing when IPv6 exists.
const dgram = require('dgram');
const os = require('os');
const { EventEmitter } = require('events');

const hasIpv6 = Object.values(os.networkInterfaces()).flat().some((a) => a && a.family === 'IPv6');
if (!hasIpv6 && !dgram.createSocket.__licht) {
  const original = dgram.createSocket;
  dgram.createSocket = function createSocket(opts, cb) {
    const type = typeof opts === 'string' ? opts : opts.type;
    if (type !== 'udp6') return original.apply(this, arguments);
    const socket = new EventEmitter();
    let port = 0;
    socket.bind = (p, h, done) => {
      if (typeof p === 'object' && p) { done = h; p = p.port; }
      if (typeof h === 'function') done = h;
      port = p || 40000 + Math.floor(Math.random() * 20000);
      setImmediate(() => { done?.(); socket.emit('listening'); });
      return socket;
    };
    socket.address = () => ({ address: '::', family: 'IPv6', port });
    socket.send = (...args) => { const f = args.find((x) => typeof x === 'function'); if (f) setImmediate(f); };
    for (const m of ['addMembership', 'dropMembership', 'setMulticastInterface', 'setMulticastLoopback',
      'setMulticastTTL', 'setBroadcast', 'setTTL', 'ref', 'unref', 'setRecvBufferSize', 'setSendBufferSize']) {
      socket[m] = () => socket;
    }
    socket.close = (f) => { setImmediate(() => { f?.(); socket.emit('close'); }); };
    return socket;
  };
  dgram.createSocket.__licht = true;
  // Make `import * as dgram from 'node:dgram'` see the patched function too.
  require('module').syncBuiltinESMExports();
}
