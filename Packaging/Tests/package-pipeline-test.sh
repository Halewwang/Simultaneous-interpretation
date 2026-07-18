#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
PKG="$ROOT/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"
bash "$ROOT/Packaging/build-internal-pkg.sh"
test -s "$PKG"
VERIFY_OUTPUT="$(bash "$ROOT/Packaging/verify-internal-pkg.sh" "$PKG" 2>&1)"
echo "$VERIFY_OUTPUT"
if /usr/bin/grep -q 'unbound variable' <<< "$VERIFY_OUTPUT"; then
  echo "verifier emitted a shell runtime error" >&2; exit 1
fi
/usr/bin/grep -q 'PASS: internal pkg verified (unsigned, not notarized)' \
  <<< "$VERIFY_OUTPUT"
TEMP="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/emke-pkg-test.XXXXXX")"
trap '/usr/bin/find "$TEMP" -depth -delete 2>/dev/null || true' EXIT
/usr/sbin/pkgutil --expand-full "$PKG" "$TEMP/expanded"
test "$(/usr/bin/stat -f '%Lp' "$TEMP/expanded/Payload")" = 755
/usr/sbin/pkgutil --payload-files "$PKG" > "$TEMP/payload-files"
/usr/bin/grep -q 'Applications/EMKE Translation.app/Contents/Info.plist' \
  "$TEMP/payload-files"
/usr/bin/grep -q \
  'Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/Contents/Info.plist' \
  "$TEMP/payload-files"
/usr/bin/grep -q \
  'Library/Application Support/EMKE Translation/uninstall-emke.sh' \
  "$TEMP/payload-files"
echo "PASS: package pipeline"
