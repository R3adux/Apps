// Connects the web UI (running in a WebView) with the Matter hub running in the app.
// The page gets window.LichtApi – the same methods as the HTTP API of the PC hub.

export const injectedApi = `
(function () {
  if (window.LichtApi) return;
  var pending = new Map();
  var next = 1;
  var listeners = new Set();
  function call(method, args) {
    return new Promise(function (resolve, reject) {
      var id = next++;
      pending.set(id, { resolve: resolve, reject: reject });
      window.ReactNativeWebView.postMessage(JSON.stringify({ id: id, method: method, args: args }));
    });
  }
  window.__lichtReply = function (id, ok, value) {
    var p = pending.get(id);
    if (!p) return;
    pending.delete(id);
    if (ok) p.resolve(value);
    else {
      var error = new Error(value && value.message);
      error.status = value && value.status;
      p.reject(error);
    }
  };
  window.__lichtEvent = function (event) {
    listeners.forEach(function (fn) { fn(event); });
  };
  window.LichtApi = {
    info: function () { return call('info', []); },
    lights: function () { return call('lights', []); },
    update: function (id, change) { return call('update', [id, change]); },
    add: function (options) { return call('add', [options]); },
    rename: function (id, name) { return call('rename', [id, name]); },
    share: function (id) { return call('share', [id]); },
    remove: function (id) { return call('remove', [id]); },
    discover: function (code) { return call('discover', [code]); },
    subscribe: function (onEvent, onStatus) {
      listeners.add(onEvent);
      onStatus(true);
      return function () { listeners.delete(onEvent); };
    },
  };
})();
true;
`;

/** Runs a call from the page against the hub. Returns a promise of the result. */
export async function handleCall(hub, method, args) {
  switch (method) {
    case 'info':
      return { ble: false };
    case 'lights':
      return hub.lights();
    case 'update':
      return { light: await hub.update(args[0], args[1] ?? {}) };
    case 'add': {
      const { code, ip, wifi } = args[0] ?? {};
      const id = await hub.commission({ code, ip: ip || undefined, wifi: wifi?.ssid ? wifi : undefined });
      return hub.light(id);
    }
    case 'rename':
      await hub.rename(args[0], args[1]);
      return { light: hub.light(args[0]) };
    case 'share':
      return hub.share(args[0]);
    case 'remove':
      await hub.remove(args[0]);
      return { ok: true };
    case 'discover':
      return hub.discover();
    default:
      throw Object.assign(new Error(`Unbekannte Funktion: ${method}`), { status: 400 });
  }
}

/** Script that delivers a reply or event into the page. */
export const replyScript = (id, ok, value) => `window.__lichtReply(${id}, ${ok}, ${JSON.stringify(value ?? null)}); true;`;
export const eventScript = (event) => `window.__lichtEvent && window.__lichtEvent(${JSON.stringify(event)}); true;`;
