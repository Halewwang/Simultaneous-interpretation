#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-icon-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT

bash "$ROOT/Packaging/Scripts/build-app-icon.sh" \
  "$ROOT/Packaging/Assets/EMKE-AppIcon-Approved.png" "$TEMP"

for file in \
  icon_16x16.png icon_16x16@2x.png \
  icon_32x32.png icon_32x32@2x.png \
  icon_128x128.png icon_128x128@2x.png \
  icon_256x256.png icon_256x256@2x.png \
  icon_512x512.png icon_512x512@2x.png; do
  test -f "$TEMP/AppIcon.iconset/$file"
done
test -f "$TEMP/AppIcon.icns"
swift "$ROOT/Packaging/Tests/assert-icon-alpha.swift" \
  "$TEMP/AppIcon-1024.png"
echo "PASS: icon pipeline"
