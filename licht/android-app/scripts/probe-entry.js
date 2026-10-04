// Diagnostic entry: matter.js + Licht core as Metro bundles them, without React Native.
import { LightHub } from '../../core/hub.js';
import { Environment } from '@matter/main';
console.log('PROBE loaded', typeof LightHub, typeof Environment);
