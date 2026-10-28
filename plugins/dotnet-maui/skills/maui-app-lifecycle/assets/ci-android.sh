#!/usr/bin/env bash
# Run only against an isolated test emulator. Copy to eng/maui-agent/.
set -euo pipefail
: "${APP_PROJECT:?Set APP_PROJECT}"
: "${APP_PACKAGE:?Set APP_PACKAGE}"
: "${APP_NAME:?Set APP_NAME}"
TFM="${TFM:-net10.0-android}"
RID="${ANDROID_RID:-android-x64}"
DEVICE="${ANDROID_SERIAL:-emulator-5554}"
PORT="${DEVFLOW_PORT:-9223}"
APPIUM_PORT="${APPIUM_PORT:-4725}"
DEBUG_FLOW="${DEBUG_FLOW:-maui-tests/smoke.md}"
NATIVE_SUITE="${NATIVE_SUITE:-maui-tests/native.json}"
APP_DIRECTORY="$(dirname "$APP_PROJECT")"
DEBUG_APK="$APP_DIRECTORY/bin/Debug/$TFM/$RID/$APP_PACKAGE-Signed.apk"
RELEASE_APK="$APP_DIRECTORY/bin/Release/$TFM/$RID/$APP_PACKAGE-Signed.apk"
mkdir -p .maui-agent
if [ "$(adb -s "$DEVICE" shell getprop ro.kernel.qemu | tr -d '\r')" != 1 ]; then
  echo "CI reset requires an isolated emulator" >&2
  exit 1
fi
adb -s "$DEVICE" install --no-incremental -r "$DEBUG_APK"
adb -s "$DEVICE" shell pm clear "$APP_PACKAGE"
maui devflow broker start
python3 eng/maui-agent/android_launch.py --device "$DEVICE" --app "$APP_NAME" --package "$APP_PACKAGE" \
  --host-port "$PORT" --output .maui-agent/debug-launch/report.json
ACTIVITY="$(python3 -c 'import json; print(json.load(open(".maui-agent/debug-launch/report.json"))["activity"])')"
python3 eng/maui-agent/devflow_check.py --app "$APP_NAME" --platform android --agent-port "$PORT" \
  --flow "$DEBUG_FLOW" --require-layout --output .maui-agent/debug/report.json
appium --address 127.0.0.1 --port "$APPIUM_PORT" --log .maui-agent/appium-server.log &
appium_pid=$!
trap 'kill "$appium_pid" 2>/dev/null || true' EXIT
for attempt in $(seq 1 60); do
  if curl --fail --silent "http://127.0.0.1:$APPIUM_PORT/status" > /dev/null; then break; fi
  if [ "$attempt" = 60 ]; then exit 1; fi
  sleep 1
done
for run in 1 2; do
  python3 eng/maui-agent/android_check.py --device "$DEVICE" --app "$APP_NAME" --package "$APP_PACKAGE" \
    --activity "$ACTIVITY" --apk "$RELEASE_APK" --suite "$NATIVE_SUITE" --clear-test-data \
    --server "http://127.0.0.1:$APPIUM_PORT" --orientation PORTRAIT --output ".maui-agent/native-$run/report.json"
done
python3 eng/maui-agent/android_check.py --device "$DEVICE" --app "$APP_NAME" --package "$APP_PACKAGE" \
  --activity "$ACTIVITY" --apk "$RELEASE_APK" --suite "$NATIVE_SUITE" --clear-test-data \
  --server "http://127.0.0.1:$APPIUM_PORT" --orientation LANDSCAPE --font-scale 1.3 --night-mode yes \
  --output .maui-agent/large-font-landscape/report.json
if [ -n "${NATIVE_BOUNDARY_SUITE:-}" ]; then
  for run in 1 2; do
    python3 eng/maui-agent/android_check.py --device "$DEVICE" --app "$APP_NAME" --package "$APP_PACKAGE" \
      --activity "$ACTIVITY" --apk "$RELEASE_APK" --suite "$NATIVE_BOUNDARY_SUITE" --clear-test-data \
      --server "http://127.0.0.1:$APPIUM_PORT" --orientation PORTRAIT --output ".maui-agent/boundary-$run/report.json"
  done
fi
