import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  applyChange, hsToXy, levelToPercent, miredsToPercent, percentToLevel, percentToMireds, toCommands, toLight, xyToHs,
} from '../core/model.js';

const features = { level: true, hueSaturation: true, xy: true, colorTemperature: true };
const attrs = {
  onOff: true, currentLevel: 254, colorMode: 2, currentHue: 127, currentSaturation: 254,
  colorTemperatureMireds: 370, colorTempPhysicalMinMireds: 153, colorTempPhysicalMaxMireds: 370,
};

test('brightness percent ↔ level round trip', () => {
  assert.equal(percentToLevel(1), 1);
  assert.equal(percentToLevel(100), 254);
  for (let pct = 1; pct <= 100; pct++) assert.equal(levelToPercent(percentToLevel(pct)), pct);
  assert.equal(levelToPercent(null), 100);
});

test('colour temperature: 0 % = warm (max mireds), 100 % = cold (min mireds)', () => {
  const range = { min: 153, max: 370 };
  assert.equal(percentToMireds(0, range), 370);
  assert.equal(percentToMireds(100, range), 153);
  assert.equal(miredsToPercent(370, range), 0);
  assert.equal(miredsToPercent(153, range), 100);
  assert.equal(miredsToPercent(percentToMireds(40, range), range), 40);
});

test('toLight maps Matter attributes', () => {
  assert.deepEqual(toLight({ id: '1', name: 'Wohnzimmer', online: true, attrs, features }), {
    id: '1', name: 'Wohnzimmer', online: true, on: true, mode: 'white',
    supports: { brightness: true, temperature: true, color: true },
    brightness: 100, temperature: 0, color: { h: 180, s: 100 },
  });
  const colour = toLight({ id: '1', name: 'x', online: false, attrs: { ...attrs, colorMode: 0, onOff: false }, features });
  assert.equal(colour.mode, 'colour');
  assert.equal(colour.on, false);
  assert.equal(colour.online, false);
});

test('toLight converts xy colours', () => {
  const { x, y } = hsToXy(120, 100);
  const light = toLight({ id: '1', name: 'x', online: true, attrs: { ...attrs, colorMode: 1, currentX: x, currentY: y }, features });
  assert.ok(Math.abs(light.color.h - 120) <= 2, `hue ${light.color.h}`);
  assert.ok(light.color.s >= 95);
});

test('xy round trip keeps the hue', () => {
  for (const h of [0, 30, 60, 120, 180, 240, 300]) {
    const { x, y } = hsToXy(h, 100);
    const back = xyToHs(x, y);
    const diff = Math.min(Math.abs(back.h - h), 360 - Math.abs(back.h - h));
    assert.ok(diff <= 2, `${h} → ${back.h}`);
  }
});

test('toCommands: off is a single command', () => {
  assert.deepEqual(toCommands({ on: false, brightness: 50 }, { features, attrs }), [{ cluster: 'onOff', command: 'off', args: undefined }]);
});

test('toCommands: on only', () => {
  assert.deepEqual(toCommands({ on: true }, { features, attrs }), [{ cluster: 'onOff', command: 'on', args: undefined }]);
});

test('toCommands: colour, then brightness with on', () => {
  const cmds = toCommands({ on: true, mode: 'colour', brightness: 50, color: { h: 180, s: 50 } }, { features, attrs });
  assert.deepEqual(cmds.map((c) => c.command), ['moveToHueAndSaturation', 'moveToLevelWithOnOff']);
  assert.equal(cmds[0].args.hue, 127);
  assert.equal(cmds[0].args.saturation, 127);
  assert.deepEqual(cmds[0].args.optionsMask, { executeIfOff: true });
  assert.equal(cmds[1].args.level, percentToLevel(50));
});

test('toCommands: white temperature', () => {
  const cmds = toCommands({ mode: 'white', temperature: 100, brightness: 100 }, { features, attrs });
  assert.deepEqual(cmds.map((c) => [c.command, c.args.colorTemperatureMireds ?? c.args.level]),
    [['moveToColorTemperature', 153], ['moveToLevelWithOnOff', 254]]);
});

test('toCommands uses xy when the lamp has no hue/saturation', () => {
  const cmds = toCommands({ mode: 'colour', color: { h: 0, s: 100 } }, { features: { ...features, hueSaturation: false }, attrs });
  assert.equal(cmds[0].command, 'moveToColor');
  assert.ok(cmds[0].args.colorX > 40000);
});

test('toCommands skips what the lamp cannot do', () => {
  const onOffOnly = { level: false, hueSaturation: false, xy: false, colorTemperature: false };
  assert.deepEqual(toCommands({ on: true, mode: 'colour', brightness: 10, color: { h: 0, s: 100 } }, { features: onOffOnly }),
    [{ cluster: 'onOff', command: 'on', args: undefined }]);
});

test('applyChange updates cached attributes', () => {
  const next = applyChange({ ...attrs, onOff: false }, { on: true, mode: 'colour', brightness: 1, color: { h: 90, s: 100 } }, features);
  assert.equal(next.onOff, true);
  assert.equal(next.currentLevel, 1);
  assert.equal(next.colorMode, 0);
  assert.equal(next.currentHue, 64);
});
