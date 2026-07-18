#!/bin/bash
set -euo pipefail

READ_ONLY=0
if [[ "${1:-}" == "--read-only" ]]; then
    READ_ONLY=1
    shift
fi
BUNDLE_PATH="${1:-}"
if [[ "$#" -ne 1 || -z "$BUNDLE_PATH" || ! -d "$BUNDLE_PATH" ]]; then
    echo "driver bundle not found: ${BUNDLE_PATH:-<missing>}" >&2
    exit 1
fi

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd -P)"
PLIST_PATH="$BUNDLE_PATH/Contents/Info.plist"
EXECUTABLE_PATH="$BUNDLE_PATH/Contents/MacOS/EMKEAudioDriver"
SMOKE_EXECUTABLE="$ROOT_DIR/.build/driver/verify-bundle"

[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundlePackageType' "$PLIST_PATH")" == "BNDL" ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PLIST_PATH")" == "com.emke.translation.audio-driver" ]]
/usr/libexec/PlistBuddy -c 'Print :CFPlugInFactories:E4A04A37-A2C4-4C65-B6F6-0E9F5A59B8D1' "$PLIST_PATH" | grep -qx 'EMKEAudioDriver_Create'
/usr/libexec/PlistBuddy -c 'Print :CFPlugInTypes:443ABAB8-E7B3-491A-B985-BEB9187030DB' "$PLIST_PATH" | grep -q 'E4A04A37-A2C4-4C65-B6F6-0E9F5A59B8D1'
file "$EXECUTABLE_PATH" | grep -q 'arm64'
otool -L "$EXECUTABLE_PATH" | grep -q 'CoreAudio.framework'
otool -L "$EXECUTABLE_PATH" | grep -q 'CoreFoundation.framework'
nm -gU "$EXECUTABLE_PATH" | grep -q '_EMKEAudioDriver_Create'

# File Provider workspaces can quarantine newly generated bundles. Mutating
# build verification clears and signs only generated artifacts. Read-only
# package verification must preserve the expanded payload byte-for-byte.
if [[ "$READ_ONLY" == "0" ]]; then
    xattr -cr "$BUNDLE_PATH"
    /usr/bin/codesign --sign - --force "$BUNDLE_PATH"
fi
/usr/bin/codesign --verify --strict "$BUNDLE_PATH"

xcrun clang -std=c11 -arch arm64 -mmacosx-version-min=14.0 \
    -Wall -Wextra -Werror \
    "$ROOT_DIR/Driver/verify-bundle.c" \
    -framework CoreAudio -framework CoreFoundation \
    -o "$SMOKE_EXECUTABLE"
xattr -cr "$SMOKE_EXECUTABLE"
/usr/bin/codesign --sign - --force --options runtime \
    --entitlements "$ROOT_DIR/Driver/verify-bundle.entitlements" \
    "$SMOKE_EXECUTABLE"
"$SMOKE_EXECUTABLE" "$BUNDLE_PATH"

echo "bundle-id: com.emke.translation.audio-driver"
echo "architecture: arm64"
echo "factory: EMKEAudioDriver_Create"
echo "PASS"
