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

  constructor({ environment = Environment.default, label = 'Licht', id = 'licht' } = {}) {
    this.environment = environment;
    this.label = label;
    this.id = id;
  }

  async start() {
    this.#controller = new CommissioningController({
      environment: { environment: this.environment, id: this.id },
      autoConnect: false,
      adminFabricLabel: this.label,
    });
    await this.#controller.start();
    for (const nodeId of this.#controller.getCommissionedNodes()) {
      await this.#attach(nodeId);
    }
  }

  async close() {
    await this.#controller?.close();
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
    return toLight({ id: lamp.id, name: lamp.name, online: lamp.node.isConnected, attrs: lamp.attrs, features: lamp.features });
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
    const { manualPairingCode, qrPairingCode } = await lamp.node.openEnhancedCommissioningWindow(SHARE_WINDOW_SECONDS);
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
      await lamp.node.decommission();
    } catch (err) {
      logger.warn(`Decommissioning ${id} failed, removing locally only`, err);
      await this.#controller.removeNode(lamp.nodeId, false);
    }
    this.#lamps.delete(lamp.id);
    this.#emit({ type: 'lights' });
  }

  /** Stores the name on the lamp itself (Matter "node label"), so every app shows the same name. */
  async rename(id, name) {
    const lamp = this.#lamp(id);
    const label = String(name ?? '').trim().slice(0, 32);
    if (!label) throw Object.assign(new Error('Name darf nicht leer sein.'), { status: 400 });
    await lamp.node.getRootClusterClient(BasicInformation.Complete)?.attributes.nodeLabel.set(label);
    lamp.name = label;
    this.#emit({ type: 'light', light: this.#toLight(lamp) });
  }

  /* ---------- Controlling ---------- */

  async update(id, change) {
    const lamp = this.#lamp(id);
    const commands = toCommands(change, lamp);
    try {
      for (const { cluster, command, args } of commands) {
        const client = lamp.clients[cluster];
        await (args === undefined ? client[command]() : client[command](args));
      }
    } catch (err) {
      logger.warn(`Command for ${id} failed`, err);
      throw Object.assign(new Error('Lampe ist nicht erreichbar.'), { status: 503 });
    }
    lamp.attrs = applyChange(lamp.attrs, change, lamp.features);
    const light = this.#toLight(lamp);
    this.#emit({ type: 'light', light });
    return light;
  }

  /* ---------- Connection handling ---------- */

  async #attach(nodeId) {
    const id = String(nodeId);
    const node = await this.#controller.getNode(NodeId(BigInt(id)));
    const lamp = { id, nodeId: node.nodeId, node, ready: false, attrs: {}, features: {}, clients: {}, name: 'Lampe' };
    this.#lamps.set(id, lamp);

    node.events.stateChanged.on((state) => {
      if (state === NodeStates.Connected && node.initialized) this.#read(lamp);
      if (lamp.ready) this.#emit({ type: 'light', light: this.#toLight(lamp) });
    });
    // First from stored data (fast, maybe stale), then again once the lamp itself answered.
    node.events.initialized.on(() => this.#read(lamp));
    node.events.initializedFromRemote.on(() => this.#read(lamp));
    node.events.attributeChanged.on(({ path, value }) => {
      if (path.attributeName === 'nodeLabel') {
        lamp.name = value || lamp.name;
      } else if (path.endpointId === lamp.endpoint && LIGHT_ATTRIBUTES.includes(path.attributeName)) {
        lamp.attrs[path.attributeName] = value;
      } else {
        return;
      }
      if (lamp.ready) this.#emit({ type: 'light', light: this.#toLight(lamp) });
    });

    if (!node.isConnected) node.connect();
    if (node.initialized) await this.#read(lamp);
    return lamp;
  }

  async #read(lamp) {
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
          lamp.attrs[name] = await attribute.get();
        } catch {
          // Optional attribute the lamp doesn't have.
        }
      }
    }
    const info = node.basicInformation;
    lamp.name = info?.nodeLabel || info?.productLabel || info?.productName || lamp.name;
    const wasReady = lamp.ready;
    lamp.ready = true;
    lamp.readyResolve?.();
    this.#emit(wasReady ? { type: 'light', light: this.#toLight(lamp) } : { type: 'lights' });
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
