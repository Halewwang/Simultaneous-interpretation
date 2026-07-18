#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
PKG="${1:?missing pkg path}"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-pkg-verify.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
EXPANDED="$TEMP/expanded"
require() { "$@" || { echo "verification failed: $*" >&2; exit 1; }; }
require_only_provenance_xattrs() {
  local item
  local attrs
  local attr
  while IFS= read -r -d '' item; do
    attrs="$(/usr/bin/xattr "$item")"
    while IFS= read -r attr; do
      test -z "$attr" && continue
      if test "$attr" != com.apple.provenance; then
        echo "disallowed payload xattr: $attr ($item)" >&2
        exit 1
      fi
    done <<< "$attrs"
  done < <(/usr/bin/find "$1" -print0)
}
require_payload_modes() {
  local item
  local actual
  local expected
  while IFS= read -r -d '' item; do
    actual="$(/usr/bin/stat -f '%Lp' "$item")"
    if test -d "$item"; then
      expected=755
    else
      case "$item" in
        "$APP/Contents/MacOS/EMKEMenuBarApp"|\
        "$DRIVER/Contents/MacOS/EMKEAudioDriver"|"$UNINSTALLER") expected=755 ;;
        *) expected=644 ;;
      esac
    fi
    if test "$actual" != "$expected"; then
      echo "unexpected payload mode: $actual (expected $expected): $item" >&2
      exit 1
    fi
  done < <(/usr/bin/find "$1" \( -type d -o -type f \) -print0)
  if /usr/bin/find "$1" ! -type d ! -type f -print -quit | /usr/bin/grep -q .; then
    echo "unexpected payload object type" >&2; exit 1
  fi
}

require test -s "$PKG"
/usr/sbin/pkgutil --expand-full "$PKG" "$EXPANDED"
PACKAGE_INFO="$(find "$EXPANDED" -name PackageInfo -type f -print -quit)"
APP="$(find "$EXPANDED" -path '*/Applications/EMKE Translation.app' -type d -print -quit)"
DRIVER="$(find "$EXPANDED" -path '*/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver' -type d -print -quit)"
UNINSTALLER="$(find "$EXPANDED" -path '*/Library/Application Support/EMKE Translation/uninstall-emke.sh' -type f -print -quit)"
PAYLOAD_ROOT="$(find "$EXPANDED" -path '*/Payload' -type d -print -quit)"
require test -n "$PACKAGE_INFO"; require test -n "$APP"
require test -n "$DRIVER"; require test -n "$UNINSTALLER"
require test -n "$PAYLOAD_ROOT"
require /usr/bin/grep -q 'identifier="com.emke.translation.internal"' "$PACKAGE_INFO"
require /usr/bin/grep -q 'version="0.1.0"' "$PACKAGE_INFO"
if find "$EXPANDED" -name '._*' -print -quit | /usr/bin/grep -q .; then
  echo "physical AppleDouble file found in payload" >&2; exit 1
fi
require_only_provenance_xattrs "$PAYLOAD_ROOT"
require_payload_modes "$PAYLOAD_ROOT"

/usr/sbin/pkgutil --payload-files "$PKG" > "$TEMP/payload-files"
while IFS= read -r path; do
  path="${path#./}"
  if test "$path" = . || test -z "$path"; then continue; fi
  IFS='/' read -r -a components <<< "$path"
  decoded=""
  appledouble_count=0
  for component in "${components[@]}"; do
    if [[ "$component" == ._* ]]; then
      component="${component#._}"
      require test -n "$component"
      appledouble_count=$((appledouble_count + 1))
    fi
    decoded="${decoded:+$decoded/}$component"
  done
  if test "$appledouble_count" -gt 1; then
    echo "ambiguous AppleDouble payload path: $path" >&2; exit 1
  fi
  if test "$appledouble_count" -eq 1 && \
    ! /usr/bin/grep -Fqx "./$decoded" "$TEMP/payload-files" && \
    ! /usr/bin/grep -Fqx "$decoded" "$TEMP/payload-files"; then
    echo "orphan AppleDouble payload path: $path" >&2; exit 1
  fi
  path="$decoded"
  case "$path" in
    Applications|Applications/EMKE\ Translation.app|\
    Applications/EMKE\ Translation.app/*|\
    Library|Library/Audio|Library/Audio/Plug-Ins|\
    Library/Audio/Plug-Ins/HAL|\
    Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver|\
    Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/*|\
    Library/Application\ Support|Library/Application\ Support/EMKE\ Translation|\
    Library/Application\ Support/EMKE\ Translation/uninstall-emke.sh) ;;
    "") ;;
    *) echo "unexpected payload path: $path" >&2; exit 1 ;;
  esac
done < "$TEMP/payload-files"

PLIST="$APP/Contents/Info.plist"
require test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PLIST")" = com.emke.translation.app
require test "$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$PLIST")" = 14.0
require test "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$PLIST")" = true
require test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' "$PLIST")" = AppIcon.icns
require test "$(/usr/libexec/PlistBuddy -c 'Print :NSMicrophoneUsageDescription' "$PLIST")" = \
  'EMKE 需要访问麦克风，以便在本机翻译并将译音发送到会议应用。'
require /usr/bin/codesign --verify --strict --verbose=2 "$APP"
require /usr/bin/codesign --verify --strict --verbose=2 "$DRIVER"
require /usr/bin/file "$APP/Contents/MacOS/EMKEMenuBarApp"
require /usr/bin/file "$DRIVER/Contents/MacOS/EMKEAudioDriver"
/usr/bin/file "$APP/Contents/MacOS/EMKEMenuBarApp" | require /usr/bin/grep -q arm64
/usr/bin/file "$DRIVER/Contents/MacOS/EMKEAudioDriver" | require /usr/bin/grep -q arm64
require test -s "$APP/Contents/Resources/AppIcon.icns"
DECODED_ICONSET="$TEMP/decoded.iconset"
/usr/bin/iconutil -c iconset "$APP/Contents/Resources/AppIcon.icns" \
  -o "$DECODED_ICONSET"
for file in icon_16x16.png icon_16x16@2x.png icon_32x32.png \
  icon_32x32@2x.png icon_128x128.png icon_128x128@2x.png \
  icon_256x256.png icon_256x256@2x.png icon_512x512.png \
  icon_512x512@2x.png; do require test -s "$DECODED_ICONSET/$file"; done
require test "$(/usr/bin/stat -f '%Lp' "$UNINSTALLER")" = 755
require test "$(/usr/bin/stat -f '%Lp' "$APP/Contents/Info.plist")" = 644

/usr/bin/strings "$DRIVER/Contents/MacOS/EMKEAudioDriver" > "$TEMP/driver-strings"
require /usr/bin/grep -qx com.emke.translation.virtual-speaker "$TEMP/driver-strings"
require /usr/bin/grep -qx com.emke.translation.virtual-microphone "$TEMP/driver-strings"
if /usr/bin/strings "$APP/Contents/MacOS/EMKEMenuBarApp" | \
  /usr/bin/grep -E 'sk-[A-Za-z0-9_-]{20,}|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY'; then
  echo "credential-like value found" >&2; exit 1
fi
if find "$EXPANDED" -type f \( -name '*.wav' -o -name '*.aiff' \
  -o -name '*.m4a' -o -name '*.mp3' \) -print -quit | /usr/bin/grep -q .; then
  echo "audio file found in payload" >&2; exit 1
fi
if find "$EXPANDED" -type f \( -iname '*transcript*' -o \
  -iname '*subtitle*' -o -iname '*recording*' \) -print -quit | \
  /usr/bin/grep -q .; then
  echo "transcript or recording artifact found in payload" >&2; exit 1
fi
if find "$EXPANDED" -perm -0002 -print -quit | /usr/bin/grep -q .; then
  echo "world-writable payload path found" >&2; exit 1
fi

bash "$ROOT/Driver/verify-bundle.sh" "$DRIVER"
/usr/sbin/pkgutil --check-signature "$PKG" > "$TEMP/pkg-signature" 2>&1 || true
if ! /usr/bin/grep -Eiq 'unsigned|no signature' "$TEMP/pkg-signature"; then
  echo "expected unsigned internal package status" >&2; exit 1
fi
echo "PASS: internal pkg verified (unsigned, not notarized)"
