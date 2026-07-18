#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
DIST="$ROOT/.build/distribution"
STAGE="$DIST/staging-root"
COMPONENTS="$DIST/components"
APP="$COMPONENTS/EMKE Translation.app"
PKG="$DIST/EMKE-Translation-0.1.0-internal.pkg"

require_tool() { command -v "$1" >/dev/null 2>&1 || {
  echo "missing required tool: $1" >&2; exit 69; }; }
validate_sanitized_root() {
  local item
  local attrs
  local attr
  if /usr/bin/find "$1" -name '._*' -print -quit | /usr/bin/grep -q .; then
    echo "physical AppleDouble file found in sanitized package root" >&2
    exit 1
  fi
  while IFS= read -r -d '' item; do
    attrs="$(/usr/bin/xattr "$item")"
    while IFS= read -r attr; do
      test -z "$attr" && continue
      if test "$attr" != com.apple.provenance; then
        echo "disallowed package-root xattr: $attr ($item)" >&2
        exit 1
      fi
    done <<< "$attrs"
  done < <(/usr/bin/find "$1" -print0)
}
test "$(uname -s)" = Darwin
test "$(uname -m)" = arm64
for tool in swift make iconutil sips codesign pkgbuild pkgutil; do
  require_tool "$tool"
done

rm -rf "$STAGE" "$COMPONENTS" "$PKG"
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
SANITIZED_RAW="$(/usr/bin/mktemp -d "$TEMP_BASE/emke-pkg-stage.XXXXXX")"
SANITIZED="$(cd "$SANITIZED_RAW" && pwd -P)"
case "$SANITIZED" in
  "$TEMP_BASE"/emke-pkg-stage.*) ;;
  *) echo "unsafe sanitized package root: $SANITIZED" >&2; exit 1 ;;
esac
cleanup_sanitized_root() {
  case "$SANITIZED" in
    "$TEMP_BASE"/emke-pkg-stage.*) /bin/rm -rf -- "$SANITIZED" ;;
    *) echo "refusing unsafe package-root cleanup: $SANITIZED" >&2 ;;
  esac
}
trap cleanup_sanitized_root EXIT
COPYFILE_DISABLE=1 /usr/bin/ditto --norsrc --noextattr --noqtn \
  "$STAGE" "$SANITIZED"
/bin/chmod 755 "$SANITIZED"
/usr/bin/xattr -cr "$SANITIZED" 2>/dev/null || true
validate_sanitized_root "$SANITIZED"
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
echo "$PKG"
