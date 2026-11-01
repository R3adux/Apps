// The calls a Licht window may make to the hub – the same for the Windows window (over a pipe) and the
// Android app (over the WebView bridge). Arguments arrive as JSON, so everything is checked here.

import { parsePairingCodes } from './pairing.js';

const invalid = (message) => Object.assign(new Error(message), { status: 400 });
const id = (value) => {
  if (typeof value !== 'string' && typeof value !== 'number') throw invalid('Lampe fehlt.');
  return String(value);
};

/** Runs one call. Resolves with a JSON-serialisable result; errors carry a German message and a status. */
export async function callHub(hub, method, args = []) {
  switch (method) {
    case 'lights':
      return hub.lights();
    case 'update':
      return { light: await hub.update(id(args[0]), args[1] ?? {}) };
    case 'add': {
      const { code, ip, wifi } = args[0] ?? {};
      const result = await hub.add({
        code: String(code ?? ''),
        ip: ip ? String(ip) : undefined,
        wifi: wifi?.ssid ? { ssid: String(wifi.ssid), password: String(wifi.password ?? '') } : undefined,
      });
      return { ...result, lights: hub.lights() };
    }
    case 'rename':
      await hub.rename(id(args[0]), args[1]);
      return { light: hub.light(id(args[0])) };
    case 'share': {
      // One lamp (id), several ([ids]) or all (nothing).
      const ids = args[0] === undefined || args[0] === null ? undefined : [args[0]].flat().map(id);
      return hub.share(ids);
    }
    case 'connections':
      return hub.connections(id(args[0]));
    case 'removeConnection': {
      const index = Number(args[1]);
      if (!Number.isInteger(index) || index < 1 || index > 254) throw invalid('Verbindung fehlt.');
      return hub.removeConnection(id(args[0]), index);
    }
    case 'remove': {
      const { localOnly } = (await hub.remove(id(args[0]))) ?? {};
      return { ok: true, localOnly: Boolean(localOnly) };
    }
    case 'discover':
      return discover(hub, args[0]);
    case 'wake':
      hub.wake();
      return { ok: true };
    case 'refresh':
      hub.refresh();
      return { ok: true };
    default:
      throw invalid(`Unbekannte Funktion: ${method}`);
  }
}

// Lamps ready to be added right now; with a code, those this code belongs to are marked.
async function discover(hub, code) {
  let wanted;
  try {
    wanted = code ? parsePairingCodes(code).map((entry) => entry.identifierData) : undefined;
  } catch {
    wanted = undefined;
  }
  return (await hub.discover()).map((device) => ({
    ...device,
    matchesCode: wanted ? wanted.some((ids) => ids.shortDiscriminator === device.shortDiscriminator
      || ids.longDiscriminator === device.discriminator) : undefined,
  }));
}
