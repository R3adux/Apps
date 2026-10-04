// Puts react-native-quick-crypto on global.crypto (and Buffer) – before matter.js loads.
// A module of its own because imports run before any statement in index.js.
import { install } from 'react-native-quick-crypto';

install();
