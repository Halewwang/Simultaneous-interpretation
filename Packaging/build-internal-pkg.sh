#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
DIST="$ROOT/.build/distribution"
STAGE="$DIST/staging-root"
COMPONENTS="$DIST/components"
APP="$COMPONENTS/EMKE Translation.app"
PKG="$DIST/EMKE-Translation-0.1.0-internal.pkg"

bash "$ROOT/Packaging/Scripts/validate-build-cleanup.sh" \
  "$ROOT" "$STAGE" "$COMPONENTS" "$PKG"

require_tool() { command -v "$1" >/dev/null 2>&1 || {
  echo "missing required tool: $1" >&2; exit 69; }; }
validate_sanitized_root() {
  local listing="$2"
  local physical_appledouble
  local item
  local attrs
  local attr
  if ! physical_appledouble="$(/usr/bin/find "$1" -name '._*' -print -quit)"; then
    echo "sanitized package-root AppleDouble discovery failed" >&2
    exit 1
  fi
  if test -n "$physical_appledouble"; then
    echo "physical AppleDouble file found in sanitized package root" >&2
    exit 1
  fi
  if ! /usr/bin/find "$1" -print0 > "$listing"; then
    echo "sanitized package-root path discovery failed" >&2
    exit 1
  fi
  while IFS= read -r -d '' item; do
    if ! attrs="$(/usr/bin/xattr "$item")"; then
      echo "sanitized package-root xattr scan failed" >&2
      exit 1
    fi
    while IFS= read -r attr; do
      test -z "$attr" && continue
      if test "$attr" != com.apple.provenance; then
        echo "disallowed package-root xattr: $attr ($item)" >&2
        exit 1
      fi
    done <<< "$attrs"
  done < "$listing"
}
test "$(uname -s)" = Darwin
test "$(uname -m)" = arm64
for tool in swift make iconutil sips codesign pkgbuild pkgutil; do
  require_tool "$tool"
done

/bin/rm -rf -- "$STAGE" "$COMPONENTS"
/bin/rm -f -- "$PKG"
mkdir -p "$STAGE/Applications" \
  "$STAGE/Library/Audio/Plug-Ins/HAL" \
  "$STAGE/Library/Application Support/EMKE Translation" \
  "$COMPONENTS"

bash "$ROOT/Packaging/Scripts/build-app-bundle.sh" "$APP"
make -C "$ROOT/Driver" clean verify
/usr/bin/ditto "$APP" "$STAGE/Applications/EMKE Translation.app"
/usr/bin/ditto "$ROOT/.build/driver/EMKEAudioDriver.driver" \
  "$STAGE/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
/usr/bin/ditto "$ROOT/Packaging/Scripts/uninstall-emke.sh" \
  "$STAGE/Library/Application Support/EMKE Translation/uninstall-emke.sh"
/usr/bin/xattr -cr "$STAGE" 2>/dev/null || true
/usr/bin/find "$STAGE" -type d -exec /bin/chmod 755 {} +
/usr/bin/find "$STAGE" -type f -exec /bin/chmod 644 {} +
/bin/chmod 755 \
  "$STAGE/Applications/EMKE Translation.app/Contents/MacOS/EMKEMenuBarApp" \
  "$STAGE/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/Contents/MacOS/EMKEAudioDriver" \
  "$STAGE/Library/Application Support/EMKE Translation/uninstall-emke.sh"
TEMP_BASE="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
test "$TEMP_BASE" != /
SANITIZED=""
SCAN_LIST=""
cleanup_sanitized_root() {
  case "$SANITIZED" in
    "$TEMP_BASE"/emke-pkg-stage.*) /bin/rm -rf -- "$SANITIZED" ;;
    "") ;;
    *) echo "refusing unsafe package-root cleanup: $SANITIZED" >&2 ;;
  esac
  case "$SCAN_LIST" in
    "$TEMP_BASE"/emke-pkg-scan.*) /bin/rm -f -- "$SCAN_LIST" ;;
    "") ;;
    *) echo "refusing unsafe scan-list cleanup: $SCAN_LIST" >&2 ;;
  esac
}
SANITIZED="$(/usr/bin/mktemp -d "$TEMP_BASE/emke-pkg-stage.XXXXXX")"
trap cleanup_sanitized_root EXIT
SANITIZED="$(cd "$SANITIZED" && pwd -P)"
case "$SANITIZED" in
  "$TEMP_BASE"/emke-pkg-stage.*) ;;
  *) echo "unsafe sanitized package root: $SANITIZED" >&2; exit 1 ;;
esac
SCAN_LIST="$(/usr/bin/mktemp "$TEMP_BASE/emke-pkg-scan.XXXXXX")"
COPYFILE_DISABLE=1 /usr/bin/ditto --norsrc --noextattr --noqtn \
  "$STAGE" "$SANITIZED"
/bin/chmod 755 "$SANITIZED"
/usr/bin/xattr -cr "$SANITIZED" 2>/dev/null || true
validate_sanitized_root "$SANITIZED" "$SCAN_LIST"
/usr/bin/codesign --verify --strict \
  "$SANITIZED/Applications/EMKE Translation.app"
/usr/bin/codesign --verify --strict \
  "$SANITIZED/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"

/usr/bin/pkgbuild --root "$SANITIZED" \
  --identifier com.emke.translation.internal \
  --version 0.1.0 \
  --install-location / \
  --ownership recommended \
  --scripts "$ROOT/Packaging/InstallerScripts" \
  "$PKG"
bash "$ROOT/Packaging/verify-internal-pkg.sh" "$PKG"
echo "VERIFIED_DELIVERY_PKG=$PKG"
