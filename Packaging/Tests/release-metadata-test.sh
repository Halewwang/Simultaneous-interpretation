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

WORKFLOW="$ROOT/.github/workflows/release.yml"
if ! test -s "$WORKFLOW"; then
  echo "release workflow is missing" >&2
  exit 1
fi

require_workflow_text() {
  if ! /usr/bin/grep -Fq -- "$1" "$WORKFLOW"; then
    echo "release workflow is missing required contract: $1" >&2
    exit 1
  fi
}

workflow_line() {
  local match
  match="$(/usr/bin/grep -nF -- "$1" "$WORKFLOW" | /usr/bin/head -n 1)"
  test -n "$match" || {
    echo "release workflow ordering marker is missing: $1" >&2
    exit 1
  }
  /usr/bin/printf '%s\n' "${match%%:*}"
}

assert_workflow_order() {
  local first
  local second
  first="$(workflow_line "$1")"
  second="$(workflow_line "$2")"
  if test "$first" -ge "$second"; then
    echo "release workflow order is unsafe: $1 must precede $2" >&2
    exit 1
  fi
}

require_workflow_text 'tags:'
require_workflow_text '- "v[0-9]*.[0-9]*.[0-9]*"'
require_workflow_text 'contents: write'
require_workflow_text 'runs-on: macos-26'
require_workflow_text 'actions/checkout@v6'
require_workflow_text 'Packaging/Tests/run-all.sh'
require_workflow_text 'Packaging/build-internal-pkg.sh'
require_workflow_text 'EMKE_VERSION="$EMKE_VERSION"'
require_workflow_text 'EMKE_BUILD_NUMBER="$EMKE_BUILD_NUMBER"'
require_workflow_text 'sign_update'
require_workflow_text 'render-appcast.sh'
require_workflow_text 'gh release create'
require_workflow_text 'gh-pages'
require_workflow_text 'secrets.SPARKLE_PRIVATE_KEY'
require_workflow_text 'github.token'
require_workflow_text 'mktemp "$runner_temp/emke-sparkle-key.XXXXXX"'
require_workflow_text 'chmod 600 "$key_file"'
require_workflow_text 'trap cleanup_key EXIT'
require_workflow_text 'sparkle:edSignature="([A-Za-z0-9+/]{86}==)" length="([0-9]+)"'
require_workflow_text 'signature="${BASH_REMATCH[1]}"'
require_workflow_text 'signed_length="${BASH_REMATCH[2]}"'
require_workflow_text 'test -s "$pkg"'
require_workflow_text '[[ "$length" =~ ^[0-9]+$ ]]'
require_workflow_text 'test "$length" -gt 0'
require_workflow_text 'test "$signed_length" = "$length"'
require_workflow_text 'diff --cached --quiet'
require_workflow_text 'GIT_ASKPASS="$askpass"'

test "$(/usr/bin/grep -Fc 'secrets.SPARKLE_PRIVATE_KEY' "$WORKFLOW")" -eq 1
! /usr/bin/grep -Fq 'echo "$SPARKLE_PRIVATE_KEY"' "$WORKFLOW"
! /usr/bin/grep -Fq 'x-access-token:${GITHUB_TOKEN}' "$WORKFLOW"
! /usr/bin/grep -Fq 'secrets.GITHUB_TOKEN' "$WORKFLOW"
! /usr/bin/grep -Fq 'pull_request:' "$WORKFLOW"

assert_workflow_order '- name: Resolve version and build' \
  '- name: Run Swift tests'
assert_workflow_order '- name: Run Swift tests' \
  '- name: Run packaging tests'
assert_workflow_order '- name: Run packaging tests' \
  '- name: Build versioned internal package'
assert_workflow_order '- name: Build versioned internal package' \
  '- name: Sign update and render Appcast'
assert_workflow_order '- name: Sign update and render Appcast' \
  '- name: Create GitHub Release'
assert_workflow_order '- name: Create GitHub Release' \
  '- name: Publish Appcast to gh-pages'

RESOLVE_BLOCK="$TEMP/resolve-version-and-build.sh"
/usr/bin/awk '
  $0 == "      - name: Resolve version and build" {
    found_step = 1
    next
  }
  found_step && $0 == "        run: |" {
    in_block = 1
    next
  }
  in_block && $0 ~ /^      - name:/ {
    exit
  }
  in_block {
    if ($0 == "") {
      print
      next
    }
    if (substr($0, 1, 10) != "          ") {
      exit 65
    }
    print substr($0, 11)
  }
' "$WORKFLOW" > "$RESOLVE_BLOCK"
test -s "$RESOLVE_BLOCK"

assert_tag_resolves() {
  local tag="$1"
  local expected_version="$2"
  local expected_build="$3"
  local github_env="$TEMP/github-env"
  local expected_env="$TEMP/expected-github-env"
  : > "$github_env"
  /usr/bin/printf 'EMKE_VERSION=%s\nEMKE_BUILD_NUMBER=%s\n' \
    "$expected_version" "$expected_build" > "$expected_env"
  GITHUB_REF_NAME="$tag" GITHUB_ENV="$github_env" \
    /bin/bash "$RESOLVE_BLOCK" > "$TEMP/resolve.stdout" \
    2> "$TEMP/resolve.stderr"
  /usr/bin/cmp "$expected_env" "$github_env"
}

assert_tag_rejected() {
  local tag="$1"
  local github_env="$TEMP/github-env"
  : > "$github_env"
  if GITHUB_REF_NAME="$tag" GITHUB_ENV="$github_env" \
    /bin/bash "$RESOLVE_BLOCK" > "$TEMP/resolve.stdout" \
    2> "$TEMP/resolve.stderr"; then
    echo "unsafe release tag was accepted: $tag" >&2
    exit 1
  fi
  test ! -s "$github_env"
}

assert_tag_resolves "v0.0.0" "0.0.0" "0"
assert_tag_resolves "v1.2.3" "1.2.3" "1002003"
assert_tag_resolves "v999.999.999" "999.999.999" "999999999"
assert_tag_rejected "1.2.3"
assert_tag_rejected "v01.2.3"
assert_tag_rejected "v1.02.3"
assert_tag_rejected "v1.2.03"
assert_tag_rejected "v1.2.3-alpha"
assert_tag_rejected "v1.2.3.4"
assert_tag_rejected "v1000.0.0"
assert_tag_rejected "v0.1000.0"
assert_tag_rejected "v0.0.1000"
assert_tag_rejected "v9223372036854775807.0.0"
echo "PASS: release metadata"
