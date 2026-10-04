// Talks to the Worker API, or simulates lamps in demo mode.

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

export class HttpApi {
  constructor(password) {
    this.password = password;
  }

  async #call(method, path, body) {
    let res;
    try {
      res = await fetch(path, {
        method,
        headers: {
          Authorization: `Bearer ${this.password}`,
          ...(body ? { 'Content-Type': 'application/json' } : {}),
        },
        body: body ? JSON.stringify(body) : undefined,
      });
    } catch {
      throw new ApiError('Keine Verbindung zum Server.', 0);
    }
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new ApiError(data.error || `Fehler ${res.status}`, res.status);
    return data;
  }

  async lights() {
    return (await this.#call('GET', '/api/lights')).lights;
  }

  update(id, change) {
    return this.#call('POST', `/api/lights/${encodeURIComponent(id)}`, change);
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export class DemoApi {
  constructor() {
    const supports = { brightness: true, temperature: true, color: true };
    this.state = [
      { id: 'demo-1', name: 'Wohnzimmer', online: true, on: true, mode: 'white', brightness: 80, temperature: 20, color: { h: 30, s: 100 }, supports },
      { id: 'demo-2', name: 'Schlafzimmer', online: true, on: true, mode: 'colour', brightness: 45, temperature: 50, color: { h: 275, s: 80 }, supports },
      { id: 'demo-3', name: 'Flur', online: true, on: false, mode: 'white', brightness: 100, temperature: 60, color: { h: 0, s: 100 }, supports },
      { id: 'demo-4', name: 'Balkon', online: false, on: false, mode: 'white', brightness: 100, temperature: 50, color: { h: 0, s: 100 }, supports },
    ];
  }

  async lights() {
    await sleep(120);
    return structuredClone(this.state);
  }

  async update(id, change) {
    await sleep(120);
    const light = this.state.find((l) => l.id === id);
    if (!light?.online) throw new ApiError('Gerät ist offline.', 502);
    Object.assign(light, structuredClone(change));
    return { ok: true };
  }
}
