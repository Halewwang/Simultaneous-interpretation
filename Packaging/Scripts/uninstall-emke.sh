#!/bin/bash
set -euo pipefail

usage() {
  echo "usage: uninstall-emke.sh [--purge-user-data]" >&2
  exit 64
}

PURGE=0
if [[ "$#" -gt 1 ]]; then
  usage
fi
if [[ "$#" -eq 1 ]]; then
  case "$1" in
    --purge-user-data) PURGE=1 ;;
    *) usage ;;
  esac
fi

TEST_MODE="${EMKE_TEST_MODE:-0}"
TEST_ROOT="${EMKE_TEST_ROOT:-}"
PREFIX=""

test_root_error() {
  echo "$1" >&2
  exit 65
}

if [[ "$TEST_MODE" == "1" ]]; then
  [[ -n "$TEST_ROOT" ]] || test_root_error "EMKE_TEST_MODE=1 requires EMKE_TEST_ROOT"
  [[ -d "$TEST_ROOT" ]] || test_root_error "EMKE_TEST_ROOT must be an existing directory"

  TEMP_BASE="${TMPDIR:-/tmp}"
  [[ -d "$TEMP_BASE" ]] || test_root_error "temporary directory is unavailable"
  CANON_TEMP="$(cd "$TEMP_BASE" 2>/dev/null && pwd -P)" || \
    test_root_error "cannot canonicalize temporary directory"
  CANON_ROOT="$(cd "$TEST_ROOT" 2>/dev/null && pwd -P)" || \
    test_root_error "cannot canonicalize EMKE_TEST_ROOT"

  [[ "$CANON_TEMP" != "/" ]] || test_root_error "temporary directory cannot be /"
  [[ "$CANON_ROOT" != "/" ]] || test_root_error "EMKE_TEST_ROOT cannot resolve to /"
  case "$CANON_ROOT" in
    "$CANON_TEMP"/*) ;;
    *) test_root_error "EMKE_TEST_ROOT must resolve inside the canonical temporary directory" ;;
  esac
  PREFIX="$CANON_ROOT"
elif [[ -n "$TEST_ROOT" ]]; then
  test_root_error "EMKE_TEST_ROOT requires EMKE_TEST_MODE=1"
fi

APP="$PREFIX/Applications/EMKE Translation.app"
DRIVER="$PREFIX/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
SUPPORT="$PREFIX/Library/Application Support/EMKE Translation"
RECEIPT="com.emke.translation.internal"

require_owned_path() {
  case "$1" in
    "$APP"|"$DRIVER"|"$SUPPORT") ;;
    *) echo "refusing non-owned path: $1" >&2; exit 66 ;;
  esac
}

validate_test_target() {
  local target="$1"
  local cursor
  local canonical_cursor
  require_owned_path "$target"
  cursor="$(/usr/bin/dirname "$target")"
  while [[ ! -e "$cursor" && ! -L "$cursor" ]]; do
    [[ "$cursor" != "$PREFIX" && "$cursor" != "/" ]] || break
    cursor="$(/usr/bin/dirname "$cursor")"
  done
  canonical_cursor="$(cd "$cursor" 2>/dev/null && pwd -P)" || {
    echo "refusing unresolvable test target parent: $cursor" >&2
    exit 67
  }
  case "$canonical_cursor" in
    "$PREFIX"|"$PREFIX"/*) ;;
    *) echo "refusing test target outside isolated root: $target" >&2; exit 67 ;;
  esac
}

remove_owned_path() {
  require_owned_path "$1"
  if [[ "$TEST_MODE" == "1" ]]; then
    /bin/rm -rf -- "$1"
  else
    /usr/bin/sudo /bin/rm -rf -- "$1"
  fi
}

delete_keychain_item() {
  local output
  local status
  set +e
  output="$(/usr/bin/security delete-generic-password \
    -s com.emke.translation -a openai-api-key 2>&1)"
  status=$?
  set -e
  if [[ "$status" -eq 0 || "$status" -eq 44 ]]; then
    return 0
  fi
  [[ -z "$output" ]] || echo "$output" >&2
  return "$status"
}

delete_defaults_domain() {
  local output
  local status
  set +e
  output="$(/usr/bin/defaults delete com.emke.translation.app 2>&1)"
  status=$?
  set -e
  if [[ "$status" -eq 0 ]]; then
    return 0
  fi
  if [[ "$output" == *"Domain (com.emke.translation.app) not found."* ]]; then
    return 0
  fi
  [[ -z "$output" ]] || echo "$output" >&2
  return "$status"
}

forget_receipt() {
  local output
  local status
  set +e
  output="$(/usr/bin/sudo /usr/sbin/pkgutil --forget "$RECEIPT" 2>&1)"
  status=$?
  set -e
  if [[ "$status" -eq 0 ]]; then
    return 0
  fi
  case "$output" in
    *"No receipt for '$RECEIPT' found at '/'."*|*"No receipt for '$RECEIPT' found."*) return 0 ;;
  esac
  [[ -z "$output" ]] || echo "$output" >&2
  return "$status"
}

if [[ "$TEST_MODE" == "1" ]]; then
  validate_test_target "$APP"
  validate_test_target "$DRIVER"
  validate_test_target "$SUPPORT"
fi

if [[ "$PURGE" == "1" ]]; then
  if [[ "$TEST_MODE" == "1" ]]; then
    echo "purge-keychain:com.emke.translation:openai-api-key" >> "${EMKE_TEST_LOG:?missing test log}"
    echo "purge-defaults:com.emke.translation.app" >> "$EMKE_TEST_LOG"
  else
    delete_keychain_item
    delete_defaults_domain
  fi
elif [[ "$TEST_MODE" == "1" ]]; then
  echo "preserve-user-data" >> "${EMKE_TEST_LOG:?missing test log}"
fi

remove_owned_path "$APP"
remove_owned_path "$DRIVER"
if [[ "$TEST_MODE" == "1" ]]; then
  echo "forget-receipt:$RECEIPT" >> "$EMKE_TEST_LOG"
  echo "refresh-core-audio" >> "$EMKE_TEST_LOG"
else
  forget_receipt
  /usr/bin/sudo /usr/bin/killall coreaudiod
fi
remove_owned_path "$SUPPORT"
echo "EMKE Translation uninstalled."
