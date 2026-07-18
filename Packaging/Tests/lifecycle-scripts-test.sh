#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
UNINSTALL="$ROOT/Packaging/Scripts/uninstall-emke.sh"
POSTINSTALL="$ROOT/Packaging/InstallerScripts/postinstall"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-life-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

prepare_owned_paths() {
  local root="$1"
  mkdir -p "$root/Applications/EMKE Translation.app" \
    "$root/Applications/Unrelated.app" \
    "$root/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver" \
    "$root/Library/Audio/Plug-Ins/HAL/Unrelated.driver" \
    "$root/Library/Application Support/EMKE Translation" \
    "$root/Library/Application Support/Unrelated Data"
}

assert_owned_paths_exist() {
  local root="$1"
  test -e "$root/Applications/EMKE Translation.app" || fail "app mutated"
  test -e "$root/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver" || fail "driver mutated"
  test -e "$root/Library/Application Support/EMKE Translation" || fail "support mutated"
}

assert_unrelated_paths_exist() {
  local root="$1"
  test -e "$root/Applications/Unrelated.app" || fail "unrelated app removed"
  test -e "$root/Library/Audio/Plug-Ins/HAL/Unrelated.driver" || fail "unrelated driver removed"
  test -e "$root/Library/Application Support/Unrelated Data" || fail "unrelated support removed"
}

expect_rejected_before_mutation() {
  local label="$1"
  local expected_status="$2"
  local protected_root="$3"
  shift 3
  : > "$TEMP/validation-log"
  set +e
  "$@" >"$TEMP/$label-output" 2>&1
  local status=$?
  set -e
  test "$status" -eq "$expected_status" || \
    fail "$label returned $status, expected $expected_status: $(<"$TEMP/$label-output")"
  assert_owned_paths_exist "$protected_root"
  assert_unrelated_paths_exist "$protected_root"
  test ! -s "$TEMP/validation-log" || fail "$label emitted mutation markers"
}

SAFE_ROOT="$TEMP/safe-root"
prepare_owned_paths "$SAFE_ROOT"

# Argument validation must happen before purge or deletion. This is intentionally
# first so the pre-fix implementation fails safely inside the temporary root.
expect_rejected_before_mutation extra-args 64 "$SAFE_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 \
    EMKE_TEST_ROOT="$SAFE_ROOT" \
    EMKE_TEST_LOG="$TEMP/validation-log" \
    bash "$UNINSTALL" --purge-user-data --extra
expect_rejected_before_mutation unknown-option 64 "$SAFE_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 \
    EMKE_TEST_ROOT="$SAFE_ROOT" \
    EMKE_TEST_LOG="$TEMP/validation-log" \
    bash "$UNINSTALL" --unknown

# Validation-only mode shares the production target checks but exits before any
# purge or removal. Negative roots can therefore never reach a remover.
expect_rejected_before_mutation missing-root 65 "$SAFE_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 \
    EMKE_TEST_LOG="$TEMP/validation-log" \
    bash "$UNINSTALL"
expect_rejected_before_mutation nonexistent-root 65 "$SAFE_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 \
    EMKE_TEST_ROOT="$TEMP/does-not-exist" \
    EMKE_TEST_LOG="$TEMP/validation-log" bash "$UNINSTALL"
expect_rejected_before_mutation slash-root 65 "$SAFE_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 EMKE_TEST_ROOT=/ \
    EMKE_TEST_LOG="$TEMP/validation-log" bash "$UNINSTALL"
expect_rejected_before_mutation outside-root 65 "$SAFE_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 EMKE_TEST_ROOT="$ROOT" \
    EMKE_TEST_LOG="$TEMP/validation-log" bash "$UNINSTALL"

ln -s / "$TEMP/slash-link"
ln -s "$ROOT" "$TEMP/outside-link"
expect_rejected_before_mutation slash-symlink-root 65 "$SAFE_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 \
    EMKE_TEST_ROOT="$TEMP/slash-link" \
    EMKE_TEST_LOG="$TEMP/validation-log" bash "$UNINSTALL"
expect_rejected_before_mutation outside-symlink-root 65 "$SAFE_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 \
    EMKE_TEST_ROOT="$TEMP/outside-link" \
    EMKE_TEST_LOG="$TEMP/validation-log" bash "$UNINSTALL"

# A symlinked target parent must not escape an otherwise valid isolated root.
LINK_ROOT="$TEMP/link-root"
ESCAPE_ROOT="$TEMP/escape-root"
mkdir -p "$LINK_ROOT/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver" \
  "$LINK_ROOT/Library/Audio/Plug-Ins/HAL/Unrelated.driver" \
  "$LINK_ROOT/Library/Application Support/EMKE Translation" \
  "$LINK_ROOT/Library/Application Support/Unrelated Data" \
  "$ESCAPE_ROOT/Applications/EMKE Translation.app" \
  "$ESCAPE_ROOT/Applications/Unrelated.app"
ln -s "$ESCAPE_ROOT/Applications" "$LINK_ROOT/Applications"
expect_rejected_before_mutation target-parent-symlink 67 "$LINK_ROOT" \
  /usr/bin/env EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 \
    EMKE_TEST_ROOT="$LINK_ROOT" \
    EMKE_TEST_LOG="$TEMP/validation-log" bash "$UNINSTALL"

# A valid isolated root also proves validation-only mode is an explicit no-op.
VALIDATION_ONLY_ROOT="$TEMP/validation-only-root"
prepare_owned_paths "$VALIDATION_ONLY_ROOT"
: > "$TEMP/validation-only-log"
EMKE_TEST_MODE=1 EMKE_VALIDATE_ONLY=1 \
  EMKE_TEST_ROOT="$VALIDATION_ONLY_ROOT" \
  EMKE_TEST_LOG="$TEMP/validation-only-log" bash "$UNINSTALL"
assert_owned_paths_exist "$VALIDATION_ONLY_ROOT"
assert_unrelated_paths_exist "$VALIDATION_ONLY_ROOT"
test ! -s "$TEMP/validation-only-log" || fail "validation-only mode mutated state"

# Default uninstall removes all owned system payloads while preserving user data
# and unrelated siblings. The log is compared exactly to forbid purge markers.
: > "$TEMP/preserve-log"
EMKE_TEST_MODE=1 EMKE_TEST_ROOT="$SAFE_ROOT" EMKE_TEST_LOG="$TEMP/preserve-log" \
  bash "$UNINSTALL" > "$TEMP/preserve-output"
test ! -e "$SAFE_ROOT/Applications/EMKE Translation.app"
test ! -e "$SAFE_ROOT/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
test ! -e "$SAFE_ROOT/Library/Application Support/EMKE Translation"
assert_unrelated_paths_exist "$SAFE_ROOT"
printf '%s\n' \
  'preserve-user-data' \
  'forget-receipt:com.emke.translation.internal' \
  'refresh-core-audio' > "$TEMP/preserve-expected"
/usr/bin/cmp -s "$TEMP/preserve-expected" "$TEMP/preserve-log" || \
  fail "preserve log was not exact"
test "$(<"$TEMP/preserve-output")" = "EMKE Translation uninstalled."
if /usr/bin/grep -q '^purge-' "$TEMP/preserve-log"; then
  fail "preserve mode emitted purge markers"
fi

# Explicit purge removes the same owned paths and emits exact purge, receipt,
# and Core Audio markers without touching unrelated siblings.
mkdir -p "$SAFE_ROOT/Applications/EMKE Translation.app" \
  "$SAFE_ROOT/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver" \
  "$SAFE_ROOT/Library/Application Support/EMKE Translation"
: > "$TEMP/purge-log"
EMKE_TEST_MODE=1 EMKE_TEST_ROOT="$SAFE_ROOT" EMKE_TEST_LOG="$TEMP/purge-log" \
  bash "$UNINSTALL" --purge-user-data > "$TEMP/purge-output"
test ! -e "$SAFE_ROOT/Applications/EMKE Translation.app"
test ! -e "$SAFE_ROOT/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
test ! -e "$SAFE_ROOT/Library/Application Support/EMKE Translation"
assert_unrelated_paths_exist "$SAFE_ROOT"
printf '%s\n' \
  'purge-keychain:com.emke.translation:openai-api-key' \
  'purge-defaults:com.emke.translation.app' \
  'forget-receipt:com.emke.translation.internal' \
  'refresh-core-audio' > "$TEMP/purge-expected"
/usr/bin/cmp -s "$TEMP/purge-expected" "$TEMP/purge-log" || \
  fail "purge log was not exact"
test "$(<"$TEMP/purge-output")" = "EMKE Translation uninstalled."

EMKE_TEST_MODE=1 EMKE_TEST_LOG="$TEMP/postinstall-log" bash "$POSTINSTALL"
printf '%s\n' 'refresh-core-audio' > "$TEMP/postinstall-expected"
/usr/bin/cmp -s "$TEMP/postinstall-expected" "$TEMP/postinstall-log" || \
  fail "postinstall log was not exact"

if /usr/bin/grep -qF '|| true' "$UNINSTALL"; then
  fail "uninstaller contains blanket error suppression"
fi

echo "PASS: lifecycle scripts"
