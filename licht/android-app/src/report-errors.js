// Logs uncaught JavaScript errors with their stack (adb logcat, tag ReactNativeJS).
// Imported first in index.js so that errors while starting up are reported as well.
const previous = global.ErrorUtils?.getGlobalHandler?.();
global.ErrorUtils?.setGlobalHandler?.((error, isFatal) => {
  console.error(`LICHT_SELFTEST FAIL ${isFatal ? '(fatal) ' : ''}${error?.message}\n${error?.stack}`);
  previous?.(error, isFatal);
});
