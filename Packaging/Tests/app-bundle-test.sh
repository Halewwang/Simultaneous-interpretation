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
test -s "$APP/Contents/Resources/EMKE-MenuBarIcon.png"
MENU_ICON="$APP/Contents/Resources/EMKE-MenuBarIcon.png"
test "$(/usr/bin/sips -g pixelWidth "$MENU_ICON" | \
  /usr/bin/awk '/pixelWidth/ { print $2 }')" = 36
test "$(/usr/bin/sips -g pixelHeight "$MENU_ICON" | \
  /usr/bin/awk '/pixelHeight/ { print $2 }')" = 36
test "$(/usr/bin/sips -g hasAlpha "$MENU_ICON" | \
  /usr/bin/awk '/hasAlpha/ { print $2 }')" = yes
test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PLIST")" \
  = "com.emke.translation.app"
test "$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$PLIST")" \
  = "14.0"
test "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$PLIST")" = "true"
/usr/bin/codesign --verify --strict --verbose=2 "$APP"
FILE_OUTPUT="$(/usr/bin/file "$APP/Contents/MacOS/EMKEMenuBarApp")"
/usr/bin/grep -q arm64 <<< "$FILE_OUTPUT"
echo "PASS: app bundle"
