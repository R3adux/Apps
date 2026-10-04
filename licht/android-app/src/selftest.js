// Used by CI on an Android emulator: commissions a simulated lamp, switches it and
// reports the result in the log (adb logcat, tag ReactNativeJS).
import { Logger } from '@matter/main';

export async function selfTest(hub, hubReady, url) {
  Logger.level = 'info';
  const log = (text) => console.log(`LICHT_SELFTEST ${text}`);
  try {
    // React Native's URL has no searchParams, parse by hand.
    const query = Object.fromEntries(url.split('?')[1].split('&').map((kv) => kv.split('=').map(decodeURIComponent)));
    const params = { get: (key) => query[key] };
    await hubReady;
    log('hub started');
    const id = await hub.commission({ code: params.get('code'), ip: params.get('ip') ?? undefined });
    log(`commissioned ${id}`);
    await hub.update(id, { on: true, mode: 'colour', brightness: 50, color: { h: 240, s: 100 } });
    log(`light ${JSON.stringify(hub.light(id))}`);
    log('OK');
  } catch (err) {
    log(`FAIL ${err?.message ?? err} ${err?.stack ?? ''}`);
  }
}
