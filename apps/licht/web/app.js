import { DemoApi } from './api.js';
import { PRESETS, lampRgb, temperatureToRgb, hsvToRgb } from './color.js';
import { qrSvg, startScanner } from './qr.js';

const SEND_GAP_MS = 60; // breathing room between commands to the same lamp
const QUIET_AFTER_INPUT_MS = 1500; // ignore live updates for a lamp the user is adjusting
const CACHE_KEY = 'licht.lights.v1'; // last known lamps, shown instantly on the next start

const $ = (sel, root = document) => root.querySelector(sel);

const storage = {
  get(key) {
    try { return localStorage.getItem(key); } catch { return null; }
  },
  set(key, value) {
    try { localStorage.setItem(key, value); } catch { /* not available */ }
  },
};

// The Android app provides window.LichtApi (android-app/src/bridge.js). Opened in a plain browser, the page
// shows made-up lamps (demo) – handy for trying out the look.
const demo = !window.LichtApi;
const api = window.LichtApi ?? new DemoApi();

let lights = [];
let loaded = false; // first real data received (until then: cached data or "loading")
let info = { ble: false };

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
    let failed = false;
    try {
      await api.update(this.id, change);
    } catch (err) {
      failed = true;
      toast(err.message);
      // Show the lamp as it really is again – not the change that did not happen.
      this.pending = null;
      touched.delete(this.id);
    }
    await new Promise((r) => setTimeout(r, SEND_GAP_MS));
    this.busy = false;
    // A new tap right after a failure is sent (the user tries again); otherwise show the real state.
    if (this.pending) this.#run();
    else if (failed) reload();
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

  // Only what changed: dimming is one command to the lamp, not dimming plus re-sending the colour.
  const payload = { ...next };
  if (['temperature', 'color', 'mode'].some((k) => k in patch)) {
    payload.mode = light.mode;
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
    lights.forEach((light, index) => {
      if (!cards.has(light.id)) cards.set(light.id, buildCard(light));
      const card = cards.get(light.id);
      updateCard(card, light);
      // Keep the hub's order, but only move a card when it is out of place: moving the card whose
      // slider is being dragged would interrupt the drag (and costs a relayout on every step).
      if (container.children[index] !== card.el) container.insertBefore(card.el, container.children[index] ?? null);
    });
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
  } catch (err) {
    toast(err.message);
  } finally {
    setStatus('');
  }
}

function onEvent(event) {
  if (event.type === 'adding') showAddProgress(event);
  if (event.type === 'lights') merge(event.lights);
  if (event.type === 'light') {
    // While the user adjusts the lamp, the controls stay; reachability and name are shown anyway.
    const index = lights.findIndex((l) => l.id === event.light.id);
    if (index === -1) return;
    lights[index] = isBusy(event.light.id) ? { ...event.light, ...pickControls(lights[index]) } : event.light;
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
  if (button.disabled) return; // already adding
  button.disabled = true;
  $('#scan-btn').disabled = true;
  $('#add-error').hidden = true;
  $('#add-progress').textContent = /^\s*MT:/i.test(code)
    ? 'Lampen werden hinzugefügt …'
    : 'Suche Lampe … das kann bis zu zwei Minuten dauern.';
  $('#add-progress').hidden = false;
  try {
    const result = await api.add({ code, ip: ip.trim() || undefined, wifi: ssid ? { ssid, password } : undefined });
    $('#add-dialog').close();
    await reload();
    if (result?.failed?.length) {
      await ask({
        title: 'Nicht alle Lampen hinzugefügt',
        text: `${addSummary(result)} Die übrigen waren nicht erreichbar oder ihr Code ist abgelaufen (15 Minuten). `
          + 'Auf dem anderen Gerät neu teilen und noch einmal scannen.',
        notice: true,
      });
    } else {
      toast(addSummary(result), 'ok');
    }
  } catch (err) {
    $('#add-error').textContent = err.message;
    $('#add-error').hidden = false;
  } finally {
    button.disabled = false;
    $('#scan-btn').disabled = false;
    $('#add-progress').hidden = true;
  }
});

// Several lamps from one code: "Lampe 2 von 3 …"
function showAddProgress({ index, total, done }) {
  if (done || !(total > 1)) return;
  $('#add-progress').textContent = `Lampe ${index + 1} von ${total} wird hinzugefügt …`;
}

$('#scan-btn').addEventListener('click', () => {
  const dialog = $('#scan-dialog');
  const status = $('#scan-status');
  status.textContent = 'Halte die Kamera auf den QR-Code.';
  status.classList.remove('error');
  dialog.showModal();
  const stop = startScanner($('#scan-video'), (text) => {
    dialog.close();
    const form = $('#add-form');
    if (!/^MT:/i.test(text.trim())) {
      $('#add-error').textContent = 'Das ist kein Matter-Code. Gesucht ist der QR-Code auf der Lampe, ihrer Verpackung '
        + 'oder der, den Licht beim Teilen zeigt.';
      $('#add-error').hidden = false;
      return;
    }
    form.elements.code.value = text.trim();
    form.requestSubmit();
  }, (problem) => {
    status.textContent = problem;
    status.classList.add('error');
  });
  dialog.addEventListener('close', stop, { once: true });
});

/* ---------- Sharing: a QR code another Licht scans ---------- */

let sharedCode = null;
let shareRequest = 0; // a late answer to an earlier share must not replace the current one
async function share(ids, title) {
  const request = ++shareRequest;
  const dialog = $('#share-dialog');
  $('#share-title').textContent = title;
  $('#share-preparing').hidden = false;
  $('#share-ready').hidden = true;
  $('#share-error').hidden = true;
  $('#share-copy').hidden = true;
  sharedCode = null;
  dialog.showModal();
  try {
    const { code, manual, lamps = [], skipped = [], validMinutes } = await api.share(ids);
    if (request !== shareRequest) return;
    sharedCode = code;
    $('#share-qr').innerHTML = qrSvg(code);
    $('#share-lamps').textContent = lamps.length === 1 ? `Enthält: ${lamps[0]}` : `Enthält ${lamps.length} Lampen: ${lamps.join(', ')}`;
    $('#share-skipped').textContent = `Nicht dabei, weil gerade nicht erreichbar: ${skipped.join(', ')}. Später noch einmal teilen.`;
    $('#share-skipped').hidden = !skipped.length;
    $('#share-valid').textContent = `Gültig für ${validMinutes} Minuten. Jede Lampe lässt sich mit diesem Code einmal hinzufügen.`;
    $('#share-manual').hidden = !manual;
    $('#share-code').textContent = manual ? formatCode(manual) : '';
    $('#share-ready').hidden = false;
    $('#share-copy').hidden = false;
  } catch (err) {
    if (request !== shareRequest) return;
    $('#share-error').textContent = err.message;
    $('#share-error').hidden = false;
  } finally {
    if (request === shareRequest) $('#share-preparing').hidden = true;
  }
}

$('#share-copy').addEventListener('click', async () => {
  if (!sharedCode) return;
  try {
    await navigator.clipboard.writeText(sharedCode);
    toast('Code kopiert.', 'ok');
  } catch {
    toast('Kopieren ging gerade nicht.');
  }
});

/**
 * Small modal question. With `input`, resolves to the entered text (or null when cancelled),
 * otherwise to true/false. notice: only an OK button (a message, not a question).
 */
function ask({ title, text = '', input, label = '', okLabel = 'OK', danger = false, notice = false }) {
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
  $('#ask-cancel').hidden = notice;
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

/** What adding did, in words: { added: [ids], present: n, failed: [...] } (core/hub.js add). */
function addSummary(result) {
  const added = result?.added?.length ?? 0;
  const present = result?.present ?? 0;
  const failed = result?.failed?.length ?? 0;
  if (!added && !failed && present) return present === 1 ? 'Diese Lampe ist schon in Licht.' : `Alle ${present} Lampen sind schon in Licht.`;
  const parts = [];
  if (added) parts.push(added === 1 ? '1 Lampe hinzugefügt' : `${added} Lampen hinzugefügt`);
  if (present) parts.push(present === 1 ? '1 war schon da' : `${present} waren schon da`);
  if (failed) parts.push(failed === 1 ? '1 hat nicht geklappt' : `${failed} haben nicht geklappt`);
  return parts.length ? `${parts.join(', ')}.` : 'Keine Lampe hinzugefügt.';
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
    if (action === 'share') await share(id, `„${light.name}“ teilen`);
    if (action === 'remove') {
      const sure = await ask({
        title: `„${light.name}“ entfernen?`,
        text: 'Licht vergisst die Lampe. Andere Apps, mit denen sie geteilt ist, funktionieren weiter.',
        okLabel: 'Entfernen',
        danger: true,
      });
      if (!sure) return;
      toast(`„${light.name}“ wird entfernt …`, 'ok'); // can take a moment if the lamp is not reachable
      const result = await api.remove(id);
      $('#toast').hidden = true;
      lights = lights.filter((l) => l.id !== id);
      render();
      if (result?.localOnly) {
        await ask({
          title: 'Lampe war nicht erreichbar',
          text: `Licht hat „${light.name}“ vergessen. Die Lampe selbst kennt Licht aber noch, weil sie gerade nicht `
            + 'antwortet. Wenn du sie nicht mehr mit Licht nutzen willst: in der anderen App entfernen oder die Lampe '
            + 'zurücksetzen (5× aus- und einschalten).',
          notice: true,
        });
      }
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
  if (action === 'share-all') {
    if (lights.length) share(undefined, 'Alle Lampen teilen');
    else toast('Noch keine Lampen zum Teilen.');
  }
  if (action === 'refresh') reload();
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

/* ---------- Start ---------- */

async function start() {
  $('#demo-badge').hidden = !demo;
  lights = loadCache();
  render();
  if (!lights.length) setStatus('Lade Lampen …');
  const [, infoResult] = await Promise.all([reload(), api.info().catch(() => info)]);
  info = infoResult;
  api.subscribe(onEvent, () => {});

  // Coming back to the app: reconnect lamps right away and refresh.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState !== 'visible') return;
    api.wake?.().catch(() => {});
    reload();
  });
}

start();
