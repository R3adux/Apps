#!/usr/bin/env bash
# Runs inside the Android emulator job: installs Licht.apk, lets the app commission the
# simulated lamp on the host (10.0.2.2 from inside the emulator) and waits for the result.
#
# Must pass: the app starts, its Matter hub starts, the page talks to the hub (bridge), the app does
# not crash and the lamp accepts the setup code. The last commissioning step finds the lamp again via
# mDNS – multicast does not cross the emulator's network (NAT), so that step cannot work here and
# only gives a notice. On a real phone in the home network it works (covered by the probe tests too).
set -u
APK="$1"
adb install -r "$APK"
adb logcat -c
adb shell "am start -W -a android.intent.action.VIEW -d 'licht://selftest?code=34970112332&ip=10.0.2.2:5540' de.r3adux.licht"

log() { adb logcat -d | sed 's/\x1b\[[0-9;]*m//g'; }
# Only crashes of Licht itself – other apps in the emulator image crash now and then.
crashed() { log | grep -A3 "FATAL EXCEPTION" | grep -q "de.r3adux.licht"; }

started=""
for _ in $(seq 1 45); do
  if log | grep -q "LICHT_SELFTEST hub started" && log | grep -q "LICHT_PAGE connected"; then started=1; break; fi
  crashed && break
  sleep 2
done

result=""
if [ -n "$started" ]; then
  for _ in $(seq 1 100); do
    line=$(log | grep -E "LICHT_SELFTEST (OK|FAIL)" | tail -1)
    if [ -n "$line" ]; then result="$line"; break; fi
    crashed && break
    sleep 2
  done
fi

echo "----- App log -----"
# Only the interesting lines: our markers, warnings/errors, commissioning and connection steps.
log | grep -E "LICHT_|AndroidRuntime|FATAL|WARN|ERROR|commissioning step|Reconnect|Resolving|IpServiceStatus|Operational address|Connected" \
  | grep -v "DEBUG" | cut -c1-400 | tail -150
echo "----- Simulated lamp -----"
cat "$LAMP_LOG" || true

if crashed; then echo "Selbsttest: die App ist abgestürzt"; exit 1; fi
if [ -z "$started" ]; then echo "Selbsttest: App, Matter-Teil oder Seite starten nicht (siehe LICHT_-Zeilen)"; exit 1; fi
if ! log | grep -q "commissioning step 15.1"; then
  case "$result" in
    *"LICHT_SELFTEST OK"*) ;;
    *) echo "Selbsttest: die Lampe hat die Einrichtung nicht angenommen: ${result:-keine Antwort}"; exit 1 ;;
  esac
fi
case "$result" in
  *"LICHT_SELFTEST OK"*) echo "Selbsttest bestanden (komplett)" ;;
  *) echo "::notice::Licht-App: Start, Seite und Einrichtung bis zum letzten Schritt OK. Der letzte Schritt (Lampe per mDNS wiederfinden) ist im Emulator nicht möglich." ;;
esac
exit 0
