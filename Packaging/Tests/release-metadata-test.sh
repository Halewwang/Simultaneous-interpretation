#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-release-test.XXXXXX")"
TEMP="$(cd "$TEMP" && pwd -P)"
trap 'rm -rf "$TEMP"' EXIT
OUTPUT="$TEMP/appcast.xml"
COPY="$TEMP/appcast-copy.xml"
URL="https://example.com/EMKE-Translation-1.2.3.pkg?channel=internal&source=test"
SIGNATURE='base64+signature/with=&<>"'"'"

SOURCE_DATE_EPOCH=0 bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "$OUTPUT"

/usr/bin/xmllint --noout "$OUTPUT"
/usr/bin/grep -Fq 'sparkle:shortVersionString="1.2.3"' "$OUTPUT"
/usr/bin/grep -Fq 'sparkle:version="123"' "$OUTPUT"
/usr/bin/grep -Fq \
  'url="https://example.com/EMKE-Translation-1.2.3.pkg?channel=internal&amp;source=test"' \
  "$OUTPUT"
/usr/bin/grep -Fq \
  'sparkle:edSignature="base64+signature/with=&amp;&lt;&gt;&quot;&apos;"' \
  "$OUTPUT"
/usr/bin/grep -Fq 'length="4567"' "$OUTPUT"
/usr/bin/grep -Fq '<pubDate>Thu, 01 Jan 1970 00:00:00 +0000</pubDate>' \
  "$OUTPUT"

SOURCE_DATE_EPOCH=0 bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "$COPY"
/usr/bin/cmp "$OUTPUT" "$COPY"

expect_rejected() {
  if "$@" > "$TEMP/rejected.stdout" 2> "$TEMP/rejected.stderr"; then
    echo "unsafe release metadata input was accepted: $*" >&2
    exit 1
  fi
}

expect_rejected bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1..2" "123" "$URL" "$SIGNATURE" "4567" "$OUTPUT"
expect_rejected bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "12x" "$URL" "$SIGNATURE" "4567" "$OUTPUT"
expect_rejected bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "http://example.com/update.pkg" "$SIGNATURE" "4567" "$OUTPUT"
expect_rejected bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "" "4567" "$OUTPUT"
expect_rejected bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "-1" "$OUTPUT"
expect_rejected bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "relative-appcast.xml"
expect_rejected bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "$TEMP/../escape.xml"

/bin/ln -s "$TEMP" "$TEMP/linked-parent"
expect_rejected bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "4567" \
  "$TEMP/linked-parent/escape.xml"

PRIVATE_MARKER="SPARKLE_"'PRIVATE_KEY'
PEM_MARKER="BEGIN "'PRIVATE KEY'
! /usr/bin/grep -RIlE "$PRIVATE_MARKER|$PEM_MARKER" \
  "$ROOT/Packaging" > "$TEMP/private-content-files"
test -z "$(/usr/bin/find "$ROOT/Packaging" -type f \
  \( -iname '*private*key*' -o -iname '*sparkle*secret*' \) -print)"
! /usr/bin/grep -Fq "$PRIVATE_MARKER" "$OUTPUT"
echo "PASS: release metadata"
