const net = globalThis.__probeRequire('node:net');
module.exports = { createServer: net.createServer, createConnection: net.createConnection, Socket: net.Socket };
