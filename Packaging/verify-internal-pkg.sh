#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
PKG="${1:?missing pkg path}"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-pkg-verify.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
EXPANDED="$TEMP/expanded"
require() { "$@" || { echo "verification failed: $*" >&2; exit 1; }; }
require_unique_expanded_path() {
  local name="$1"
  local expected="$2"
  local listing="$TEMP/expanded-name-$name"
  local item
  local count=0
  local exact=0
  if ! /usr/bin/find "$EXPANDED" -name "$name" -print0 > "$listing"; then
    echo "expanded package path discovery failed: $name" >&2; exit 1
  fi
  while IFS= read -r -d '' item; do
    count=$((count + 1))
    test "$item" = "$expected" && exact=$((exact + 1))
  done < "$listing"
  if test "$count" -ne 1 || test "$exact" -ne 1; then
    echo "ambiguous expanded package path: $name" >&2; exit 1
  fi
}
require_exact_expanded_layout() {
  local listing="$TEMP/expanded-top-level"
  local item
  if ! /usr/bin/find "$EXPANDED" -mindepth 1 -maxdepth 1 -print0 > "$listing"; then
    echo "expanded package top-level discovery failed" >&2; exit 1
  fi
  while IFS= read -r -d '' item; do
    case "$item" in
      "$EXPANDED/Bom") require test -f "$item" ;;
      "$PACKAGE_INFO") require test -f "$item" ;;
      "$PAYLOAD_ROOT"|"$SCRIPTS_ROOT") require test -d "$item" ;;
      *) echo "unexpected expanded package entry" >&2; exit 1 ;;
    esac
  done < "$listing"
  require_unique_expanded_path PackageInfo "$PACKAGE_INFO"
  require_unique_expanded_path Payload "$PAYLOAD_ROOT"
  require_unique_expanded_path Scripts "$SCRIPTS_ROOT"
}
require_no_control_character_paths() {
  local LC_ALL=C
  local listing="$TEMP/package-paths"
  local item
  if ! /usr/bin/find "$PAYLOAD_ROOT" "$SCRIPTS_ROOT" -print0 > "$listing"; then
    echo "package path discovery failed" >&2; exit 1
  fi
  while IFS= read -r -d '' item; do
    case "$item" in
      *[$'\001'-$'\037'$'\177']*)
        echo "control character found in package path" >&2; exit 1 ;;
    esac
  done < "$listing"
}
require_no_world_writable_paths() {
  local listing="$TEMP/world-writable-paths"
  local item
  if ! /usr/bin/find "$PAYLOAD_ROOT" "$SCRIPTS_ROOT" \
    -perm -0002 -print0 > "$listing"; then
    echo "world-writable path discovery failed" >&2; exit 1
  fi
  if IFS= read -r -d '' item < "$listing"; then
    echo "world-writable package path found" >&2; exit 1
  fi
}
require_exact_scripts() {
  local listing="$TEMP/scripts-entries"
  local item
  if ! /usr/bin/find "$SCRIPTS_ROOT" -mindepth 1 -maxdepth 1 -print0 > "$listing"; then
    echo "installer scripts discovery failed" >&2; exit 1
  fi
  while IFS= read -r -d '' item; do
    if test "$item" != "$SCRIPTS_ROOT/postinstall" || ! test -f "$item"; then
      echo "unexpected installer script entry" >&2; exit 1
    fi
  done < "$listing"
  require test -f "$SCRIPTS_ROOT/postinstall"
  require /usr/bin/cmp -s "$ROOT/Packaging/InstallerScripts/postinstall" \
    "$SCRIPTS_ROOT/postinstall"
}
require_only_provenance_xattrs() {
  local item
  local attrs
  local attr
  local listing="$TEMP/payload-xattr-items"
  if ! /usr/bin/find "$1" -print0 > "$listing"; then
    echo "payload xattr path discovery failed" >&2; exit 1
  fi
  while IFS= read -r -d '' item; do
    if ! attrs="$(/usr/bin/xattr "$item")"; then
      echo "payload xattr scan failed" >&2; exit 1
    fi
    while IFS= read -r attr; do
      test -z "$attr" && continue
      if test "$attr" != com.apple.provenance; then
        echo "disallowed payload xattr: $attr ($item)" >&2
        exit 1
      fi
    done <<< "$attrs"
  done < "$listing"
}
require_payload_modes() {
  local item
  local actual
  local expected
  local listing="$TEMP/payload-mode-items"
  local special
  if ! /usr/bin/find "$1" \( -type d -o -type f \) -print0 > "$listing"; then
    echo "payload mode path discovery failed" >&2; exit 1
  fi
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
  done < "$listing"
  if ! special="$(/usr/bin/find "$1" ! -type d ! -type f -print -quit)"; then
    echo "payload object-type discovery failed" >&2; exit 1
  fi
  if test -n "$special"; then
    echo "unexpected payload object type" >&2; exit 1
  fi
}
require_root_bom_ownership() {
  local listing="$TEMP/bom-ownership"
  local uid
  local gid
  local extra
  if ! /usr/bin/lsbom -p ug "$BOM" > "$listing"; then
    echo "package BOM ownership discovery failed" >&2; exit 1
  fi
  if ! test -s "$listing"; then
    echo "package BOM contains no payload entries" >&2; exit 1
  fi
  while IFS=$'\t' read -r uid gid extra; do
    if test "$uid" != 0 || test "$gid" != 0 || test -n "$extra"; then
      echo "non-root package BOM ownership: uid=$uid gid=$gid" >&2
      exit 1
    fi
  done < "$listing"
}
scan_credential_like_values() {
  local item
  local index=0
  local listing="$TEMP/credential-files"
  if ! /usr/bin/find "$PAYLOAD_ROOT" "$SCRIPTS_ROOT" -type f -print0 > "$listing"; then
    echo "credential scan file discovery failed" >&2; exit 1
  fi
  while IFS= read -r -d '' item; do
    index=$((index + 1))
    if ! /usr/bin/strings "$item" > "$TEMP/credential-strings-$index"; then
      echo "credential scan strings extraction failed" >&2; exit 1
    fi
    if /usr/bin/grep -Eq \
      'sk-[A-Za-z0-9_-]{20,}|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY' \
      "$TEMP/credential-strings-$index"; then
      echo "credential-like value found in package content" >&2; exit 1
    fi
  done < "$listing"
}
require_raw_payload_entries() {
  local path
  for path in \
    'Applications' \
    'Applications/EMKE Translation.app' \
    'Applications/EMKE Translation.app/Contents/Info.plist' \
    'Applications/EMKE Translation.app/Contents/MacOS/EMKEMenuBarApp' \
    'Applications/EMKE Translation.app/Contents/Resources/AppIcon.icns' \
    'Applications/EMKE Translation.app/Contents/Resources/EMKE-MenuBarIcon.png' \
    'Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver' \
    'Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/Contents/Info.plist' \
    'Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/Contents/MacOS/EMKEAudioDriver' \
    'Library/Application Support/EMKE Translation/uninstall-emke.sh'; do
    if ! /usr/bin/grep -Fqx "$path" "$TEMP/business-payload-paths"; then
      echo "missing required payload entry: $path" >&2; exit 1
    fi
  done
}
require_plist_value() {
  local key="$1"
  local expected="$2"
  local actual
  actual="$(/usr/libexec/PlistBuddy -c "Print :$key" "$PLIST")"
  if test "$actual" != "$expected"; then
    echo "unexpected app Info.plist value: $key" >&2; exit 1
  fi
}

require test -s "$PKG"
/usr/sbin/pkgutil --expand-full "$PKG" "$EXPANDED"
PACKAGE_INFO="$EXPANDED/PackageInfo"
BOM="$EXPANDED/Bom"
PAYLOAD_ROOT="$EXPANDED/Payload"
SCRIPTS_ROOT="$EXPANDED/Scripts"
APP="$PAYLOAD_ROOT/Applications/EMKE Translation.app"
DRIVER="$PAYLOAD_ROOT/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
UNINSTALLER="$PAYLOAD_ROOT/Library/Application Support/EMKE Translation/uninstall-emke.sh"
require_exact_expanded_layout
require test -f "$BOM"
require_no_control_character_paths
require_no_world_writable_paths
require_exact_scripts
if ! test -d "$APP"; then
  echo "missing required payload entry: Applications/EMKE Translation.app" >&2; exit 1
fi
if ! test -d "$DRIVER"; then
  echo "missing required payload entry: EMKEAudioDriver.driver" >&2; exit 1
fi
if ! test -f "$UNINSTALLER"; then
  echo "missing required payload entry: uninstall-emke.sh" >&2; exit 1
fi
PACKAGE_IDENTIFIER="$(/usr/bin/xmllint --xpath \
  'string(/pkg-info/@identifier)' "$PACKAGE_INFO")"
PACKAGE_VERSION="$(/usr/bin/xmllint --xpath \
  'string(/pkg-info/@version)' "$PACKAGE_INFO")"
INSTALL_LOCATION="$(/usr/bin/xmllint --xpath \
  'string(/pkg-info/@install-location)' "$PACKAGE_INFO")"
PACKAGE_AUTH="$(/usr/bin/xmllint --xpath \
  'string(/pkg-info/@auth)' "$PACKAGE_INFO")"
if test "$PACKAGE_IDENTIFIER" != com.emke.translation.internal; then
  echo "unexpected package identifier" >&2; exit 1
fi
if test "$PACKAGE_VERSION" != 0.1.0; then
  echo "unexpected package version" >&2; exit 1
fi
if test "$INSTALL_LOCATION" != /; then
  echo "unexpected package install-location" >&2; exit 1
fi
if test "$PACKAGE_AUTH" != root; then
  echo "unexpected package auth; expected root" >&2; exit 1
fi
require_root_bom_ownership
if ! PHYSICAL_APPLEDOUBLE="$(/usr/bin/find "$PAYLOAD_ROOT" "$SCRIPTS_ROOT" \
  -name '._*' -print -quit)"; then
  echo "physical AppleDouble discovery failed" >&2; exit 1
fi
if test -n "$PHYSICAL_APPLEDOUBLE"; then
  echo "physical AppleDouble file found in payload" >&2; exit 1
fi
require_only_provenance_xattrs "$PAYLOAD_ROOT"
require_payload_modes "$PAYLOAD_ROOT"
scan_credential_like_values

if ! /usr/sbin/pkgutil --payload-files "$PKG" > "$TEMP/payload-files"; then
  echo "payload listing failed" >&2; exit 1
fi
if ! bash "$ROOT/Packaging/Scripts/verify-payload-list.sh" \
  "$TEMP/payload-files" > "$TEMP/business-payload-paths"; then
  echo "payload path verification failed" >&2; exit 1
fi
require_raw_payload_entries

PLIST="$APP/Contents/Info.plist"
require_plist_value CFBundleDevelopmentRegion zh_CN
require_plist_value CFBundleDisplayName 'EMKE Translation'
require_plist_value CFBundleExecutable EMKEMenuBarApp
require_plist_value CFBundleIconFile AppIcon.icns
require_plist_value CFBundleIdentifier com.emke.translation.app
require_plist_value CFBundleInfoDictionaryVersion 6.0
require_plist_value CFBundleName 'EMKE Translation'
require_plist_value CFBundlePackageType APPL
require_plist_value CFBundleShortVersionString 0.1.0
require_plist_value CFBundleVersion 1
require_plist_value LSMinimumSystemVersion 14.0
require_plist_value LSUIElement true
require_plist_value NSHighResolutionCapable true
require_plist_value NSMicrophoneUsageDescription \
  'EMKE 需要访问麦克风，以便在本机翻译并将译音发送到会议应用。'
require_plist_value NSPrincipalClass NSApplication
require /usr/bin/codesign --verify --strict --verbose=2 "$APP"
require /usr/bin/codesign --verify --strict --verbose=2 "$DRIVER"
if ! /usr/bin/codesign -dv --verbose=4 "$APP" > "$TEMP/app-codesign" 2>&1; then
  echo "app codesign metadata capture failed" >&2; exit 1
fi
if ! /usr/bin/codesign -dv --verbose=4 "$DRIVER" > "$TEMP/driver-codesign" 2>&1; then
  echo "driver codesign metadata capture failed" >&2; exit 1
fi
require bash "$ROOT/Packaging/Scripts/verify-codesign-metadata.sh" \
  com.emke.translation.app "$TEMP/app-codesign"
require bash "$ROOT/Packaging/Scripts/verify-codesign-metadata.sh" \
  com.emke.translation.audio-driver "$TEMP/driver-codesign"
require test "$(/usr/bin/lipo -archs "$APP/Contents/MacOS/EMKEMenuBarApp")" = arm64
require test "$(/usr/bin/lipo -archs "$DRIVER/Contents/MacOS/EMKEAudioDriver")" = arm64
require test -s "$APP/Contents/Resources/AppIcon.icns"
require test -s "$APP/Contents/Resources/EMKE-MenuBarIcon.png"
require test "$(/usr/bin/sips -g pixelWidth \
  "$APP/Contents/Resources/EMKE-MenuBarIcon.png" | \
  /usr/bin/awk '/pixelWidth/ { print $2 }')" = 36
require test "$(/usr/bin/sips -g pixelHeight \
  "$APP/Contents/Resources/EMKE-MenuBarIcon.png" | \
  /usr/bin/awk '/pixelHeight/ { print $2 }')" = 36
require test "$(/usr/bin/sips -g hasAlpha \
  "$APP/Contents/Resources/EMKE-MenuBarIcon.png" | \
  /usr/bin/awk '/hasAlpha/ { print $2 }')" = yes
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
if ! AUDIO_ARTIFACT="$(/usr/bin/find "$PAYLOAD_ROOT" -type f \
  \( -name '*.wav' -o -name '*.aiff' -o -name '*.m4a' -o -name '*.mp3' \) \
  -print -quit)"; then
  echo "audio artifact discovery failed" >&2; exit 1
fi
if test -n "$AUDIO_ARTIFACT"; then
  echo "audio file found in payload" >&2; exit 1
fi
if ! TRANSCRIPT_ARTIFACT="$(/usr/bin/find "$PAYLOAD_ROOT" -type f \
  \( -iname '*transcript*' -o -iname '*subtitle*' -o -iname '*recording*' \) \
  -print -quit)"; then
  echo "transcript artifact discovery failed" >&2; exit 1
fi
if test -n "$TRANSCRIPT_ARTIFACT"; then
  echo "transcript or recording artifact found in payload" >&2; exit 1
fi
bash "$ROOT/Driver/verify-bundle.sh" --read-only "$DRIVER"
set +e
/usr/sbin/pkgutil --check-signature "$PKG" > "$TEMP/pkg-signature" 2>&1
PACKAGE_SIGNATURE_STATUS=$?
set -e
if test "$(/usr/bin/grep -Fxc '   Status: no signature' \
  "$TEMP/pkg-signature")" != 1; then
  echo "expected unsigned internal package status" >&2; exit 1
fi
if test "$PACKAGE_SIGNATURE_STATUS" -gt 1; then
  echo "package signature status check failed" >&2; exit 1
fi
set +e
/usr/sbin/spctl --assess --type install --verbose=4 "$PKG" \
  > "$TEMP/notarization-status" 2>&1
NOTARIZATION_STATUS=$?
set -e
if test "$NOTARIZATION_STATUS" -eq 0 || \
  ! /usr/bin/grep -Fqx "$PKG: rejected" "$TEMP/notarization-status" || \
  ! /usr/bin/grep -Fqx 'source=no usable signature' "$TEMP/notarization-status"; then
  echo "expected not-notarized internal package status" >&2; exit 1
fi
echo "PASS: internal pkg verified (unsigned, not notarized)"
