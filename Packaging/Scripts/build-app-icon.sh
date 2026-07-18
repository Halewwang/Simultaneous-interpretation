#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
INPUT="${1:?missing approved icon master}"
OUTPUT="${2:?missing icon output directory}"
MASTER="$OUTPUT/AppIcon-1024.png"
ICONSET="$OUTPUT/AppIcon.iconset"

rm -rf "$ICONSET"
mkdir -p "$ICONSET"
swift "$ROOT/Packaging/Scripts/prepare-icon-master.swift" "$INPUT" "$MASTER"

resize() { /usr/bin/sips -s format png -z "$1" "$1" "$MASTER" \
  --out "$ICONSET/$2" >/dev/null; }
resize 16 icon_16x16.png
resize 32 icon_16x16@2x.png
resize 32 icon_32x32.png
resize 64 icon_32x32@2x.png
resize 128 icon_128x128.png
resize 256 icon_128x128@2x.png
resize 256 icon_256x256.png
resize 512 icon_256x256@2x.png
resize 512 icon_512x512.png
resize 1024 icon_512x512@2x.png
/usr/bin/iconutil -c icns "$ICONSET" -o "$OUTPUT/AppIcon.icns"
test -s "$OUTPUT/AppIcon.icns"
