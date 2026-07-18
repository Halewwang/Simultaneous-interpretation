#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-app-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
APP="$TEMP/EMKE Translation.app"
bash "$ROOT/Packaging/Scripts/build-app-bundle.sh" "$APP"

PLIST="$APP/Contents/Info.plist"
test -x "$APP/Contents/MacOS/EMKEMenuBarApp"
test -s "$APP/Contents/Resources/AppIcon.icns"
test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PLIST")" \
  = "com.emke.translation.app"
test "$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$PLIST")" \
  = "14.0"
test "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$PLIST")" = "true"
/usr/bin/codesign --verify --strict --verbose=2 "$APP"
/usr/bin/file "$APP/Contents/MacOS/EMKEMenuBarApp" | /usr/bin/grep -q arm64
echo "PASS: app bundle"
