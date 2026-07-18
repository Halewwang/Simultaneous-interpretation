#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-life-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
mkdir -p "$TEMP/Applications/EMKE Translation.app" \
  "$TEMP/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver" \
  "$TEMP/Library/Application Support/EMKE Translation"

EMKE_TEST_MODE=1 EMKE_TEST_ROOT="$TEMP" EMKE_TEST_LOG="$TEMP/log" \
  bash "$ROOT/Packaging/Scripts/uninstall-emke.sh"
test ! -e "$TEMP/Applications/EMKE Translation.app"
test ! -e "$TEMP/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
/usr/bin/grep -q '^preserve-user-data$' "$TEMP/log"

if EMKE_TEST_MODE=1 EMKE_TEST_ROOT="$TEMP" \
  bash "$ROOT/Packaging/Scripts/uninstall-emke.sh" --unknown; then
  echo "unknown option unexpectedly succeeded" >&2; exit 1
fi

: > "$TEMP/log"
EMKE_TEST_MODE=1 EMKE_TEST_ROOT="$TEMP" EMKE_TEST_LOG="$TEMP/log" \
  bash "$ROOT/Packaging/Scripts/uninstall-emke.sh" --purge-user-data
/usr/bin/grep -q '^purge-keychain:com.emke.translation:openai-api-key$' "$TEMP/log"
/usr/bin/grep -q '^purge-defaults:com.emke.translation.app$' "$TEMP/log"

EMKE_TEST_MODE=1 EMKE_TEST_LOG="$TEMP/postinstall-log" \
  bash "$ROOT/Packaging/InstallerScripts/postinstall"
/usr/bin/grep -q '^refresh-core-audio$' "$TEMP/postinstall-log"
echo "PASS: lifecycle scripts"
