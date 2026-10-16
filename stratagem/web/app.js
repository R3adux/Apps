import { api, liveState, keyLabel, arrows } from './api.js';

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => [...document.querySelectorAll(sel)];
const el = (tag, props = {}, ...children) => {
  const node = Object.assign(document.createElement(tag), props);
  node.append(...children.filter((c) => c !== null && c !== undefined));
  return node;
};

let settings = null;   // as stored (GET /api/settings), edited here and saved debounced
let state = null;      // live (WebSocket)
let catalog = [];
let saveTimer = 0;
let addCode = '';

// --- saving -------------------------------------------------------------------------------------

function changed() {
  renderSettings();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(async () => {
    try {
      settings = await api.saveSettings(settings);
      saved();
    } catch (e) {
      alert(`Speichern fehlgeschlagen: ${e.message}`);
    }
  }, 300);
}

let savedTimer = 0;
function saved() {
  const badge = $('#saved');
  badge.hidden = false;
  badge.classList.remove('fade');
  clearTimeout(savedTimer);
  savedTimer = setTimeout(() => badge.classList.add('fade'), 1200);
}

const get = (path) => path.split('.').reduce((o, k) => o?.[k], settings);
const set = (path, value) => {
  const keys = path.split('.');
  const last = keys.pop();
  keys.reduce((o, k) => o[k], settings)[last] = value;
};

// --- key capture --------------------------------------------------------------------------------

const mouseNames = ['MouseLeft', 'MouseMiddle', 'MouseRight', 'MouseX1', 'MouseX2'];

function captureKey() {
  return new Promise((resolve) => {
    const overlay = $('#capture');
    overlay.hidden = false;
    const started = performance.now();
    const done = (value) => {
      overlay.hidden = true;
      removeEventListener('keydown', onKey, true);
      removeEventListener('mousedown', onMouse, true);
      removeEventListener('contextmenu', block, true);
      resolve(value);
    };
    const onKey = (e) => {
      e.preventDefault();
      e.stopPropagation();
      done(e.code === 'Escape' ? null : e.code);
    };
    const onMouse = (e) => {
      if (performance.now() - started < 250) return; // the click that opened this
      e.preventDefault();
      done(mouseNames[e.button] ?? null);
    };
    const block = (e) => e.preventDefault();
    addEventListener('keydown', onKey, true);
    addEventListener('mousedown', onMouse, true);
    addEventListener('contextmenu', block, true);
  });
}

document.addEventListener('click', async (e) => {
  const btn = e.target.closest('[data-capture]');
  if (!btn) return;
  const key = await captureKey();
  if (!key) return;
  set(btn.dataset.capture, key);
  changed();
});

// --- list ---------------------------------------------------------------------------------------

// Our own list is shown from the local settings right away (no waiting for the save round trip);
// a list read from the game comes from the live state.
function currentItems() {
  return settings.list.source === 'Manual' || !state ? manualItems() : state.list.items;
}

function manualItems() {
  return settings.list.manual.map((e, index) => ({
    index,
    name: e.name || catalog.find((c) => c.code === e.code)?.name || null,
    code: e.code,
    arrows: arrows(e.code),
    available: true,
    hasIcon: false,
  }));
}

function renderList() {
  const list = $('#items');
  const manual = settings.list.source === 'Manual';
  const items = currentItems();
  list.replaceChildren(...items.map((item, i) => {
    const actions = el('div', { className: 'item-actions' },
      el('button', { type: 'button', className: 'icon-btn test', title: 'In 3 Sekunden senden (zum Testen ins Spiel wechseln)', textContent: '▶', onclick: () => test(i, item) }));
    if (manual) {
      actions.append(
        el('button', { type: 'button', className: 'icon-btn', title: 'Nach oben', textContent: '▲', disabled: i === 0, onclick: () => move(i, -1) }),
        el('button', { type: 'button', className: 'icon-btn', title: 'Nach unten', textContent: '▼', disabled: i === items.length - 1, onclick: () => move(i, 1) }),
        el('button', { type: 'button', className: 'icon-btn danger', title: 'Entfernen', textContent: '✕', onclick: () => remove(i) }));
    }
    return el('li', { className: item.available ? '' : 'unavailable' },
      item.hasIcon ? el('img', { src: `/api/icon/${i}`, alt: '', className: 'icon' }) : el('span', { className: 'slot', textContent: i + 1 }),
      el('div', { className: 'item-text' },
        el('strong', { textContent: item.name ?? 'Unbekannt' }),
        el('span', { className: 'code', textContent: item.arrows })),
      actions);
  }));
  $('#add-form').hidden = !manual;
  const problem = state?.list.problem;
  $('#list-problem').hidden = !problem;
  $('#list-problem').textContent = problem ?? '';
}

// Sends after a countdown, so there is time to switch to the game (Alt+Tab).
async function test(index, item) {
  const button = $('#items').children[index]?.querySelector('.test');
  if (!button || button.disabled) return;
  button.disabled = true;
  for (let n = 3; n > 0; n--) {
    button.textContent = n;
    await new Promise((r) => setTimeout(r, 1000));
  }
  button.textContent = '▶';
  button.disabled = false;
  try {
    const r = await api.activate({ slot: index }, 'Test');
    if (!r.ok) flash(`${item.name ?? item.arrows}: nicht gesendet – siehe Protokoll`);
  } catch (e) { flash(e.message); }
}

function move(i, delta) {
  const list = settings.list.manual;
  [list[i], list[i + delta]] = [list[i + delta], list[i]];
  changed();
}

function remove(i) {
  settings.list.manual.splice(i, 1);
  changed();
}

function renderAddCode() {
  $('#add-code').textContent = addCode ? arrows(addCode) : '–';
}

$('#add-name').addEventListener('input', (e) => {
  const hit = catalog.find((c) => c.name.toLowerCase() === e.target.value.trim().toLowerCase());
  if (hit) { addCode = hit.code; renderAddCode(); }
});

$$('.code-input .arrow').forEach((b) => b.addEventListener('click', () => {
  addCode = b.dataset.dir === 'back' ? addCode.slice(0, -1) : (addCode + b.dataset.dir).slice(0, 12);
  renderAddCode();
}));

$('#add-code').tabIndex = 0;
$('#add-code').addEventListener('keydown', (e) => {
  const dir = { ArrowUp: 'U', ArrowDown: 'D', ArrowLeft: 'L', ArrowRight: 'R' }[e.key];
  if (dir) addCode = (addCode + dir).slice(0, 12);
  else if (e.key === 'Backspace') addCode = addCode.slice(0, -1);
  else return;
  e.preventDefault();
  renderAddCode();
});

$('#add-form').addEventListener('submit', (e) => {
  e.preventDefault();
  if (!addCode) { flash('Erst den Code eingeben (Pfeile).'); return; }
  const name = $('#add-name').value.trim() || null;
  settings.list.manual.push({ name, code: addCode });
  addCode = '';
  $('#add-name').value = '';
  renderAddCode();
  changed();
});

$('#source').addEventListener('change', (e) => { settings.list.source = e.target.value; changed(); });

// --- radial -------------------------------------------------------------------------------------

function renderPreview() {
  const svg = $('#preview');
  const items = currentItems();
  const n = items.length;
  const ns = 'http://www.w3.org/2000/svg';
  const make = (tag, attrs, text) => {
    const node = document.createElementNS(ns, tag);
    for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
    if (text !== undefined) node.textContent = text;
    return node;
  };
  const R = 100;
  const r0 = Math.max(18, Math.min(60, (settings.radial.deadzoneCounts / settings.radial.clampCounts) * R));
  const nodes = [make('circle', { r: R, class: 'pv-bg' })];
  items.forEach((item, i) => {
    const center = (-90 + (i * 360) / n) * Math.PI / 180;
    const half = Math.PI / n;
    if (n > 1) {
      const a = center - half;
      nodes.push(make('line', { x1: Math.cos(a) * r0, y1: Math.sin(a) * r0, x2: Math.cos(a) * R, y2: Math.sin(a) * R, class: 'pv-sep' }));
    }
    const tr = (R + r0) / 2;
    const x = Math.cos(center) * tr;
    const y = Math.sin(center) * tr;
    nodes.push(make('text', { x, y: y - 3, class: `pv-name${item.available ? '' : ' off'}` }, (item.name ?? '').slice(0, 14)));
    nodes.push(make('text', { x, y: y + 9, class: 'pv-code' }, item.arrows.replaceAll(' ', '')));
  });
  nodes.push(make('circle', { r: r0, class: 'pv-center' }));
  nodes.push(make('text', { x: 0, y: 4, class: 'pv-cancel' }, 'Abbrechen'));
  svg.replaceChildren(...nodes);
}

function radialHelp() {
  const r = settings.radial;
  const trigger = keyLabel(r.trigger);
  return r.mode === 'HoldRelease'
    ? `${trigger} halten, Maus in Richtung schieben, ${trigger} loslassen. In der Mitte loslassen, Esc oder Rechtsklick = abbrechen. Die Kamera bewegt sich währenddessen nicht.`
    : `${trigger} antippen, dann mit gedrückter ${dragLabel(r.dragButton)} Maustaste in die Richtung ziehen und loslassen. Nochmal ${trigger}, Esc oder Rechtsklick = schließen. Schließt sich nach ${Math.round(r.timeoutMs / 1000)} s von selbst.`;
}

const dragLabel = (b) => ({ Left: 'linker', Right: 'rechter', Middle: 'mittlerer', X1: 'Seiten-', X2: 'Seiten-' })[b] ?? '';

// --- hotkeys ------------------------------------------------------------------------------------

function renderHotkeys() {
  const items = currentItems();
  $('#hotkeys').replaceChildren(...settings.hotkeys.map((h, i) => {
    const select = el('select', { 'aria-label': 'Aktion' });
    const max = Math.max(items.length, 8);
    for (let s = 1; s <= max; s++) {
      const item = items[s - 1];
      select.append(el('option', { value: `slot:${s}`, textContent: `Platz ${s}${item ? ` – ${item.name ?? item.arrows}` : ''}` }));
    }
    for (const c of catalog) select.append(el('option', { value: `code:${c.code}`, textContent: `Immer: ${c.name}` }));
    select.value = h.code ? `code:${h.code}` : `slot:${h.slot ?? 1}`;
    if (h.code && select.value !== `code:${h.code}`) {
      select.append(el('option', { value: `code:${h.code}`, textContent: `Immer: ${arrows(h.code)}` }));
      select.value = `code:${h.code}`;
    }
    select.addEventListener('change', () => {
      const [kind, value] = select.value.split(':');
      if (kind === 'slot') { h.slot = Number(value); h.code = null; } else { h.code = value; h.slot = null; }
      changed();
    });
    const key = el('button', { type: 'button', className: 'btn key', textContent: keyLabel(h.input) || 'Taste wählen' });
    key.addEventListener('click', async () => {
      const k = await captureKey();
      if (k) { h.input = k; changed(); }
    });
    return el('li', {}, key, el('span', { textContent: '→' }), select,
      el('button', { type: 'button', className: 'icon-btn danger', textContent: '✕', title: 'Entfernen', onclick: () => { settings.hotkeys.splice(i, 1); changed(); } }));
  }));
}

$('#hotkey-add').addEventListener('click', async () => {
  const k = await captureKey();
  if (!k) return;
  const used = new Set(settings.hotkeys.filter((h) => h.slot).map((h) => h.slot));
  let slot = 1;
  while (used.has(slot)) slot++;
  settings.hotkeys.push({ input: k, slot, code: null });
  changed();
});

// --- bindings, timing, misc ---------------------------------------------------------------------

function renderBindings() {
  const b = state?.bindings;
  const readGame = settings.game.readBindingsFromGame;
  $('#manual-bindings').hidden = readGame;
  if (!b) return;
  const rows = [
    ['Stratagem-Liste', `${keyLabel(b.menu)} (${b.menuMode === 'Hold' ? 'halten' : 'drücken'})`],
    ['Richtungen', `${keyLabel(b.up)} ${keyLabel(b.down)} ${keyLabel(b.left)} ${keyLabel(b.right)}`],
    ['Quelle', b.sourceFile ? 'Spieleinstellungen' : 'hier eingestellt'],
  ];
  $('#bindings').replaceChildren(...rows.flatMap(([k, v]) => [el('dt', { textContent: k }), el('dd', { textContent: v })]));
  if (b.sourceFile) $('#bindings').lastChild.title = b.sourceFile;
  $('#binding-notes').replaceChildren(...b.notes.map((n) => el('li', { textContent: n })));
}

function bindRange(id, path, label, format) {
  const input = $(id);
  input.addEventListener('input', () => {
    set(path, Number(input.value));
    $(label).textContent = format(Number(input.value));
    changed();
  });
  return () => {
    input.value = get(path);
    $(label).textContent = format(get(path));
  };
}

function bindCheck(id, path) {
  const input = $(id);
  input.addEventListener('change', () => { set(path, input.checked); changed(); });
  return () => { input.checked = !!get(path); };
}

function bindSelect(id, path) {
  const input = $(id);
  input.addEventListener('change', () => { set(path, input.value); changed(); });
  return () => { input.value = get(path); };
}

const ms = (v) => `${v} ms`;
const renderers = [
  bindRange('#radial-deadzone', 'radial.deadzoneCounts', '#deadzone-val', (v) => `${v}`),
  bindRange('#radial-size', 'radial.sizePx', '#size-val', (v) => `${v} px`),
  bindRange('#radial-opacity', 'radial.opacity', '#opacity-val', (v) => `${Math.round(v * 100)} %`),
  bindRange('#timing-hold', 'timing.keyHoldMs', '#hold-val', ms),
  bindRange('#timing-gap', 'timing.keyGapMs', '#gap-val', ms),
  bindRange('#timing-open', 'timing.menuOpenMs', '#open-val', ms),
  bindCheck('#radial-enabled', 'radial.enabled'),
  bindCheck('#read-game', 'game.readBindingsFromGame'),
  bindCheck('#only-focused', 'onlyWhenGameFocused'),
  bindCheck('#allow-lan', 'web.allowLan'),
  bindSelect('#radial-mode', 'radial.mode'),
  bindSelect('#radial-drag', 'radial.dragButton'),
  bindSelect('#menu-mode', 'game.menuMode'),
];

$('#reload-bindings').addEventListener('click', () => api.reloadBindings().catch((e) => flash(e.message)));

function renderSettings() {
  if (!settings) return;
  renderers.forEach((r) => r());
  $('#source').value = settings.list.source;
  $$('[data-capture]').forEach((b) => { b.textContent = keyLabel(get(b.dataset.capture)); });
  $('#drag-field').hidden = settings.radial.mode !== 'TapDrag';
  $('#radial-help').textContent = radialHelp();
  renderList();
  renderPreview();
  renderHotkeys();
  renderBindings();
}

// --- live state ---------------------------------------------------------------------------------

function renderState() {
  if (!state) return;
  const g = state.game;
  const pill = $('#game-pill');
  pill.textContent = !g.running ? 'Spiel aus' : g.focused ? 'Spiel aktiv' : 'Spiel im Hintergrund';
  pill.className = `pill ${g.focused ? 'ok' : g.running ? 'warn' : ''}`;
  $('#pause-btn').textContent = state.paused ? 'Fortsetzen' : 'Pausieren';
  $('#pause-btn').classList.toggle('primary', state.paused);
  const hint = $('#inactive-hint');
  hint.hidden = !state.paused && g.onlyWhenFocused;
  hint.textContent = state.paused
    ? 'Pausiert: Radialmenü und Tasten sind aus.'
    : 'Achtung: Stratagems werden auch gesendet, wenn das Spiel nicht im Vordergrund ist.';
  changedOnly('events', state.events, () => $('#events').replaceChildren(...state.events.map((e) =>
    el('li', {}, el('time', { textContent: new Date(e.time).toLocaleTimeString('de-DE') }), ' ', e.text))));
  if (!settings) return;
  changedOnly('bindings', state.bindings, renderBindings);
  if (settings.list.source !== 'Manual') {
    changedOnly('list', state.list, () => {
      renderList();
      renderPreview();
      // A dropdown the user is working with is not replaced under their hands.
      if (!$('#hotkeys').contains(document.activeElement)) renderHotkeys();
    });
  } else changedOnly('problem', state.list.problem, renderList);
}

const lastRendered = new Map();
function changedOnly(key, data, render) {
  const json = JSON.stringify(data);
  if (lastRendered.get(key) === json) return;
  lastRendered.set(key, json);
  render();
}

let flashTimer = 0;
function flash(text) {
  const hint = $('#inactive-hint');
  hint.hidden = false;
  hint.textContent = text;
  clearTimeout(flashTimer);
  flashTimer = setTimeout(renderState, 4000);
}

$('#pause-btn').addEventListener('click', () => api.pause(!state?.paused).catch((e) => flash(e.message)));

// --- menu ---------------------------------------------------------------------------------------

const menu = $('#menu');
$('#menu-btn').addEventListener('click', (e) => {
  e.stopPropagation();
  menu.hidden = !menu.hidden;
  $('#menu-btn').setAttribute('aria-expanded', String(!menu.hidden));
});
document.addEventListener('click', () => { menu.hidden = true; });
menu.addEventListener('click', async (e) => {
  const action = e.target.dataset?.action;
  try {
    if (action === 'autostart') {
      const r = await api.autostart(!e.target.classList.contains('on'));
      e.target.classList.toggle('on', r.autostart);
    } else if (action === 'folder') await api.openFolder();
    else if (action === 'quit' && confirm('Stratagem beenden? Radialmenü und Tasten sind dann aus.')) {
      await api.quit();
      document.body.replaceChildren(el('p', { className: 'banner', textContent: 'Stratagem wurde beendet. Zum Starten Stratagem.exe doppelklicken.' }));
    }
  } catch (err) { flash(err.message); }
});

// --- start --------------------------------------------------------------------------------------

async function start() {
  try {
    [settings, catalog] = await Promise.all([api.settings(), api.catalog()]);
    const info = await api.info();
    if (info.desktop) {
      $$('[data-desktop]').forEach((b) => { b.hidden = false; });
      menu.querySelector('[data-action="autostart"]').classList.toggle('on', info.desktop.autostart);
    }
    $('#lan-urls').replaceChildren(...(info.lan ?? []).map((u) => el('div', {}, el('a', { href: u, textContent: u }))));
    $('#catalog').replaceChildren(...catalog.map((c) => el('option', { value: c.name })));
  } catch (e) {
    $('#offline').hidden = false;
    $('#offline').textContent = e.message;
    return;
  }
  renderSettings();
  liveState((s) => { state = s; renderState(); }, (online) => { $('#offline').hidden = online; });
}

start();
