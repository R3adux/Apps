// The conversation between Licht (this process, with the lamps) and its window program (LichtFenster.exe).
// JSON, one object per line; field t says what it is. The window program implements the same list
// (pc/fenster/Licht.Fenster.Core: Hub.cs, MainViewModel.cs).
//
// Window → Licht
//   { t: 'ready' }                              window program started (tray icon there)
//   { t: 'call', id, method, args }             a request; methods: those of core/api.js, plus
//                                               'autostart' (args: [] = ask, [true|false] = set) and 'quit'
//   { t: 'log', text }                          a line for licht.log
//   { t: 'quit' }                               the user ended Licht (tray menu)
//
// Licht → window
//   { t: 'hello', autostart, hint }             after 'ready'; hint: first start, show a short tip
//   { t: 'lights', lights }                     all lamps – after start and whenever the list changes
//   { t: 'light', light }                       one lamp changed
//   { t: 'adding', index, total } / { t: 'adding', done: true, total }   adding lamps from a code
//   { t: 'reply', id, ok: true, result } / { t: 'reply', id, ok: false, error, status }
//   { t: 'show' }                               bring the window to the front (Licht.exe started again)
//   { t: 'message', kind, title, text }         something to tell the user (kind: 'info' | 'error')
//   { t: 'fatal', title, text }                 Licht cannot run: show it, then end
//   { t: 'quit' }                               end now

import { callHub } from '../core/api.js';

/**
 * desktop: { autostart: { get(), set(enabled) } | undefined, quit(), hint }.
 * Returns { handle(message), attachHub(hub), hubFailed(error), notify(message), setSend(send) }.
 * setSend: where messages to the window go (changes when the window program is restarted).
 */
export function createBridge(desktop) {
  let send = () => false;
  let hub;
  let failure;
  let hubReady;
  const ready = new Promise((resolve, reject) => { hubReady = { resolve, reject }; });
  ready.catch(() => {}); // reported to each call instead

  const lightsMessage = () => ({ t: 'lights', lights: hub.lights() });

  const desktopCall = async (method, args) => {
    if (method === 'autostart') {
      if (!desktop.autostart) return false;
      return args.length ? desktop.autostart.set(Boolean(args[0])) : desktop.autostart.get();
    }
    if (method === 'quit') {
      setTimeout(() => desktop.quit(), 50); // after the reply
      return { ok: true };
    }
    return undefined;
  };

  async function call({ id, method, args }) {
    const list = Array.isArray(args) ? args : [];
    try {
      let result = await desktopCall(method, list);
      if (result === undefined) {
        await ready;
        result = await callHub(hub, method, list);
      }
      send({ t: 'reply', id, ok: true, result: result ?? null });
    } catch (err) {
      const status = err?.status ?? 500;
      if (status >= 500 && status !== 502 && status !== 503) console.error(`Aufruf ${method} fehlgeschlagen:`, err);
      send({ t: 'reply', id, ok: false, error: err?.message ?? String(err), status });
    }
  }

  return {
    setSend(fn) {
      send = fn;
    },

    async handle(message) {
      switch (message?.t) {
        case 'ready':
          send({ t: 'hello', autostart: desktop.autostart ? await desktop.autostart.get() : false, hint: Boolean(desktop.hint) });
          if (failure) send({ t: 'fatal', title: 'Licht konnte nicht starten', text: failure });
          else if (hub) send(lightsMessage());
          break;
        case 'call':
          await call(message);
          break;
        case 'log':
          console.log(`Fenster: ${String(message.text ?? '').slice(0, 2000)}`);
          break;
        case 'quit':
          desktop.quit();
          break;
        default:
          console.warn('Fenster: unbekannte Nachricht', message?.t);
      }
    },

    attachHub(theHub) {
      hub = theHub;
      hub.subscribe((event) => {
        if (event.type === 'lights') send(lightsMessage());
        else if (event.type === 'light') send({ t: 'light', light: event.light });
        else if (event.type === 'adding') send({ t: 'adding', ...withoutType(event) });
      });
      send(lightsMessage());
      hubReady.resolve();
    },

    hubFailed(error) {
      failure = error?.message ?? String(error);
      hubReady.reject(Object.assign(new Error(failure), { status: 500 }));
      send({ t: 'fatal', title: 'Licht konnte nicht starten', text: failure });
    },

    notify(message) {
      return send(message);
    },
  };
}

function withoutType({ type, ...rest }) {
  return rest;
}
