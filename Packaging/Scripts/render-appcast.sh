#!/bin/bash
set -euo pipefail

if test "$#" -ne 6; then
  echo "usage: render-appcast.sh VERSION BUILD URL SIGNATURE LENGTH OUTPUT" >&2
  exit 64
fi

VERSION="$1"
BUILD="$2"
URL="$3"
SIGNATURE="$4"
LENGTH="$5"
OUTPUT="$6"

fail() {
  echo "$1" >&2
  exit 64
}

if [[ ! "$VERSION" =~ ^[0-9]+(\.[0-9]+)*$ ]] || \
  [[ "${#VERSION}" -gt 64 ]]; then
  fail "invalid version"
fi
if [[ ! "$BUILD" =~ ^[0-9]+$ ]] || [[ "${#BUILD}" -gt 20 ]]; then
  fail "invalid build"
fi
if [[ ! "$LENGTH" =~ ^[0-9]+$ ]] || [[ "${#LENGTH}" -gt 20 ]]; then
  fail "invalid length"
fi
if [[ ! "$URL" =~ ^https://[^[:space:]]+$ ]] || \
  [[ "$URL" == *[$'\001'-$'\037'$'\177']* ]]; then
  fail "invalid update URL"
fi
if test -z "$SIGNATURE" || \
  [[ "$SIGNATURE" == *[$'\001'-$'\037'$'\177']* ]]; then
  fail "invalid signature"
fi
if [[ "$OUTPUT" != /* ]] || [[ "$OUTPUT" == *'//'* ]] || \
  [[ "$OUTPUT" == *'/./'* ]] || [[ "$OUTPUT" == *'/../'* ]] || \
  [[ "$OUTPUT" == */. ]] || [[ "$OUTPUT" == */.. ]] || \
  [[ "$OUTPUT" == */ ]] || \
  [[ "$OUTPUT" == *[$'\001'-$'\037'$'\177']* ]]; then
  fail "unsafe output path"
fi

PARENT="${OUTPUT%/*}"
BASENAME="${OUTPUT##*/}"
test -n "$PARENT" && test -n "$BASENAME" || fail "unsafe output path"
test "$PARENT" != / || fail "unsafe output parent"
test -d "$PARENT" || fail "output parent must exist"
test ! -L "$PARENT" || fail "output parent must not be a symlink"
PHYSICAL_PARENT="$(cd "$PARENT" 2>/dev/null && pwd -P)" || \
  fail "cannot validate output parent"
test "$PARENT" = "$PHYSICAL_PARENT" || fail "output parent must be canonical"
if test -e "$OUTPUT" && ! test -f "$OUTPUT"; then
  fail "output must be a regular file or absent"
fi
test ! -L "$OUTPUT" || fail "output must not be a symlink"

if test -n "${SOURCE_DATE_EPOCH:-}"; then
  [[ "$SOURCE_DATE_EPOCH" =~ ^[0-9]+$ ]] || fail "invalid SOURCE_DATE_EPOCH"
  [[ "${#SOURCE_DATE_EPOCH}" -le 12 ]] || fail "invalid SOURCE_DATE_EPOCH"
  PUB_DATE="$(LC_ALL=C /bin/date -u -r "$SOURCE_DATE_EPOCH" \
    '+%a, %d %b %Y %H:%M:%S +0000')" || fail "invalid SOURCE_DATE_EPOCH"
else
  PUB_DATE="$(LC_ALL=C /bin/date -u '+%a, %d %b %Y %H:%M:%S +0000')"
fi

xml_escape() {
  /usr/bin/printf '%s\n' "$1" | LC_ALL=C /usr/bin/sed \
    -e 's/&/\&amp;/g' \
    -e 's/</\&lt;/g' \
    -e 's/>/\&gt;/g' \
    -e 's/"/\&quot;/g' \
    -e "s/'/\\&apos;/g"
}

VERSION_XML="$(xml_escape "$VERSION")"
BUILD_XML="$(xml_escape "$BUILD")"
URL_XML="$(xml_escape "$URL")"
SIGNATURE_XML="$(xml_escape "$SIGNATURE")"
LENGTH_XML="$(xml_escape "$LENGTH")"
PUB_DATE_XML="$(xml_escape "$PUB_DATE")"

TEMP_OUTPUT="$(/usr/bin/mktemp "$PARENT/.appcast.XXXXXX")"
cleanup() {
  if test -n "${TEMP_OUTPUT:-}"; then
    /bin/rm -f -- "$TEMP_OUTPUT"
  fi
}
trap cleanup EXIT

/usr/bin/printf '%s\n' \
  '<?xml version="1.0" encoding="utf-8"?>' \
  '<rss version="2.0"' \
  '  xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">' \
  '  <channel>' \
  '    <title>EMKE Translation Updates</title>' \
  '    <link>https://github.com/Halewwang/Simultaneous-interpretation</link>' \
  '    <description>Signed EMKE Translation updates</description>' \
  '    <language>en</language>' \
  '    <item>' \
  "      <title>EMKE Translation $VERSION_XML</title>" \
  "      <pubDate>$PUB_DATE_XML</pubDate>" \
  '      <enclosure' \
  "        url=\"$URL_XML\"" \
  "        sparkle:version=\"$BUILD_XML\"" \
  "        sparkle:shortVersionString=\"$VERSION_XML\"" \
  "        sparkle:edSignature=\"$SIGNATURE_XML\"" \
  "        length=\"$LENGTH_XML\"" \
  '        type="application/octet-stream" />' \
  '    </item>' \
  '  </channel>' \
  '</rss>' > "$TEMP_OUTPUT"

/bin/chmod 644 "$TEMP_OUTPUT"
/bin/mv -f -- "$TEMP_OUTPUT" "$OUTPUT"
TEMP_OUTPUT=""
