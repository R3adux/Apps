# Licht – lokale Lampensteuerung ohne Cloud

Ersatz für die LEDVANCE SMART+ App, gebaut für die **SMART+ MATTER Classic A60 Multicolor**.
Funktioniert mit jeder Matter-Lampe über WLAN.

Licht spricht **direkt im Heimnetz** mit den Lampen, über den offenen Standard **Matter**.
Es gibt keine Cloud, kein Konto, kein Tuya und kein LEDVANCE dazwischen.

- An/Aus, Helligkeit, Weißton (warm ↔ kalt), Farbe, Sättigung, Farb-Vorlagen
- „Alle aus / Alle an“, Live-Updates auf allen Geräten
- Lampen umbenennen (der Name wird in der Lampe gespeichert), entfernen
- **Teilen per QR-Code:** alle eingerichteten Lampen auf einmal an ein anderes Gerät weitergeben –
  dort muss niemand die Lampen neu einrichten
- Für **Windows** (eigenes Programm, kein Browser) und **Android** (eigene App). Beide brauchen
  einander nicht und laufen ohne Server.

## Aufbau

```
 Licht.exe (Windows) ──┐                ┌─ Lampe 1
                       ├──── Matter ────┼─ Lampe 2
 Licht.apk (Android) ──┘                └─ …
```

| Teil | Was |
|---|---|
| `core/` | Matter-Steuerung (Lampen aufnehmen, schalten, teilen). Nutzen Windows und Android |
| `pc/` | Windows: der Lampen-Teil von `Licht.exe` (Node.js) |
| `pc/fenster/` | Windows: das Licht-Fenster mit Taskleisten-Symbol (C#/WPF, `LichtFenster.exe`) |
| `web/` | Die Oberfläche der Android-App (HTML/CSS/JS) |
| `android-app/` | Android: die App mit eingebauter Matter-Steuerung |
| `scripts/build-exe.mjs` | Packt den Windows-Teil in eine einzige Datei (`Licht.exe`) |

Jede Matter-Lampe akzeptiert mindestens 5 Steuergeräte. PC und Handy steuern die Lampe deshalb
unabhängig voneinander. Die SMART+ App kann parallel weiterlaufen, du brauchst sie aber nicht mehr.

## Einrichten

### Windows-PC: Licht.exe

1. **[Licht.exe herunterladen](https://github.com/R3adux/Apps/releases/download/licht-latest/Licht.exe)**
   und irgendwo ablegen, z. B. auf dem Desktop.
2. Doppelklicken. Licht öffnet sich in einem **eigenen kleinen Fenster** unten rechts – ein ganz
   normales Windows-Programm, kein Browser.
   - Beim ersten Mal warnt Windows-SmartScreen, weil die Datei nicht signiert ist:
     **„Weitere Informationen“ → „Trotzdem ausführen“**.
   - Fragt die Firewall nach: **„Private Netzwerke“ erlauben**, sonst findet Licht die Lampe nicht.
3. Das Fenster schließen beendet Licht nicht: Licht läuft im Hintergrund weiter, unten rechts in der
   Taskleiste ist ein **Lampen-Symbol** (eventuell hinter dem Pfeil `^`):
   - **Klick**: Fenster öffnen – noch ein Klick legt es wieder weg. Ein erneuter Doppelklick auf
     `Licht.exe` öffnet es ebenfalls.
   - **Rechtsklick**: **Licht öffnen**, **Mit Windows starten** (an/aus) und **Licht beenden**.
   - Dasselbe gibt es im Fenster im Menü **⋯**, dazu **Lampe hinzufügen** und **Alle Lampen teilen**.
   - Hell oder dunkel wie Windows; das Fenster merkt sich Position und Größe.

Nichts weiter zu installieren (das Fenster nutzt .NET Framework 4.8, das bei Windows 10/11 dabei ist).
Licht öffnet keinen Browser und hat keinen Webserver: es spricht nur mit den Lampen.

Alles von Licht liegt in `C:\Users\<Name>\.licht`: die Kopplungsdaten, das Protokoll `licht.log`
(falls einmal etwas nicht klappt), `fenster\` (das Fenster-Programm, das Licht.exe beim Start dort
auspackt), `fenster-lampen.json` (die Lampen für die sofortige Anzeige beim Start) und
`fenster-platz.json` (Position des Fensters).
Ein Update: Licht beenden, die neue `Licht.exe` an dieselbe Stelle legen und starten.
`Licht.exe --quit` beendet ein laufendes Licht (z. B. für Skripte).

### Android-Handy: Licht.apk

Die Android-App hat ihre eigene Matter-Steuerung eingebaut und funktioniert **ohne PC**.

1. Auf dem Handy **[Licht.apk herunterladen](https://github.com/R3adux/Apps/releases/download/licht-android/Licht.apk)** und öffnen.
   Android fragt, ob der Browser Apps installieren darf: erlauben.
2. Lampen übernehmen: Am PC in Licht **⋯ → Alle Lampen teilen** wählen. In der Handy-App
   **⋯ → Lampe hinzufügen → QR-Code scannen** und den QR-Code vom Bildschirm abfotografieren.
   Alle Lampen kommen auf einmal dazu. (Beim ersten Scan fragt Android, ob Licht die Kamera benutzen darf.)

Handy und Lampe müssen im selben WLAN sein.

Licht bleibt nach dem Schließen im Hintergrund bereit, damit es beim nächsten Mal (und später über das
Widget) sofort schaltet. Dabei schläft es: Nach einer Minute im Hintergrund hält es keine Abos zu den Lampen
mehr, die Lampen wecken das Handy also nicht. Android zeigt dafür evtl. eine stille Benachrichtigung
„Licht ist bereit“ (ab Android 13 nur, wenn Licht Benachrichtigungen zeigen darf).
Im Menü **⋯ → Messwerte** steht, wie lange der Start gedauert hat und wie schnell die Lampen aus dem
Schlaf antworten.

## Lampe hinzufügen

Im Menü **⋯ → Lampe hinzufügen**:
- am Handy **QR-Code scannen** (den QR-Code auf der Lampe oder der Verpackung), oder
- den **Matter-Code** eingeben: 11 Ziffern von der Lampe oder der Verpackung, z. B. `3497-011-2332`.

Wichtig: Eine Lampe nimmt neue Steuergeräte nur an, wenn sie gerade **im Kopplungsmodus** ist.
Dafür gibt es zwei Wege:

**A) Die Lampe ist schon in der SMART+ App eingerichtet**
Suche in der SMART+ App in den Lampen-Einstellungen nach „Matter“, „Teilen“ oder
„Mit anderer App verbinden“. Dort entsteht ein neuer Code, der ein paar Minuten gültig ist.
Diesen Code gibst du in Licht ein.

**B) Über Google Home (Android)**, für eine neue oder zurückgesetzte Lampe
1. Lampe zurücksetzen (5× aus- und einschalten, bis sie blinkt) und mit der Google-Home-App einrichten
   (Code von der Lampe).
2. In Google Home: Lampe → Einstellungen → **Verknüpfte Matter-Apps und -Dienste** →
   **App oder Dienst verknüpfen**. Es erscheint ein neuer Code.
3. Diesen Code in Licht eingeben. Google Home kannst du danach wieder entfernen.

Tipp: Wenn die Suche lange dauert, unter **Erweitert** die IP-Adresse der Lampe angeben
(steht in der Geräteliste deines Routers). **Im Netz suchen** zeigt, ob eine Lampe gerade bereit ist.

### Lampen teilen (QR-Code)

Damit nicht jeder alle Lampen neu einrichten muss: Hat ein Gerät die Lampen schon, gibt es sie weiter.

- **Alle Lampen:** Menü **⋯ → Alle Lampen teilen**. **Eine Lampe:** ihr Menü **⋮ → Teilen (QR-Code)**.
- Licht zeigt einen QR-Code (15 Minuten gültig). Auf dem anderen Gerät **Lampe hinzufügen →
  QR-Code scannen** – alle Lampen darin kommen auf einmal dazu, auch mit Namen. Lampen, die das andere
  Gerät schon hat, werden erkannt und übersprungen.
- Ein PC hat meist keine Kamera: dort **Code kopieren** auf dem einen Gerät und den Text (beginnt mit
  `MT:`) auf dem PC unter **Lampe hinzufügen** einfügen. Für eine einzelne Lampe zeigt Licht zusätzlich
  11 Ziffern zum Abtippen – die gehen auch in anderen Matter-Apps (z. B. SMART+).
- Lampen, die beim Teilen gerade nicht erreichbar sind, sind nicht dabei; Licht sagt, welche.

### Verbundene Apps (wenn eine Lampe „voll“ ist)

Eine Matter-Lampe nimmt nur wenige Apps auf – die SMART+ A60 höchstens **5** (z. B. SMART+, Google Home, Licht am
PC, Licht am Handy …). Ist sie voll, kann kein weiteres Gerät sie hinzufügen; Licht sagt das beim Teilen.
Dann im Lampen-Menü **⋮ → Verbundene Apps …** nachsehen: Dort steht jede App, die die Lampe steuern darf, und
nicht mehr benutzte (z. B. eine frühere Installation von Licht) lassen sich **entfernen** – ohne die Lampe
zurückzusetzen. Jedes Licht heißt dort mit einem eigenen Kürzel, z. B. „Licht PC 3F2A“ oder „Licht Handy 08E1“,
und sich selbst zeigt es als „Dieses Licht“.

## Wenn etwas nicht klappt

| Problem | Lösung |
|---|---|
| Lampe wird nicht gefunden | Ist sie im Kopplungsmodus (siehe oben)? Windows-Firewall für private Netze erlaubt? IP-Adresse unter *Erweitert* angeben. |
| „Verbinde …“ | Licht baut die Verbindung zur Lampe gerade (wieder) auf, z. B. nach dem Start oder wenn die Lampe Strom bekommt. Dauert meist nur Sekunden; schalten kannst du trotzdem. |
| „Nicht erreichbar“ | Lampe hat keinen Strom, oder Gerät und Lampe sind in verschiedenen Netzen (z. B. Gäste-WLAN). Sobald sie wieder da ist, verbindet Licht sich von selbst. |
| Geteilter Code geht nicht | Er gilt 15 Minuten und für jede Lampe einmal: auf dem anderen Gerät neu teilen. |
| „Schon mit 5 Apps verbunden“ | Die Lampe ist voll: **⋮ → Verbundene Apps …** und eine nicht mehr benutzte App entfernen (siehe oben). |
| Handy: Kamera geht nicht | Android-Einstellungen → Apps → Licht → Berechtigungen → Kamera erlauben. |
| PC: Licht startet nicht | Meldung lesen; Details in `C:\Users\<Name>\.licht\licht.log`. |

Einstellungen über Umgebungsvariablen (PC, vor allem zur Fehlersuche): `LICHT_DATA` (Speicherort,
Standard `~/.licht`), `LICHT_LOG` (ausführlichere Matter-Meldungen, z. B. `info`).

## Entwicklung

```sh
npm test                                  # alle Tests, inkl. Ende-zu-Ende-Tests mit simulierten Matter-Lampen
cd pc/fenster && dotnet test --project Licht.Fenster.Tests   # Logik des Windows-Fensters (geht überall)
node scripts/build-exe.mjs                # Licht.exe bauen (unter Windows, braucht das .NET SDK)
```

Das Windows-Fenster (`pc/fenster/`):
- `Licht.Fenster.Core` – alles außer dem Zeichnen: Gespräch mit Licht, Lampenzustände, Senden,
  Teilen, Hinzufügen, QR-Code. Wird mit `Licht.Fenster.Tests` auf jedem System getestet.
- `LichtFenster` – das WPF-Fenster (XAML) und das Taskleisten-Symbol.
  `LichtFenster.exe --render <Ordner>` zeichnet das Fenster mit erfundenen Lampen in allen Zuständen
  (hell und dunkel) als PNG – so lässt sich jede Änderung am Aussehen prüfen.
- `Licht.exe` enthält die Dateien des Fensters, packt sie beim Start nach `.licht\fenster\<Version>\` aus,
  startet es und spricht mit ihm zeilenweise über Standard-Ein-/Ausgabe (Nachrichten siehe `pc/bridge.js`).

Die Windows-`Licht.exe` baut GitHub Actions (`.github/workflows/licht-windows.yml`) bei jeder Änderung in
`main`: Tests, Bilder des Fensters (Artefakt „Licht-Fenster-Bilder“), dann wird `Licht.exe` auf Windows
gestartet und per Fernsteuerung bedient (Fenster, Dialog, Fehlermeldung, Schließen, zweiter Start, kein
Browser, kein offener Port, Beenden). Danach wird sie als Release `licht-latest` veröffentlicht.

Die Oberfläche der Android-App ohne Handy ansehen: `web/` mit einem kleinen Webserver öffnen – ohne die
App zeigt sie erfundene Lampen (Demo).

`test/e2e.test.js` startet simulierte Matter-Farblampen (`test/support/virtual-light.mjs`) und prüft über
das echte Matter-Protokoll: Aufnehmen per Code, Farbe/Weiß/Helligkeit, Umbenennen, Teilen mit einer
zweiten Licht (einzeln und alle Lampen mit einem QR-Code), gegenseitige Live-Updates und Entfernen.
