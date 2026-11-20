// The Matter controller: commissions lamps, keeps their state in sync and sends commands.
// Talks to the lamps directly in the local network – no cloud involved.
// Platform-independent: the server (Node) and the Android app both use it.

import { Environment, Logger, Minutes, Seconds } from '@matter/main';
import { BasicInformation, ColorControl, GeneralCommissioning, LevelControl, OnOff, OperationalCredentials } from '@matter/main/clusters';
import { Invoke, PeerSet } from '@matter/main/protocol';
import { FabricIndex, NodeId, Status, StatusResponseError, ValidationError } from '@matter/main/types';
import { CommissioningController } from '@project-chip/matter.js';
import { NodeStates } from '@project-chip/matter.js/device';
import { applyChange, normalizeChange, toCommands, toLight } from './model.js';
import { parseAddress, parsePairingCodes, shareCode } from './pairing.js';

const logger = Logger.get('Licht');

const LIGHT_ATTRIBUTES = [
  'onOff', 'currentLevel', 'colorMode', 'currentHue', 'currentSaturation', 'currentX', 'currentY',
  'colorTemperatureMireds', 'colorTempPhysicalMinMireds', 'colorTempPhysicalMaxMireds',
];
const SHARE_WINDOW_SECONDS = 15 * 60; // Matter command parameter, in seconds
// matter.js durations are in milliseconds. How long to search for and pair a new lamp.
export const COMMISSIONING_TIMEOUT = Minutes(2);
// A lamp from a share code is already in the network with its window open: it is found within seconds.
const SHARED_LAMP_SEARCH = Seconds(30);
const SHARE_TIMEOUT_MS = 15_000;
// Removing another app's connection: a lamp may first try to reach that app (seen: about 11 s), then answers.
const REMOVE_CONNECTION_MS = 15_000;
const REMOVE_CHECKS = 6;
const REMOVE_CHECK_GAP_MS = 5000;

// Responsiveness. Without an answer after COMMAND_TIMEOUT_MS, the lamp counts as unreachable
// (OFFLINE_COMMAND_TIMEOUT_MS for a lamp that has been away for a while already).
export const COMMAND_TIMEOUT_MS = 8000;
const OFFLINE_COMMAND_TIMEOUT_MS = 4000;
// A lamp answers within 10-50 ms (a phone: a few hundred). No answer this long over an existing
// connection means the connection is dead – typically the lamp lost power and restarted, and no
// longer knows it. Then a new connection is opened right away and the command sent once more
// (our commands are absolute values, so repeating them is safe). matter.js alone would notice
// only after 15 s.
const FIRST_ANSWER_MS = 1500;
// Shutting down must never hang, e.g. on commands still waiting for an unreachable lamp.
const CLOSE_TIMEOUT_MS = 3000;
// Asked of the lamps as longest reporting interval (they report at least this often, also when
// nothing changed). Each report counts as a sign of life; we also check ourselves:
const KEEPALIVE_SECONDS = 30;
// Phone app in the background this long: Licht sleeps (see pause). A quick switch to another app keeps all as it is.
const SLEEP_AFTER_MS = 60_000;
// Measurement (phone app): how long to wait for the lamps to be connected again after sleeping.
const RECONNECT_WAIT_MS = 30_000;
// Every WATCHDOG_MS: reconnect lamps that are away (matter.js itself only retries every 10 minutes)
// and ask lamps that have not been heard of for LIVENESS_MS whether they are still there.
const WATCHDOG_MS = 10_000;
const LIVENESS_MS = 15_000;
const PROBE_TIMEOUT_MS = 4000;
// Don't start a new connection attempt to the same lamp more often than this.
const WAKE_THROTTLE_MS = 5000;
// A lamp that is away: restart matter.js's (ever slower) connection attempts at most this often –
// in the background, and when the user taps (see #restartConnecting).
const RESTART_CONNECTING_MS = 15_000;
const RESTART_ON_TAP_MS = 3000;
const LONG_AWAY_MS = 5 * 60_000;
const RESTART_LONG_AWAY_MS = 60_000;
// After Licht itself replaced a dead connection while sending a command, the lamp is still shown as
// connected this long – usually it is back within a second, and other screens should not flicker.
const HOLD_ONLINE_MS = 3000;
// After losing the connection, a lamp is shown as "connecting" for this long, then as unreachable.
const CONNECTING_GRACE_MS = 30_000;
// Several attribute changes in a row (e.g. a colour change) are sent as one update.
const EMIT_DELAY_MS = 15;

const CLUSTERS = { onOff: OnOff.Complete, levelControl: LevelControl.Complete, colorControl: ColorControl.Complete };

const UNREACHABLE = 'Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?';

/** Rejects with an error carrying `status` if `promise` takes longer than `ms`. */
function withTimeout(promise, ms, message, status = 503) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(Object.assign(new Error(message), { status })), ms);
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}


// The lamp answered that it already belongs to this Licht (same fabric): nothing to add.
const alreadyInLicht = (err) => err?.name === 'DeviceAlreadyCommissionedToThisFabricError'
  || /already commissioned into this fabric|device-already-commissioned-to-this-fabric/i.test(String(err?.message ?? err));

// Four characters that tell this installation apart from others (its key is created on the first start).
function installationTag(publicKey) {
  let hash = 2166136261;
  for (const byte of publicKey ?? []) hash = Math.imul(hash ^ byte, 16777619) >>> 0;
  return hash.toString(16).toUpperCase().padStart(8, '0').slice(0, 4);
}

// Makers of apps a lamp may be connected to (Matter vendor IDs), for the list of connected apps.
const VENDORS = new Map([
  [0x6006, 'Google'], [0x1349, 'Apple'], [0x1217, 'Amazon Alexa'], [0x110a, 'Samsung SmartThings'],
]);

/** What a lamp's connection (Matter "fabric") is called in the list of connected apps. */
export function connectionName({ label, vendorId }) {
  if (label) return label;
  if (VENDORS.has(vendorId)) return VENDORS.get(vendorId);
  if (vendorId >= 0xfff1 && vendorId <= 0xfff4) return 'App ohne Namen (z. B. ein älteres Licht)';
  return `App ohne Namen (Hersteller ${vendorId})`;
}

const LAMP_FULL = (used, supported) =>
  `Die Lampe ist schon mit ${used} Apps verbunden – mehr als ${supported} geht bei ihr nicht. `
  + 'Auf dem Gerät, das sie teilt, im Lampen-Menü ⋮ unter „Verbundene Apps“ eine nicht mehr benutzte Verbindung '
  + 'entfernen (z. B. von einer früheren Installation), dann neu teilen.';

// Turns matter.js commissioning errors into hints a person can act on.
export function commissioningError(err) {
  const text = String(err?.message ?? err);
  const full = /Commissioned fabrics \((\d+)\) exceed supported fabrics \((\d+)\)/i.exec(text);
  if (full || err?.name === 'MaximumCommissionedFabricsReachedError') {
    return Object.assign(new Error(LAMP_FULL(full?.[1] ?? 'zu vielen', full?.[2] ?? 'das')), { status: 409, lampFull: true });
  }
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
  #sleepTimer;
  #paused = false;
  #asleep = false;
  #fallingAsleep = Promise.resolve();
  #pauseOver = Promise.resolve('resumed'); // see pause
  #endPause = () => {};

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
    });
    await this.#controller.start();
    // The name lamps show for this Licht in their list of connected apps: with a short tag of this installation,
    // so two phones (or an old installation) can be told apart. Lamps take it over when they connect next.
    this.connectionLabel = `${this.label} ${installationTag(this.#controller.fabric.rootPublicKey)}`.slice(0, 32);
    await this.#controller.updateFabricLabel(this.connectionLabel).catch((err) => logger.warn('Fabric label', err));
    await Promise.all(this.#controller.getCommissionedNodes().map((nodeId) =>
      this.#attach(nodeId).catch((err) => logger.warn(`Could not load lamp ${nodeId}`, err))));
    this.#startWatchdog();
  }

  #startWatchdog() {
    clearInterval(this.#watchdog);
    this.#watchdog = setInterval(() => this.#checkConnections(), WATCHDOG_MS);
    this.#watchdog.unref?.();
  }

  /**
   * Phone app in the background. Right away: no checking and reconnecting while nobody looks. After
   * sleepAfter: Licht sleeps – the lamps' subscriptions end, so the lamps stop sending updates and do not
   * wake the phone every half minute. A command still works while asleep: it goes straight to that one
   * lamp, without a subscription. resume() connects everything again.
   *
   * Returns a promise for the end of this pause: 'asleep' once Licht slept, 'resumed' when resume() came
   * first. The phone app's background task waits for it – React Native only runs timers (the countdown
   * here) while such a task runs. Calling pause() again while paused keeps the running countdown.
   */
  pause({ sleepAfter = SLEEP_AFTER_MS } = {}) {
    if (this.#paused) return this.#pauseOver;
    this.#paused = true;
    clearInterval(this.#watchdog);
    this.#watchdog = undefined;
    this.#pauseOver = new Promise((resolve) => {
      this.#endPause = resolve;
      this.#sleepTimer = setTimeout(() => this.#sleep().then(() => resolve('asleep')), sleepAfter);
      this.#sleepTimer.unref?.();
    });
    return this.#pauseOver;
  }

  /** In the background (see pause) – and maybe already asleep. */
  get paused() {
    return this.#paused;
  }

  /** Sleeping (see pause). */
  get asleep() {
    return this.#asleep;
  }

  #sleep() {
    if (this.#asleep) return this.#fallingAsleep;
    this.#asleep = true;
    logger.info('Sleeping: subscriptions end');
    this.#fallingAsleep = Promise.all([...this.#lamps.values()].map((lamp) =>
      lamp.node.disconnect().catch((err) => logger.debug(`Lamp ${lamp.id}: ending the subscription failed`, err))));
    return this.#fallingAsleep;
  }

  /**
   * Phone app in the foreground again. After sleeping, all lamps are connected again; after a longer
   * time away the lamps are checked right away (see refresh); otherwise only lamps that are not connected
   * are retried.
   */
  resume({ checkAll = false } = {}) {
    this.#paused = false;
    clearTimeout(this.#sleepTimer);
    this.#endPause('resumed');
    this.#startWatchdog();
    if (this.#asleep) {
      this.#asleep = false;
      this.#fallingAsleep.then(() => {
        if (this.#asleep) return; // asleep again in the meantime
        for (const lamp of this.#lamps.values()) if (!lamp.node.isConnected) lamp.node.connect();
      });
      return;
    }
    if (checkAll) this.refresh();
    else this.wake();
  }

  /**
   * For the measurement in the phone app – exactly what happens in the background and after it, nothing
   * more: Licht falls asleep, asks each lamp once (like a tap on the widget while asleep; a small read, no
   * lamp switches), then wakes up and waits until every lamp is connected again.
   * Returns [{ name, ms | error, back }]: ms – answer while asleep; back – ms until connected again, or
   * null if not within RECONNECT_WAIT_MS.
   */
  async measureFromSleep() {
    const lamps = [...this.#lamps.values()].filter((l) => l.ready);
    await this.#sleep();
    const results = [];
    for (const lamp of lamps) {
      const started = Date.now();
      try {
        await withTimeout(lamp.clients.onOff.attributes.onOff.get(true), COMMAND_TIMEOUT_MS, UNREACHABLE);
        results.push({ name: lamp.name, ms: Date.now() - started });
      } catch (err) {
        results.push({ name: lamp.name, error: err?.message ?? String(err) });
      }
    }
    this.resume();
    const resumed = Date.now();
    const back = new Map();
    while (back.size < lamps.length && Date.now() - resumed < RECONNECT_WAIT_MS) {
      for (const lamp of lamps) if (!back.has(lamp) && this.#isConnected(lamp)) back.set(lamp, Date.now() - resumed);
      if (back.size < lamps.length) await new Promise((resolve) => setTimeout(resolve, 200));
    }
    return results.map((result, i) => ({ ...result, back: back.get(lamps[i]) ?? null }));
  }

  async close() {
    clearInterval(this.#watchdog);
    clearTimeout(this.#sleepTimer);
    this.#endPause('resumed');
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
      if (!lamp.ready) continue;
      if (!this.#isConnected(lamp)) this.#restartConnecting(lamp); // found again soon after power returns
      else if (!lamp.probing && now - lamp.lastSeen > LIVENESS_MS) this.#probe(lamp);
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
      await this.#dropConnection(lamp);
    } finally {
      lamp.probing = false;
    }
  }

  /**
   * Back in the foreground (phone app resumed): checks right away whether the lamps still answer –
   * one short read each – and reconnects only those that do not. Much lighter than reconnecting
   * everything, which re-reads all lamp data and keeps a phone busy just when the user taps.
   */
  refresh() {
    for (const lamp of this.#lamps.values()) {
      if (lamp.ready && !lamp.probing && lamp.node.connectionState === NodeStates.Connected) this.#probe(lamp);
    }
    this.wake();
  }

  /**
   * Reconnects lamps that are not connected right now (throttled per lamp).
   * force: also renew connections that look fine.
   */
  wake({ force = false, ids } = {}) {
    if (this.#asleep) return; // see pause
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
    if (lamp.node.connectionState === NodeStates.Connected || Date.now() < lamp.holdOnlineUntil) return 'online';
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
   * Adds lamps from a setup code: the code printed on a lamp, or a share code from another Licht with one
   * or several lamps (QR code text "MT:…"). One lamp after the other; progress goes to the listeners as
   * { type: 'adding', index, total } and finally { type: 'adding', done: true, total }.
   * ip: address of a single lamp, speeds up finding it; wifi: { ssid, password } – only for a
   * factory-new lamp that is set up via Bluetooth.
   * Returns { added: [ids], present: number (already in Licht), failed: [{ index, message }] }.
   */
  async add({ code, ip, wifi } = {}) {
    const entries = parsePairingCodes(code);
    const single = entries.length === 1;
    const result = { added: [], present: 0, failed: [] };
    try {
      for (const [index, entry] of entries.entries()) {
        this.#emit({ type: 'adding', index, total: entries.length });
        const address = (single ? parseAddress(ip) : undefined) ?? entry.address;
        if (address && this.#lampAt(address)) {
          result.present++;
          continue;
        }
        try {
          const search = single ? COMMISSIONING_TIMEOUT : SHARED_LAMP_SEARCH;
          result.added.push(await this.#commission(entry, { address, wifi, search }));
        } catch (err) {
          // Not recognised by its address (a PC with several network addresses), but the lamp itself says so.
          if (err.alreadyInLicht) result.present++;
          else if (single) throw err;
          else result.failed.push({ index, message: err.message });
        }
      }
    } finally {
      this.#emit({ type: 'adding', done: true, total: entries.length });
    }
    if (!result.added.length && !result.present && result.failed.length) {
      throw Object.assign(new Error(result.failed[0].message), { status: 502 });
    }
    return result;
  }

  /** Adds one lamp (see add) and returns its id. */
  async commission(options) {
    const { added, present } = await this.add(options);
    if (!added.length && present) throw Object.assign(new Error('Diese Lampe ist schon in Licht.'), { status: 409 });
    return added[0];
  }

  async #commission({ passcode, identifierData }, { address, wifi, search }) {
    const viaBle = Boolean(wifi?.ssid);
    const run = (knownAddress) => this.#controller.commissionNode({
      passcode,
      discovery: {
        identifierData,
        discoveryCapabilities: { onIpNetwork: true, ble: viaBle },
        knownAddress,
        timeout: search,
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
      if (address) {
        try {
          nodeId = await run(address);
        } catch (err) {
          if (alreadyInLicht(err)) throw err;
          // The lamp may have another address or port by now – find it via the network instead.
          logger.warn(`Commissioning via ${address.ip}:${address.port} failed, searching the network instead:`, err);
        }
      }
      nodeId ??= await run(undefined);
    } catch (err) {
      if (alreadyInLicht(err)) {
        logger.info('Lamp is already in Licht');
        throw Object.assign(new Error('Diese Lampe ist schon in Licht.'), { status: 409, alreadyInLicht: true });
      }
      logger.warn('Commissioning failed', err);
      throw commissioningError(err);
    }
    const lamp = await this.#attach(nodeId);
    await this.#whenReady(lamp, 30_000);
    this.#emit({ type: 'lights' });
    return lamp.id;
  }

  // The lamp Licht already has at this address, if any.
  #lampAt({ ip, port }) {
    return [...this.#lamps.values()].find((lamp) => this.#addresses(lamp)
      .some((a) => a.ip.toLowerCase() === ip.toLowerCase() && a.port === port));
  }

  #addresses(lamp) {
    try {
      return (lamp.node.node.state.commissioning.addresses ?? []).filter((a) => a.type === 'udp' && a.ip);
    } catch {
      return [];
    }
  }

  // The best address to hand to another app: IPv4, otherwise a routable IPv6 address.
  #shareAddress(lamp) {
    const addresses = this.#addresses(lamp);
    return addresses.find((a) => !a.ip.includes(':')) ?? addresses.find((a) => !/^fe80:/i.test(a.ip));
  }

  /**
   * Opens lamps for another app – e.g. Licht on a second phone – so it can add them without setting
   * them up again: one share code (QR code) for all of them, valid for 15 minutes.
   * ids: the lamps to share (default: all). Lamps that do not answer are left out.
   * Returns { code: 'MT:…', manual?: digits (one lamp only), lamps: [names], skipped: [names], validMinutes }.
   */
  async share(ids) {
    const lamps = ids?.length ? ids.map((id) => this.#lamp(id)) : [...this.#lamps.values()].filter((l) => l.ready);
    if (!lamps.length) throw Object.assign(new Error('Noch keine Lampe eingerichtet.'), { status: 404 });
    const results = await Promise.all(lamps.map(async (lamp) => {
      try {
        if (!this.#isConnected(lamp)) this.#restartConnecting(lamp, RESTART_ON_TAP_MS);
        // A lamp that takes no further app: no use opening it – say so instead.
        const room = await withTimeout(this.#room(lamp), SHARE_TIMEOUT_MS, UNREACHABLE);
        if (room && room.used >= room.supported) return { lamp, full: room };
        const { manualPairingCode, qrPairingCode } = await withTimeout(
          lamp.node.openEnhancedCommissioningWindow(SHARE_WINDOW_SECONDS), SHARE_TIMEOUT_MS, UNREACHABLE,
        );
        return { lamp, manual: manualPairingCode, qr: qrPairingCode, address: this.#shareAddress(lamp) };
      } catch (err) {
        logger.warn(`Sharing lamp ${lamp.id} failed`, err);
        return { lamp };
      }
    }));
    const shared = results.filter((r) => r.qr);
    const full = results.filter((r) => r.full);
    if (!shared.length) {
      if (full.length === results.length) {
        const message = lamps.length === 1
          ? LAMP_FULL(full[0].full.used, full[0].full.supported).replace('Auf dem Gerät, das sie teilt, im', 'Im')
          : 'Alle Lampen sind schon mit so vielen Apps verbunden, wie sie können. Im Lampen-Menü ⋮ unter '
            + '„Verbundene Apps“ nicht mehr benutzte Verbindungen entfernen, dann neu teilen.';
        throw Object.assign(new Error(message), { status: 409, lampFull: true });
      }
      const message = lamps.length === 1 ? UNREACHABLE : 'Keine Lampe antwortet gerade. Sind sie eingeschaltet?';
      throw Object.assign(new Error(message), { status: 503 });
    }
    return {
      code: shareCode(shared),
      manual: shared.length === 1 ? shared[0].manual : undefined,
      lamps: shared.map((r) => r.lamp.name),
      skipped: results.filter((r) => !r.qr && !r.full).map((r) => r.lamp.name),
      full: full.map((r) => r.lamp.name), // take no further app (see connections)
      validMinutes: SHARE_WINDOW_SECONDS / 60,
    };
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

  /**
   * Removes this hub from the lamp. Other apps the lamp is shared with keep working.
   * Returns { localOnly: true } if the lamp could not be reached: Licht forgot it, but the lamp
   * still lists Licht (and uses one of its few places for apps) until it is reset.
   */
  remove(id) {
    const lamp = this.#lamp(id);
    // Asked twice (e.g. a second tap while waiting for an unreachable lamp): one removal, one answer.
    lamp.removing ??= this.#remove(lamp).catch((err) => {
      lamp.removing = undefined; // failed: may be tried again
      throw err;
    });
    return lamp.removing;
  }

  async #remove(lamp) {
    const { id } = lamp;
    let localOnly = false;
    try {
      if (!this.#isConnected(lamp)) {
        // The lamp only lets us remove ourselves over a working connection: wait for it briefly.
        this.#restartConnecting(lamp, RESTART_ON_TAP_MS);
        await this.#whenConnected(lamp, 15_000);
      }
      await withTimeout(lamp.node.decommission(), 15_000, 'Lampe antwortet nicht');
    } catch (err) {
      logger.warn(`Decommissioning ${id} failed, removing locally only`, err);
      localOnly = true;
      await this.#controller.removeNode(lamp.nodeId, false);
    }
    clearTimeout(lamp.emitTimer);
    clearTimeout(lamp.offlineTimer);
    this.#lamps.delete(lamp.id);
    this.#emit({ type: 'lights' });
    return { localOnly };
  }

  #whenConnected(lamp, timeoutMs) {
    if (this.#isConnected(lamp)) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        unsubscribe();
        reject(new Error('Lampe nicht erreichbar'));
      }, timeoutMs);
      const listener = (state) => {
        if (state !== NodeStates.Connected) return;
        clearTimeout(timer);
        unsubscribe();
        resolve();
      };
      const unsubscribe = () => lamp.node.events.stateChanged.off(listener);
      lamp.node.events.stateChanged.on(listener);
    });
  }

  /**
   * The apps a lamp is connected to (Matter "fabrics") – a lamp takes only a few, often 5. Licht itself is one
   * of them (own: true). Returns { supported, entries: [{ index, name, label, vendorId, own }] }.
   */
  async connections(id) {
    const lamp = this.#lamp(id);
    const creds = this.#credentials(lamp);
    this.wake({ ids: [lamp.id] });
    const [fabrics, own, supported] = await withTimeout(Promise.all([
      creds.attributes.fabrics.get(true, false), // all of them, not only Licht's own
      creds.attributes.currentFabricIndex.get(true),
      creds.attributes.supportedFabrics.get(true),
    ]), COMMAND_TIMEOUT_MS, UNREACHABLE);
    const entries = (fabrics ?? []).map((fabric) => ({
      index: Number(fabric.fabricIndex),
      name: connectionName(fabric),
      label: fabric.label ?? '',
      vendorId: Number(fabric.vendorId),
      own: fabric.fabricIndex === own,
    }));
    entries.sort((a, b) => Number(b.own) - Number(a.own) || a.index - b.index);
    return { supported: Number(supported ?? entries.length), entries };
  }

  /** Removes another app's connection from a lamp (frees a place). Returns connections(id) afterwards. */
  async removeConnection(id, index) {
    const lamp = this.#lamp(id);
    const { entries } = await this.connections(id);
    const entry = entries.find((e) => e.index === Number(index));
    if (!entry) throw Object.assign(new Error('Diese Verbindung gibt es nicht mehr.'), { status: 404 });
    if (entry.own) {
      throw Object.assign(new Error('Das ist Licht selbst. Um die Lampe aus Licht zu nehmen: „Entfernen“ im Lampen-Menü.'), { status: 409 });
    }
    let result;
    try {
      result = await withTimeout(
        this.#credentials(lamp).commands.removeFabric({ fabricIndex: FabricIndex(entry.index) }), REMOVE_CONNECTION_MS, UNREACHABLE,
      );
    } catch (err) {
      // A lamp can be busy for a while first (it tries to reach the removed app one last time) and answer late:
      // look whether the connection is gone.
      const gone = await this.#connectionGone(id, entry.index);
      if (gone) return gone;
      throw err;
    }
    if (result?.statusCode !== undefined && result.statusCode !== OperationalCredentials.NodeOperationalCertStatus.Ok) {
      throw Object.assign(new Error(`Die Lampe hat das Entfernen abgelehnt (Code ${result.statusCode}).`), { status: 502 });
    }
    logger.info(`Lamp ${lamp.id}: connection ${entry.index} (${entry.name}) removed`);
    return this.connections(id);
  }

  // connections(id) once the connection with this index is no longer there; undefined if it stays.
  async #connectionGone(id, index) {
    for (let attempt = 0; attempt < REMOVE_CHECKS; attempt++) {
      await new Promise((resolve) => setTimeout(resolve, REMOVE_CHECK_GAP_MS));
      const now = await this.connections(id).catch(() => undefined);
      if (now && !now.entries.some((e) => e.index === index)) return now;
    }
    return undefined;
  }

  #credentials(lamp) {
    const creds = lamp.node.getRootClusterClient(OperationalCredentials.Cluster);
    if (!creds) throw Object.assign(new Error('Diese Lampe zeigt ihre Verbindungen nicht an.'), { status: 409 });
    return creds;
  }

  // How many apps the lamp has and takes, or undefined when it does not say.
  async #room(lamp) {
    const creds = lamp.node.getRootClusterClient(OperationalCredentials.Cluster);
    if (!creds) return undefined;
    const [used, supported] = await Promise.all([
      creds.attributes.commissionedFabrics.get(true), creds.attributes.supportedFabrics.get(true),
    ]);
    return used === undefined || supported === undefined ? undefined : { used: Number(used), supported: Number(supported) };
  }

  /** Stores the name on the lamp itself (Matter "node label"), so every app shows the same name. */
  async rename(id, name) {
    const lamp = this.#lamp(id);
    const label = String(name ?? '').trim().slice(0, 32);
    if (!label) throw Object.assign(new Error('Name darf nicht leer sein.'), { status: 400 });
    const info = lamp.node.getRootClusterClient(BasicInformation.Complete);
    if (!info) throw Object.assign(new Error('Diese Lampe kann keinen Namen speichern.'), { status: 409 });
    this.wake({ ids: [lamp.id] });
    await withTimeout(info.attributes.nodeLabel.set(label), COMMAND_TIMEOUT_MS, UNREACHABLE);
    lamp.name = label;
    this.#emit({ type: 'light', light: this.#toLight(lamp) });
  }

  /* ---------- Controlling ---------- */

  async update(id, change) {
    const lamp = this.#lamp(id);
    change = normalizeChange(change);
    const commands = toCommands(change, lamp);
    if (commands.length) await this.#command(lamp, commands);
    lamp.lastSeen = Date.now();
    lamp.attrs = applyChange(lamp.attrs, change, lamp.features);
    const light = this.#toLight(lamp);
    this.#emit({ type: 'light', light });
    return light;
  }

  // Sends commands so that they either arrive quickly or not at all: a command Licht has given up on
  // is cancelled, so the lamp can never switch later by itself because of an old tap.
  async #command(lamp, commands) {
    const offline = this.#status(lamp) === 'offline';
    const deadline = Date.now() + (offline ? OFFLINE_COMMAND_TIMEOUT_MS : COMMAND_TIMEOUT_MS);
    if (!this.#isConnected(lamp)) this.#restartConnecting(lamp, RESTART_ON_TAP_MS);
    for (let attempt = 1; ; attempt++) {
      const connected = this.#hasSession(lamp);
      // Over an existing connection the answer comes almost at once; otherwise allow time to connect.
      const wait = Math.min(deadline - Date.now(), connected ? FIRST_ANSWER_MS : deadline - Date.now());
      const abort = new AbortController();
      const timer = setTimeout(() => abort.abort(new Error('timeout')), wait);
      try {
        await this.#send(lamp, commands, abort.signal);
        return;
      } catch (err) {
        if (err instanceof StatusResponseError || err instanceof ValidationError) {
          // The lamp is there but answered with an error: no connection problem.
          const code = err.code;
          if ((code === Status.Busy || code === Status.ResourceExhausted) && attempt < 3) {
            await new Promise((r) => setTimeout(r, 200)); // busy for a moment: once more
            continue;
          }
          logger.warn(`Lamp ${lamp.id} refused the command`, err);
          const message = code === Status.UnsupportedAccess
            ? 'Die Lampe kennt Licht nicht mehr (zurückgesetzt oder in einer anderen App entfernt?). Bitte neu hinzufügen.'
            : 'Die Lampe hat den Befehl abgelehnt.';
          throw Object.assign(new Error(message), { status: 502 });
        }
        const left = deadline - Date.now();
        if (attempt >= 3 || left < 300) {
          logger.info(`Command for ${lamp.id} not answered:`, err.message ?? err);
          lamp.holdOnlineUntil = 0;
          this.#dropConnection(lamp);
          throw Object.assign(new Error(UNREACHABLE), { status: 503 });
        }
        // No answer over the existing connection: it is dead. Open a new one and send again.
        logger.info(`Command for ${lamp.id} not answered (${err.message ?? err}), new connection and once more`);
        await this.#dropConnection(lamp, { holdOnline: true });
      } finally {
        clearTimeout(timer);
      }
    }
  }

  async #send(lamp, commands, signal) {
    const interaction = lamp.node.node.interaction;
    for (const { cluster, command, args } of commands) {
      const request = Invoke({
        commands: [Invoke.ConcreteCommandRequest({ endpoint: lamp.endpoint, cluster: CLUSTERS[cluster], command, fields: args })],
      });
      for await (const chunks of interaction.invoke(request, { abort: signal })) {
        for (const chunk of chunks) {
          if (chunk.kind !== 'cmd-response' && chunk.status !== undefined && chunk.status !== Status.Success) {
            throw new StatusResponseError(`Lamp answered ${chunk.status}`, chunk.status, chunk.clusterStatus);
          }
        }
      }
    }
  }

  #isConnected(lamp) {
    return lamp.node.connectionState === NodeStates.Connected;
  }

  #peer(lamp) {
    return this.#controller.node.env.get(PeerSet).for(this.#controller.fabric.addressOf(lamp.nodeId));
  }

  #hasSession(lamp) {
    try {
      return this.#peer(lamp).hasSession;
    } catch {
      return false;
    }
  }

  // Forgets the (dead) connection to a lamp and starts a fresh one right away.
  // holdOnline: keep showing the lamp as connected for a moment (see HOLD_ONLINE_MS).
  async #dropConnection(lamp, { holdOnline = false } = {}) {
    if (holdOnline && this.#isConnected(lamp)) {
      lamp.holdOnlineUntil = Date.now() + HOLD_ONLINE_MS;
      setTimeout(() => this.#changed(lamp), HOLD_ONLINE_MS + 50).unref?.();
    }
    try {
      await this.#peer(lamp).disconnect(new Error('no answer'));
    } catch (err) {
      logger.debug('Dropping connection failed', err);
    }
    lamp.lastWake = Date.now();
    if (!this.#asleep) lamp.node.triggerReconnect(); // asleep: the next command connects by itself
    this.#changed(lamp);
  }

  // matter.js waits longer and longer between attempts to reach a lamp that is away (up to 2 minutes).
  // A lamp just switched on again would stay unreachable for that long: restart the attempt, so it
  // begins again with quick retries.
  #restartConnecting(lamp, minGap) {
    if (this.#asleep) return; // no attempts running while asleep; a command connects by itself
    // Away for long (unplugged): try less often.
    minGap ??= Date.now() - lamp.lostAt > LONG_AWAY_MS ? RESTART_LONG_AWAY_MS : RESTART_CONNECTING_MS;
    if (this.#hasSession(lamp) || Date.now() - lamp.lastRestart < minGap) return;
    lamp.lastRestart = Date.now();
    // Only abort the slowed-down attempt: whoever waits for the connection (the lamp's subscription,
    // or the user's command) starts a fresh one with quick retries by itself. No resubscribe here,
    // which would also write to storage each time.
    Promise.resolve()
      .then(() => this.#peer(lamp).disconnect())
      .catch((err) => logger.debug('Restarting connection attempt failed', err));
  }

  /* ---------- Connection handling ---------- */

  async #attach(nodeId) {
    const id = String(nodeId);
    const node = await this.#controller.getNode(NodeId(BigInt(id)));
    const lamp = {
      id, nodeId: node.nodeId, node, ready: false, attrs: {}, features: {}, clients: {}, name: 'Lampe',
      lastWake: 0, lastRestart: Date.now(), lastSeen: Date.now(), probing: false, holdOnlineUntil: 0,
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
    node.events.connectionAlive.on(() => { lamp.lastSeen = Date.now(); });
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
    try {
      await node.node.set({ network: { defaultSubscription: { maxIntervalCeiling: Seconds(KEEPALIVE_SECONDS) } } });
    } catch (err) {
      logger.debug('Could not set the reporting interval', err);
    }
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
