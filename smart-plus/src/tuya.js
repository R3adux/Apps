// Minimal client for the Tuya Cloud OpenAPI (HMAC-SHA256 request signing).
// Uses only Web Crypto + fetch, so it runs in Cloudflare Workers and Node 18+.

const encoder = new TextEncoder();

const toHex = (buf) =>
  [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');

export async function sha256Hex(text) {
  return toHex(await crypto.subtle.digest('SHA-256', encoder.encode(text)));
}

export async function hmacSha256Hex(secret, text) {
  const key = await crypto.subtle.importKey(
    'raw', encoder.encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign'],
  );
  return toHex(await crypto.subtle.sign('HMAC', key, encoder.encode(text)));
}

// Tuya signs the path with its query parameters sorted by key.
export function canonicalPath(path, query = {}) {
  const keys = Object.keys(query).filter((k) => query[k] !== undefined).sort();
  if (!keys.length) return path;
  return `${path}?${keys.map((k) => `${k}=${query[k]}`).join('&')}`;
}

export async function signRequest({ clientId, secret, accessToken = '', t, nonce = '', method, url, body = '' }) {
  const stringToSign = [method.toUpperCase(), await sha256Hex(body), '', url].join('\n');
  const sign = await hmacSha256Hex(secret, clientId + accessToken + t + nonce + stringToSign);
  return sign.toUpperCase();
}

// Error codes meaning the access token must be fetched again.
const TOKEN_ERRORS = new Set([1010, 1011, 1012]);

export class TuyaError extends Error {
  constructor(code, msg) {
    super(`Tuya ${code}: ${msg}`);
    this.code = code;
  }
}

export class TuyaClient {
  constructor({ endpoint, clientId, secret, fetchImpl = fetch, now = Date.now }) {
    this.endpoint = endpoint.replace(/\/$/, '');
    this.clientId = clientId;
    this.secret = secret;
    this.fetch = fetchImpl;
    this.now = now;
    this.token = null; // { value, expiresAt }
  }

  async #send(method, path, { query, body, accessToken = '' } = {}) {
    const url = canonicalPath(path, query);
    const payload = body === undefined ? '' : JSON.stringify(body);
    const t = String(this.now());
    const sign = await signRequest({
      clientId: this.clientId, secret: this.secret, accessToken, t, method, url, body: payload,
    });
    const headers = {
      client_id: this.clientId,
      sign,
      sign_method: 'HMAC-SHA256',
      t,
      'Content-Type': 'application/json',
    };
    if (accessToken) headers.access_token = accessToken;
    const res = await this.fetch(this.endpoint + url, {
      method, headers, body: payload || undefined,
    });
    const data = await res.json();
    if (!data.success) throw new TuyaError(data.code, data.msg);
    return data.result;
  }

  async #accessToken() {
    if (this.token && this.token.expiresAt > this.now()) return this.token.value;
    const result = await this.#send('GET', '/v1.0/token', { query: { grant_type: 1 } });
    // Renew one minute before Tuya expires the token.
    this.token = {
      value: result.access_token,
      expiresAt: this.now() + (result.expire_time - 60) * 1000,
    };
    return this.token.value;
  }

  async request(method, path, opts = {}) {
    try {
      return await this.#send(method, path, { ...opts, accessToken: await this.#accessToken() });
    } catch (err) {
      if (!(err instanceof TuyaError) || !TOKEN_ERRORS.has(err.code)) throw err;
      this.token = null;
      return this.#send(method, path, { ...opts, accessToken: await this.#accessToken() });
    }
  }

  // All devices of the app accounts (Smart Life / Tuya Smart) linked to the cloud project.
  async listDevices() {
    const devices = [];
    let lastRowKey;
    do {
      const page = await this.request('GET', '/v1.0/iot-01/associated-users/devices', {
        query: { size: 100, last_row_key: lastRowKey },
      });
      devices.push(...(page.devices ?? []));
      lastRowKey = page.has_more ? page.last_row_key : undefined;
    } while (lastRowKey);
    return devices;
  }

  specification(deviceId) {
    return this.request('GET', `/v1.0/iot-03/devices/${encodeURIComponent(deviceId)}/specification`);
  }

  status(deviceId) {
    return this.request('GET', `/v1.0/iot-03/devices/${encodeURIComponent(deviceId)}/status`);
  }

  sendCommands(deviceId, commands) {
    return this.request('POST', `/v1.0/iot-03/devices/${encodeURIComponent(deviceId)}/commands`, {
      body: { commands },
    });
  }
}
