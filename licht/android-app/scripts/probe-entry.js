// Fast check without building an APK (npm run probe): the Licht core and matter.js,
// transformed and bundled by Metro exactly like in the app, run in Node with the
// JavaScript features Hermes lacks removed. It commissions a simulated lamp and switches it.
// Only the platform layer differs: Node networking instead of React Native's.
import '../src/polyfills';
import '@matter/nodejs';
import { Environment, Logger } from '@matter/main';
import { LightHub } from '../../core/hub.js';

Logger.level = process.env.PROBE_LOG ?? 'error';
Environment.default.vars.set('storage.path', process.env.PROBE_STORE);

(async () => {
  try {
    setTimeout(() => {
      console.log('PROBE FAIL timeout');
      process.exit(1);
    }, 90_000).unref();
    console.log('probe: starting hub');
    const hub = new LightHub({ id: 'probe', label: 'Licht Probe' });
    await hub.start();
    console.log('probe: commissioning', process.env.PROBE_IP);
    const id = await hub.commission({ code: process.env.PROBE_CODE, ip: process.env.PROBE_IP });
    const light = await hub.update(id, { on: true, mode: 'colour', brightness: 50, color: { h: 240, s: 100 } });
    console.log('PROBE OK', JSON.stringify(light));
    await hub.close();
    process.exit(0);
  } catch (err) {
    console.log('PROBE FAIL', err?.stack ?? err);
    process.exit(1);
  }
})();
