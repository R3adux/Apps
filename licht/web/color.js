// Colour helpers for showing a lamp's current light on screen.

export function hsvToRgb(h, s, v = 1) {
  const f = (n) => {
    const k = (n + h / 60) % 6;
    return v - v * s * Math.max(0, Math.min(k, 4 - k, 1));
  };
  return [f(5), f(3), f(1)].map((c) => Math.round(c * 255));
}

const WARM = [255, 166, 77];
const NEUTRAL = [255, 241, 220];
const COLD = [207, 227, 255];

// temperature: 0 = warm white, 100 = cold white.
export function temperatureToRgb(temperature) {
  const t = Math.min(100, Math.max(0, temperature)) / 50;
  const [from, to, k] = t <= 1 ? [WARM, NEUTRAL, t] : [NEUTRAL, COLD, t - 1];
  return from.map((c, i) => Math.round(c + (to[i] - c) * k));
}

export function lampRgb(light) {
  return light.mode === 'colour'
    ? hsvToRgb(light.color.h, light.color.s / 100)
    : temperatureToRgb(light.temperature);
}

export const PRESETS = [
  { label: 'Warmweiß', mode: 'white', temperature: 0 },
  { label: 'Neutralweiß', mode: 'white', temperature: 50 },
  { label: 'Kaltweiß', mode: 'white', temperature: 100 },
  { label: 'Rot', mode: 'colour', color: { h: 0, s: 100 } },
  { label: 'Orange', mode: 'colour', color: { h: 28, s: 100 } },
  { label: 'Gelb', mode: 'colour', color: { h: 52, s: 100 } },
  { label: 'Grün', mode: 'colour', color: { h: 120, s: 100 } },
  { label: 'Türkis', mode: 'colour', color: { h: 180, s: 100 } },
  { label: 'Blau', mode: 'colour', color: { h: 230, s: 100 } },
  { label: 'Lila', mode: 'colour', color: { h: 275, s: 100 } },
  { label: 'Pink', mode: 'colour', color: { h: 320, s: 85 } },
];
