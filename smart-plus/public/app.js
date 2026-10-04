import { ApiError, DemoApi, HttpApi } from './api.js';
import { PRESETS, lampRgb, temperatureToRgb, hsvToRgb } from './color.js';

const POLL_MS = 20_000;
const SEND_GAP_MS = 150; // breathing room between commands to the same lamp
const PASSWORD_KEY = 'smartplus.password';
const DEMO_KEY = 'smartplus.demo';

const $ = (sel, root = document) => root.querySelector(sel);

const storage = {
  get(key) {
    try { return localStorage.getItem(key); } catch { return null; }
  },
  set(key, value) {
    try { localStorage.setItem(key, value); } catch { /* private mode: stay logged in for this tab only */ }
  },
  remove(key) {
    try { localStorage.removeItem(key); } catch { /* ignore */ }
  },
};

let api = null;
let lights = [];
const cards = new Map(); // id → { el, refs }
const senders = new Map(); // id → Sender
let pollTimer = null;
let lastInteraction = 0;

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
      handleError(err);
      scheduleRefresh(1000);
    }
    await new Promise((r) => setTimeout(r, SEND_GAP_MS));
    this.busy = false;
    this.#run();
  }
}

function send(light, change) {
  lastInteraction = Date.now();
  if (!senders.has(light.id)) senders.set(light.id, new Sender(light.id));
  senders.get(light.id).push(change);
}

// Applies a change locally right away, then sends it. Changing light settings
// on a lamp that is off switches it on.
function change(light, patch) {
  const next = { ...patch };
  if (patch.on === undefined && !light.on) next.on = true;
  Object.assign(light, structuredClone(next));
  render();

  const payload = { ...next };
  if (['brightness', 'temperature', 'color', 'mode'].some((k) => k in patch)) {
    payload.mode = light.mode;
    payload.brightness = light.brightness;
    if (light.mode === 'colour') payload.color = light.color;
    else if (light.supports.temperature) payload.temperature = light.temperature;
  }
  send(light, payload);
}

/* ---------- Rendering ---------- */

function buildCard(light) {
  const el = $('#light-template').content.firstElementChild.cloneNode(true);
  const refs = {
    name: $('.light-name', el),
    state: $('.light-state', el),
    toggle: $('.switch', el),
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
  return { el, refs };
}

// Sliders the user is dragging right now must not jump back.
function setValue(input, value) {
  if (document.activeElement !== input) input.value = value;
}

function updateCard({ el, refs }, light) {
  const mode = light.supports.color && !light.supports.temperature ? 'colour' : light.mode;
  el.classList.toggle('on', light.on && light.online);
  el.classList.toggle('offline', !light.online);
  el.dataset.mode = mode;
  el.style.setProperty('--lamp', lampRgb({ ...light, mode }).join(' '));
  el.style.setProperty('--glow', light.on ? (0.35 + light.brightness / 155).toFixed(2) : 0);

  refs.name.textContent = light.name;
  refs.state.textContent = !light.online ? 'Offline' : light.on ? `An · ${light.brightness} %` : 'Aus';
  refs.toggle.setAttribute('aria-checked', String(light.on));
  refs.toggle.setAttribute('aria-label', `${light.name} ${light.on ? 'ausschalten' : 'einschalten'}`);
  refs.toggle.disabled = !light.online;
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
  if (!lights.length) {
    container.innerHTML = '<p class="empty">Keine Lampen gefunden. Sind sie in der Smart-Life-App und mit dem Tuya-Projekt verknüpft?</p>';
    cards.clear();
  } else {
    $('.empty', container)?.remove();
    for (const [id, card] of cards) {
      if (!lights.some((l) => l.id === id)) {
        card.el.remove();
        cards.delete(id);
      }
    }
    for (const light of lights) {
      if (!cards.has(light.id)) cards.set(light.id, buildCard(light));
      const card = cards.get(light.id);
      updateCard(card, light);
      container.append(card.el); // keeps the server's order
    }
  }
  const anyOn = lights.some((l) => l.on && l.online);
  const allToggle = $('#all-toggle');
  allToggle.textContent = anyOn ? 'Alle aus' : 'Alle an';
  allToggle.disabled = !lights.some((l) => l.online);
}

/* ---------- Loading & polling ---------- */

let toastTimer = null;
function toast(message) {
  const el = $('#toast');
  el.textContent = message;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 4000);
}

function handleError(err) {
  if (err instanceof ApiError && err.status === 401) {
    logout('Das Passwort stimmt nicht mehr. Bitte neu anmelden.');
    return;
  }
  toast(err.message || 'Unbekannter Fehler');
}

async function refresh({ quiet = false } = {}) {
  // Don't overwrite what the user is doing with an older server state.
  const busy = [...senders.values()].some((s) => !s.idle) || Date.now() - lastInteraction < 3000;
  if (quiet && busy) return;
  if (!quiet) $('#status').textContent = 'Lade Lampen …';
  try {
    lights = await api.lights();
    $('#status').textContent = '';
    render();
  } catch (err) {
    $('#status').textContent = '';
    handleError(err);
  }
}

function scheduleRefresh(delay) {
  clearTimeout(pollTimer);
  pollTimer = setTimeout(async () => {
    if (document.visibilityState === 'visible') await refresh({ quiet: true });
    scheduleRefresh(POLL_MS);
  }, delay);
}

async function allToggle() {
  const turnOn = !lights.some((l) => l.on && l.online);
  for (const light of lights) {
    if (light.online && light.on !== turnOn) change(light, { on: turnOn });
  }
}

/* ---------- Start, login, menu ---------- */

async function startApp(newApi, { demo = false } = {}) {
  api = newApi;
  $('#login').hidden = true;
  $('#app').hidden = false;
  $('#demo-badge').hidden = !demo;
  await refresh();
  scheduleRefresh(POLL_MS);

  // Shortcut from the home-screen icon (long press → "Alle aus").
  const params = new URLSearchParams(location.search);
  if (params.get('action') === 'all-off') {
    lights.filter((l) => l.on && l.online).forEach((l) => change(l, { on: false }));
    history.replaceState(null, '', location.pathname);
  }
}

function showLogin(message = '') {
  clearTimeout(pollTimer);
  $('#app').hidden = true;
  $('#login').hidden = false;
  const error = $('#login-error');
  error.textContent = message;
  error.hidden = !message;
  $('#password').focus();
}

function logout(message) {
  storage.remove(PASSWORD_KEY);
  storage.remove(DEMO_KEY);
  lights = [];
  cards.clear();
  senders.clear();
  $('#lights').innerHTML = '';
  showLogin(message);
}

$('#login-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  const password = $('#password').value;
  const candidate = new HttpApi(password);
  const button = $('#login-form button');
  button.disabled = true;
  try {
    await candidate.lights();
    storage.set(PASSWORD_KEY, password);
    storage.remove(DEMO_KEY);
    await startApp(candidate);
  } catch (err) {
    showLogin(err.status === 401 ? 'Falsches Passwort.' : err.message);
  } finally {
    button.disabled = false;
  }
});

$('#demo-start').addEventListener('click', () => {
  storage.set(DEMO_KEY, '1');
  startApp(new DemoApi(), { demo: true });
});

$('#all-toggle').addEventListener('click', allToggle);

const menu = $('#menu');
const menuBtn = $('#menu-btn');
function setMenu(open) {
  menu.hidden = !open;
  menuBtn.setAttribute('aria-expanded', String(open));
}
menuBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  setMenu(menu.hidden);
});
document.addEventListener('click', () => setMenu(false));
menu.addEventListener('click', (e) => {
  const action = e.target.dataset.action;
  if (action === 'refresh') refresh();
  if (action === 'logout') logout();
});

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible' && api) refresh({ quiet: true });
});

if ('serviceWorker' in navigator && location.protocol === 'https:') {
  navigator.serviceWorker.register('sw.js').catch(() => {});
}

// Start
const saved = storage.get(PASSWORD_KEY);
if (storage.get(DEMO_KEY)) startApp(new DemoApi(), { demo: true });
else if (saved) startApp(new HttpApi(saved));
else showLogin();
