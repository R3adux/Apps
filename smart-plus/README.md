# Licht – schnelle Steuerung für SMART+ Lampen

Eine schlanke Web-App als Ersatz für die LEDVANCE SMART+ App, gebaut für die
**SMART+ WiFi Classic A60 E27 Multicolor** und andere Tuya-basierte WLAN-Lampen.

- An/Aus, Helligkeit, Farbtemperatur (warm ↔ kalt), Farbe und Sättigung
- Farb-Vorlagen mit einem Tipp, „Alle aus / Alle an“
- Öffnet sofort, kein Login-Zwang bei Tuya/LEDVANCE, keine Werbung, kein Tracking
- Lässt sich auf dem Handy wie eine App installieren („Zum Startbildschirm hinzufügen“).
  Ein langer Druck auf das Icon bietet die Abkürzung **„Alle aus“**
- Demo-Modus zum Ausprobieren ohne Lampen

## Wie es funktioniert

```
Browser / Handy  ──►  Cloudflare Worker (deine eigene URL)  ──►  Tuya-Cloud (EU)  ──►  Lampe
   public/             src/worker.js – prüft dein Passwort,
                       signiert die Anfragen an Tuya
```

Ein Browser kann nicht direkt mit der Lampe im WLAN sprechen, und die Tuya-Cloud
lässt sich aus dem Browser nicht direkt aufrufen. Deshalb läuft ein kleiner
**Cloudflare Worker** dazwischen. Er ist kostenlos, läuft in der Cloud und
braucht keinen Raspberry Pi oder anderen Rechner zu Hause. Deine Tuya-Zugangsdaten
liegen nur dort, nie im Browser.

Die Lampe selbst ist immer mit der Tuya-Cloud verbunden – das ist bei der
originalen SMART+ App genauso. Diese App ersetzt nur die langsame App-Oberfläche.

## Einrichtung (einmalig, ca. 20 Minuten)

### 1. Lampe in die Smart-Life-App umziehen

Die SMART+ App ist eine Tuya-„Sonderversion“, deren Konto sich nicht mit eigenen
Tuya-Projekten verbinden lässt. Die normale **Smart Life**-App nutzt dieselbe Cloud
und funktioniert.

1. **Smart Life** installieren (Android/iOS) und ein Konto anlegen – Land: **Deutschland**.
2. Lampe in der SMART+ App entfernen.
3. Lampe zurücksetzen: 5× aus- und einschalten (jeweils ca. 1 Sekunde), bis sie schnell blinkt.
4. In Smart Life **Gerät hinzufügen** → Beleuchtung → Lampe (WLAN). Am besten der Lampe
   gleich einen guten Namen geben, z. B. „Wohnzimmer“ – der erscheint später in der App.

Die Smart-Life-App brauchst du danach nur noch selten (z. B. für neue Lampen).

### 2. Tuya-Entwicklerprojekt anlegen

1. Auf [platform.tuya.com](https://platform.tuya.com) registrieren (kostenlos).
2. **Cloud → Development → Create Cloud Project**
   - Industry: *Smart Home*, Development Method: *Smart Home*
   - Data Center: **Central Europe Data Center**
3. Bei den API-Diensten mindestens **IoT Core** und **Authorization Token Management** aktiviert lassen.
4. Im Projekt: **Devices → Link App Account → Add App Account**. Einen QR-Code anzeigen
   lassen und mit der Smart-Life-App scannen (Ich → Scan-Symbol oben rechts).
   Bei der Geräteberechtigung **„Controllable“ / „Steuerbar“** wählen.
5. Danach erscheint die Lampe unter *All Devices*.
6. Unter **Overview** stehen **Access ID** und **Access Secret** – die brauchst du gleich.

> **Hinweis:** Der kostenlose „IoT Core“-Tarif ist eine Testphase, die Tuya alle
> 1–6 Monate verlängern lässt (Cloud → *Cloud Services* → IoT Core → *Extend Trial Period*,
> kurzes Formular). Wenn die App plötzlich „No permissions“ oder „expired“ meldet, ist das der Grund.

### 3. Auf Cloudflare veröffentlichen

Geht komplett im Browser, auch vom Handy aus:

1. Kostenloses Konto auf [dash.cloudflare.com](https://dash.cloudflare.com) anlegen.
2. **Workers & Pages → Create → Import a repository** → GitHub verbinden → Repository
   `r3adux/apps` wählen.
3. Einstellungen:
   - Project name: `smart-plus`
   - Root directory (unter *Advanced*): `smart-plus`
   - Deploy command: `npx wrangler deploy`
4. Nach dem ersten Deploy: Worker öffnen → **Settings → Variables and Secrets** → drei
   **Secrets** anlegen:

   | Name | Wert |
   |---|---|
   | `TUYA_CLIENT_ID` | Access ID aus Schritt 2 |
   | `TUYA_CLIENT_SECRET` | Access Secret aus Schritt 2 |
   | `APP_PASSWORD` | Ein langes Passwort deiner Wahl – damit meldest du dich in der App an |

5. Die App läuft unter `https://smart-plus.<dein-name>.workers.dev`. Auf dem Handy öffnen,
   Passwort eingeben, im Browser-Menü **„Zum Startbildschirm hinzufügen“**.

Jede Änderung, die in diesem Ordner auf GitHub landet, veröffentlicht Cloudflare
danach automatisch.

<details>
<summary>Alternativ per Kommandozeile (mit Node.js)</summary>

```sh
cd smart-plus
npx wrangler@4 login
npx wrangler@4 secret put TUYA_CLIENT_ID
npx wrangler@4 secret put TUYA_CLIENT_SECRET
npx wrangler@4 secret put APP_PASSWORD
npm run deploy
```
</details>

## Wenn etwas nicht klappt

| Meldung | Ursache / Lösung |
|---|---|
| „Keine Lampen gefunden“ | Smart-Life-Konto nicht mit dem Projekt verknüpft, oder falsches Rechenzentrum. Steht dein Konto unter *Western Europe*, in `wrangler.toml` `TUYA_ENDPOINT = "https://openapi-weaz.tuyaeu.com"` setzen. |
| `Tuya 1004: sign invalid` | Access ID oder Secret falsch kopiert. |
| `Tuya 1106: permission deny` | Gerät nur „Read Only“ verknüpft – in Tuya unter *Devices* auf „Controllable“ stellen. |
| Fehler mit „No permissions“ / „expired“ | IoT-Core-Testphase abgelaufen – verlängern (siehe oben). |
| „Falsches Passwort“ | `APP_PASSWORD` im Worker prüfen. |

## Entwicklung

```sh
npm test          # Tests (Node 18+, keine Abhängigkeiten)
npm run dev       # lokal starten; Secrets in .dev.vars eintragen (siehe unten)
```

`.dev.vars` (wird nicht eingecheckt):

```
TUYA_CLIENT_ID=...
TUYA_CLIENT_SECRET=...
APP_PASSWORD=...
```

Nur die Oberfläche ansehen: `python3 -m http.server -d public` und „Demo“ wählen.

| Datei | Inhalt |
|---|---|
| `src/worker.js` | API (`GET /api/lights`, `POST /api/lights/:id`) und Passwortprüfung |
| `src/tuya.js` | Tuya-Cloud-Client mit Request-Signierung und Token-Verwaltung |
| `src/light.js` | Übersetzung zwischen Tuya-Datenpunkten und dem einfachen Lampenmodell |
| `public/` | Web-App (HTML/CSS/JS ohne Build-Schritt), Service Worker, Manifest |

## Ideen für später

- Räume / Gruppen und eigene Szenen
- Zeitpläne und Automationen (z. B. Sonnenuntergang, Aufwachlicht)
- Lokale Steuerung im WLAN ohne Cloud (bräuchte doch einen kleinen Rechner zu Hause)
