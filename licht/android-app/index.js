// Entry point of the Licht Android app.
// Order matters: crypto and the Matter platform must be set up before anything uses matter.js.
import { install } from 'react-native-quick-crypto';
install();
import './src/matter-platform';
import { AppRegistry } from 'react-native';
import App from './src/App';
import { name as appName } from './app.json';

AppRegistry.registerComponent(appName, () => App);
