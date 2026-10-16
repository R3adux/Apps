# Stratagem – Stratagems ohne Pfeil-Tipperei (Helldivers 2)

> **Experimentell.** Noch nicht im Spiel getestet – siehe [Testliste](docs/KONZEPT.md#7-testliste-für-den-ersten-test-im-spiel).

Stratagem ruft Stratagems in Helldivers 2 für dich auf:

- **Radialmenü:** Strg halten, Maus in die Richtung schieben, loslassen – oder (umschaltbar) Strg antippen
  und mit gedrückter Maustaste ziehen. Halbtransparenter Kreis über dem Spiel, ein Segment pro Stratagem.
- **Direkte Tasten:** jede Taste oder Maus-Seitentaste → Platz der Liste oder festes Stratagem.
- **Handy als Stream Deck:** große Kacheln im Handy-Browser (gleiches WLAN), Adresse `hellwheel.local`
  statt IP-Nummer, einrichten per QR-Code.
- **Liest deine Tastenbelegung aus dem Spiel** – auch wenn du sie später änderst.
- **Liest die verfügbaren Stratagems aus dem Spiel** (experimentell): Pfeile, Icons und Abklingzeit direkt vom
  Bildschirm – keine Liste mehr pflegen. Jedes Stratagem behält seine Richtung im Radialmenü.
- Geplant: Stream-Deck-Plugin.

Leistung: Außerhalb des Spiels fängt Stratagem nichts ab, im Spiel nur Tastendrücke – die Maus erst,
wenn das Radialmenü offen ist. Details: [Konzept → Leistung](docs/KONZEPT.md#leistung-kein-merkbarer-einfluss-aufs-spiel).

Kein Eingriff ins Spiel: Stratagem drückt nur Tasten, wie Stream Deck oder AutoHotkey.
Trotzdem gilt: keine Garantie gegen Maßnahmen des Anti-Cheat-Systems.

## Benutzen

1. `Stratagem.exe` herunterladen ([Release](https://github.com/R3adux/Apps/releases/tag/stratagem-latest), sobald gebaut) und doppelklicken.
   - SmartScreen warnt, weil die Datei nicht signiert ist: **„Weitere Informationen“ → „Trotzdem ausführen“**.
2. Das Stratagem-Fenster mit den Einstellungen öffnet sich. Schließen ist kein Beenden: Stratagem läuft im
   Hintergrund weiter, Symbol unten rechts in der Taskleiste (Klick = Fenster öffnen, Rechtsklick = Im Browser öffnen /
   Pausieren / Mit Windows starten / Beenden). Das Fenster nutzt Edge WebView2, das bei Windows 10/11 dabei ist –
   fehlt es, öffnen sich die Einstellungen im Browser (`http://localhost:8133`). Solange das Fenster zu ist,
   kostet es nichts.
3. Liste füllen (Name wählen, Code wird ergänzt), Radialmenü und Tasten einstellen – alles wird sofort gespeichert.
4. Helldivers 2 im **Randlos-Modus** spielen (im exklusiven Vollbild ist das Radialmenü unsichtbar).

Radialmenü und Tasten wirken nur, solange Helldivers 2 im Vordergrund ist.

| Einstellung | Bedeutung |
|---|---|
| Halten & Loslassen | Taste halten → Kreis, Maus schieben, loslassen = auslösen. In der Mitte loslassen, Esc oder Rechtsklick = abbrechen. |
| Antippen & Ziehen | Taste antippen → Kreis, mit gedrückter Maustaste ziehen und loslassen. Nochmal antippen, Esc, Rechtsklick oder 5 s warten = schließen. |
| Mitte (Abbrechen-Bereich) | Wie weit die Maus mindestens bewegt werden muss. Bei hoher Maus-DPI größer stellen. |
| Timing | Wenn Pfeile verloren gehen: Werte erhöhen (bei niedrigen FPS nötig). |

Strg + andere Taste (z. B. Strg+V im Chat) funktioniert weiter wie gewohnt.

### Handy als Stream Deck

1. In den Einstellungen „Im Heimnetz erreichbar“ einschalten und Stratagem neu starten. Fragt die Windows-Firewall,
   „Private Netzwerke“ erlauben.
2. Den QR-Code mit der Handy-Kamera scannen (Handy im selben WLAN). Danach reicht im Handy-Browser
   **`hellwheel.local`** – der Name ist in den Einstellungen änderbar.
3. Findet das Handy den Namen nicht (manche ältere Android-Versionen kennen keine `.local`-Namen),
   den QR-Code des Links mit IP-Adresse nehmen.

Ein Name ganz ohne `.local` (z. B. `app.hellwheel`) ginge nur über den Router: In der FRITZ!Box den PC unter
Heimnetz → Netzwerk umbenennen, dann heißt er `<name>.fritz.box`.
Ist Port 80 auf dem PC schon belegt, lautet die Adresse `hellwheel.local:8133`.

Daten: `C:\Users\<Name>\.stratagem` (`settings.json`, Protokoll `stratagem.log`, Fenstergröße `window.json`, `WebView2\`).

## Entwickeln

```
dotnet test                                  # Tests (laufen auch unter Linux/macOS)
dotnet run --project src/Stratagem.Dev       # Oberfläche ohne Windows/Spiel: http://localhost:8133
dotnet publish src/Stratagem.App -c Release -r win-x64 -o dist   # Stratagem.exe
```

- Aufbau, Recherche, schwierige Stellen, Spielerprobleme: [docs/KONZEPT.md](docs/KONZEPT.md)
- Schnittstelle für weitere Auslöser: [docs/API.md](docs/API.md)
- GitHub Actions baut und prüft `Stratagem.exe` auf Windows (`.github/workflows/stratagem-windows.yml`).
