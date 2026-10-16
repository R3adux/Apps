// Like probe-entry.js, but with the app's real React Native platform layer
// (@matter/react-native + src/matter-platform.js). Native modules are replaced by
// Node-backed stand-ins (scripts/rn-mocks). See scripts/probe.mjs.
import '../src/polyfills';
import '../src/crypto-install';
import '../src/matter-platform';
import { Logger } from '@matter/main';
import { LightHub } from '../../core/hub.js';

Logger.level = process.env.PROBE_LOG ?? 'error';

(async () => {
  try {
    setTimeout(() => {
      console.log('PROBE FAIL timeout');
      process.exit(1);
    }, 90_000).unref();
    console.log('probe (react-native platform): starting hub');
    const hub = new LightHub({ id: 'probe', label: 'Licht Probe' });
    await hub.start();
    console.log('probe: commissioning', process.env.PROBE_IP);
    const id = await hub.commission({ code: process.env.PROBE_CODE, ip: process.env.PROBE_IP });
    const light = await hub.update(id, { on: true, mode: 'colour', brightness: 50, color: { h: 240, s: 100 } });
    console.log('PROBE OK', JSON.stringify(light));
    await Promise.race([hub.close(), new Promise((r) => setTimeout(r, 3000))]);
    process.exit(0);
  } catch (err) {
    console.log('PROBE FAIL', err?.stack ?? err);
    process.exit(1);
  }
})();
