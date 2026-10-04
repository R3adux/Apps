// The Matter controller: commissions lamps, keeps their state in sync and sends commands.
// Talks to the lamps directly in the local network – no cloud involved.
// Platform-independent: the server (Node) and the Android app both use it.

import { Environment, Logger } from '@matter/main';
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
const SHARE_WINDOW_SECONDS = 15 * 60;
const MATTER_PORT = 5540;

// "192.168.1.20" or "192.168.1.20:5540" → known address for faster discovery.
function parseAddress(text) {
  const match = String(text ?? '').trim().match(/^(\d{1,3}(?:\.\d{1,3}){3})(?::(\d+))?$/);
  if (!match) return undefined;
  return { ip: match[1], port: Number(match[2] ?? MATTER_PORT), type: 'udp' };
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
    return toLight({ id: lamp.id, name: lamp.name, online: lamp.online, attrs: lamp.attrs, features: lamp.features });
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
    const nodeId = await this.#controller.commissionNode({
      passcode,
      discovery: {
        identifierData,
        discoveryCapabilities: { onIpNetwork: true, ble: viaBle },
        knownAddress: parseAddress(ip),
        timeout: 60,
      },
      commissioning: {
        regulatoryLocation: GeneralCommissioning.RegulatoryLocationType.Indoor,
        regulatoryCountryCode: 'DE',
        wifiNetwork: viaBle ? { wifiSsid: wifi.ssid, wifiCredentials: wifi.password ?? '' } : undefined,
      },
    });
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
    if (!lamp.online) throw Object.assign(new Error('Lampe ist nicht erreichbar.'), { status: 503 });
    const commands = toCommands(change, lamp);
    for (const { cluster, command, args } of commands) {
      const client = lamp.clients[cluster];
      await (args === undefined ? client[command]() : client[command](args));
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
    const lamp = { id, nodeId: node.nodeId, node, ready: false, online: false, attrs: {}, features: {}, clients: {}, name: 'Lampe' };
    this.#lamps.set(id, lamp);

    node.events.stateChanged.on((state) => {
      lamp.online = state === NodeStates.Connected;
      if (lamp.online && node.initialized) this.#read(lamp);
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
    lamp.online = node.isConnected;
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
