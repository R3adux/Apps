// Checks of the Windows window's XAML that would otherwise only show when the window opens on Windows:
// every resource it uses exists, and the theme colours (Theme.cs) do not hide a style of the same name.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const dir = fileURLToPath(new URL('../pc/fenster/LichtFenster/', import.meta.url));
const xaml = Object.fromEntries(readdirSync(dir).filter((f) => f.endsWith('.xaml')).map((f) => [f, readFileSync(join(dir, f), 'utf8')]));
const code = readdirSync(dir).filter((f) => f.endsWith('.cs')).map((f) => readFileSync(join(dir, f), 'utf8')).join('\n');
const theme = readFileSync(join(dir, 'Theme.cs'), 'utf8');

const themeKeys = new Set([...theme.matchAll(/\("(\w+)", "#/g)].flatMap(([, key]) => [key, `${key}Color`]));
const declared = new Set(Object.values(xaml).flatMap((text) => [...text.matchAll(/x:Key="([^"]+)"/g)].map((m) => m[1])));

test('theme colours have names of their own (a style with the same name would be hidden)', () => {
  assert.ok(themeKeys.has('Muted') && themeKeys.has('Accent'));
  assert.deepEqual([...themeKeys].filter((key) => declared.has(key)), []);
});

test('every StaticResource exists', () => {
  const missing = [];
  for (const [file, text] of Object.entries(xaml)) {
    for (const [, key] of text.matchAll(/\{StaticResource ([\w.]+)\}/g)) {
      if (!declared.has(key)) missing.push(`${file}: ${key}`);
    }
  }
  for (const [, key] of code.matchAll(/FindResource\("([\w.]+)"\)/g)) {
    if (!declared.has(key) && !themeKeys.has(key)) missing.push(`code: ${key}`);
  }
  assert.deepEqual(missing, []);
});

test('every DynamicResource is a theme colour', () => {
  const unknown = [];
  for (const [file, text] of Object.entries(xaml)) {
    for (const [, key] of text.matchAll(/\{DynamicResource (\w+)\}/g)) if (!themeKeys.has(key)) unknown.push(`${file}: ${key}`);
  }
  for (const [, key] of code.matchAll(/SetResourceReference\([^,]+, (?:[^)]*\? )?"(\w+)"(?: : "(\w+)")?\)/g)) {
    if (!themeKeys.has(key)) unknown.push(`code: ${key}`);
  }
  for (const [, key] of code.matchAll(/Resources\["(\w+)"\]/g)) if (!themeKeys.has(key)) unknown.push(`code: ${key}`);
  assert.deepEqual(unknown, []);
});
