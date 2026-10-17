# Stratagem – HTTP/WebSocket-API

Für Weboberfläche, Handy-Deck und weitere Auslöser (Stream Deck, Touch Portal, Skripte).
Standard: `http://localhost:8133`. JSON in camelCase, Aufzählungen als Text.

## Schutz
- Jede ändernde Anfrage (alles außer GET) braucht den Header `X-Stratagem: 1`.
- Anfragen mit fremdem `Origin` und fremdem `Host` werden abgelehnt (erlaubt: `localhost`, IP-Adressen und im
  Heimnetz `<name>.local`).
- Aus dem Heimnetz (Option „Im Heimnetz erreichbar“) nur mit Token: `?token=…` (setzt ein Cookie),
  Cookie `stratagem_token` oder Header `X-Stratagem-Token`. `/` führt dort zum Deck.
- Im Heimnetz: zusätzlich Port `web.lanPort` (80, falls frei) und mDNS-Antwort für `web.name` + `.local`.

## Endpunkte

| Methode | Pfad | Zweck |
|---|---|---|
| GET | `/api/state` | Gesamtzustand (siehe unten) |
| GET | `/api/settings` | Einstellungen (wie `settings.json`) |
| PUT | `/api/settings` | Einstellungen ersetzen und speichern |
| POST | `/api/activate` | `{ "slot": 0 }` (0-basiert) oder `{ "code": "URDDD" }`, optional `"origin": "Stream Deck"` → `{ "ok": true }` |
| POST | `/api/pause` | `{ "paused": true }` |
| POST | `/api/reload-bindings` | Tastenbelegung des Spiels neu lesen |
| POST | `/api/read-picture` | Bildschirmfoto (PNG, ganzer Bildschirm) als Body → erkannte Zeilen `[{ index, state, code, arrows, name, icon }]` (Diagnose ohne Spiel) |
| GET | `/api/catalog` | bekannte Stratagems `[{ name, code, arrows }]` |
| GET | `/api/keys` | gültige Tastennamen (W3C `KeyboardEvent.code`) |
| GET | `/api/icon/{index}` | PNG-Icon eines Listeneintrags (sobald die Bildschirmerkennung Icons liefert) |
| GET | `/api/info` | Version, `apiVersion`; nur lokal: `desktop.autostart`, Heimnetz-Links |
| GET | `/api/qr/{index}` | nur lokal: QR-Code (SVG) des Heimnetz-Links `lan[index]` aus `/api/info` |
| POST | `/api/autostart`, `/api/open-folder`, `/api/show-window`, `/api/quit` | nur lokal (Tray-App) |
| WS | `/api/events` | sendet bei jeder Änderung `{ "type": "state", "state": … }`, beim Verbinden sofort |

`activate` gibt `ok: false` zurück, wenn das Spiel nicht im Vordergrund ist, pausiert ist, der Eintrag
im Cooldown ist oder gerade ein anderes Stratagem läuft (Grund steht im Protokoll, `state.events`).
`ok: true` heißt angenommen: Beim Lesen aus dem Spiel prüft Stratagem danach die frisch gelesene Spielliste und
tippt nichts, wenn das Stratagem dort in Abklingzeit ist, fehlt oder die Liste nicht aufgeht (steht im Protokoll).

## Zustand (`/api/state`)

```json
{
  "apiVersion": 1,
  "appVersion": "0.1.0",
  "paused": false,
  "active": true,
  "busy": false,
  "game": { "running": true, "focused": true, "windowTitle": "HELLDIVERS™ 2", "onlyWhenFocused": true },
  "bindings": { "menu": "ControlLeft", "menuMode": "Hold", "up": "ArrowUp", "down": "KeyS", "left": "KeyA", "right": "KeyD",
                "sourceFile": "C:\\…\\input_settings.config", "notes": [] },
  "list": { "source": "Manual", "problem": null,
            "items": [{ "index": 0, "label": "Reinforce", "name": "Reinforce", "code": "UDRLU", "arrows": "↑ ↓ → ← ↑", "available": true, "hasIcon": false }] },
  "events": [{ "time": "2026-10-13T23:21:42+02:00", "text": "Radialmenü: Reinforce (↑ ↓ → ← ↑)" }]
}
```

Änderungen an der API erhöhen `apiVersion`.
