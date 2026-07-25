#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
APP="${1:?missing output app path}"
ICON_OUTPUT="$(dirname "$APP")/icon-build"
EMKE_VERSION="${EMKE_VERSION:-0.2.1}"
EMKE_BUILD_NUMBER="${EMKE_BUILD_NUMBER:-2001}"

case "$APP" in *.app) ;; *) echo "output must end in .app" >&2; exit 64;; esac
if [[ ! "$EMKE_VERSION" =~ ^[0-9]+(\.[0-9]+)*$ ]] || \
  [[ "${#EMKE_VERSION}" -gt 64 ]]; then
  echo "invalid EMKE_VERSION" >&2
  exit 64
fi
if [[ ! "$EMKE_BUILD_NUMBER" =~ ^[0-9]+$ ]] || \
  [[ "${#EMKE_BUILD_NUMBER}" -gt 20 ]]; then
  echo "invalid EMKE_BUILD_NUMBER" >&2
  exit 64
fi
swift build --package-path "$ROOT" -c release --product EMKEMenuBarApp
BIN_DIR="$(swift build --package-path "$ROOT" -c release --show-bin-path)"
test -x "$BIN_DIR/EMKEMenuBarApp"
SPARKLE_FRAMEWORK="$BIN_DIR/Sparkle.framework"
test -d "$SPARKLE_FRAMEWORK"

rm -rf "$APP" "$ICON_OUTPUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" \
  "$APP/Contents/Frameworks" "$ICON_OUTPUT"
bash "$ROOT/Packaging/Scripts/build-app-icon.sh" \
  "$ROOT/Packaging/Assets/EMKE-AppIcon-Approved.png" "$ICON_OUTPUT"
/usr/bin/ditto "$BIN_DIR/EMKEMenuBarApp" \
  "$APP/Contents/MacOS/EMKEMenuBarApp"
/usr/bin/ditto "$SPARKLE_FRAMEWORK" \
  "$APP/Contents/Frameworks/Sparkle.framework"
/usr/bin/ditto "$ROOT/Packaging/App/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c \
  "Set :CFBundleShortVersionString $EMKE_VERSION" \
  "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c \
  "Set :CFBundleVersion $EMKE_BUILD_NUMBER" \
  "$APP/Contents/Info.plist"
/usr/bin/ditto "$ICON_OUTPUT/AppIcon.icns" \
  "$APP/Contents/Resources/AppIcon.icns"
/usr/bin/ditto \
  "$ROOT/Sources/EMKEMenuBarApp/Resources/EMKE-MenuBarIcon.png" \
  "$APP/Contents/Resources/EMKE-MenuBarIcon.png"
/bin/chmod 755 "$APP/Contents/MacOS/EMKEMenuBarApp"
if ! /usr/bin/otool -l "$APP/Contents/MacOS/EMKEMenuBarApp" | \
  /usr/bin/grep -Fq '@executable_path/../Frameworks'; then
  /usr/bin/install_name_tool -add_rpath \
    '@executable_path/../Frameworks' \
    "$APP/Contents/MacOS/EMKEMenuBarApp"
fi
/usr/bin/xattr -cr "$APP"
/usr/bin/xattr -d com.apple.FinderInfo "$APP" 2>/dev/null || true
/usr/bin/xattr -d 'com.apple.fileprovider.fpfs#P' "$APP" 2>/dev/null || true
/usr/bin/plutil -lint "$APP/Contents/Info.plist"
SPARKLE_DEST="$APP/Contents/Frameworks/Sparkle.framework"
if test -f "$SPARKLE_DEST/Versions/Current/Autoupdate"; then
  /usr/bin/codesign --force --sign - --options runtime --timestamp=none \
    "$SPARKLE_DEST/Versions/Current/Autoupdate"
fi
while IFS= read -r nested; do
  /usr/bin/codesign --force --sign - --options runtime --timestamp=none \
    "$nested"
done < <(
  /usr/bin/find "$SPARKLE_DEST" \
    -type d \( -name '*.xpc' -o -name '*.app' \) -print | \
    /usr/bin/sort -r
)
/usr/bin/codesign --force --sign - --options runtime --timestamp=none \
  "$SPARKLE_DEST"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$SPARKLE_DEST"
/usr/bin/codesign --force --sign - --options runtime --timestamp=none \
  --entitlements "$ROOT/Packaging/App/EMKETranslation.entitlements" "$APP"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$APP"
