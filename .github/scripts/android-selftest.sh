#!/usr/bin/env bash
# Runs inside the Android emulator job: installs Licht.apk, lets the app commission the
# simulated lamp on the host (10.0.2.2 from inside the emulator) and waits for the result.
set -u
APK="$1"
adb install -r "$APK"
adb logcat -c
adb shell "am start -W -a android.intent.action.VIEW -d 'licht://selftest?code=34970112332&ip=10.0.2.2:5540' de.r3adux.licht"

result=""
for _ in $(seq 1 120); do
  line=$(adb logcat -d | grep -E "LICHT_SELFTEST (OK|FAIL)" | tail -1)
  if [ -n "$line" ]; then result="$line"; break; fi
  sleep 2
done

echo "----- App log -----"
adb logcat -d | grep -E "LICHT_SELFTEST|ReactNativeJS|AndroidRuntime|FATAL" | tail -200
echo "----- Simulated lamp -----"
cat "$LAMP_LOG" || true

case "$result" in
  *"LICHT_SELFTEST OK"*) echo "Selbsttest bestanden"; exit 0 ;;
  "") echo "Selbsttest: keine Antwort von der App"; exit 1 ;;
  *) echo "Selbsttest fehlgeschlagen: $result"; exit 1 ;;
esac
