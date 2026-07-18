#!/bin/bash
set -euo pipefail
INPUT="${1:?missing payload listing path}"
test -f "$INPUT"
TEMP="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/emke-payload-list.XXXXXX")"
trap '/usr/bin/find "$TEMP" -depth -delete 2>/dev/null || true' EXIT
BUSINESS="$TEMP/business-paths"
METADATA="$TEMP/metadata-targets"
: > "$BUSINESS"
: > "$METADATA"

fail_path() { echo "$1" >&2; exit 1; }
allow_path() {
  case "$1" in
    Applications|Applications/EMKE\ Translation.app|\
    Applications/EMKE\ Translation.app/*|\
    Library|Library/Audio|Library/Audio/Plug-Ins|\
    Library/Audio/Plug-Ins/HAL|\
    Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver|\
    Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/*|\
    Library/Application\ Support|Library/Application\ Support/EMKE\ Translation|\
    Library/Application\ Support/EMKE\ Translation/uninstall-emke.sh) return 0 ;;
    *) return 1 ;;
  esac
}

if LC_ALL=C /usr/bin/grep -q '[[:cntrl:]]' "$INPUT"; then
  fail_path "control character in payload listing"
fi

root_seen=0
while IFS= read -r raw || test -n "$raw"; do
  test -n "$raw" || fail_path "empty payload path"
  if test "$raw" = .; then
    test "$root_seen" -eq 0 || fail_path "duplicate payload root"
    root_seen=1
    continue
  fi
  case "$raw" in
    /*) fail_path "absolute payload path: $raw" ;;
    */) fail_path "empty payload component: $raw" ;;
    *'//'*) fail_path "empty payload component: $raw" ;;
  esac
  path="${raw#./}"
  test -n "$path" || fail_path "empty payload path"
  IFS='/' read -r -a components <<< "$path"
  decoded=""
  metadata_count=0
  for component in "${components[@]}"; do
    test -n "$component" || fail_path "empty payload component: $raw"
    case "$component" in
      .|..) fail_path "relative payload component: $raw" ;;
    esac
    if [[ "$component" == ._* ]]; then
      component="${component#._}"
      test -n "$component" || fail_path "empty AppleDouble target: $raw"
      case "$component" in
        .|..) fail_path "relative AppleDouble target: $raw" ;;
      esac
      metadata_count=$((metadata_count + 1))
    elif [[ "$component" == .* ]]; then
      fail_path "ambiguous dot-prefixed payload component: $raw"
    fi
    decoded="${decoded:+$decoded/}$component"
  done
  test "$metadata_count" -le 1 || fail_path "ambiguous AppleDouble path: $raw"
  allow_path "$decoded" || fail_path "unexpected payload path: $raw"
  if test "$metadata_count" -eq 1; then
    if /usr/bin/grep -Fqx "$decoded" "$METADATA"; then
      fail_path "multiple AppleDouble records for target: $decoded"
    fi
    /usr/bin/printf '%s\n' "$decoded" >> "$METADATA"
  else
    if /usr/bin/grep -Fqx "$decoded" "$BUSINESS"; then
      fail_path "duplicate business payload path: $decoded"
    fi
    /usr/bin/printf '%s\n' "$decoded" >> "$BUSINESS"
  fi
done < "$INPUT"
test "$root_seen" -eq 1 || fail_path "missing payload root"

while IFS= read -r target; do
  test -n "$target" || continue
  /usr/bin/grep -Fqx "$target" "$BUSINESS" || \
    fail_path "orphan AppleDouble target: $target"
done < "$METADATA"

while IFS= read -r path; do
  test -n "$path" && /usr/bin/printf '%s\n' "$path"
done < "$BUSINESS"
