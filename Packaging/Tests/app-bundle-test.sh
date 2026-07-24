#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-app-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
APP="$TEMP/EMKE Translation.app"
export EMKE_VERSION=9.8.7
export EMKE_BUILD_NUMBER=987
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
test "$(/usr/libexec/PlistBuddy -c \
  'Print :CFBundleShortVersionString' "$PLIST")" = "9.8.7"
test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$PLIST")" = "987"
test "$(/usr/libexec/PlistBuddy -c \
  'Print :SUEnableAutomaticChecks' "$PLIST")" = true
test "$(/usr/libexec/PlistBuddy -c \
  'Print :SUAutomaticallyUpdate' "$PLIST")" = true
test "$(/usr/libexec/PlistBuddy -c 'Print :SUPublicEDKey' "$PLIST")" \
  = "6JsBQ/d+InVfoZEG2nlLM+L9GaVss0kaC/ZyoMhDYoM="
test "$(/usr/libexec/PlistBuddy -c 'Print :SUFeedURL' "$PLIST")" \
  = "https://raw.githubusercontent.com/Halewwang/Simultaneous-interpretation/gh-pages/appcast.xml"
test -d "$APP/Contents/Frameworks/Sparkle.framework"
test -L "$APP/Contents/Frameworks/Sparkle.framework/Versions/Current"
test "$(/usr/bin/readlink \
  "$APP/Contents/Frameworks/Sparkle.framework/Versions/Current")" = B
/usr/bin/codesign --verify --strict --verbose=2 \
  "$APP/Contents/Frameworks/Sparkle.framework"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$APP"
/usr/bin/otool -l "$APP/Contents/MacOS/EMKEMenuBarApp" | \
  /usr/bin/grep -Fq '@executable_path/../Frameworks'
ENTITLEMENTS="$TEMP/app-entitlements.plist"
/usr/bin/codesign -d --entitlements :- "$APP" > "$ENTITLEMENTS" 2>/dev/null
test "$(/usr/libexec/PlistBuddy -c \
  'Print :com.apple.security.device.audio-input' "$ENTITLEMENTS")" = true
test "$(/usr/libexec/PlistBuddy -c \
  'Print :com.apple.security.cs.disable-library-validation' \
  "$ENTITLEMENTS")" = true
FILE_OUTPUT="$(/usr/bin/file "$APP/Contents/MacOS/EMKEMenuBarApp")"
/usr/bin/grep -q arm64 <<< "$FILE_OUTPUT"
echo "PASS: app bundle"
