// Phone as stream deck: one tile per stratagem, tap = call it in the game.
import { api, liveState } from './api.js';

const tiles = document.querySelector('#tiles');
const pill = document.querySelector('#game-pill');
let state = null;
let lastItems = '';

function render() {
  const g = state.game;
  pill.textContent = state.paused ? 'Pausiert' : g.focused ? 'Spiel aktiv' : g.running ? 'Spiel im Hintergrund' : 'Spiel aus';
  pill.className = `pill ${state.active ? 'ok' : 'warn'}`;
  // Tiles only change with the list (a tap's feedback must not be wiped by an unrelated update).
  const key = JSON.stringify(state.list.items);
  if (key === lastItems) return;
  lastItems = key;
  tiles.replaceChildren(...state.list.items.map((item, i) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = `tile${item.available ? '' : ' unavailable'}`;
    b.disabled = !item.available;
    if (item.hasIcon) {
      const img = document.createElement('img');
      img.src = `/api/icon/${i}`;
      img.alt = '';
      b.append(img);
    }
    const name = document.createElement('strong');
    name.textContent = item.name ?? `Platz ${i + 1}`;
    const code = document.createElement('span');
    code.className = 'code';
    code.textContent = item.arrows;
    b.append(name, code);
    b.addEventListener('click', async () => {
      navigator.vibrate?.(30);
      b.classList.add('pending');
      try {
        const r = await api.activate({ slot: i }, 'Handy');
        b.classList.add(r.ok ? 'sent' : 'failed');
      } catch {
        b.classList.add('failed');
      }
      b.classList.remove('pending');
      setTimeout(() => b.classList.remove('sent', 'failed'), 600);
    });
    return b;
  }));
}

// Keep the screen on while the deck is open.
async function keepAwake() {
  try { await navigator.wakeLock?.request('screen'); } catch { /* not allowed (e.g. http) */ }
}
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') keepAwake(); });
keepAwake();

liveState((s) => { state = s; render(); }, (online) => { document.querySelector('#offline').hidden = online; });
