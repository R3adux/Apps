// Entry point of the Licht Android app. matter.js is not loaded here but right after the first frame
// (src/matter.js, loaded by src/App.js), so the lamps are on screen at once.
import './src/report-errors';
import './src/no-internet-checks';
import { AppRegistry } from 'react-native';
import App from './src/App';
import { name as appName } from './app.json';

AppRegistry.registerComponent(appName, () => App);
