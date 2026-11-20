// The task "LichtBackground" (android LichtTaskService), started when the app is no longer visible.
// React Native only runs timers while such a task runs: it keeps them going until Licht has fallen asleep
// (core/hub.js pause) or the app is open again, then ends – after that, nothing runs in the background.
import { AppState } from 'react-native';
import { hubReady } from './App';

export async function background() {
  console.log(`LICHT_BACKGROUND started (app: ${AppState.currentState})`); // the emulator test checks these lines
  if (AppState.currentState === 'active') return; // back in front before the task even started
  const hub = await hubReady();
  if (AppState.currentState === 'active') return;
  const end = await hub.pause();
  console.log(`LICHT_BACKGROUND ${end}`); // 'asleep' or 'resumed'
}
