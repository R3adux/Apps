// Talks to the Stratagem app (same origin). Changes carry the header X-Stratagem: 1 (protection against other web pages).

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

async function call(method, path, body) {
  let res;
  try {
    res = await fetch(path, {
      method,
      headers: method === 'GET' ? {} : { 'Content-Type': 'application/json', 'X-Stratagem': '1' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError('Keine Verbindung zu Stratagem.', 0);
  }
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new ApiError(data.error || `Fehler ${res.status}`, res.status);
  return data;
}

export const api = {
  info: () => call('GET', '/api/info'),
  state: () => call('GET', '/api/state'),
  settings: () => call('GET', '/api/settings'),
  saveSettings: (settings) => call('PUT', '/api/settings', settings),
  catalog: () => call('GET', '/api/catalog'),
  activate: (target, origin) => call('POST', '/api/activate', { ...target, origin }),
  pause: (paused) => call('POST', '/api/pause', { paused }),
  reloadBindings: () => call('POST', '/api/reload-bindings', {}),
  autostart: (enabled) => call('POST', '/api/autostart', { enabled }),
  openFolder: () => call('POST', '/api/open-folder', {}),
  quit: () => call('POST', '/api/quit', {}),
};

/** Live state via WebSocket, reconnects by itself. onState(state) / onConnection(bool). */
export function liveState(onState, onConnection) {
  let delay = 500;
  const connect = () => {
    const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/api/events`);
    ws.onopen = () => { delay = 500; onConnection(true); };
    ws.onmessage = (e) => {
      const msg = JSON.parse(e.data);
      if (msg.type === 'state') onState(msg.state);
    };
    ws.onclose = () => {
      onConnection(false);
      setTimeout(connect, delay);
      delay = Math.min(delay * 2, 5000);
    };
  };
  connect();
}

/** Display name of a key/button stored as W3C code ("ControlLeft", "MouseX1"). */
export function keyLabel(name) {
  if (!name) return '–';
  const special = {
    ControlLeft: 'Strg links', ControlRight: 'Strg rechts', ShiftLeft: 'Umschalt links', ShiftRight: 'Umschalt rechts',
    AltLeft: 'Alt', AltRight: 'AltGr', ArrowUp: '↑', ArrowDown: '↓', ArrowLeft: '←', ArrowRight: '→',
    Space: 'Leertaste', Enter: 'Enter', Escape: 'Esc', Backspace: 'Rücktaste', Tab: 'Tab', CapsLock: 'Feststell',
    Home: 'Pos1', End: 'Ende', PageUp: 'Bild ↑', PageDown: 'Bild ↓', Insert: 'Einfg', Delete: 'Entf',
    MouseLeft: 'Maus links', MouseRight: 'Maus rechts', MouseMiddle: 'Maus Mitte',
    MouseX1: 'Maus Seitentaste 1', MouseX2: 'Maus Seitentaste 2',
  };
  if (special[name]) return special[name];
  if (name.startsWith('Key')) return name.slice(3);
  if (name.startsWith('Digit')) return name.slice(5);
  if (name.startsWith('Numpad')) return 'Num ' + name.slice(6).replace('Add', '+').replace('Subtract', '−').replace('Multiply', '×').replace('Divide', '÷').replace('Decimal', ',');
  return name;
}

export const arrows = (letters) => [...(letters || '')].map((c) => ({ U: '↑', D: '↓', L: '←', R: '→' })[c] || '?').join(' ');
