#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-release-test.XXXXXX")"
TEMP="$(cd "$TEMP" && pwd -P)"
trap 'rm -rf "$TEMP"' EXIT
OUTPUT="$TEMP/appcast.xml"
COPY="$TEMP/appcast-copy.xml"
EXPECTED="$TEMP/expected-appcast.xml"
URL="https://example.com/EMKE-Translation-1.2.3.pkg?channel=internal&source=test"
SIGNATURE='base64+signature/with=&<>"'"'"

/bin/cat > "$EXPECTED" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0"
  xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>EMKE Translation Updates</title>
    <link>https://github.com/Halewwang/Simultaneous-interpretation</link>
    <description>Signed EMKE Translation updates</description>
    <language>en</language>
    <item>
      <title>EMKE Translation 1.2.3</title>
      <pubDate>Thu, 01 Jan 1970 00:00:00 +0000</pubDate>
      <enclosure
        url="https://example.com/EMKE-Translation-1.2.3.pkg?channel=internal&amp;source=test"
        sparkle:version="123"
        sparkle:shortVersionString="1.2.3"
        sparkle:edSignature="base64+signature/with=&amp;&lt;&gt;&quot;&apos;"
        length="4567"
        type="application/octet-stream" />
    </item>
  </channel>
</rss>
XML

run_exact_fixture() {
  local bash_path="$1"
  local label="$2"
  local fixture_output="$TEMP/appcast-$label.xml"
  SOURCE_DATE_EPOCH=0 "$bash_path" \
    "$ROOT/Packaging/Scripts/render-appcast.sh" \
    "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "$fixture_output"
  /usr/bin/xmllint --noout "$fixture_output"
  if ! /usr/bin/cmp "$EXPECTED" "$fixture_output"; then
    /usr/bin/diff -u "$EXPECTED" "$fixture_output" >&2 || true
    echo "exact XML escaping failed under $bash_path" >&2
    exit 1
  fi
}

run_exact_fixture /bin/bash system-bash
/usr/bin/ditto "$TEMP/appcast-system-bash.xml" "$OUTPUT"

while IFS= read -r candidate; do
  test -x "$candidate" || continue
  test "$candidate" != /bin/bash || continue
  major="$("$candidate" -c 'printf "%s" "${BASH_VERSINFO[0]}"')"
  if test "$major" -ge 5 2>/dev/null; then
    run_exact_fixture "$candidate" "bash-$major"
    break
  fi
done < <(/usr/bin/which -a bash 2>/dev/null || true)

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
if /usr/bin/grep -Fq '${value//' \
  "$ROOT/Packaging/Scripts/render-appcast.sh"; then
  echo "renderer uses Bash-version-dependent pattern substitution" >&2
  exit 1
fi

SOURCE_DATE_EPOCH=0 /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "$COPY"
/usr/bin/cmp "$OUTPUT" "$COPY"

expect_rejected() {
  if "$@" > "$TEMP/rejected.stdout" 2> "$TEMP/rejected.stderr"; then
    echo "unsafe release metadata input was accepted: $*" >&2
    exit 1
  fi
}

expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1..2" "123" "$URL" "$SIGNATURE" "4567" "$OUTPUT"
expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "12x" "$URL" "$SIGNATURE" "4567" "$OUTPUT"
expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "http://example.com/update.pkg" "$SIGNATURE" "4567" "$OUTPUT"
expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "" "4567" "$OUTPUT"
expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" $'signature\ninjection' "4567" "$OUTPUT"
expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "-1" "$OUTPUT"
expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "relative-appcast.xml"
expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "$TEMP/../escape.xml"

/bin/ln -s "$TEMP" "$TEMP/linked-parent"
expect_rejected /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
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
