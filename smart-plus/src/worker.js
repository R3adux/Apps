// Cloudflare Worker: serves the web app (public/) and a small JSON API under /api
// that talks to the Tuya cloud on behalf of the browser.
//
// Secrets (wrangler secret put …):  TUYA_CLIENT_ID, TUYA_CLIENT_SECRET, APP_PASSWORD
// Variable (wrangler.toml):         TUYA_ENDPOINT

import { TuyaClient, TuyaError, sha256Hex } from './tuya.js';
import { LIGHT_CATEGORIES, capabilities, toCommands, toLight } from './light.js';

// Kept per Worker isolate: the access token and device specifications rarely change.
let client = null;
const specs = new Map();

function tuya(env) {
  if (!client || client.clientId !== env.TUYA_CLIENT_ID) {
    client = new TuyaClient({
      endpoint: env.TUYA_ENDPOINT || 'https://openapi.tuyaeu.com',
      clientId: env.TUYA_CLIENT_ID,
      secret: env.TUYA_CLIENT_SECRET,
    });
    specs.clear();
  }
  return client;
}

// A missing specification is not fatal: capabilities() falls back to the status codes.
async function spec(api, deviceId) {
  if (!specs.has(deviceId)) {
    const result = await api.specification(deviceId).catch(() => null);
    if (!result) return null;
    specs.set(deviceId, result);
  }
  return specs.get(deviceId);
}

const json = (data, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' },
  });

// Compares SHA-256 digests so the comparison time does not depend on the password.
async function passwordMatches(given, expected) {
  const [a, b] = await Promise.all([sha256Hex(given), sha256Hex(expected)]);
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

async function listLights(api) {
  const devices = await api.listDevices();
  const lights = [];
  for (const device of devices) {
    const caps = capabilities(await spec(api, device.id), device.status);
    const isLight = LIGHT_CATEGORIES.has(device.category) || caps.brightCode || caps.colourCode;
    if (isLight && caps.switchCode) lights.push(toLight(device, caps));
  }
  return lights.sort((a, b) => a.name.localeCompare(b.name, 'de'));
}

async function updateLight(api, deviceId, change) {
  const deviceSpec = await spec(api, deviceId);
  const caps = capabilities(deviceSpec, deviceSpec ? [] : await api.status(deviceId));
  const commands = toCommands(change, caps);
  if (!commands.length) return json({ error: 'Keine passende Änderung für dieses Gerät.' }, 400);
  await api.sendCommands(deviceId, commands);
  return json({ ok: true });
}

async function handleApi(request, env, url) {
  if (!env.APP_PASSWORD) return json({ error: 'APP_PASSWORD ist nicht gesetzt.' }, 500);
  if (!env.TUYA_CLIENT_ID || !env.TUYA_CLIENT_SECRET) {
    return json({ error: 'TUYA_CLIENT_ID / TUYA_CLIENT_SECRET sind nicht gesetzt.' }, 500);
  }
  const auth = request.headers.get('Authorization') ?? '';
  const given = auth.startsWith('Bearer ') ? auth.slice(7) : '';
  if (!(await passwordMatches(given, env.APP_PASSWORD))) {
    return json({ error: 'Falsches Passwort.' }, 401);
  }

  const api = tuya(env);
  try {
    if (url.pathname === '/api/lights' && request.method === 'GET') {
      return json({ lights: await listLights(api) });
    }
    const match = url.pathname.match(/^\/api\/lights\/([\w-]+)$/);
    if (match && request.method === 'POST') {
      const change = await request.json().catch(() => null);
      if (!change || typeof change !== 'object') return json({ error: 'Ungültige Anfrage.' }, 400);
      return await updateLight(api, match[1], change);
    }
    return json({ error: 'Nicht gefunden.' }, 404);
  } catch (err) {
    if (err instanceof TuyaError) return json({ error: err.message, code: err.code }, 502);
    throw err;
  }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname.startsWith('/api/')) return handleApi(request, env, url);
    return env.ASSETS.fetch(request);
  },
};

// For tests only.
export function resetState() {
  client = null;
  specs.clear();
}
