#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
PKG="$ROOT/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"
test -s "$PKG" || bash "$ROOT/Packaging/build-internal-pkg.sh"
TEMP="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/emke-driver-read-only.XXXXXX")"
trap '/usr/bin/find "$TEMP" -depth -delete 2>/dev/null || true' EXIT
/usr/sbin/pkgutil --expand-full "$PKG" "$TEMP/expanded"
DRIVER="$TEMP/expanded/Payload/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
EXECUTABLE="$DRIVER/Contents/MacOS/EMKEAudioDriver"
SIGNATURE="$DRIVER/Contents/_CodeSignature/CodeResources"
EXECUTABLE_BEFORE="$(/usr/bin/shasum -a 256 "$EXECUTABLE")"
SIGNATURE_BEFORE="$(/usr/bin/shasum -a 256 "$SIGNATURE")"
bash "$ROOT/Driver/verify-bundle.sh" --read-only "$DRIVER"
test "$(/usr/bin/shasum -a 256 "$EXECUTABLE")" = "$EXECUTABLE_BEFORE"
test "$(/usr/bin/shasum -a 256 "$SIGNATURE")" = "$SIGNATURE_BEFORE"
echo "PASS: packaged driver verification is read-only"
