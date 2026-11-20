// When the steps of starting happened (Date.now()), shown in the app under "Messwerte". Imported first
// in index.js, so jsStart is the moment the app's JavaScript began.
export const timings = { jsStart: Date.now() };

/** Notes the first time something happened. */
export function mark(name) {
  if (timings[name] === undefined) timings[name] = Date.now();
}
