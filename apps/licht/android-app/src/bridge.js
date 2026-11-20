// Connects the web UI (running in a WebView) with the Matter hub running in the app.
// The page gets window.LichtApi; its calls are the ones in core/api.js (the same as the Windows app's).
// Nothing of matter.js is imported here: the page must come up before matter.js is loaded (see App.js).

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
    connections: function (id) { return call('connections', [id]); },
    removeConnection: function (id, index) { return call('removeConnection', [id, index]); },
    discover: function (code) { return call('discover', [code]); },
    timings: function () { return call('timings', []); },
    measure: function () { return call('measure', []); },
    wake: function () { return call('wake', []); },
    subscribe: function (onEvent, onStatus) {
      listeners.add(onEvent);
      onStatus(true);
      return function () { listeners.delete(onEvent); };
    },
  };
})();
true;
`;

/**
 * Runs a call from the page against the hub. Returns a promise of the result.
 * timings: when the app's start steps happened (src/timings.js), for "Messwerte".
 */
export async function handleCall(hub, method, args, { timings } = {}) {
  if (method === 'info') return { ble: false };
  if (method === 'timings') return { ...timings, asleep: hub.asleep };
  if (method === 'measure') return hub.measureFromSleep();
  return require('../../core/api.js').callHub(hub, method, args); // loaded with matter.js by now
}

/** Script that delivers a reply or event into the page. */
export const replyScript = (id, ok, value) => `window.__lichtReply(${id}, ${ok}, ${JSON.stringify(value ?? null)}); true;`;
export const eventScript = (event) => `window.__lichtEvent && window.__lichtEvent(${JSON.stringify(event)}); true;`;
