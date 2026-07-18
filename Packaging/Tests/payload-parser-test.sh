#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
PARSER="$ROOT/Packaging/Scripts/verify-payload-list.sh"
TEMP="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/emke-payload-parser.XXXXXX")"
trap '/usr/bin/find "$TEMP" -depth -delete 2>/dev/null || true' EXIT

write_list() { /usr/bin/printf '%s\n' "$@" > "$TEMP/input"; }
expect_rejected() {
  local label="$1"
  if bash "$PARSER" "$TEMP/input" > "$TEMP/output" 2> "$TEMP/error"; then
    echo "parser accepted $label" >&2; exit 1
  fi
}

write_list "." \
  "./Applications" \
  "./Applications/EMKE Translation.app" \
  "./Applications/EMKE Translation.app/Contents/Info.plist" \
  "./Library" \
  "./Library/Audio" \
  "./Library/Audio/Plug-Ins" \
  "./Library/Audio/Plug-Ins/HAL" \
  "./Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver" \
  "./Library/Application Support" \
  "./Library/Application Support/EMKE Translation" \
  "./Library/Application Support/EMKE Translation/uninstall-emke.sh" \
  "./._Applications" \
  "./Library/._Audio"
bash "$PARSER" "$TEMP/input" > "$TEMP/output"
/usr/bin/grep -Fqx 'Applications/EMKE Translation.app/Contents/Info.plist' \
  "$TEMP/output"
if /usr/bin/grep -q '\._' "$TEMP/output"; then
  echo "normalized output retained AppleDouble syntax" >&2; exit 1
fi

write_list "." "./Library" "./Library/../etc"
expect_rejected traversal
write_list "." "/Applications/EMKE Translation.app"
expect_rejected leading-slash
write_list "." "./Library//Audio"
expect_rejected empty-component
write_list "." "./Library" "./Library/./Audio"
expect_rejected dot-component
write_list "." "./ApplicationsEvil"
expect_rejected prefix-ambiguity
write_list "." "./Library" "./Library"
expect_rejected duplicate-business-path
write_list "." "./Library" "./Library/._Audio"
expect_rejected orphan-appledouble
write_list "." "./Library" "./Library/Audio" \
  "./Library/Audio/Plug-Ins" "./Library/._Audio/._Plug-Ins"
expect_rejected ambiguous-appledouble
write_list "." "./Library" "./Library/Audio" \
  "./Library/._Audio" "./._Library/Audio"
expect_rejected duplicate-metadata-target
/usr/bin/printf '.\n./Library\n./Library/\tAudio\n' > "$TEMP/input"
expect_rejected control-character
echo "PASS: payload parser rejects unsafe paths"
