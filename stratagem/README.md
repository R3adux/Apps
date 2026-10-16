# Stratagem – Stratagems ohne Pfeil-Tipperei (Helldivers 2)

> **Experimentell.** Noch nicht im Spiel getestet – siehe [Testliste](docs/KONZEPT.md#7-testliste-für-den-ersten-test-im-spiel).

Stratagem ruft Stratagems in Helldivers 2 für dich auf:

- **Radialmenü:** Strg halten, Maus in die Richtung schieben, loslassen – oder (umschaltbar) Strg antippen
  und mit gedrückter Maustaste ziehen. Halbtransparenter Kreis über dem Spiel, ein Segment pro Stratagem.
- **Direkte Tasten:** jede Taste oder Maus-Seitentaste → Platz der Liste oder festes Stratagem.
- **Handy als Stream Deck:** große Kacheln im Handy-Browser (gleiches WLAN).
- **Liest deine Tastenbelegung aus dem Spiel** – auch wenn du sie später änderst.
- Geplant: verfügbare Stratagems **direkt vom Bildschirm lesen** (dann keine Liste mehr pflegen), Stream-Deck-Plugin.

Leistung: Außerhalb des Spiels fängt Stratagem nichts ab, im Spiel nur Tastendrücke – die Maus erst,
wenn das Radialmenü offen ist. Details: [Konzept → Leistung](docs/KONZEPT.md#leistung-kein-merkbarer-einfluss-aufs-spiel).

Kein Eingriff ins Spiel: Stratagem drückt nur Tasten, wie Stream Deck oder AutoHotkey.
Trotzdem gilt: keine Garantie gegen Maßnahmen des Anti-Cheat-Systems.

## Benutzen

1. `Stratagem.exe` herunterladen ([Release](https://github.com/R3adux/Apps/releases/tag/stratagem-latest), sobald gebaut) und doppelklicken.
   - SmartScreen warnt, weil die Datei nicht signiert ist: **„Weitere Informationen“ → „Trotzdem ausführen“**.
2. Die Einstellungen öffnen sich im Browser (`http://localhost:8133`). Stratagem läuft im Hintergrund,
   Symbol unten rechts in der Taskleiste (Klick = Einstellungen, Rechtsklick = Pausieren / Mit Windows starten / Beenden).
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

Daten: `C:\Users\<Name>\.stratagem` (`settings.json`, Protokoll `stratagem.log`).

## Entwickeln

```
dotnet test                                  # Tests (laufen auch unter Linux/macOS)
dotnet run --project src/Stratagem.Dev       # Oberfläche ohne Windows/Spiel: http://localhost:8133
dotnet publish src/Stratagem.App -c Release -r win-x64 -o dist   # Stratagem.exe
```

- Aufbau, Recherche, schwierige Stellen, Spielerprobleme: [docs/KONZEPT.md](docs/KONZEPT.md)
- Schnittstelle für weitere Auslöser: [docs/API.md](docs/API.md)
- GitHub Actions baut und prüft `Stratagem.exe` auf Windows (`.github/workflows/stratagem-windows.yml`).
