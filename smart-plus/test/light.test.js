import { test } from 'node:test';
import assert from 'node:assert/strict';
import { capabilities, toCommands, toLight } from '../src/light.js';

// Specification as returned by Tuya for a LEDVANCE SMART+ WiFi RGBW bulb (category dj).
const spec = {
  category: 'dj',
  functions: [
    { code: 'switch_led', type: 'Boolean', values: '{}' },
    { code: 'work_mode', type: 'Enum', values: '{"range":["white","colour","scene","music"]}' },
    { code: 'bright_value_v2', type: 'Integer', values: '{"min":10,"max":1000,"scale":0,"step":1}' },
    { code: 'temp_value_v2', type: 'Integer', values: '{"min":0,"max":1000,"scale":0,"step":1}' },
    { code: 'colour_data_v2', type: 'Json', values: '{"h":{"min":0,"scale":0,"unit":"","max":360,"step":1},"s":{"min":0,"scale":0,"unit":"","max":1000,"step":1},"v":{"min":0,"scale":0,"unit":"","max":1000,"step":1}}' },
  ],
};

const device = (status, extra = {}) => ({ id: 'bf1', name: 'Wohnzimmer', online: true, category: 'dj', status, ...extra });

test('capabilities reads codes and ranges from the specification', () => {
  const caps = capabilities(spec);
  assert.equal(caps.switchCode, 'switch_led');
  assert.equal(caps.brightCode, 'bright_value_v2');
  assert.deepEqual(caps.bright, { min: 10, max: 1000 });
  assert.deepEqual(caps.colour.v, { min: 0, max: 1000 });
});

test('capabilities falls back to status codes with v1 defaults', () => {
  const caps = capabilities(null, [{ code: 'switch_led' }, { code: 'bright_value' }, { code: 'colour_data' }]);
  assert.deepEqual(caps.bright, { min: 25, max: 255 });
  assert.deepEqual(caps.colour.s, { min: 0, max: 255 });
  assert.equal(caps.tempCode, null);
});

test('toLight maps a white-mode bulb', () => {
  const light = toLight(device([
    { code: 'switch_led', value: true },
    { code: 'work_mode', value: 'white' },
    { code: 'bright_value_v2', value: 1000 },
    { code: 'temp_value_v2', value: 250 },
    { code: 'colour_data_v2', value: '{"h":120,"s":500,"v":300}' },
  ]), capabilities(spec));
  assert.deepEqual(
    { on: light.on, mode: light.mode, brightness: light.brightness, temperature: light.temperature, color: light.color },
    { on: true, mode: 'white', brightness: 100, temperature: 25, color: { h: 120, s: 50 } },
  );
  assert.deepEqual(light.supports, { brightness: true, temperature: true, color: true });
});

test('toLight takes brightness from the colour value in colour mode', () => {
  const light = toLight(device([
    { code: 'switch_led', value: false },
    { code: 'work_mode', value: 'colour' },
    { code: 'bright_value_v2', value: 1000 },
    { code: 'colour_data_v2', value: { h: 240, s: 1000, v: 0 } },
  ], { online: false }), capabilities(spec));
  assert.equal(light.mode, 'colour');
  assert.equal(light.brightness, 1);
  assert.equal(light.online, false);
  assert.equal(light.on, false);
});

test('toCommands: switching only', () => {
  assert.deepEqual(toCommands({ on: false }, capabilities(spec)), [{ code: 'switch_led', value: false }]);
});

test('toCommands: colour', () => {
  assert.deepEqual(
    toCommands({ on: true, mode: 'colour', brightness: 100, color: { h: 275, s: 80 } }, capabilities(spec)),
    [
      { code: 'switch_led', value: true },
      { code: 'work_mode', value: 'colour' },
      { code: 'colour_data_v2', value: { h: 275, s: 800, v: 1000 } },
    ],
  );
});

test('toCommands: white with brightness and temperature', () => {
  assert.deepEqual(
    toCommands({ mode: 'white', brightness: 1, temperature: 100 }, capabilities(spec)),
    [
      { code: 'work_mode', value: 'white' },
      { code: 'bright_value_v2', value: 10 },
      { code: 'temp_value_v2', value: 1000 },
    ],
  );
});

test('brightness survives a round trip', () => {
  const caps = capabilities(spec);
  for (const pct of [1, 2, 37, 50, 99, 100]) {
    const [, cmd] = toCommands({ mode: 'white', brightness: pct }, caps);
    const back = toLight(device([{ code: 'switch_led', value: true }, { code: 'bright_value_v2', value: cmd.value }]), caps);
    assert.equal(back.brightness, pct);
  }
});

test('toCommands ignores what the device cannot do', () => {
  const caps = capabilities(null, [{ code: 'switch_led' }, { code: 'bright_value' }]);
  assert.deepEqual(
    toCommands({ mode: 'colour', brightness: 100, color: { h: 0, s: 100 } }, caps),
    [{ code: 'bright_value', value: 255 }],
  );
});
