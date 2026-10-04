@echo off
rem Startet die Licht-Zentrale unter Windows. Zum Autostart eine Verknüpfung
rem zu dieser Datei in den Ordner "shell:startup" legen.
cd /d "%~dp0"
if not exist node_modules call npm install --omit=optional
node server\main.js
pause
