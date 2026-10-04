// Entry point of the Licht Android app.
// Order matters: imports run in this order, and matter.js needs the first four set up.
import './src/report-errors';
import './src/polyfills';
import './src/crypto-install';
import './src/matter-platform';
import { AppRegistry } from 'react-native';
import App from './src/App';
import { name as appName } from './app.json';

AppRegistry.registerComponent(appName, () => App);
