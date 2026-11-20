# Licht für Android

Die Android-App von Licht (React Native). Sie enthält ihre eigene Matter-Steuerung (`../core/`, dieselbe
wie unter Windows) und braucht keinen PC. Bedienung und Einrichtung: siehe [../README.md](../README.md).

| Teil | Was |
|---|---|
| `src/App.js` | Startet die Matter-Steuerung und zeigt die Oberfläche in einem WebView |
| `src/bridge.js` | Verbindung Oberfläche ↔ Matter-Steuerung (`window.LichtApi`, Aufrufe wie in `../core/api.js`) |
| `src/web-html.js` | Die Oberfläche aus `../web/` als eine Seite – erzeugt von `scripts/build-web.mjs`, nicht im Repository |
| `android/app/src/main/java/de/r3adux/licht/LichtService.kt` | Hält Licht im Hintergrund bereit (Vordergrund-Dienst); im Hintergrund schläft der Lampen-Teil (`core/hub.js` pause) |
| `android/app/src/main/java/de/r3adux/licht/LichtTaskService.kt`, `src/background.js` | Nach dem Verlassen der App: eine kurze Hintergrund-Aufgabe, in der React Native die Zeitgeber laufen lässt (sonst hält es sie im Hintergrund an) – Licht schläft nach einer Minute ein, dann endet sie |
| `src/timings.js` | Zeiten des Starts für „Messwerte“ |
| `src/selftest.js` | Selbsttest im Emulator (`licht://selftest?…`, siehe `.github/scripts/licht-android-selftest.sh`) |
| `scripts/probe.mjs` | Schnelltest: der Matter-Teil wie in der App verpackt, gegen eine simulierte Lampe |

Die Seite läuft nur in der App: Sie lädt nichts aus dem Netz, und der WebView lässt keine andere Adresse zu.
Die Kamera (QR-Code scannen) fragt Android beim ersten Mal; das Bild wird nur auf dem Handy ausgewertet.

## Bauen

```sh
(cd .. && npm ci)                 # Pakete des Licht-Ordners (Matter, QR-Code), auch für die Oberfläche
npm ci
node scripts/build-web.mjs        # Oberfläche einbetten
cd android && ./gradlew assembleRelease
```

GitHub Actions (`.github/workflows/licht-android.yml`) baut die App bei jeder Änderung in `main`,
veröffentlicht sie als Release `licht-android` und startet sie danach im Emulator.
