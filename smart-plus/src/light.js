// Translates between Tuya light data points and the simple model the web app uses:
//   { on, mode: 'white' | 'colour', brightness: 1-100, temperature: 0-100 (warm → cold),
//     color: { h: 0-360, s: 0-100 } }
// Newer bulbs use the *_v2 codes (0-1000 ranges), older ones the v1 codes (0-255).

const SWITCH_CODES = ['switch_led', 'switch_1', 'switch'];

const V2 = {
  bright: { min: 10, max: 1000 },
  temp: { min: 0, max: 1000 },
  colour: { h: { min: 0, max: 360 }, s: { min: 0, max: 1000 }, v: { min: 10, max: 1000 } },
};
const V1 = {
  bright: { min: 25, max: 255 },
  temp: { min: 0, max: 255 },
  colour: { h: { min: 0, max: 360 }, s: { min: 0, max: 255 }, v: { min: 25, max: 255 } },
};

const parseJson = (value) => {
  if (typeof value !== 'string') return value ?? null;
  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
};

const range = (r, fallback) =>
  r && Number.isFinite(r.min) && Number.isFinite(r.max) && r.max > r.min
    ? { min: r.min, max: r.max }
    : fallback;

// Works out which data point codes and value ranges a light has, preferably from
// its Tuya specification, otherwise from the codes present in its status.
export function capabilities(spec, status = []) {
  const defs = new Map();
  for (const f of [...(spec?.functions ?? []), ...(spec?.status ?? [])]) {
    if (!defs.has(f.code)) defs.set(f.code, parseJson(f.values) ?? {});
  }
  for (const s of status) if (!defs.has(s.code)) defs.set(s.code, {});

  const pick = (...codes) => codes.find((c) => defs.has(c));
  const brightCode = pick('bright_value_v2', 'bright_value');
  const tempCode = pick('temp_value_v2', 'temp_value');
  const colourCode = pick('colour_data_v2', 'colour_data');
  const base = (code) => (code?.endsWith('_v2') ? V2 : V1);

  const colourDef = colourCode && defs.get(colourCode);
  const colourBase = base(colourCode).colour;
  return {
    switchCode: pick(...SWITCH_CODES) ?? null,
    modeCode: pick('work_mode') ?? null,
    brightCode: brightCode ?? null,
    bright: brightCode ? range(defs.get(brightCode), base(brightCode).bright) : null,
    tempCode: tempCode ?? null,
    temp: tempCode ? range(defs.get(tempCode), base(tempCode).temp) : null,
    colourCode: colourCode ?? null,
    colour: colourCode
      ? {
          h: range(colourDef.h, colourBase.h),
          s: range(colourDef.s, colourBase.s),
          v: range(colourDef.v, colourBase.v),
        }
      : null,
  };
}

const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
// Brightness: 1 % is the device minimum, 100 % its maximum.
const toPercent = (value, r) => clamp(Math.round(1 + ((value - r.min) / (r.max - r.min)) * 99), 1, 100);
const fromPercent = (pct, r) => Math.round(r.min + ((clamp(pct, 1, 100) - 1) / 99) * (r.max - r.min));
// Saturation and colour temperature: plain share of the maximum.

const scale = (value, r) => clamp(Math.round((value / r.max) * 100), 0, 100);
const unscale = (pct, r) => clamp(Math.round((clamp(pct, 0, 100) / 100) * r.max), r.min, r.max);

export function toLight(device, caps) {
  const dp = Object.fromEntries((device.status ?? []).map((s) => [s.code, s.value]));
  const colour = caps.colourCode ? parseJson(dp[caps.colourCode]) : null;
  const mode = dp[caps.modeCode] === 'colour' ? 'colour' : 'white';

  const light = {
    id: device.id,
    name: device.name,
    online: device.online !== false,
    on: caps.switchCode ? Boolean(dp[caps.switchCode]) : false,
    mode,
    supports: {
      brightness: Boolean(caps.brightCode || caps.colourCode),
      temperature: Boolean(caps.tempCode),
      color: Boolean(caps.colourCode),
    },
    brightness: 100,
    temperature: 50,
    color: { h: 30, s: 100 },
  };
  if (caps.brightCode && Number.isFinite(dp[caps.brightCode])) light.brightness = toPercent(dp[caps.brightCode], caps.bright);
  if (caps.tempCode && Number.isFinite(dp[caps.tempCode])) light.temperature = scale(dp[caps.tempCode], caps.temp);
  if (colour && Number.isFinite(colour.h)) {
    light.color = { h: clamp(Math.round(colour.h), 0, 360), s: scale(colour.s, caps.colour.s) };
    if (mode === 'colour') light.brightness = toPercent(colour.v, caps.colour.v);
  }
  return light;
}

// Builds Tuya commands from a change. The web app always sends the target `mode`
// together with brightness/temperature/color so no extra status lookup is needed.
export function toCommands(change, caps) {
  const commands = [];
  if (typeof change.on === 'boolean' && caps.switchCode) {
    commands.push({ code: caps.switchCode, value: change.on });
  }
  const touchesLight = ['brightness', 'temperature', 'color'].some((k) => change[k] !== undefined);
  if (!touchesLight) return commands;

  if (change.mode === 'colour' && caps.colourCode) {
    if (caps.modeCode) commands.push({ code: caps.modeCode, value: 'colour' });
    const { h = 0, s = 100 } = change.color ?? {};
    const { colour } = caps;
    commands.push({
      code: caps.colourCode,
      value: {
        h: clamp(Math.round(h), colour.h.min, colour.h.max),
        s: unscale(s, colour.s),
        v: fromPercent(change.brightness ?? 100, colour.v),
      },
    });
    return commands;
  }

  if (caps.modeCode) commands.push({ code: caps.modeCode, value: 'white' });
  if (change.brightness !== undefined && caps.brightCode) {
    commands.push({ code: caps.brightCode, value: fromPercent(change.brightness, caps.bright) });
  }
  if (change.temperature !== undefined && caps.tempCode) {
    commands.push({ code: caps.tempCode, value: unscale(change.temperature, caps.temp) });
  }
  return commands;
}

// Tuya category codes for lights (dj = bulb, dd = strip, xdd = ceiling, fwd = ambient, dc = string).
export const LIGHT_CATEGORIES = new Set(['dj', 'dd', 'xdd', 'fwd', 'dc', 'tgq', 'tgkg', 'gyd']);
