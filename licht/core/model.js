// Converts between Matter cluster values and the simple model the app uses:
//   { on, mode: 'white' | 'colour', brightness: 1-100, temperature: 0-100 (warm → cold),
//     color: { h: 0-360, s: 0-100 } }
// Pure functions only, so they work the same on the hub (Node) and in the Android app.

const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

// LevelControl.currentLevel: 1-254
export const levelToPercent = (level) =>
  level == null ? 100 : clamp(Math.round(1 + ((level - 1) / 253) * 99), 1, 100);
export const percentToLevel = (pct) => Math.round(1 + ((clamp(pct, 1, 100) - 1) / 99) * 253);

// ColorControl hue / saturation: 0-254
export const hueToDegrees = (hue) => Math.round((clamp(hue ?? 0, 0, 254) / 254) * 360);
export const degreesToHue = (deg) => Math.round((clamp(deg, 0, 360) / 360) * 254);
export const satToPercent = (sat) => Math.round((clamp(sat ?? 0, 0, 254) / 254) * 100);
export const percentToSat = (pct) => Math.round((clamp(pct, 0, 100) / 100) * 254);

// Colour temperature in mireds: a high value is warm (2700 K ≈ 370), a low value cold (6500 K ≈ 153).
export const DEFAULT_MIREDS = { min: 153, max: 370 };
export function miredsToPercent(mireds, { min, max } = DEFAULT_MIREDS) {
  if (mireds == null || max <= min) return 50;
  return clamp(Math.round(((max - mireds) / (max - min)) * 100), 0, 100);
}
export function percentToMireds(pct, { min, max } = DEFAULT_MIREDS) {
  return Math.round(max - (clamp(pct, 0, 100) / 100) * (max - min));
}

// Colour modes (ColorControl.colorMode)
export const COLOR_MODE = { hueSaturation: 0, xy: 1, temperature: 2 };

/* ---- CIE xy (used when a lamp only reports or accepts xy) ---- */

const gammaToLinear = (c) => (c > 0.04045 ? ((c + 0.055) / 1.055) ** 2.4 : c / 12.92);
const linearToGamma = (c) => (c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055);

export function hsvToRgb(h, s, v = 1) {
  const f = (n) => {
    const k = (n + h / 60) % 6;
    return v - v * s * Math.max(0, Math.min(k, 4 - k, 1));
  };
  return [f(5), f(3), f(1)];
}

export function rgbToHsv(r, g, b) {
  const max = Math.max(r, g, b);
  const d = max - Math.min(r, g, b);
  let h = 0;
  if (d) {
    if (max === r) h = ((g - b) / d) % 6;
    else if (max === g) h = (b - r) / d + 2;
    else h = (r - g) / d + 4;
  }
  return { h: (h * 60 + 360) % 360, s: max ? d / max : 0, v: max };
}

// Matter encodes x and y as value * 65536 (0-65279).
export function hsToXy(hDeg, sPct) {
  const [r, g, b] = hsvToRgb(hDeg, sPct / 100).map(gammaToLinear);
  const X = r * 0.4124 + g * 0.3576 + b * 0.1805;
  const Y = r * 0.2126 + g * 0.7152 + b * 0.0722;
  const Z = r * 0.0193 + g * 0.1192 + b * 0.9505;
  const sum = X + Y + Z || 1;
  return { x: Math.round((X / sum) * 65536), y: Math.round((Y / sum) * 65536) };
}

export function xyToHs(xRaw, yRaw) {
  const x = xRaw / 65536;
  const y = yRaw / 65536 || 1e-6;
  const Y = 1;
  const X = (x / y) * Y;
  const Z = ((1 - x - y) / y) * Y;
  let rgb = [
    X * 3.2406 - Y * 1.5372 - Z * 0.4986,
    -X * 0.9689 + Y * 1.8758 + Z * 0.0415,
    X * 0.0557 - Y * 0.204 + Z * 1.057,
  ].map((c) => Math.max(0, c));
  const peak = Math.max(...rgb) || 1;
  rgb = rgb.map((c) => linearToGamma(c / peak));
  const { h, s } = rgbToHsv(...rgb);
  return { h: Math.round(h), s: Math.round(s * 100) };
}

/* ---- Matter attributes → app model ---- */

// attrs: { onOff, currentLevel, colorMode, currentHue, currentSaturation, currentX, currentY,
//          colorTemperatureMireds, colorTempPhysicalMinMireds, colorTempPhysicalMaxMireds }
// features: { level, hueSaturation, xy, colorTemperature }
export function toLight({ id, name, online, attrs, features }) {
  const mireds = {
    min: attrs.colorTempPhysicalMinMireds || DEFAULT_MIREDS.min,
    max: attrs.colorTempPhysicalMaxMireds || DEFAULT_MIREDS.max,
  };
  const color =
    attrs.colorMode === COLOR_MODE.xy && attrs.currentX != null
      ? xyToHs(attrs.currentX, attrs.currentY)
      : { h: hueToDegrees(attrs.currentHue), s: satToPercent(attrs.currentSaturation ?? 254) };
  const supportsColor = Boolean(features.hueSaturation || features.xy);
  const mode = supportsColor && (!features.colorTemperature || attrs.colorMode !== COLOR_MODE.temperature)
    ? 'colour'
    : 'white';
  return {
    id,
    name,
    online,
    on: Boolean(attrs.onOff),
    mode,
    supports: {
      brightness: Boolean(features.level),
      temperature: Boolean(features.colorTemperature),
      color: supportsColor,
    },
    brightness: levelToPercent(attrs.currentLevel),
    temperature: miredsToPercent(attrs.colorTemperatureMireds, mireds),
    color,
  };
}

/* ---- app change → Matter commands ---- */

// Returns a list of { cluster, command, args } in the order they should be sent.
// Colour is set before brightness so a lamp being switched on shows the new colour right away.
export function toCommands(change, { features, attrs = {} }) {
  const commands = [];
  const always = { executeIfOff: true };
  const options = { transitionTime: 0, optionsMask: always, optionsOverride: always };

  if (change.on === false) return [{ cluster: 'onOff', command: 'off', args: undefined }];

  if (change.mode === 'colour' && change.color && (features.hueSaturation || features.xy)) {
    const { h, s } = change.color;
    if (features.hueSaturation) {
      commands.push({
        cluster: 'colorControl',
        command: 'moveToHueAndSaturation',
        args: { hue: degreesToHue(h), saturation: percentToSat(s), ...options },
      });
    } else {
      const { x, y } = hsToXy(h, s);
      commands.push({ cluster: 'colorControl', command: 'moveToColor', args: { colorX: x, colorY: y, ...options } });
    }
  } else if (change.mode === 'white' && change.temperature !== undefined && features.colorTemperature) {
    const range = {
      min: attrs.colorTempPhysicalMinMireds || DEFAULT_MIREDS.min,
      max: attrs.colorTempPhysicalMaxMireds || DEFAULT_MIREDS.max,
    };
    commands.push({
      cluster: 'colorControl',
      command: 'moveToColorTemperature',
      args: { colorTemperatureMireds: percentToMireds(change.temperature, range), ...options },
    });
  }

  if (change.brightness !== undefined && features.level) {
    // "WithOnOff" also switches the lamp on.
    commands.push({
      cluster: 'levelControl',
      command: 'moveToLevelWithOnOff',
      args: { level: percentToLevel(change.brightness), transitionTime: 0, optionsMask: {}, optionsOverride: {} },
    });
  } else if (change.on === true) {
    commands.push({ cluster: 'onOff', command: 'on', args: undefined });
  }
  return commands;
}

// Applies a change to the cached attributes right away (the lamp confirms via its subscription later).
export function applyChange(attrs, change, features) {
  const next = { ...attrs };
  for (const { cluster, command, args } of toCommands(change, { features, attrs })) {
    if (cluster === 'onOff') next.onOff = command === 'on';
    if (command === 'moveToLevelWithOnOff') Object.assign(next, { currentLevel: args.level, onOff: true });
    if (command === 'moveToHueAndSaturation') {
      Object.assign(next, { colorMode: COLOR_MODE.hueSaturation, currentHue: args.hue, currentSaturation: args.saturation });
    }
    if (command === 'moveToColor') Object.assign(next, { colorMode: COLOR_MODE.xy, currentX: args.colorX, currentY: args.colorY });
    if (command === 'moveToColorTemperature') {
      Object.assign(next, { colorMode: COLOR_MODE.temperature, colorTemperatureMireds: args.colorTemperatureMireds });
    }
  }
  return next;
}
