# Apps

Eigene Apps als Ersatz für Apps, die mir nicht gefallen – lokal, ohne Konto, ohne Cloud.

## Übersicht

| App | Wofür | Plattform | Stand | Download |
|---|---|---|---|---|
| [Licht](apps/licht/) | Matter-Lampen steuern (statt LEDVANCE SMART+ App), ohne Cloud | Windows (eigenes Fenster) / Raspberry Pi + Browser, Android | stabil, Android experimentell | [Licht.exe](https://github.com/R3adux/Apps/releases/download/licht-latest/Licht.exe) · [Licht.apk](https://github.com/R3adux/Apps/releases/download/licht-android/Licht.apk) |
| [Stratagem](apps/stratagem/) | Helldivers 2: Stratagems per Radialmenü, Taste oder Handy aufrufen, Liste direkt aus dem Spiel lesen | Windows + Browser | experimentell, im Spiel noch ungetestet | [Stratagem.exe](https://github.com/R3adux/Apps/releases/download/stratagem-latest/Stratagem.exe) |

Jede App hat im eigenen Ordner eine README mit Anleitung (für Nutzer) und Entwicklerhinweisen.
Alle Downloads: [Releases](https://github.com/R3adux/Apps/releases).

## Aufbau

```
apps/
  licht/            eine App = ein Ordner, alles darin (Code, Tests, Doku, Web-Oberfläche)
  stratagem/
docs/
  NEUE-APP.md       Regeln und Checkliste für neue Apps
.github/
  workflows/        <app>-<plattform>.yml – baut, testet und veröffentlicht je App
  scripts/          <app>-….sh – Hilfsskripte der Workflows
```

- **Eine App, ein Ordner** unter `apps/`. Apps hängen nicht voneinander ab; ein Ordner lässt sich jederzeit
  in ein eigenes Repo herauslösen.
- **Builds** laufen in GitHub Actions, nur für die App, deren Ordner sich geändert hat. Im Actions-Tab steht
  der App-Name vorne („Licht · Windows“, „Stratagem · Windows“).
- **Downloads** liegen als Release `<app>-latest` (bzw. `<app>-<plattform>`) mit festem Link, der sich nie ändert.

Neue App anlegen: [docs/NEUE-APP.md](docs/NEUE-APP.md).
