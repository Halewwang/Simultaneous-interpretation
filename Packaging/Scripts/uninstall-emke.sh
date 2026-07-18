#!/bin/bash
set -euo pipefail
PURGE=0
case "${1:-}" in
  "") ;;
  --purge-user-data) PURGE=1 ;;
  *) echo "usage: uninstall-emke.sh [--purge-user-data]" >&2; exit 64 ;;
esac

TEST_MODE="${EMKE_TEST_MODE:-0}"
TEST_ROOT="${EMKE_TEST_ROOT:-}"
if [[ -n "$TEST_ROOT" && "$TEST_MODE" != "1" ]]; then
  echo "EMKE_TEST_ROOT requires EMKE_TEST_MODE=1" >&2; exit 65
fi
PREFIX="$TEST_ROOT"
APP="$PREFIX/Applications/EMKE Translation.app"
DRIVER="$PREFIX/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
SUPPORT="$PREFIX/Library/Application Support/EMKE Translation"
RECEIPT="com.emke.translation.internal"

remove_owned_path() {
  case "$1" in "$APP"|"$DRIVER"|"$SUPPORT") ;; *) exit 66;; esac
  if [[ "$TEST_MODE" == "1" ]]; then /bin/rm -rf -- "$1"
  else /usr/bin/sudo /bin/rm -rf -- "$1"; fi
}

if [[ "$PURGE" == "1" ]]; then
  if [[ "$TEST_MODE" == "1" ]]; then
    echo "purge-keychain:com.emke.translation:openai-api-key" >> "$EMKE_TEST_LOG"
    echo "purge-defaults:com.emke.translation.app" >> "$EMKE_TEST_LOG"
  else
    /usr/bin/security delete-generic-password \
      -s com.emke.translation -a openai-api-key >/dev/null 2>&1 || true
    /usr/bin/defaults delete com.emke.translation.app >/dev/null 2>&1 || true
  fi
elif [[ "$TEST_MODE" == "1" ]]; then
  echo "preserve-user-data" >> "$EMKE_TEST_LOG"
fi

remove_owned_path "$APP"
remove_owned_path "$DRIVER"
if [[ "$TEST_MODE" == "1" ]]; then
  echo "forget-receipt:$RECEIPT" >> "$EMKE_TEST_LOG"
  echo "refresh-core-audio" >> "$EMKE_TEST_LOG"
else
  /usr/bin/sudo /usr/sbin/pkgutil --forget "$RECEIPT" >/dev/null || true
  /usr/bin/sudo /usr/bin/killall coreaudiod
fi
remove_owned_path "$SUPPORT"
echo "EMKE Translation uninstalled."
