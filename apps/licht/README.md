# Licht – lokale Lampensteuerung ohne Cloud

Ersatz für die LEDVANCE SMART+ App, gebaut für die **SMART+ MATTER Classic A60 Multicolor**.
Funktioniert mit jeder Matter-Lampe über WLAN.

Licht spricht **direkt im Heimnetz** mit den Lampen, über den offenen Standard **Matter**.
Es gibt keine Cloud, kein Konto, kein Tuya und kein LEDVANCE dazwischen.

- An/Aus, Helligkeit, Weißton (warm ↔ kalt), Farbe, Sättigung, Farb-Vorlagen
- „Alle aus / Alle an“, Live-Updates in allen offenen Fenstern
- Lampen umbenennen (der Name wird in der Lampe gespeichert), teilen, entfernen
- Läuft im Browser auf PC und Handy. Auf dem Handy kannst du sie über „Zum Startbildschirm hinzufügen“ wie eine App nutzen

## Aufbau

```
 PC-Browser ─┐                                         ┌─ Lampe 1
             ├──►  Licht-Zentrale (Pi oder PC)  ──Matter──┼─ Lampe 2
 Handy ──────┘     server/ + core/ + web/               └─ …
```

| Teil | Was |
|---|---|
| `scripts/build-exe.mjs` | Packt alles in eine einzige Datei (`Licht.exe`) |
| `core/` | Matter-Steuerung (Lampen aufnehmen, schalten, teilen). Wird auch von der Android-App genutzt |
| `server/` | Die Zentrale: Web-Server + API, läuft dauerhaft auf Pi oder PC |
| `web/` | Die Oberfläche (HTML/CSS/JS, kein Build-Schritt) |

Jede Matter-Lampe akzeptiert mindestens 5 Steuergeräte. Die Zentrale und später die Android-App
steuern die Lampe deshalb unabhängig voneinander. Die SMART+ App kann parallel weiterlaufen,
du brauchst sie aber nicht mehr.

## Zentrale einrichten

### Windows-PC: Licht.exe

1. **[Licht.exe herunterladen](https://github.com/R3adux/Apps/releases/download/licht-latest/Licht.exe)**
   und irgendwo ablegen, z. B. auf dem Desktop.
2. Doppelklicken. Licht startet und öffnet sich im Browser (`http://localhost:8080`).
   - Beim ersten Mal warnt Windows-SmartScreen, weil die Datei nicht signiert ist:
     **„Weitere Informationen“ → „Trotzdem ausführen“**.
   - Fragt die Firewall nach: **„Private Netzwerke“ erlauben**, sonst findet Licht die Lampe nicht.
3. Licht läuft im Hintergrund, ohne schwarzes Fenster. Unten rechts in der Taskleiste ist ein
   **Licht-Symbol** (eventuell hinter dem Pfeil `^`):
   - **Klick**: App im Browser öffnen. Ein erneuter Doppelklick auf `Licht.exe` macht dasselbe.
   - **Rechtsklick**: **Mit Windows starten** (an/aus) und **Beenden**.
   - Beides gibt es auch in der App im Menü **⋯** (nur auf dem PC selbst).

Nichts weiter zu installieren. Die Kopplungsdaten liegen in `C:\Users\<Name>\.licht`, dort steht
auch das Protokoll `licht.log`, falls einmal etwas nicht klappt.
Ein Update: Licht beenden, die neue `Licht.exe` an dieselbe Stelle legen und starten.

> Läuft die Zentrale auf dem PC, geht das Handy nur, solange der PC an ist.
> Die Android-App (siehe unten) braucht keinen PC.

### Android-Handy: Licht.apk (experimentell)

Die Android-App hat ihre eigene Matter-Steuerung eingebaut und funktioniert **ohne laufenden PC**.

1. Auf dem Handy **[Licht.apk herunterladen](https://github.com/R3adux/Apps/releases/download/licht-android/Licht.apk)** und öffnen.
   Android fragt, ob der Browser Apps installieren darf: erlauben.
2. In der PC-Version von Licht beim Lampen-Menü **⋮ → Mit anderem Gerät teilen** wählen.
3. In der Handy-App **⋯ → Lampe hinzufügen** wählen und den angezeigten Code eingeben.
   Klappt das nicht, unter „Erweitert“ zusätzlich die IP-Adresse der Lampe angeben.

Handy und Lampe müssen im selben WLAN sein.

### Raspberry Pi (oder aus dem Quellcode)

```sh
# Node.js 22 installieren (einmalig)
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
sudo apt install -y nodejs git

git clone https://github.com/R3adux/Apps.git
cd Apps/licht
npm install
npm start
```

Danach im Browser (PC oder Handy) die angezeigte Adresse öffnen, z. B. `http://192.168.178.20:8080`.

**Autostart:** In `server/licht.service` Benutzer und Pfad prüfen, dann:

```sh
sudo cp server/licht.service /etc/systemd/system/
sudo systemctl enable --now licht
```

## Lampe hinzufügen

Im Menü **⋯ → Lampe hinzufügen** den **Matter-Code** eingeben: 11 Ziffern von der Lampe oder
der Verpackung, z. B. `3497-011-2332`.

Wichtig: Eine Lampe nimmt neue Steuergeräte nur an, wenn sie gerade **im Kopplungsmodus** ist.
Dafür gibt es drei Wege:

**A) Die Lampe ist schon in der SMART+ App eingerichtet**
Suche in der SMART+ App in den Lampen-Einstellungen nach „Matter“, „Teilen“ oder
„Mit anderer App verbinden“. Dort entsteht ein neuer Code, der ein paar Minuten gültig ist.
Diesen Code gibst du in Licht ein.

**B) Lampe zurücksetzen und per Bluetooth einrichten** (Zentrale mit Bluetooth, z. B. Pi 3/4/5)
1. Zentrale mit Bluetooth starten (nur aus dem Quellcode, nicht mit Licht.exe):
   einmalig `npm install @matter/nodejs-ble`, dann `LICHT_BLE=1 npm start`
   (auf dem Pi ggf. einmalig: `sudo setcap cap_net_raw+eip $(readlink -f $(which node))`)
2. Lampe zurücksetzen: 5× aus- und einschalten, bis sie blinkt.
3. In Licht den Code von der Lampe eingeben. Unter **Erweitert** zusätzlich WLAN-Name und
   -Passwort eintragen. Die Lampe bekommt die WLAN-Daten per Bluetooth.

**C) Über Google Home (Android)**, wenn die Zentrale kein Bluetooth hat
1. Lampe zurücksetzen und mit der Google-Home-App einrichten (Code von der Lampe).
2. In Google Home: Lampe → Einstellungen → **Verknüpfte Matter-Apps und -Dienste** →
   **App oder Dienst verknüpfen**. Es erscheint ein neuer Code.
3. Diesen Code in Licht eingeben. Google Home kannst du danach wieder entfernen.

Tipp: Wenn die Suche lange dauert, unter **Erweitert** die IP-Adresse der Lampe angeben
(steht in der Geräteliste deines Routers).

### Mit dem Handy teilen

Lampen-Menü **⋮ → Mit anderem Gerät teilen** zeigt einen Code, der 15 Minuten gilt.
Diesen Code in der Licht-App auf dem Handy unter „Lampe hinzufügen“ eingeben.

## Wenn etwas nicht klappt

| Problem | Lösung |
|---|---|
| Lampe wird nicht gefunden | Ist sie im Kopplungsmodus (siehe oben)? Windows-Firewall für private Netze erlaubt? IP-Adresse unter *Erweitert* angeben. |
| „Verbinde …“ | Licht baut die Verbindung zur Lampe gerade (wieder) auf, z. B. nach dem Start oder wenn die Lampe Strom bekommt. Dauert meist nur Sekunden; schalten kannst du trotzdem. |
| „Nicht erreichbar“ | Lampe hat keinen Strom, oder Zentrale und Lampe sind in verschiedenen Netzen (z. B. Gäste-WLAN). Sobald sie wieder da ist, verbindet Licht sich von selbst. |
| PC: Licht startet nicht | Meldung lesen; Details in `C:\Users\<Name>\.licht\licht.log`. |
| Ausführliche Meldungen | `LICHT_LOG=info npm start` |

Einstellungen über Umgebungsvariablen: `PORT` (Standard 8080), `LICHT_DATA` (Speicherort der
Kopplungsdaten, Standard `~/.licht`), `LICHT_BLE=1`, `LICHT_LOG`.

Die Zentrale ist für das Heimnetz gedacht und hat kein Passwort. Jeder im WLAN kann das
Licht steuern, wie bei einem Lichtschalter. Gib den Port nicht ins Internet frei.

## Entwicklung

```sh
npm test                       # alle Tests, inkl. Ende-zu-Ende-Test mit simulierter Matter-Lampe
npm start                      # Zentrale starten
node scripts/build-exe.mjs     # Einzeldatei bauen (Licht.exe unter Windows)
```

Die Windows-`Licht.exe` baut GitHub Actions (`.github/workflows/licht-windows.yml`) bei jeder
Änderung in `main`. Sie wird auf Windows gestartet und geprüft und dann als Release
`licht-latest` veröffentlicht.

Ohne Lampen ausprobieren: `http://localhost:8080/?demo` (Demo beenden im Menü).

`test/e2e.test.js` startet eine simulierte Matter-Farblampe (`test/support/virtual-light.mjs`)
und prüft über das echte Matter-Protokoll: Aufnehmen per Code, Farbe/Weiß/Helligkeit,
Umbenennen, Teilen mit einer zweiten Zentrale, gegenseitige Live-Updates und Entfernen.
