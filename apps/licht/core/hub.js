// The Matter controller: commissions lamps, keeps their state in sync and sends commands.
// Talks to the lamps directly in the local network – no cloud involved.
// Platform-independent: the server (Node) and the Android app both use it.

import { Environment, Logger, Minutes, Seconds } from '@matter/main';
import { BasicInformation, ColorControl, GeneralCommissioning, LevelControl, OnOff } from '@matter/main/clusters';
import { NodeId } from '@matter/main/types';
import { CommissioningController } from '@project-chip/matter.js';
import { NodeStates } from '@project-chip/matter.js/device';
import { applyChange, toCommands, toLight } from './model.js';
import { parsePairingCode } from './pairing.js';

const logger = Logger.get('Licht');

const LIGHT_ATTRIBUTES = [
  'onOff', 'currentLevel', 'colorMode', 'currentHue', 'currentSaturation', 'currentX', 'currentY',
  'colorTemperatureMireds', 'colorTempPhysicalMinMireds', 'colorTempPhysicalMaxMireds',
];
const SHARE_WINDOW_SECONDS = 15 * 60; // Matter command parameter, in seconds
// matter.js durations are in milliseconds. How long to search for and pair a new lamp.
export const COMMISSIONING_TIMEOUT = Minutes(2);
const MATTER_PORT = 5540;

// Responsiveness. Without an answer after COMMAND_TIMEOUT_MS, the lamp counts as unreachable.
// If a command fails earlier (e.g. the lamp restarted and dropped the old connection), the
// connection is renewed and the command sent once more – our commands are absolute values,
// so repeating them is safe.
export const COMMAND_TIMEOUT_MS = 8000;
// Shutting down must never hang, e.g. on commands still waiting for an unreachable lamp.
const CLOSE_TIMEOUT_MS = 3000;
// Asked of the lamps as reporting interval. They may choose a longer one, so we also check ourselves:
const KEEPALIVE_SECONDS = 30;
// Every WATCHDOG_MS: reconnect lamps that are away (matter.js itself only retries every 10 minutes)
// and ask lamps that have not been heard of for LIVENESS_MS whether they are still there.
const WATCHDOG_MS = 10_000;
const LIVENESS_MS = 15_000;
const PROBE_TIMEOUT_MS = 4000;
// Don't start a new connection attempt to the same lamp more often than this.
const WAKE_THROTTLE_MS = 5000;
// After losing the connection, a lamp is shown as "connecting" for this long, then as unreachable.
const CONNECTING_GRACE_MS = 30_000;
// Several attribute changes in a row (e.g. a colour change) are sent as one update.
const EMIT_DELAY_MS = 15;

/** Rejects with an error carrying `status` if `promise` takes longer than `ms`. */
function withTimeout(promise, ms, message, status = 503) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(Object.assign(new Error(message), { status })), ms);
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

// "192.168.1.20" or "192.168.1.20:5540" → known address for faster discovery.
function parseAddress(text) {
  const match = String(text ?? '').trim().match(/^(\d{1,3}(?:\.\d{1,3}){3})(?::(\d+))?$/);
  if (!match) return undefined;
  return { ip: match[1], port: Number(match[2] ?? MATTER_PORT), type: 'udp' };
}

// Turns matter.js commissioning errors into hints a person can act on.
export function commissioningError(err) {
  const text = String(err?.message ?? err);
  let hint;
  if (/No commissionable device was discovered/i.test(text)) {
    hint = 'Lampe nicht gefunden. Ist sie im Kopplungsmodus (Teilen-Code aus der anderen App oder frisch zurückgesetzt)? '
      + 'Tipp: unter „Erweitert“ die IP-Adresse der Lampe angeben.';
  } else if (/attempt\(s\) failed/i.test(text)) {
    hint = 'Lampe gefunden, aber sie lehnt den Code ab. Ist der Code richtig und noch gültig? '
      + 'Teilen-Codes laufen nach einigen Minuten ab – dann einen neuen erzeugen.';
  } else if (/Could not connect to device/i.test(text)) {
    hint = 'Die Lampe antwortet nicht auf die Kopplung. Meist ist sie gerade nicht im Kopplungsmodus: '
      + 'neuen Teilen-Code in der anderen App erzeugen und sofort eingeben. „Im Netz suchen“ zeigt, ob die Lampe bereit ist.';
  } else if (/canceled or timed out|timed? ?out/i.test(text)) {
    hint = 'Lampe gefunden, aber sie hat nicht rechtzeitig geantwortet. Bitte noch einmal versuchen, '
      + 'ggf. mit IP-Adresse unter „Erweitert“.';
  } else {
    return err;
  }
  return Object.assign(new Error(`${hint} (Details: ${text})`), { status: 502 });
}

export class LightHub {
  #controller;
  #lamps = new Map(); // id → lamp entry
  #listeners = new Set();
  #watchdog;

  constructor({ environment = Environment.default, label = 'Licht', id = 'licht' } = {}) {
    this.environment = environment;
    this.label = label;
    this.id = id;
  }

  /**
   * Starts the controller. Returns as soon as the stored lamps are loaded – connecting to them
   * happens in the background, so the lamps can be shown immediately.
   */
  async start() {
    this.#controller = new CommissioningController({
      environment: { environment: this.environment, id: this.id },
      autoConnect: false,
      adminFabricLabel: this.label,
      subscribeMaxIntervalCeilingSeconds: KEEPALIVE_SECONDS,
    });
    await this.#controller.start();
    await Promise.all(this.#controller.getCommissionedNodes().map((nodeId) =>
      this.#attach(nodeId).catch((err) => logger.warn(`Could not load lamp ${nodeId}`, err))));
    this.#watchdog = setInterval(() => this.#checkConnections(), WATCHDOG_MS);
    this.#watchdog.unref?.();
  }

  async close() {
    clearInterval(this.#watchdog);
    for (const lamp of this.#lamps.values()) {
      clearTimeout(lamp.emitTimer);
      clearTimeout(lamp.offlineTimer);
    }
    if (!this.#controller) return;
    await withTimeout(this.#controller.close(), CLOSE_TIMEOUT_MS, 'close timeout').catch((err) => {
      logger.warn('Controller did not close in time:', err.message);
    });
  }

  #checkConnections() {
    this.wake();
    const now = Date.now();
    for (const lamp of this.#lamps.values()) {
      if (lamp.ready && !lamp.probing && lamp.node.connectionState === NodeStates.Connected
        && now - lamp.lastSeen > LIVENESS_MS) {
        this.#probe(lamp);
      }
    }
  }

  // A tiny read; no answer means the connection is gone even if matter.js has not noticed yet.
  async #probe(lamp) {
    lamp.probing = true;
    try {
      await withTimeout(lamp.clients.onOff.attributes.onOff.get(true), PROBE_TIMEOUT_MS, 'no answer');
      lamp.lastSeen = Date.now();
    } catch (err) {
      logger.info(`Lamp ${lamp.id} did not answer (${err.message}), reconnecting`);
      lamp.lastSeen = Date.now();
      this.wake({ ids: [lamp.id], force: true });
    } finally {
      lamp.probing = false;
    }
  }

  /**
   * Reconnects lamps that are not connected right now (throttled per lamp).
   * force: also renew connections that look fine – e.g. after the phone app was in the
   * background, when connections may be stale without anyone having noticed yet.
   */
  wake({ force = false, ids } = {}) {
    const now = Date.now();
    for (const lamp of this.#lamps.values()) {
      if (ids && !ids.includes(lamp.id)) continue;
      const state = lamp.node.connectionState;
      if (!force && (state === NodeStates.Connected || state === NodeStates.Reconnecting)) continue;
      if (now - lamp.lastWake < WAKE_THROTTLE_MS) continue;
      lamp.lastWake = now;
      lamp.node.triggerReconnect();
      this.#changed(lamp);
    }
  }

  /** Subscribe to changes: listener({ type: 'light', light } | { type: 'lights' }). */
  subscribe(listener) {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  #emit(event) {
    for (const listener of this.#listeners) {
      try {
        listener(event);
      } catch (err) {
        logger.warn('Listener failed', err);
      }
    }
  }

  lights() {
    return [...this.#lamps.values()]
      .filter((lamp) => lamp.ready)
      .map((lamp) => this.#toLight(lamp))
      .sort((a, b) => a.name.localeCompare(b.name, 'de'));
  }

  light(id) {
    const lamp = this.#lamps.get(id);
    return lamp?.ready ? this.#toLight(lamp) : undefined;
  }

  #toLight(lamp) {
    const status = this.#status(lamp);
    return {
      ...toLight({ id: lamp.id, name: lamp.name, online: status === 'online', attrs: lamp.attrs, features: lamp.features }),
      status,
    };
  }

  // 'online', 'connecting' (connection lost less than CONNECTING_GRACE_MS ago, or never yet
  // established since start) or 'offline'. Stable: retries in the background don't make it flicker.
  #status(lamp) {
    if (lamp.node.connectionState === NodeStates.Connected) return 'online';
    return Date.now() - lamp.lostAt < CONNECTING_GRACE_MS ? 'connecting' : 'offline';
  }

  #stateChanged(lamp, state) {
    if (state === NodeStates.Connected) {
      lamp.lastSeen = Date.now();
      clearTimeout(lamp.offlineTimer);
      this.#read(lamp);
    } else if (lamp.wasConnected) {
      // Just lost: "connecting" now, "offline" once the grace period is over.
      lamp.lostAt = Date.now();
      clearTimeout(lamp.offlineTimer);
      lamp.offlineTimer = setTimeout(() => this.#changed(lamp), CONNECTING_GRACE_MS + 100);
      lamp.offlineTimer.unref?.();
    }
    lamp.wasConnected = state === NodeStates.Connected;
    this.#changed(lamp);
  }

  // Coalesces bursts of changes into one 'light' event.
  #changed(lamp) {
    if (!lamp.ready || lamp.emitTimer) return;
    lamp.emitTimer = setTimeout(() => {
      lamp.emitTimer = undefined;
      if (this.#lamps.get(lamp.id) === lamp) this.#emit({ type: 'light', light: this.#toLight(lamp) });
    }, EMIT_DELAY_MS);
  }

  #lamp(id) {
    const lamp = this.#lamps.get(String(id));
    if (!lamp?.ready) throw Object.assign(new Error('Lampe nicht gefunden.'), { status: 404 });
    return lamp;
  }

  /* ---------- Adding, sharing, removing ---------- */

  /**
   * Adds a lamp. code: the Matter setup code (printed on the lamp or a share code from another app).
   * ip: optional, speeds up finding a lamp that is already in the network.
   * wifi: { ssid, password } – only for a factory-new lamp that is set up via Bluetooth.
   */
  async commission({ code, ip, wifi } = {}) {
    const { passcode, identifierData } = parsePairingCode(code);
    const viaBle = Boolean(wifi?.ssid);
    const run = (knownAddress) => this.#controller.commissionNode({
      passcode,
      discovery: {
        identifierData,
        discoveryCapabilities: { onIpNetwork: true, ble: viaBle },
        knownAddress,
        timeout: COMMISSIONING_TIMEOUT,
      },
      commissioning: {
        regulatoryLocation: GeneralCommissioning.RegulatoryLocationType.Indoor,
        regulatoryCountryCode: 'DE',
        wifiNetwork: viaBle ? { wifiSsid: wifi.ssid, wifiCredentials: wifi.password ?? '' } : undefined,
      },
    }, {
      // We connect ourselves in #attach. matter.js would otherwise wait for an "initialized"
      // event that can fire before it starts waiting (seen on Android), and hang forever.
      connectNodeAfterCommissioning: false,
    });

    let nodeId;
    try {
      const address = parseAddress(ip);
      if (address) {
        try {
          nodeId = await run(address);
        } catch (err) {
          // The lamp may use another port than the default – find it via the network instead.
          logger.warn(`Commissioning via ${ip} failed, searching the network instead:`, err);
        }
      }
      nodeId ??= await run(undefined);
    } catch (err) {
      logger.warn('Commissioning failed', err);
      throw commissioningError(err);
    }
    const lamp = await this.#attach(nodeId);
    await this.#whenReady(lamp, 30_000);
    this.#emit({ type: 'lights' });
    return lamp.id;
  }

  /** Opens the lamp for another app (e.g. Licht on the phone). Returns a temporary setup code. */
  async share(id) {
    const lamp = this.#lamp(id);
    this.wake({ ids: [lamp.id] });
    const { manualPairingCode, qrPairingCode } = await withTimeout(
      lamp.node.openEnhancedCommissioningWindow(SHARE_WINDOW_SECONDS),
      15_000, 'Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?',
    );
    return { code: manualPairingCode, qr: qrPairingCode, validMinutes: SHARE_WINDOW_SECONDS / 60 };
  }

  /**
   * Lists Matter devices in the network that are ready to be added right now.
   * mode 1 = code printed on the lamp works, mode 2 = a share code from another app works.
   */
  async discover(seconds = 8) {
    const found = new Map();
    const devices = await this.#controller.discoverCommissionableDevices(
      {}, { onIpNetwork: true }, undefined, Seconds(seconds),
    );
    for (const device of devices) {
      found.set(device.deviceIdentifier, {
        name: device.DN || 'Matter-Gerät',
        vendorProduct: device.VP,
        discriminator: device.D,
        shortDiscriminator: device.D >> 8,
        mode: device.CM,
        addresses: (device.addresses ?? []).filter((a) => a.type === 'udp').map((a) => `${a.ip}:${a.port}`),
      });
    }
    return [...found.values()];
  }

  /** Removes this hub from the lamp. Other apps the lamp is shared with keep working. */
  async remove(id) {
    const lamp = this.#lamp(id);
    try {
      await withTimeout(lamp.node.decommission(), 15_000, 'Lampe antwortet nicht');
    } catch (err) {
      logger.warn(`Decommissioning ${id} failed, removing locally only`, err);
      await this.#controller.removeNode(lamp.nodeId, false);
    }
    clearTimeout(lamp.emitTimer);
    clearTimeout(lamp.offlineTimer);
    this.#lamps.delete(lamp.id);
    this.#emit({ type: 'lights' });
  }

  /** Stores the name on the lamp itself (Matter "node label"), so every app shows the same name. */
  async rename(id, name) {
    const lamp = this.#lamp(id);
    const label = String(name ?? '').trim().slice(0, 32);
    if (!label) throw Object.assign(new Error('Name darf nicht leer sein.'), { status: 400 });
    this.wake({ ids: [lamp.id] });
    await withTimeout(
      lamp.node.getRootClusterClient(BasicInformation.Complete)?.attributes.nodeLabel.set(label),
      COMMAND_TIMEOUT_MS, 'Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?',
    );
    lamp.name = label;
    this.#emit({ type: 'light', light: this.#toLight(lamp) });
  }

  /* ---------- Controlling ---------- */

  async update(id, change) {
    const lamp = this.#lamp(id);
    // A lamp that is not connected gets a connection attempt right away; the command waits for it.
    this.wake({ ids: [lamp.id] });
    const commands = toCommands(change, lamp);
    const send = async () => {
      for (const { cluster, command, args } of commands) {
        const client = lamp.clients[cluster];
        await (args === undefined ? client[command]() : client[command](args));
      }
    };
    const deadline = Date.now() + COMMAND_TIMEOUT_MS;
    try {
      try {
        await withTimeout(send(), COMMAND_TIMEOUT_MS, 'timeout');
      } catch (err) {
        const left = deadline - Date.now();
        if (err.message === 'timeout' || left < 1000) throw err;
        logger.info(`Command for ${id} failed, reconnecting and retrying:`, err.message);
        this.wake({ ids: [lamp.id], force: true });
        await withTimeout(send(), left, 'timeout');
      }
    } catch (err) {
      logger.warn(`Command for ${id} failed`, err);
      this.wake({ ids: [lamp.id], force: true });
      throw Object.assign(new Error('Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?'), { status: 503 });
    }
    lamp.lastSeen = Date.now();
    lamp.attrs = applyChange(lamp.attrs, change, lamp.features);
    const light = this.#toLight(lamp);
    this.#emit({ type: 'light', light });
    return light;
  }

  /* ---------- Connection handling ---------- */

  async #attach(nodeId) {
    const id = String(nodeId);
    const node = await this.#controller.getNode(NodeId(BigInt(id)));
    const lamp = {
      id, nodeId: node.nodeId, node, ready: false, attrs: {}, features: {}, clients: {}, name: 'Lampe',
      lastWake: 0, lastSeen: Date.now(), probing: false,
      // Counts as "connecting" during the first moments after start.
      lostAt: Date.now(), wasConnected: false, offlineTimer: undefined,
    };
    // Show "offline" if the first connection does not succeed within the grace period.
    lamp.offlineTimer = setTimeout(() => this.#changed(lamp), CONNECTING_GRACE_MS + 100);
    lamp.offlineTimer.unref?.();
    this.#lamps.set(id, lamp);

    // Listen before connecting, so no event is missed.
    node.events.stateChanged.on((state) => this.#stateChanged(lamp, state));
    // First from stored data (instant, maybe a bit old), then again once the lamp itself answered.
    node.events.initialized.on(() => this.#read(lamp));
    node.events.initializedFromRemote.on(() => this.#read(lamp));
    node.events.attributeChanged.on(({ path, value }) => {
      lamp.lastSeen = Date.now();
      if (path.attributeName === 'nodeLabel') {
        lamp.name = value || lamp.name;
      } else if (path.endpointId === lamp.endpoint && LIGHT_ATTRIBUTES.includes(path.attributeName)) {
        lamp.attrs[path.attributeName] = value;
      } else {
        return;
      }
      this.#changed(lamp);
    });

    if (node.initialized) this.#read(lamp);
    if (!node.isConnected) node.connect();
    return lamp;
  }

  // Reads the lamp's structure and values from the locally stored data – never from the network,
  // so it is instant and works while the lamp is switched off.
  #read(lamp) {
    const { node } = lamp;
    const device = node.getDevices().find((d) => d.getClusterClient(OnOff.Complete));
    if (!device) {
      logger.warn(`Node ${lamp.id} has no light endpoint`);
      return;
    }
    lamp.endpoint = device.number;
    const onOff = device.getClusterClient(OnOff.Complete);
    const levelControl = device.getClusterClient(LevelControl.Complete);
    const colorControl = device.getClusterClient(ColorControl.Complete);
    lamp.clients = { onOff, levelControl, colorControl };
    const colorFeatures = colorControl?.supportedFeatures ?? {};
    lamp.features = {
      level: Boolean(levelControl),
      hueSaturation: Boolean(colorFeatures.hueSaturation),
      xy: Boolean(colorFeatures.xy),
      colorTemperature: Boolean(colorFeatures.colorTemperature),
    };

    for (const client of [onOff, levelControl, colorControl]) {
      if (!client) continue;
      for (const name of LIGHT_ATTRIBUTES) {
        const attribute = client.attributes[name];
        if (!attribute) continue;
        try {
          const value = attribute.getLocal();
          if (value !== undefined) lamp.attrs[name] = value;
        } catch {
          // Attribute not stored (yet).
        }
      }
    }
    const info = node.basicInformation;
    lamp.name = info?.nodeLabel || info?.productLabel || info?.productName || lamp.name;
    if (lamp.ready) {
      this.#changed(lamp);
    } else {
      lamp.ready = true;
      lamp.readyResolve?.();
      this.#emit({ type: 'lights' });
    }
  }

  #whenReady(lamp, timeoutMs) {
    if (lamp.ready) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Lampe wurde hinzugefügt, antwortet aber nicht.')), timeoutMs);
      lamp.readyResolve = () => {
        clearTimeout(timer);
        resolve();
      };
    });
  }
}
