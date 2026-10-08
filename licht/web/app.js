import { ApiError, DemoApi, HttpApi } from './api.js';
import { PRESETS, lampRgb, temperatureToRgb, hsvToRgb } from './color.js';

const SEND_GAP_MS = 60; // breathing room between commands to the same lamp
const QUIET_AFTER_INPUT_MS = 1500; // ignore live updates for a lamp the user is adjusting
const DEMO_KEY = 'licht.demo';
const CACHE_KEY = 'licht.lights.v1'; // last known lamps, shown instantly on the next start

const $ = (sel, root = document) => root.querySelector(sel);

const storage = {
  get(key) {
    try { return localStorage.getItem(key); } catch { return null; }
  },
  set(key, value) {
    try { localStorage.setItem(key, value); } catch { /* not available */ }
  },
  remove(key) {
    try { localStorage.removeItem(key); } catch { /* not available */ }
  },
};

// The Android app injects its own API (window.LichtApi); in the browser we talk to the hub.
const params = new URLSearchParams(location.search);
const demo = params.has('demo') || storage.get(DEMO_KEY) === '1';
const api = demo ? new DemoApi() : (window.LichtApi ?? new HttpApi());
if (params.has('demo')) storage.set(DEMO_KEY, '1');

let lights = [];
let loaded = false; // first real data received (until then: cached data or "loading")
let info = { ble: false };
let unsubscribe = () => {};
let ended = false; // Licht.exe was quit from this page

// 'online' | 'connecting' | 'offline' (older hubs only send `online`)
const statusOf = (light) => light.status ?? (light.online ? 'online' : 'offline');
const usable = (light) => statusOf(light) !== 'offline';
const cards = new Map(); // id → { el, refs }
const senders = new Map(); // id → Sender
const touched = new Map(); // id → time of last user input

/* ---------- Sending: always the newest value, never a queue of stale ones ---------- */

class Sender {
  constructor(id) {
    this.id = id;
    this.pending = null;
    this.busy = false;
  }

  push(change) {
    this.pending = { ...this.pending, ...change };
    this.#run();
  }

  get idle() {
    return !this.busy && !this.pending;
  }

  async #run() {
    if (this.busy || !this.pending) return;
    this.busy = true;
    const change = this.pending;
    this.pending = null;
    try {
      await api.update(this.id, change);
    } catch (err) {
      toast(err.message);
      reload();
    }
    await new Promise((r) => setTimeout(r, SEND_GAP_MS));
    this.busy = false;
    this.#run();
  }
}

// Applies a change locally right away, then sends it. Changing light settings
// on a lamp that is off switches it on.
function change(light, patch) {
  if (!light) return; // removed in the meantime
  const next = { ...patch };
  if (patch.on === undefined && !light.on) next.on = true;
  Object.assign(light, structuredClone(next));
  touched.set(light.id, Date.now());
  render();

  const payload = { ...next };
  if (['brightness', 'temperature', 'color', 'mode'].some((k) => k in patch)) {
    payload.mode = light.mode;
    payload.brightness = light.brightness;
    if (light.mode === 'colour') payload.color = light.color;
    else if (light.supports.temperature) payload.temperature = light.temperature;
  }
  if (!senders.has(light.id)) senders.set(light.id, new Sender(light.id));
  senders.get(light.id).push(payload);
}

const isBusy = (id) => !(senders.get(id)?.idle ?? true) || Date.now() - (touched.get(id) ?? 0) < QUIET_AFTER_INPUT_MS;

/* ---------- Rendering ---------- */

function buildCard(light) {
  const el = $('#light-template').content.firstElementChild.cloneNode(true);
  const refs = {
    name: $('.light-name', el),
    state: $('.light-state', el),
    toggle: $('.switch', el),
    more: $('.light-more', el),
    tabs: [...el.querySelectorAll('.segmented button')],
    segmented: $('.segmented', el),
    brightness: $('.range-brightness', el),
    brightnessValue: $('.value-brightness', el),
    temperature: $('.range-temperature', el),
    hue: $('.range-hue', el),
    saturation: $('.range-saturation', el),
    swatches: $('.swatches', el),
  };
  const current = () => lights.find((l) => l.id === light.id);

  refs.toggle.addEventListener('click', () => {
    const l = current();
    change(l, { on: !l.on });
  });
  refs.more.addEventListener('click', (e) => {
    e.stopPropagation();
    openLightMenu(light.id, refs.more);
  });
  refs.tabs.forEach((tab) => tab.addEventListener('click', () => change(current(), { mode: tab.dataset.mode })));
  refs.brightness.addEventListener('input', () => change(current(), { brightness: +refs.brightness.value }));
  refs.temperature.addEventListener('input', () => change(current(), { mode: 'white', temperature: +refs.temperature.value }));
  refs.hue.addEventListener('input', () => {
    const l = current();
    change(l, { mode: 'colour', color: { ...l.color, h: +refs.hue.value } });
  });
  refs.saturation.addEventListener('input', () => {
    const l = current();
    change(l, { mode: 'colour', color: { ...l.color, s: +refs.saturation.value } });
  });

  for (const preset of PRESETS) {
    if (preset.mode === 'colour' && !light.supports.color) continue;
    if (preset.mode === 'white' && !light.supports.temperature) continue;
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'swatch';
    btn.title = preset.label;
    btn.setAttribute('aria-label', preset.label);
    const rgb = preset.mode === 'colour' ? hsvToRgb(preset.color.h, preset.color.s / 100) : temperatureToRgb(preset.temperature);
    btn.style.background = `rgb(${rgb.join(' ')})`;
    btn.addEventListener('click', () => {
      const { label, ...patch } = preset;
      change(current(), patch);
    });
    refs.swatches.append(btn);
  }

  // Hide what the lamp cannot do.
  if (!(light.supports.color && light.supports.temperature)) refs.segmented.hidden = true;
  if (!light.supports.brightness) refs.brightness.closest('.slider-row').hidden = true;
  if (!light.supports.temperature) refs.temperature.closest('.slider-row').hidden = true;
  return { el, refs };
}

// Sliders the user is dragging right now must not jump back.
function setValue(input, value) {
  if (document.activeElement !== input) input.value = value;
}

function updateCard({ el, refs }, light) {
  const mode = light.supports.color && !light.supports.temperature ? 'colour' : light.mode;
  const status = statusOf(light);
  el.classList.toggle('on', light.on && status !== 'offline');
  el.classList.toggle('offline', status === 'offline');
  el.classList.toggle('connecting', status === 'connecting');
  el.dataset.mode = mode;
  el.style.setProperty('--lamp', lampRgb({ ...light, mode }).join(' '));
  el.style.setProperty('--glow', light.on ? (0.35 + light.brightness / 155).toFixed(2) : 0);

  refs.name.textContent = light.name;
  const onText = light.on ? `An · ${light.brightness} %` : 'Aus';
  refs.state.textContent = status === 'offline' ? 'Nicht erreichbar'
    : status === 'connecting' ? `Verbinde … · ${onText}` : onText;
  refs.toggle.setAttribute('aria-checked', String(light.on));
  refs.toggle.setAttribute('aria-label', `${light.name} ${light.on ? 'ausschalten' : 'einschalten'}`);
  refs.tabs.forEach((t) => t.setAttribute('aria-selected', String(t.dataset.mode === mode)));

  setValue(refs.brightness, light.brightness);
  refs.brightnessValue.textContent = `${light.brightness} %`;
  setValue(refs.temperature, light.temperature);
  setValue(refs.hue, light.color.h);
  setValue(refs.saturation, light.color.s);
  const full = hsvToRgb(light.color.h, 1).join(' ');
  refs.saturation.style.setProperty('--track', `linear-gradient(90deg, #fff, rgb(${full}))`);
  const lamp = lampRgb({ ...light, mode }).join(' ');
  refs.brightness.style.setProperty('--track', `linear-gradient(90deg, #2a2d36, rgb(${lamp}))`);
}

function render() {
  const container = $('#lights');
  for (const [id, card] of cards) {
    if (!lights.some((l) => l.id === id)) {
      card.el.remove();
      cards.delete(id);
    }
  }
  if (!lights.length && !loaded) {
    container.innerHTML = '';
  } else if (!lights.length) {
    if (!$('.empty', container)) {
      container.innerHTML = `<div class="empty"><p>Noch keine Lampe eingerichtet.</p>
        <button type="button" class="btn primary" data-action="add">Lampe hinzufügen</button></div>`;
    }
  } else {
    $('.empty', container)?.remove();
    for (const light of lights) {
      if (!cards.has(light.id)) cards.set(light.id, buildCard(light));
      const card = cards.get(light.id);
      updateCard(card, light);
      container.append(card.el); // keeps the hub's order
    }
  }
  const anyOn = lights.some((l) => l.on && usable(l));
  const allToggle = $('#all-toggle');
  allToggle.textContent = anyOn ? 'Alle aus' : 'Alle an';
  allToggle.disabled = !lights.some(usable);
  saveCache();
}

let cacheTimer = null;
function saveCache() {
  if (demo || !loaded || cacheTimer) return;
  cacheTimer = setTimeout(() => {
    cacheTimer = null;
    storage.set(CACHE_KEY, JSON.stringify(lights));
  }, 500);
}

// Last known lamps, so the page shows something useful before the first answer arrives.
function loadCache() {
  if (demo) return [];
  try {
    const cached = JSON.parse(storage.get(CACHE_KEY) ?? '[]');
    if (!Array.isArray(cached)) return [];
    return cached
      .filter((l) => l && typeof l.id === 'string' && l.supports && l.color)
      .map((l) => ({ ...l, status: statusOf(l) === 'online' ? 'connecting' : statusOf(l) }));
  } catch {
    return [];
  }
}

/* ---------- Data ---------- */

let toastTimer = null;
function toast(message, kind = 'error') {
  const el = $('#toast');
  el.textContent = message || 'Unbekannter Fehler';
  el.classList.toggle('ok', kind === 'ok');
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 4500);
}

function setStatus(text) {
  $('#status').textContent = text;
}

// Merges fresh data from the hub without undoing what the user is doing right now.
function merge(fresh) {
  loaded = true;
  lights = fresh.map((light) => {
    const local = lights.find((l) => l.id === light.id);
    return local && isBusy(light.id) ? { ...light, ...pickControls(local) } : light;
  });
  render();
}

const pickControls = ({ on, mode, brightness, temperature, color }) => ({ on, mode, brightness, temperature, color });

async function reload() {
  try {
    merge(await api.lights());
    setStatus('');
  } catch (err) {
    if (err instanceof ApiError && err.status === 0) {
      setStatus('Keine Verbindung zu Licht auf dem PC. Läuft es dort?');
    } else {
      setStatus('');
      toast(err.message);
    }
  }
}

function onEvent(event) {
  if (event.type === 'lights') merge(event.lights);
  if (event.type === 'light') {
    const index = lights.findIndex((l) => l.id === event.light.id);
    if (index === -1 || isBusy(event.light.id)) return;
    lights[index] = event.light;
    render();
  }
}

async function allToggle() {
  const turnOn = !lights.some((l) => l.on && usable(l));
  for (const light of lights) {
    if (usable(light) && light.on !== turnOn) change(light, { on: turnOn });
  }
}

/* ---------- Dialogs & menus ---------- */

function closeMenus() {
  for (const menu of document.querySelectorAll('.menu')) menu.hidden = true;
  $('#menu-btn').setAttribute('aria-expanded', 'false');
}

let menuLightId = null;
function openLightMenu(id, anchor) {
  closeMenus();
  menuLightId = id;
  const menu = $('#light-menu');
  menu.hidden = false;
  const rect = anchor.getBoundingClientRect();
  menu.style.top = `${rect.bottom + window.scrollY + 4}px`;
  menu.style.left = `${Math.max(16, Math.min(rect.right + window.scrollX - menu.offsetWidth, document.documentElement.clientWidth - menu.offsetWidth - 16))}px`;
}

function openAddDialog() {
  const dialog = $('#add-dialog');
  const form = $('#add-form');
  form.reset();
  $('#ble-fields').hidden = !info.ble;
  $('#add-error').hidden = true;
  $('#add-progress').hidden = true;
  $('#discover-result').hidden = true;
  dialog.showModal();
  form.elements.code.focus();
}

const MODES = { 1: 'koppelbar mit dem Code auf der Lampe', 2: 'koppelbar mit einem Teilen-Code' };

$('#discover-btn').addEventListener('click', async () => {
  const button = $('#discover-btn');
  const list = $('#discover-result');
  button.disabled = true;
  list.hidden = false;
  list.innerHTML = '<li class="muted">Suche … (ca. 8 Sekunden)</li>';
  try {
    const devices = await api.discover($('#add-form').elements.code.value.trim());
    list.innerHTML = '';
    if (!devices.length) {
      list.innerHTML = '<li>Keine koppelbare Lampe gefunden. Die Lampe ist gerade <b>nicht im Kopplungsmodus</b> – '
        + 'erzeuge in der anderen App einen Teilen-Code oder setze die Lampe zurück.</li>';
    }
    for (const d of devices) {
      const li = document.createElement('li');
      const match = d.matchesCode === true ? ' <span class="ok">✓ passt zum eingegebenen Code</span>'
        : d.matchesCode === false ? ' <span class="muted">(gehört nicht zum eingegebenen Code)</span>' : '';
      li.innerHTML = `<b></b> – ${MODES[d.mode] ?? 'koppelbar'}${match}<br><span class="muted small"></span>`;
      li.querySelector('b').textContent = d.name;
      li.querySelector('.small').textContent = d.addresses.join(', ');
      list.append(li);
    }
  } catch (err) {
    list.innerHTML = '';
    const li = document.createElement('li');
    li.className = 'error';
    li.textContent = err.message;
    list.append(li);
  } finally {
    button.disabled = false;
  }
});

$('#add-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  const form = event.target;
  const { code, ip, ssid, password } = Object.fromEntries(new FormData(form));
  const button = form.querySelector('[type=submit]');
  button.disabled = true;
  $('#add-error').hidden = true;
  $('#add-progress').hidden = false;
  try {
    const light = await api.add({ code, ip: ip.trim() || undefined, wifi: ssid ? { ssid, password } : undefined });
    $('#add-dialog').close();
    await reload();
    if (light) toast(`„${light.name}“ wurde hinzugefügt.`, 'ok');
  } catch (err) {
    $('#add-error').textContent = err.message;
    $('#add-error').hidden = false;
  } finally {
    button.disabled = false;
    $('#add-progress').hidden = true;
  }
});

/**
 * Small modal question. With `input`, resolves to the entered text (or null when cancelled),
 * otherwise to true/false.
 */
function ask({ title, text = '', input, label = '', okLabel = 'OK', danger = false }) {
  const dialog = $('#ask-dialog');
  $('#ask-title').textContent = title;
  $('#ask-text').textContent = text;
  $('#ask-text').hidden = !text;
  $('#ask-field').hidden = input === undefined;
  $('#ask-label').textContent = label;
  $('#ask-input').value = input ?? '';
  const ok = $('#ask-ok');
  ok.textContent = okLabel;
  ok.classList.toggle('danger', danger);
  return new Promise((resolve) => {
    const done = (result) => {
      dialog.removeEventListener('close', onClose);
      $('#ask-form').removeEventListener('submit', onSubmit);
      $('#ask-cancel').removeEventListener('click', onCancel);
      if (dialog.open) dialog.close();
      resolve(result);
    };
    const onSubmit = (e) => {
      e.preventDefault();
      done(input === undefined ? true : $('#ask-input').value);
    };
    const onCancel = () => done(input === undefined ? false : null);
    const onClose = () => done(input === undefined ? false : null);
    $('#ask-form').addEventListener('submit', onSubmit);
    $('#ask-cancel').addEventListener('click', onCancel);
    dialog.addEventListener('close', onClose);
    dialog.showModal();
    if (input !== undefined) $('#ask-input').select();
  });
}

const formatCode = (code) => (/^\d{11}$/.test(code) ? `${code.slice(0, 4)}-${code.slice(4, 7)}-${code.slice(7)}` : code);

async function lightAction(action, id) {
  const light = lights.find((l) => l.id === id);
  if (!light) return;
  try {
    if (action === 'rename') {
      const name = await ask({ title: 'Umbenennen', label: 'Neuer Name (max. 32 Zeichen)', input: light.name, okLabel: 'Speichern' });
      if (!name?.trim() || name.trim() === light.name) return;
      await api.rename(id, name.trim());
      light.name = name.trim().slice(0, 32);
      render();
    }
    if (action === 'share') {
      const { code, validMinutes } = await api.share(id);
      $('#share-code').textContent = formatCode(code);
      $('#share-valid').textContent = `Gültig für ${validMinutes} Minuten.`;
      $('#share-dialog').showModal();
    }
    if (action === 'remove') {
      const sure = await ask({
        title: `„${light.name}“ entfernen?`,
        text: 'Licht vergisst die Lampe. Andere Apps, mit denen sie geteilt ist, funktionieren weiter.',
        okLabel: 'Entfernen',
        danger: true,
      });
      if (!sure) return;
      await api.remove(id);
      lights = lights.filter((l) => l.id !== id);
      render();
    }
  } catch (err) {
    toast(err.message);
  }
}

document.addEventListener('click', (e) => {
  const action = e.target.closest('[data-action]')?.dataset.action;
  const inLightMenu = e.target.closest('#light-menu');
  closeMenus();
  if (!action) return;
  if (inLightMenu) return lightAction(action, menuLightId);
  if (action === 'add') openAddDialog();
  if (action === 'autostart') toggleAutostart();
  if (action === 'quit') quitApp();
  if (action === 'refresh') reload();
  if (action === 'demo-exit') {
    storage.remove(DEMO_KEY);
    location.href = location.pathname;
  }
});

$('#menu-btn').addEventListener('click', (e) => {
  e.stopPropagation();
  const menu = $('#menu');
  const open = menu.hidden;
  closeMenus();
  menu.hidden = !open;
  $('#menu-btn').setAttribute('aria-expanded', String(open));
});

for (const button of document.querySelectorAll('[data-close]')) {
  button.addEventListener('click', () => button.closest('dialog').close());
}

$('#all-toggle').addEventListener('click', allToggle);

/* ---------- PC app (Licht.exe) ---------- */

function showDesktopItems() {
  for (const item of document.querySelectorAll('[data-desktop]')) item.hidden = !info.desktop;
  const autostart = $('[data-action="autostart"]');
  autostart.textContent = `${info.desktop?.autostart ? '✓ ' : ''}Mit Windows starten`;
  autostart.setAttribute('aria-checked', String(Boolean(info.desktop?.autostart)));
}

async function toggleAutostart() {
  try {
    info.desktop.autostart = await api.setAutostart(!info.desktop.autostart);
    showDesktopItems();
    toast(info.desktop.autostart ? 'Licht startet jetzt automatisch mit Windows.' : 'Autostart ist aus.', 'ok');
  } catch (err) {
    toast(err.message);
  }
}

async function quitApp() {
  const sure = await ask({
    title: 'Licht beenden?',
    text: 'Die Lampen lassen sich dann nicht mehr über Licht steuern – auch nicht vom Handy über den PC.',
    okLabel: 'Beenden',
    danger: true,
  });
  if (!sure) return;
  ended = true;
  unsubscribe();
  await api.quit().catch(() => {});
  document.body.innerHTML = '<main class="quit"><h1>Licht ist beendet.</h1>'
    + '<p class="muted">Du kannst dieses Fenster schließen. Zum Starten Licht.exe doppelklicken.</p></main>';
}

/* ---------- Start ---------- */

async function start() {
  $('#demo-badge').hidden = !demo;
  $('[data-action="demo-exit"]').hidden = !demo;
  lights = loadCache();
  render();
  if (!lights.length) setStatus('Lade Lampen …');
  const [, infoResult] = await Promise.all([reload(), api.info().catch(() => info)]);
  info = infoResult;
  showDesktopItems();
  let interrupted = false;
  unsubscribe = api.subscribe(onEvent, (connected) => {
    setStatus(connected ? '' : 'Verbindung zu Licht auf dem PC unterbrochen …');
    // Back after an interruption: fetch everything that may have changed in between.
    if (connected && interrupted) reload();
    interrupted = !connected;
  });

  // Coming back to the app/tab: reconnect lamps right away and refresh.
  document.addEventListener('visibilitychange', () => {
    if (ended || document.visibilityState !== 'visible') return;
    api.wake?.().catch(() => {});
    reload();
  });

  // Shortcut from the home-screen icon (long press → "Alle aus").
  if (params.get('action') === 'all-off') {
    lights.filter((l) => l.on && usable(l)).forEach((l) => change(l, { on: false }));
    history.replaceState(null, '', location.pathname);
  }
}

if (!window.LichtApi && 'serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost')) {
  navigator.serviceWorker.register('sw.js').catch(() => {});
}

start();
