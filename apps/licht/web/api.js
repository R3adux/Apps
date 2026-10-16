// Talks to the Licht hub, or simulates lamps in demo mode.
// The Android app provides an object with the same methods.

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

export class HttpApi {
  async #call(method, path, body) {
    let res;
    try {
      res = await fetch(path, {
        method,
        headers: body ? { 'Content-Type': 'application/json' } : {},
        body: body ? JSON.stringify(body) : undefined,
      });
    } catch {
      throw new ApiError('Keine Verbindung zur Licht-Zentrale.', 0);
    }
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new ApiError(data.error || `Fehler ${res.status}`, res.status);
    return data;
  }

  async info() {
    return this.#call('GET', '/api/info');
  }

  async lights() {
    return (await this.#call('GET', '/api/lights')).lights;
  }

  update(id, change) {
    return this.#call('POST', `/api/lights/${id}`, change);
  }

  async add({ code, ip, wifi }) {
    return (await this.#call('POST', '/api/lights', { code, ip, wifi })).light;
  }

  rename(id, name) {
    return this.#call('POST', `/api/lights/${id}/name`, { name });
  }

  async discover(code) {
    return (await this.#call('GET', `/api/discover${code ? `?code=${encodeURIComponent(code)}` : ''}`)).devices;
  }

  /** PC only (Licht.exe, from this computer). */
  async setAutostart(enabled) {
    return (await this.#call('POST', '/api/autostart', { enabled })).autostart;
  }

  quit() {
    return this.#call('POST', '/api/quit');
  }

  /** Asks the hub to reconnect lamps that are not connected right now. */
  wake() {
    return this.#call('POST', '/api/wake');
  }

  share(id) {
    return this.#call('POST', `/api/lights/${id}/share`);
  }

  remove(id) {
    return this.#call('DELETE', `/api/lights/${id}`);
  }

  /** Live updates. onEvent({ type: 'lights', lights } | { type: 'light', light }), onStatus(connected). */
  subscribe(onEvent, onStatus) {
    const source = new EventSource('/api/events');
    source.onmessage = (e) => onEvent(JSON.parse(e.data));
    source.onopen = () => onStatus(true);
    source.onerror = () => onStatus(false);
    return () => source.close();
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const supports = { brightness: true, temperature: true, color: true };

export class DemoApi {
  constructor() {
    this.nextId = 5;
    this.state = [
      { id: '1', name: 'Wohnzimmer', online: true, on: true, mode: 'white', brightness: 80, temperature: 20, color: { h: 30, s: 100 }, supports },
      { id: '2', name: 'Schlafzimmer', online: true, on: true, mode: 'colour', brightness: 45, temperature: 50, color: { h: 275, s: 80 }, supports },
      { id: '3', name: 'Flur', online: true, on: false, mode: 'white', brightness: 100, temperature: 60, color: { h: 0, s: 100 }, supports },
      { id: '4', name: 'Balkon', online: false, on: false, mode: 'white', brightness: 100, temperature: 50, color: { h: 0, s: 100 }, supports },
    ];
  }

  #find(id) {
    const light = this.state.find((l) => l.id === id);
    if (!light) throw new ApiError('Lampe nicht gefunden.', 404);
    return light;
  }

  async info() {
    return { ble: true };
  }

  async lights() {
    await sleep(80);
    return structuredClone(this.state);
  }

  async update(id, change) {
    await sleep(40);
    const light = this.#find(id);
    if (!light.online) throw new ApiError('Lampe ist nicht erreichbar.', 503);
    Object.assign(light, structuredClone(change));
    return { light: structuredClone(light) };
  }

  async add({ code }) {
    await sleep(1200);
    if (String(code).replace(/\D/g, '').length < 11) throw new ApiError('Der Code muss 11 oder 21 Ziffern haben.', 400);
    const light = { id: String(this.nextId++), name: 'Neue Lampe', online: true, on: true, mode: 'white', brightness: 100, temperature: 30, color: { h: 0, s: 100 }, supports };
    this.state.push(light);
    return structuredClone(light);
  }

  async rename(id, name) {
    this.#find(id).name = name;
  }

  async wake() {}

  async discover() {
    await sleep(1500);
    return [{ name: 'SMART+ Classic A60', discriminator: 2400, shortDiscriminator: 9, mode: 2, addresses: ['192.168.0.50:5540'], matchesCode: true }];
  }

  async share() {
    await sleep(300);
    return { code: '34970112332', validMinutes: 15 };
  }

  async remove(id) {
    this.state = this.state.filter((l) => l.id !== id);
  }

  subscribe(_onEvent, onStatus) {
    onStatus(true);
    return () => {};
  }
}
