# Neue App anlegen

So bleibt das Repo übersichtlich, egal wie viele Apps dazukommen.

## Ordner

```
apps/<name>/              Kleinbuchstaben, ein Wort oder mit Bindestrich (z. B. apps/licht, apps/stratagem)
  README.md               Pflicht – siehe unten
  docs/                   optional: Konzept, API, Recherche, Testlisten
  …                       Aufbau nach Technik (Node: server/ web/ test/ · .NET: src/ tests/ · Android: android-app/)
  .gitignore              Build-Ausgaben der App (node_modules/, bin/, obj/, dist/ …)
```

- Alles, was zur App gehört, liegt in ihrem Ordner. Nichts im Repo-Hauptordner außer README, `docs/`, `.github/`.
- Keine Abhängigkeiten zwischen Apps. Gemeinsames lieber kopieren als verknüpfen – jede App bleibt für sich lauffähig.
- Daten der App auf dem PC: `%USERPROFILE%\.<name>` (Windows) bzw. `~/.<name>` – Einstellungen, Protokoll.

## README der App

1. Ein Satz: was die App ersetzt / wofür sie ist.
2. **Benutzen** – für Nutzer, ohne Fachbegriffe: Download-Link, erster Start (SmartScreen-Hinweis), Bedienung.
3. **Entwickeln** – Befehle für Tests, lokalen Start und Build.
4. Stand ehrlich angeben (stabil / experimentell / ungetestet).

Sprache: Deutsch für alles, was Nutzer sehen (README, Oberfläche, Meldungen). Code und Code-Kommentare: Englisch.

## Build und Download (GitHub Actions)

- Workflow `.github/workflows/<app>-<plattform>.yml`, z. B. `stratagem-windows.yml`.
- `name:` beginnt mit dem App-Namen: `<App> · <Plattform> (<Datei>)`.
- Auslöser nur für die eigene App: `paths: ['apps/<app>/**', '.github/workflows/<app>-<plattform>.yml']`.
- `defaults.run.working-directory: apps/<app>`.
- Ablauf: Tests → Bauen → gebaute Datei **starten und prüfen** (Smoke-Test) → Artefakt hochladen →
  auf `main` die Datei im Release `<app>-latest` (bzw. `<app>-<plattform>`) **ersetzen**, nicht neu anlegen –
  so bleibt der Download-Link für immer gleich. Vorlage: `licht-windows.yml` oder `stratagem-windows.yml`.
- Hilfsskripte: `.github/scripts/<app>-<zweck>.sh`.

## Checkliste

- [ ] `apps/<name>/` mit README und `.gitignore`
- [ ] Tests laufen lokal und im Workflow
- [ ] Workflow `<app>-<plattform>.yml` mit Smoke-Test und festem Release-Link
- [ ] Zeile in der Übersicht der Haupt-README (App, Wofür, Plattform, Stand, Download)
