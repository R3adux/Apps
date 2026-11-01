// Everything of matter.js the app needs. App.js loads this right after the first frame, not at start:
// matter.js is big and evaluating it takes a moment, but the page with the lamps as last seen is already
// on screen by then. Order matters: matter.js needs the first three set up.
import './polyfills';
import './crypto-install';
import './matter-platform';
import { Logger } from '@matter/main';
import { LightHub } from '../../core/hub.js';

// Quiet by default; the self-test turns on more detail (see selftest.js).
Logger.level = 'warn';

export { selfTest } from './selftest';

/** The one hub for the whole app lifetime. */
export const createHub = () => new LightHub({ label: 'Licht Handy', id: 'licht-android' });
