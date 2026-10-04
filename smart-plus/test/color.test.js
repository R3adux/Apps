import { test } from 'node:test';
import assert from 'node:assert/strict';
import { hsvToRgb, temperatureToRgb } from '../public/color.js';

test('hsvToRgb', () => {
  assert.deepEqual(hsvToRgb(0, 1), [255, 0, 0]);
  assert.deepEqual(hsvToRgb(120, 1), [0, 255, 0]);
  assert.deepEqual(hsvToRgb(240, 0.5), [128, 128, 255]);
  assert.deepEqual(hsvToRgb(77, 0), [255, 255, 255]);
});

test('temperatureToRgb goes from warm to cold', () => {
  assert.deepEqual(temperatureToRgb(0), [255, 166, 77]);
  assert.deepEqual(temperatureToRgb(100), [207, 227, 255]);
});
