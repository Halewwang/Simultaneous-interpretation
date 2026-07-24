#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
PKG="$ROOT/.build/distribution/EMKE-Translation-0.2.0-internal.pkg"
test -s "$PKG" || bash "$ROOT/Packaging/build-internal-pkg.sh"
TEMP="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/emke-driver-read-only.XXXXXX")"
trap '/usr/bin/find "$TEMP" -depth -delete 2>/dev/null || true' EXIT
/usr/sbin/pkgutil --expand-full "$PKG" "$TEMP/expanded"
DRIVER="$TEMP/expanded/Payload/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
if /usr/bin/grep -Eq \
  '\|[[:space:]]*(/usr/bin/)?grep[[:space:]]+-[^[:space:]]*q' \
  "$ROOT/Driver/verify-bundle.sh"; then
  echo "Driver verifier contains a producer-to-grep -q pipeline" >&2
  exit 1
fi
snapshot_bundle() {
  local output="$1"
  local listing="$2"
  local item
  local relative
  local attrs
  local attr
  : > "$output"
  if ! /usr/bin/find "$DRIVER" -print0 > "$listing"; then
    echo "driver snapshot path discovery failed" >&2; exit 1
  fi
  while IFS= read -r -d '' item; do
    relative="${item#"$DRIVER"}"
    printf 'path=%q stat=' "$relative" >> "$output"
    /usr/bin/stat -f '%HT,%Lp,%z' "$item" >> "$output"
    if test -L "$item"; then
      printf 'link=%q\n' "$(/usr/bin/readlink "$item")" >> "$output"
    elif test -f "$item"; then
      /usr/bin/shasum -a 256 "$item" >> "$output"
    fi
    if ! attrs="$(/usr/bin/xattr "$item")"; then
      echo "driver snapshot xattr discovery failed" >&2; exit 1
    fi
    while IFS= read -r attr; do
      test -z "$attr" && continue
      printf 'xattr=%q:%q\n' "$relative" "$attr" >> "$output"
      /usr/bin/xattr -px "$attr" "$item" >> "$output"
    done <<< "$attrs"
  done < "$listing"
  if ! /usr/bin/codesign -d --verbose=4 "$DRIVER" >> "$output" 2>&1; then
    echo "driver snapshot codesign metadata failed" >&2; exit 1
  fi
}
snapshot_bundle "$TEMP/before" "$TEMP/before-list"
bash "$ROOT/Driver/verify-bundle.sh" --read-only "$DRIVER"
snapshot_bundle "$TEMP/after" "$TEMP/after-list"
if ! /usr/bin/cmp -s "$TEMP/before" "$TEMP/after"; then
  echo "read-only driver verification mutated bundle state" >&2
  exit 1
fi
echo "PASS: packaged driver verification is read-only"
