#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
APP="${1:?missing output app path}"
ICON_OUTPUT="$(dirname "$APP")/icon-build"

case "$APP" in *.app) ;; *) echo "output must end in .app" >&2; exit 64;; esac
swift build --package-path "$ROOT" -c release --product EMKEMenuBarApp
BIN_DIR="$(swift build --package-path "$ROOT" -c release --show-bin-path)"
test -x "$BIN_DIR/EMKEMenuBarApp"

rm -rf "$APP" "$ICON_OUTPUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$ICON_OUTPUT"
bash "$ROOT/Packaging/Scripts/build-app-icon.sh" \
  "$ROOT/Packaging/Assets/EMKE-AppIcon-Approved.png" "$ICON_OUTPUT"
/usr/bin/ditto "$BIN_DIR/EMKEMenuBarApp" \
  "$APP/Contents/MacOS/EMKEMenuBarApp"
/usr/bin/ditto "$ROOT/Packaging/App/Info.plist" "$APP/Contents/Info.plist"
/usr/bin/ditto "$ICON_OUTPUT/AppIcon.icns" \
  "$APP/Contents/Resources/AppIcon.icns"
/bin/chmod 755 "$APP/Contents/MacOS/EMKEMenuBarApp"
/usr/bin/plutil -lint "$APP/Contents/Info.plist"
/usr/bin/codesign --force --sign - --options runtime --timestamp=none "$APP"
/usr/bin/codesign --verify --strict --verbose=2 "$APP"
