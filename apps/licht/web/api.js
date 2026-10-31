// Made-up lamps for the page in a plain browser (demo). The Android app provides the real API
// (window.LichtApi, see android-app/src/bridge.js) with the same methods.

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
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
    const text = String(code).trim();
    const count = /^MT:/i.test(text) ? text.split('*').length : 1;
    if (!/^MT:/i.test(text) && text.replace(/\D/g, '').length < 11) throw new ApiError('Der Code muss 11 oder 21 Ziffern haben.', 400);
    const added = [];
    for (let i = 0; i < count; i++) {
      const light = { id: String(this.nextId++), name: `Neue Lampe ${this.nextId - 5}`, online: true, on: true, mode: 'white', brightness: 100, temperature: 30, color: { h: 0, s: 100 }, supports };
      this.state.push(light);
      added.push(light.id);
    }
    return { added, present: 0, failed: [], lights: structuredClone(this.state) };
  }

  async rename(id, name) {
    this.#find(id).name = name;
  }

  async wake() {}

  async discover() {
    await sleep(1500);
    return [{ name: 'SMART+ Classic A60', discriminator: 2400, shortDiscriminator: 9, mode: 2, addresses: ['192.168.0.50:5540'], matchesCode: true }];
  }

  async share(id) {
    await sleep(900);
    const one = 'MT:Y.K90AFN00KA0648G00';
    const chosen = id ? [this.#find(id)] : this.state;
    const lamps = chosen.filter((l) => l.online).map((l) => l.name);
    if (!lamps.length) throw new ApiError('Die Lampe ist gerade nicht erreichbar.', 503);
    return {
      code: `MT:${lamps.map(() => one.slice(3)).join('*')}`,
      manual: id ? '34970112332' : undefined,
      lamps,
      skipped: chosen.filter((l) => !l.online).map((l) => l.name),
      validMinutes: 15,
    };
  }

  async remove(id) {
    this.state = this.state.filter((l) => l.id !== id);
    return { ok: true, localOnly: false };
  }

  subscribe(_onEvent, onStatus) {
    onStatus(true);
    return () => {};
  }
}
