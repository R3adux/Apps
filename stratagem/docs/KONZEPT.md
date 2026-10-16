# Stratagem – Konzept, Recherche, schwierige Stellen

Stand: Oktober 2026. Ziel: Stratagems in Helldivers 2 ohne Pfeil-Tipperei aufrufen – per Radialmenü,
direkter Taste, Handy oder (später) Stream Deck. Langfristig liest das Tool die verfügbaren Stratagems
selbst vom Bildschirm, sodass nie eine Liste gepflegt werden muss.

## 1. Aufbau (modular)

```
 Auslöser                         Kern (Stratagem.Core, ohne Windows)                Ausgabe
 ─────────                        ─────────────────────────────────────               ───────
 Tastatur/Maus ──► InputHooks ──► InputRouter ─┬─► RadialController ─┐
                  (Windows)                    └─► direkte Tasten ────┤
 Weboberfläche/Handy ──► WebServer ───────────────────────────────────┼─► StratagemService ─► MacroPlanner ─► MacroRunner ─► SendInput ─► Spiel
 Stream Deck (pausiert) ──► WebServer (/api/activate) ─────────────────┘         ▲                                            (Windows)
                                                                                │
                                   Liste: eigene Liste | Bildschirm (geplant) ───┤
                                   Tastenbelegung: input_settings.config ────────┘
```

| Projekt | Inhalt | Läuft auf |
|---|---|---|
| `src/Stratagem.Core` | Codes, Tasten (Scancodes), Makro-Planung und -Ablauf, Radialmenü-Logik als Zustandsautomat, Spieldatei-Leser (SJSON), Einstellungen, zentraler Dienst | jedem OS, voll getestet |
| `src/Stratagem.Server` | Webserver (Kestrel): Weboberfläche, Handy-Deck, JSON/WebSocket-API | jedem OS, getestet |
| `src/Stratagem.Windows` | SendInput, Low-Level-Hooks, Spielfenster-Erkennung, Overlay, Tray, Autostart, Steam-Pfade | nur Windows, dünn gehalten |
| `src/Stratagem.App` | `Stratagem.exe`: verbindet alles | Windows |
| `src/Stratagem.Dev` | Entwicklungs-Start ohne Windows/Spiel (Eingaben werden nur ausgegeben) | jedem OS |
| `web/` | Oberfläche (HTML/CSS/JS ohne Build-Schritt, wie bei Licht) | Browser |

**Warum so geschnitten:** Alles, was schiefgehen kann, ist Logik – und die liegt im Kern, ohne Windows,
mit Tests (111 Stück): Welche Taste wird geschluckt, wann wird ausgelöst, was passiert bei Abbruch,
Fokusverlust, Tastenkombinationen, gehaltenen Lauftasten. Der Windows-Teil übersetzt nur Ereignisse.

**Erweiterungspunkte** (Schnittstellen im Kern):
- `IStratagemSource` – woher die Liste kommt. Heute: eigene Liste. Geplant: Bildschirmleser.
- `IInputOutput` – wohin Eingaben gehen (Windows: SendInput; Tests: Aufzeichnung).
- `IRadialView` – wie das Radialmenü aussieht (Windows: Overlay; Tests/Dev: Konsole).
- `IGameWatcher` – ob das Spiel im Vordergrund ist.
- HTTP-API (`docs/API.md`) – jeder weitere Auslöser (Stream Deck, Touch Portal, Macro Deck, Skripte)
  braucht nur `POST /api/activate`. Keiner tippt selbst: Timing-Fixes wirken überall.

**Zukunftssicher durch Daten statt Code:** Fenstertitel, Prozessname, Tastenbelegung, Timing und die Liste
stehen in `settings.json` bzw. kommen aus der Spieldatei. Ein Spiel-Patch braucht im Normalfall kein
neues Programm. Der Code eines Stratagems kommt immer vom Spiel oder vom Spieler – der mitgelieferte
Katalog (`catalog.json`, Grundlage: MIT-lizenzierte Tabelle aus spyoungtech/helldivers2-macros) liefert
nur Namen und kann veralten, ohne etwas kaputtzumachen.

### Technik-Entscheidung: C#/.NET 10
- Systemweite Low-Level-Hooks rufen bei **jedem** Tastendruck/jeder Mausbewegung am ganzen PC unser
  Programm auf und warten auf die Antwort. In Node.js würde jede Pause der Event-Loop die Eingabe am
  ganzen PC verzögern. In .NET laufen die Hooks auf einem eigenen Thread mit eigener Nachrichtenschleife.
- Overlay (Fenster mit Alpha pro Pixel), Tray, Registry, SendInput: alles direkt verfügbar.
- Eine einzige `Stratagem.exe` (self-contained, ~64 MB), nichts zu installieren – wie `Licht.exe`.
  Kleiner ginge es mit installierter .NET-Runtime (~5 MB), das wäre aber eine Hürde für Spieler.

### Leistung: kein merkbarer Einfluss aufs Spiel

| Was | Wie |
|---|---|
| Außerhalb des Spiels / pausiert | **Keine Hooks installiert** – kein einziger Tastendruck, keine Mausbewegung läuft durch das Tool. |
| Im Spiel, Menü zu | Nur der Tastatur-Hook. Pro Taste ein paar Speicherzugriffe, keine Systemaufrufe, kein Datei-Zugriff. **Kein Maus-Hook**: Mausbewegungen (bei Gaming-Mäusen bis 8000/s) gehen direkt ans Spiel. |
| Menü offen | Maus-Hook wird in Mikrosekunden eingehängt und beim Schließen wieder entfernt. Nur mit einer Maustaste als Auslöser/Taste bleibt er im Spiel dauerhaft (dann pro Bewegung nur eine Abfrage eines Flags). |
| Overlay | Jede Ansicht (ohne/mit Markierung) wird einmal gezeichnet und gespeichert; die Grundansicht vorab, sobald sich die Liste ändert. Mausbewegung = nur Bild tauschen (< 1 ms). Ausblenden = nur Fenster-Transparenz, nichts wird neu gezeichnet. Versteckt: keine Timer. |
| Makros | Eigener Thread, schläft ohne Arbeit. Feine Timer-Auflösung (1 ms) nur während eines Makros. |
| Spielfenster-Erkennung | Windows meldet Fensterwechsel; die Prozessliste wird nur beim Wechsel befragt und alle 6 s, solange das Spiel nicht vorne ist. |
| Start / erster Tastendruck | Vorkompiliert (ReadyToRun) und beim Start einmal „aufgewärmt“ – kein Ruckler beim ersten Strg im Spiel. Garbage Collector im Modus „geringe Latenz“. |
| Weboberfläche | Nur aktiv, wenn geöffnet; Live-Updates gebündelt (max. 20/s), nur geänderte Teile werden neu gezeichnet. |

## 2. Recherche-Ergebnisse

### Tastenbelegung des Spiels
- Datei: `Steam\userdata\<SteamID>\553850\remote\input_settings.config`
  (Fallback laut Arrowhead: `%APPDATA%\Arrowhead\Helldivers2\`). Gibt es erst, wenn im Spiel eine Taste geändert wurde.
- Aktiver Steam-Nutzer: Registry `HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser`, Steam-Pfad: `HKCU\Software\Valve\Steam\SteamPath`.
- Format: SJSON der Stingray-Engine (`key = value`, `{}`, `[]`, Kommas optional). Abschnitt `Stratagem`
  mit `Up/Down/Left/Right` und `Start` (Liste öffnen), je Eintrag `trigger`, `device_type`, `input`,
  `threshold`, `input_type`. **Mit einer echten Datei abgeglichen** (liegt als Testfall unter
  `tests/Stratagem.Core.Tests/data/`). Arrowhead verlinkt im Dateikopf eine eigene Anleitung
  (content.thehelldiversgame.com/help/inputs.md).
- Gespeichert werden **nur Aktionen, die vom Standard abweichen** – eine Aktion dann mit all ihren Belegungen
  (auch Controller). Tastennamen: `"left ctrl"`, `"left shift"`, `"up"`, `"tab"`, `"c"`, `"4"`;
  Maus: `"MouseButton1…5"`, `"MouseWheel"` (Achse). Auslöser: `Press`, `Hold`, `Tap`, `LongPress`, `Y+`/`Y-`.
- Der Parser bleibt tolerant und meldet Unbekanntes in der Oberfläche, statt zu scheitern.
- Die Datei wird überwacht: Änderst du im Spiel eine Taste, übernimmt Stratagem sie ohne Neustart.

### Timing
- Andere Tools: 10 ms halten / 150 ms Pause (NicholasDJM), 100 ms (spyoungtech). Das Spiel liest einmal
  pro Bild: Ein Druck muss länger als ein Frame sein (16,7 ms bei 60 FPS, 33 ms bei 30 FPS).
- Standard hier: 35 ms halten, 35 ms Pause, 60 ms nach dem Öffnen der Liste → ein 5er-Code in ~0,4 s.
- Windows schläft standardmäßig in 15,6-ms-Schritten. Während eines Makros wird die Timer-Auflösung
  auf 1 ms gesetzt (`timeBeginPeriod`), danach zurück.

### Eingaben senden
- `SendInput` mit **Scancodes** (`KEYEVENTF_SCANCODE`), Pfeiltasten mit E0-Präfix. Spiele lesen Positionen,
  nicht Buchstaben – funktioniert so auch mit QWERTZ/AZERTY.
- Jede eigene Eingabe trägt eine Markierung (`dwExtraInfo`), die eigenen Hooks lassen sie durch.
  Dadurch muss die Stratagem-Taste im Spiel **nicht** umgelegt werden: Das Tool schluckt das echte Strg
  und schickt dem Spiel bei Bedarf sein eigenes.

### Anti-Cheat (GameGuard)
- Kein offizielles Statement von Arrowhead zu Makros gefunden, keine bestätigten Banns. Berichte, dass
  GameGuard teils AutoHotkey blockiert.
- Grundsatz hier: **kein Eingriff ins Spiel.** Kein Lesen von Spielspeicher, keine DLL im Spiel, kein
  Treiber, kein Verstecken. Das Spielfenster wird über den Prozessnamen aus der Prozessliste erkannt –
  es wird nie ein Handle auf den Spielprozess geöffnet. Bildschirm lesen = dasselbe wie OBS/Discord.
- Restrisiko bleibt dasselbe wie bei Stream Deck/AutoHotkey/G Hub: Software-Eingaben sind als solche erkennbar.

### Stream Deck (pausiert, Notizen für später)
- SDK `@elgato/streamdeck` 3.0.x, CLI `@elgato/cli` 1.10.x, Node 20/24, Manifest `SDKVersion: 3`.
- Neo-Infobar: Controller `"Neo"` (ab Stream Deck 7.6), Layout 232×50 px, `setFeedbackLayout` + `setFeedback`.
- Das SDK kann selbst keine Tasten senden → Plugin wird dünner Client: `POST /api/activate`
  (Header `X-Stratagem: 1`), Live-Liste per WebSocket `/api/events`, Tastenbild als SVG (Name + Pfeile).

## 3. Schwierige Stellen

| # | Stelle | Risiko | Lösung / Stand |
|---|---|---|---|
| 1 | **Mausbewegung im Spiel abfangen** (Radialmenü darf die Kamera nicht drehen) | hoch | Low-Level-Maus-Hook schluckt Bewegungen, solange das Menü offen ist, und wertet sie selbst aus. **Ungeklärt, ob Helldivers' Raw-Input das respektiert** → erster Test im Spiel. Plan B: Overlay kurz fokussieren (Spiel bekommt dann keine Maus), oder Auswahl per Mausrad/WASD. |
| 2 | Mauszeiger ist im Spiel gefangen | mittel | Bewegung wird als Differenz zur aktuellen Zeigerposition berechnet; da Bewegungen geschluckt werden, bleibt der Zeiger stehen. Zusätzlich „virtueller Stick“: Vektor auf Radius begrenzt, Richtungswechsel brauchen keinen langen Rückweg. |
| 3 | Overlay über dem Spiel | mittel | Click-through, nie fokussiert, immer oben, Alpha pro Pixel. **Nur im Randlos-/Fenstermodus sichtbar**, nicht im exklusiven Vollbild. |
| 4 | Strg ist die Stratagem-Taste des Spiels | gelöst | Echtes Strg wird geschluckt, das Tool schickt markiertes Strg. Strg + andere Taste (z. B. Strg+V im Chat) = „Akkord“: Menü zu, Strg geht ans Spiel **vor** der anderen Taste. Getestet. |
| 5 | Hängende Tasten | gelöst | Makro-Läufer gibt in jedem Fall (Abbruch, Fehler, Fokusverlust) alle gedrückten Tasten frei. Radialmenü schluckt das „Loslassen“ genau dann, wenn es das „Drücken“ geschluckt hat – sonst würde z. B. Dauerfeuer entstehen. Getestet. |
| 6 | Laufen während des Makros (W gehalten, Code nutzt W) | gelöst | W wird vorher losgelassen, nach dem Code wieder gedrückt, falls noch gehalten. Spieler-Drücke der Makro-Tasten werden währenddessen zurückgehalten, damit sie sich nicht in den Code mischen. Besser: im Spiel Pfeiltasten für Stratagems (das Tool bevorzugt sie automatisch). |
| 7 | Hooks werden von Windows entfernt (Timeout ~1 s) | mittel | Hook-Callbacks arbeiten nur im Speicher, Log schreibt im Hintergrund, Hooks werden bei jedem Wechsel ins Spiel neu gesetzt (außerhalb gibt es sie gar nicht). |
| 14 | Markierung flackert an Segmentgrenzen / am Rand der Mitte | gelöst | Hysterese: Die Markierung wechselt erst 7° hinter der Grenze und fällt erst bei 75 % des Abbrechen-Bereichs zurück. Getestet. |
| 16 | Auslöser oder direkte Taste ist im Spiel schon belegt (z. B. Maus-Seitentaste = Kameraseite wechseln) | gelöst | Alle Belegungen aus der Spieldatei werden mit Auslöser und Tasten verglichen; die Oberfläche warnt mit dem Namen der Spielaktion. Strg als Auslöser auf der Stratagem-Taste ist gewollt und keine Warnung. Grenze: Standardbelegungen stehen nicht in der Datei. |
| 15 | Kurzes Antippen lässt das Menü aufblitzen | gelöst | Im Halten-Modus erscheint der Kreis erst nach 90 ms – oder sofort, sobald ein Segment gewählt wird. Schnelle „Flicks“ funktionieren ohne Wartezeit, die Auswahl wird mit kurzem Ausblenden bestätigt. |
| 8 | Falsches Fenster bekommt Tasten | gelöst | Aktiv nur, wenn das Vordergrundfenster zum Prozess `helldivers2` gehört (oder exakt den Spieltitel trägt). Ein Browser-Tab „Helldivers 2 – Reddit“ zählt nicht. Fokusverlust bricht Makros ab. |
| 9 | AltGr auf deutschen Tastaturen erzeugt ein falsches Strg | gelöst | Das falsche Strg hat Scancode 0x21D, das echte 0x1D → löst nie das Menü aus. |
| 10 | Andere Webseiten im Browser drücken Tasten über den lokalen Server | gelöst | Änderungen nur mit Header `X-Stratagem: 1` (erzwingt CORS-Preflight, das nie erlaubt wird), Host-Prüfung gegen DNS-Rebinding, fremde `Origin` abgelehnt, Heimnetz nur mit Token. Getestet. |
| 11 | **Bildschirmerkennung** der Stratagem-Liste | hoch | Noch nicht gebaut – braucht Bildschirmfotos. Plan siehe Abschnitt 5. |
| 12 | Spiel als Administrator | niedrig | Windows (UIPI) verwirft dann unsere Eingaben. Wird erkannt und im Protokoll erklärt. |
| 13 | Virenscanner | mittel | Hooks + SendInput sehen für Heuristiken wie ein Keylogger aus. Gegenmittel: Quelloffen, reproduzierbar in GitHub Actions gebaut, keine Verschleierung. Später ggf. Code-Signatur. |

## 4. Probleme, die ein Spieler haben kann (und was das Tool tut)

| Problem | Was passiert / Abhilfe |
|---|---|
| Overlay erscheint nicht | Spiel im exklusiven Vollbild → in den Grafikeinstellungen „Randlos“ wählen. |
| Kamera dreht sich beim Auswählen | Siehe schwierige Stelle 1 – bitte melden, dann kommt Plan B. |
| Stratagem kommt nicht an / falsches Stratagem | Timing erhöhen (Oberfläche → Timing). Niedrige FPS brauchen längere Werte. |
| Taste im Spiel umgelegt | Wird aus der Spieldatei gelesen, auch live. Ohne Datei: in der Oberfläche einstellen. |
| Gewählte Taste ist im Spiel schon belegt | Warnung in der Oberfläche mit der betroffenen Spielaktion. |
| „Liste öffnen“ steht im Spiel auf Doppeltippen/Lang drücken | Wird gemeldet; nur „Halten“ und „Drücken“ werden unterstützt. |
| Strg im Chat (Strg+V) | Funktioniert durch die Akkord-Regel. |
| Menü bleibt nach Alt+Tab offen / Tasten hängen | Fokusverlust schließt das Menü und gibt alles frei. |
| Hohe Maus-DPI → Auswahl zu empfindlich | „Mitte (Abbrechen-Bereich)“ vergrößern. |
| Linkshänder / anderes Layout | Alle Tasten frei wählbar, auch Maus-Seitentasten; Scancodes sind layoutunabhängig. |
| Mehrere Monitore / Skalierung | Overlay erscheint mittig über dem Spielfenster, DPI pro Monitor. |
| SmartScreen-Warnung | Unsignierte Datei: „Weitere Informationen“ → „Trotzdem ausführen“. |
| Virenscanner meldet die Datei | Siehe schwierige Stelle 13. |
| Zwei Programme senden gleichzeitig (Stream Deck + Radial) | Nur ein Makro gleichzeitig; ein zweites wird verworfen (im Spiel würde es das erste ersetzen). |
| Steam Deck / Linux | Nicht unterstützt (Windows-Hooks). |
| Bann-Risiko | Siehe Anti-Cheat oben. Kein Eingriff ins Spiel, aber keine Garantie. |

## 5. Bildschirmerkennung (nächster großer Schritt)

Beim Öffnen der Stratagem-Liste zeigt das Spiel links oben jedes verfügbare Stratagem mit Icon und
Pfeilfolge, ausgegraute Einträge sind im Cooldown.

Plan:
1. **Kalibrieren:** Einmal die Liste öffnen, das Tool sucht den Bereich (Auflösung/UI-Skalierung).
   Ergebnis als Profil in `settings.json` (Daten, kein Code → Patches = neues Profil, kein neues Programm).
2. **Lesen:** Bildschirmfoto nur dieses Bereichs. Zeilen trennen, je Zeile die Pfeile per Formvergleich
   lesen (vier Richtungen, einfache Formen), Helligkeit → verfügbar/Cooldown, Icon ausschneiden.
   Sprachunabhängig, weil kein Text gelesen wird. Der Name kommt optional aus dem Katalog.
3. **Wann:** Bei jedem Öffnen des Radialmenüs (das Tool öffnet die Liste im Spiel dann ohnehin), Ergebnis
   zwischengespeichert, damit das Menü sofort erscheint. Ziel: < 100 ms.
4. **Technik:** Bildschirmaufnahme über Windows (Desktop Duplication / Graphics Capture), Erkennung im Kern
   (plattformunabhängig, mit Bildschirmfotos als Testdaten).
5. **Fallback:** Erkennung unsicher → letzte gute Liste oder eigene Liste, mit Hinweis in der Oberfläche.

Risiken: HDR (Farben), Farbfilter für Farbenblinde, UI-Skalierung, Animationen beim Öffnen, Bewegungsunschärfe.

**Benötigt:** PNG-Bildschirmfotos der geöffneten Liste in deiner Auflösung (normal, mit Cooldowns, Mission mit
Zusatz-Stratagems, ggf. zweite Auflösung). Die `input_settings.config` liegt vor.

## 6. Fahrplan

1. ✅ Kern, Radialmenü (beide Modi), direkte Tasten, Weboberfläche, Handy-Deck, Spieldatei, Tray, Build.
2. ⏳ **Test im Spiel** (Liste unten) → Plan B für die Maus falls nötig, Timing-Standardwerte anpassen.
3. Bildschirmerkennung (Abschnitt 5).
4. Stream-Deck-Plugin (pausiert).
5. Optional: Code-Signatur, Icons im Radialmenü, Mausrad-Auswahl, zweiter Ring bei vielen Stratagems.

## 7. Testliste für den ersten Test im Spiel

Spiel im **Randlos-Modus**, `Stratagem.exe` starten, Weboberfläche öffnet sich.

1. Oben „Spiel aktiv“, wenn Helldivers im Vordergrund ist? „Spiel im Hintergrund“ nach Alt+Tab?
2. Tastenbelegung: Steht „Quelle: Spieleinstellungen“? Gelbe Hinweise? (Text bitte schicken.)
3. In der Liste bei einem Stratagem ▶ drücken, innerhalb von 3 s ins Spiel wechseln: Wird es aufgerufen?
4. Radialmenü „Halten & Loslassen“: Strg halten → erscheint der Kreis? **Dreht sich die Kamera beim Schieben?**
   Loslassen auf einem Segment → richtiges Stratagem?
5. Gleiches mit „Antippen & Ziehen“. Schießt der Helldiver beim Ziehen mit gedrückter linker Maustaste?
6. Beim Laufen (W gehalten) ein Stratagem auslösen: Läuft der Helldiver danach weiter?
7. Chat öffnen, Strg+V: Funktioniert Einfügen?
8. Direkte Taste (z. B. Num 1 → Platz 1): Kommt das Stratagem?
9. Gehen Eingaben verloren? Dann Timing schrittweise erhöhen und die Werte notieren.
10. Bei Problemen: `stratagem.log` (Menü ⋯ → Datenordner öffnen) schicken.
