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

assert_absent() {
  local forbidden="$1"
  local file="$2"
  local label="${3:-forbidden text}"
  if /usr/bin/grep -Fq -- "$forbidden" "$file"; then
    echo "$label was present: $forbidden ($file)" >&2
    return 1
  else
    local status="$?"
    if test "$status" -ne 1; then
      echo "$label scan failed: $file" >&2
      return 1
    fi
  fi
}

assert_absent_regex() {
  local forbidden="$1"
  local label="$2"
  shift 2
  if /usr/bin/grep -REq -- "$forbidden" "$@"; then
    echo "$label was present" >&2
    return 1
  else
    local status="$?"
    if test "$status" -ne 1; then
      echo "$label scan failed" >&2
      return 1
    fi
  fi
}

assert_not_called() {
  local call="$1"
  local log="$2"
  assert_absent "$call" "$log" "unexpected command"
}

NEGATIVE_ASSERTION_FIXTURE="$TEMP/negative-assertion-fixture"
/usr/bin/printf '%s\n' 'forbidden-call' > "$NEGATIVE_ASSERTION_FIXTURE"
if (assert_absent 'forbidden-call' "$NEGATIVE_ASSERTION_FIXTURE") \
  > "$TEMP/assert-absent.stdout" 2> "$TEMP/assert-absent.stderr"; then
  echo "assert_absent accepted injected forbidden text" >&2
  exit 1
fi
if (assert_not_called 'forbidden-call' "$NEGATIVE_ASSERTION_FIXTURE") \
  > "$TEMP/assert-not-called.stdout" 2> "$TEMP/assert-not-called.stderr"; then
  echo "assert_not_called accepted an injected forbidden call" >&2
  exit 1
fi

if /usr/bin/grep -nE \
  '^[[:space:]]*![[:space:]]+(/usr/bin/)?grep([[:space:]]|$)' \
  "$0" > "$TEMP/naked-negative-grep"; then
  /bin/cat "$TEMP/naked-negative-grep" >&2
  echo "bare ! grep negative assertions are forbidden" >&2
  exit 1
fi

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
  local status
  if "$@" > "$TEMP/rejected.stdout" 2> "$TEMP/rejected.stderr"; then
    status=0
  else
    status="$?"
  fi
  if test "$status" -eq 0; then
    echo "unsafe release metadata input was accepted: $*" >&2
    exit 1
  fi
  if test "$status" -ne 64; then
    echo "unsafe release metadata input returned $status instead of 64: $*" >&2
    exit 1
  fi
}

expect_rejected_preserving_output() {
  local protected_output="$1"
  shift
  local snapshot="$TEMP/rejected-output.snapshot"
  local output_existed=0
  if test -e "$protected_output"; then
    /bin/cp "$protected_output" "$snapshot"
    output_existed=1
  fi
  expect_rejected "$@"
  if test "$output_existed" -eq 1; then
    if ! /usr/bin/cmp "$snapshot" "$protected_output"; then
      echo "rejected release metadata overwrote output: $protected_output" >&2
      exit 1
    fi
  elif test -e "$protected_output"; then
    echo "rejected release metadata created output: $protected_output" >&2
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

assert_control_byte_rejected() {
  local control_code="$1"
  local control_octal
  local control_byte
  local url_absent_output
  local url_existing_output
  local signature_absent_output
  local signature_existing_output
  local controlled_output
  printf -v control_octal '%03o' "$control_code"
  printf -v control_byte "\\$control_octal"

  url_absent_output="$TEMP/appcast-url-absent-$control_code.xml"
  test ! -e "$url_absent_output"
  expect_rejected_preserving_output "$url_absent_output" \
    /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
    "1.2.3" "123" "${URL}${control_byte}" "$SIGNATURE" "4567" \
    "$url_absent_output"
  url_existing_output="$TEMP/appcast-url-existing-$control_code.xml"
  test ! -e "$url_existing_output"
  /usr/bin/printf 'url sentinel %s' "$control_code" > "$url_existing_output"
  test -f "$url_existing_output" && test ! -L "$url_existing_output"
  expect_rejected_preserving_output "$url_existing_output" \
    /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
    "1.2.3" "123" "${URL}${control_byte}" "$SIGNATURE" "4567" \
    "$url_existing_output"

  signature_absent_output="$TEMP/appcast-signature-absent-$control_code.xml"
  test ! -e "$signature_absent_output"
  expect_rejected_preserving_output "$signature_absent_output" \
    /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
    "1.2.3" "123" "$URL" "signature${control_byte}" "4567" \
    "$signature_absent_output"
  signature_existing_output="$TEMP/appcast-signature-existing-$control_code.xml"
  test ! -e "$signature_existing_output"
  /usr/bin/printf 'signature sentinel %s' "$control_code" \
    > "$signature_existing_output"
  test -f "$signature_existing_output" && test ! -L "$signature_existing_output"
  expect_rejected_preserving_output "$signature_existing_output" \
    /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
    "1.2.3" "123" "$URL" "signature${control_byte}" "4567" \
    "$signature_existing_output"

  controlled_output="$TEMP/appcast-control-$control_code.xml${control_byte}"
  test ! -e "$controlled_output"
  expect_rejected_preserving_output "$controlled_output" \
    /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
    "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "$controlled_output"
  /usr/bin/printf 'output sentinel %s' "$control_code" > "$controlled_output"
  test -f "$controlled_output" && test ! -L "$controlled_output"
  expect_rejected_preserving_output "$controlled_output" \
    /bin/bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
    "1.2.3" "123" "$URL" "$SIGNATURE" "4567" "$controlled_output"
}

for ((control_code = 1; control_code <= 31; control_code += 1)); do
  assert_control_byte_rejected "$control_code"
done
assert_control_byte_rejected 127

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

PRIVATE_MATERIAL_REGEX="-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|SPARKLE_PRIVATE_KEY[[:space:]]*=[[:space:]]*['\"]?[A-Za-z0-9+/]{32,}={0,2}"

REGEX_PEM_FIXTURE="$TEMP/private-material-pem"
/usr/bin/printf '%s\n' '-----BEGIN ''PRIVATE KEY-----' > "$REGEX_PEM_FIXTURE"
if (assert_absent_regex "$PRIVATE_MATERIAL_REGEX" "injected PEM material" \
  "$REGEX_PEM_FIXTURE") > "$TEMP/assert-regex-pem.stdout" \
  2> "$TEMP/assert-regex-pem.stderr"; then
  echo "assert_absent_regex accepted an injected PEM header" >&2
  exit 1
fi
/usr/bin/grep -Fq 'injected PEM material was present' \
  "$TEMP/assert-regex-pem.stderr"

REGEX_ASSIGNMENT_FIXTURE="$TEMP/private-material-assignment"
/usr/bin/printf '%s\n' \
  'SPARKLE_PRIVATE_''KEY=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef' \
  > "$REGEX_ASSIGNMENT_FIXTURE"
if (assert_absent_regex "$PRIVATE_MATERIAL_REGEX" \
  "injected long secret assignment" "$REGEX_ASSIGNMENT_FIXTURE") \
  > "$TEMP/assert-regex-assignment.stdout" \
  2> "$TEMP/assert-regex-assignment.stderr"; then
  echo "assert_absent_regex accepted an injected long secret assignment" >&2
  exit 1
fi
/usr/bin/grep -Fq 'injected long secret assignment was present' \
  "$TEMP/assert-regex-assignment.stderr"

REGEX_LEGAL_MENTION_FIXTURE="$TEMP/private-material-legal-mention"
/usr/bin/printf '%s\n' \
  'Read SPARKLE_PRIVATE_''KEY from repository secrets.' \
  > "$REGEX_LEGAL_MENTION_FIXTURE"
assert_absent_regex "$PRIVATE_MATERIAL_REGEX" \
  "legal private-key variable mention" "$REGEX_LEGAL_MENTION_FIXTURE"

assert_absent_regex "$PRIVATE_MATERIAL_REGEX" "private key material" \
  "$ROOT/.github" \
  "$ROOT/Packaging"
test -z "$(/usr/bin/find "$ROOT/Packaging" -type f \
  \( -iname '*private*key*' -o -iname '*sparkle*secret*' \) -print)"
assert_absent_regex \
  '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----' \
  "private key material in Appcast" "$OUTPUT"

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
require_workflow_text 'gh release view'
require_workflow_text 'gh release upload "$GITHUB_REF_NAME" "$pkg"'
require_workflow_text 'gh api "repos/${GITHUB_REPOSITORY}/releases/tags/${GITHUB_REF_NAME}"'
require_workflow_text '--verify-tag'
require_workflow_text 'asset_name="$(basename "$pkg")"'
require_workflow_text '/usr/bin/shasum -a 256 "$pkg"'
require_workflow_text 'local_digest="sha256:${local_sha}"'
require_workflow_text 'test "$remote_digest" = "$local_digest"'
require_workflow_text 'SOURCE_DATE_EPOCH="$SOURCE_DATE_EPOCH"'
require_workflow_text 'git show -s --format=%ct "${GITHUB_SHA}^{commit}"'
require_workflow_text 'existing_build='
require_workflow_text 'existing_version='
require_workflow_text 'existing_url='
require_workflow_text 'existing_signature='
require_workflow_text 'existing_length='
require_workflow_text 'refusing Appcast rollback'
require_workflow_text 'Appcast metadata mismatch for existing build'
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
require_workflow_text '/usr/bin/cmp "$runner_temp/appcast.xml" "$existing_appcast"'
require_workflow_text 'GIT_ASKPASS="$askpass"'

test "$(/usr/bin/grep -Fc 'secrets.SPARKLE_PRIVATE_KEY' "$WORKFLOW")" -eq 1
assert_absent 'echo "$SPARKLE_PRIVATE_KEY"' "$WORKFLOW" \
  "private key echo"
assert_absent '--ed-key-file -' "$WORKFLOW" \
  "private key stdin materialization"
assert_absent '-s "$SPARKLE_PRIVATE_KEY"' "$WORKFLOW" \
  "private key command-line argument"
assert_absent 'x-access-token:${GITHUB_TOKEN}' "$WORKFLOW" \
  "token-bearing remote URL"
assert_absent 'secrets.GITHUB_TOKEN' "$WORKFLOW" \
  "committed GitHub token secret reference"
assert_absent 'pull_request:' "$WORKFLOW" \
  "unexpected pull-request trigger"
assert_absent '--clobber' "$WORKFLOW" \
  "mutable Release upload"

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
  local expected_epoch="$4"
  local github_env="$TEMP/github-env"
  local expected_env="$TEMP/expected-github-env"
  : > "$github_env"
  /usr/bin/printf \
    'EMKE_VERSION=%s\nEMKE_BUILD_NUMBER=%s\nSOURCE_DATE_EPOCH=%s\n' \
    "$expected_version" "$expected_build" "$expected_epoch" > "$expected_env"
  GITHUB_REF_NAME="$tag" GITHUB_SHA="$RESOLVE_SHA" \
    GITHUB_ENV="$github_env" \
    /bin/bash "$RESOLVE_BLOCK" > "$TEMP/resolve.stdout" \
    2> "$TEMP/resolve.stderr"
  /usr/bin/cmp "$expected_env" "$github_env"
}

assert_tag_rejected() {
  local tag="$1"
  local github_env="$TEMP/github-env"
  : > "$github_env"
  if GITHUB_REF_NAME="$tag" GITHUB_SHA="$RESOLVE_SHA" \
    GITHUB_ENV="$github_env" \
    /bin/bash "$RESOLVE_BLOCK" > "$TEMP/resolve.stdout" \
    2> "$TEMP/resolve.stderr"; then
    echo "unsafe release tag was accepted: $tag" >&2
    exit 1
  fi
  test ! -s "$github_env"
}

RESOLVE_SHA="$(git -C "$ROOT" rev-parse 'HEAD^{commit}')"
RESOLVE_EPOCH="$(git -C "$ROOT" show -s --format=%ct "${RESOLVE_SHA}^{commit}")"
assert_tag_resolves "v0.0.0" "0.0.0" "0" "$RESOLVE_EPOCH"
assert_tag_resolves "v1.2.3" "1.2.3" "1002003" "$RESOLVE_EPOCH"
assert_tag_resolves "v999.999.999" "999.999.999" "999999999" \
  "$RESOLVE_EPOCH"
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
: > "$TEMP/github-env"
if GITHUB_REF_NAME="v1.2.3" GITHUB_SHA="not-a-commit" \
  GITHUB_ENV="$TEMP/github-env" /bin/bash "$RESOLVE_BLOCK" \
  > "$TEMP/resolve.stdout" 2> "$TEMP/resolve.stderr"; then
  echo "invalid tagged commit was accepted" >&2
  exit 1
fi
test ! -s "$TEMP/github-env"

RELEASE_BLOCK="$TEMP/release-publication.sh"
/usr/bin/awk '
  $0 == "      - name: Create GitHub Release" {
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
' "$WORKFLOW" > "$RELEASE_BLOCK"
test -s "$RELEASE_BLOCK"
/bin/bash -n "$RELEASE_BLOCK"

MOCK_BIN="$TEMP/mock-bin"
MOCK_GH_LOG="$TEMP/mock-gh.log"
MOCK_GH_VIEW_STATE="$TEMP/mock-gh-view.state"
MOCK_GH_API_STATE="$TEMP/mock-gh-api.state"
RELEASE_WORKSPACE="$TEMP/release-workspace"
/bin/mkdir -p "$MOCK_BIN" "$RELEASE_WORKSPACE/.build/distribution"
RELEASE_PKG="$RELEASE_WORKSPACE/.build/distribution/EMKE-Translation-1.2.3-internal.pkg"
/usr/bin/printf '%s' 'release-fixture-pkg' > "$RELEASE_PKG"
EXPECTED_ASSET_NAME="${RELEASE_PKG##*/}"
EXPECTED_ASSET_SIZE="$(/usr/bin/stat -f '%z' "$RELEASE_PKG")"
EXPECTED_ASSET_SHA="$(
  /usr/bin/shasum -a 256 "$RELEASE_PKG" | /usr/bin/awk '{print $1}'
)"
EXPECTED_ASSET_DIGEST="sha256:$EXPECTED_ASSET_SHA"

/bin/cat > "$MOCK_BIN/gh" <<'MOCK_GH'
#!/bin/bash
set -euo pipefail
/usr/bin/printf '%s\n' "$*" >> "$MOCK_GH_LOG"
has_arg() {
  local expected="$1"
  shift
  local argument
  for argument in "$@"; do
    test "$argument" = "$expected" && return 0
  done
  return 1
}
if test "${1:-}" = release && test "${2:-}" = view; then
  view_count=0
  test ! -s "$MOCK_GH_VIEW_STATE" || \
    read -r view_count < "$MOCK_GH_VIEW_STATE"
  view_count="$((view_count + 1))"
  /usr/bin/printf '%s\n' "$view_count" > "$MOCK_GH_VIEW_STATE"
  case "$MOCK_RELEASE_MODE" in
    absent-release)
      exit 1
      ;;
    create-race)
      test "$view_count" -gt 1
      /usr/bin/printf '%s\n' 'release-id'
      exit 0
      ;;
    create-failure)
      exit 1
      ;;
    *)
      /usr/bin/printf '%s\n' 'release-id'
      exit 0
      ;;
  esac
fi
if test "${1:-}" = release && test "${2:-}" = create; then
  has_arg --verify-tag "$@" || exit 96
  case "$MOCK_RELEASE_MODE" in
    absent-release) exit 0 ;;
    create-race|create-failure) exit 1 ;;
    *) exit 99 ;;
  esac
fi
if test "${1:-}" = release && test "${2:-}" = upload; then
  has_arg --clobber "$@" && exit 98
  test "$MOCK_RELEASE_MODE" != upload-failure
  exit 0
fi
if test "${1:-}" = api; then
  api_count=0
  test ! -s "$MOCK_GH_API_STATE" || \
    read -r api_count < "$MOCK_GH_API_STATE"
  api_count="$((api_count + 1))"
  /usr/bin/printf '%s\n' "$api_count" > "$MOCK_GH_API_STATE"
  case "$MOCK_RELEASE_MODE" in
    asset-identical)
      /usr/bin/printf '%s\t%s\t%s\n' \
        "$EXPECTED_ASSET_NAME" "$EXPECTED_ASSET_SIZE" "$EXPECTED_ASSET_DIGEST"
      ;;
    asset-missing|absent-release|create-race)
      if test "$api_count" -eq 1; then
        /usr/bin/printf '%s\t%s\t%s\n' \
          "other.pkg" "$EXPECTED_ASSET_SIZE" "$EXPECTED_ASSET_DIGEST"
      else
        /usr/bin/printf '%s\t%s\t%s\n' \
          "$EXPECTED_ASSET_NAME" "$EXPECTED_ASSET_SIZE" "$EXPECTED_ASSET_DIGEST"
      fi
      ;;
    asset-different)
      /usr/bin/printf '%s\t%s\t%s\n' \
        "$EXPECTED_ASSET_NAME" "$EXPECTED_ASSET_SIZE" \
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
      ;;
    asset-size-mismatch)
      /usr/bin/printf '%s\t%s\t%s\n' \
        "$EXPECTED_ASSET_NAME" "$((EXPECTED_ASSET_SIZE + 1))" \
        "$EXPECTED_ASSET_DIGEST"
      ;;
    digest-missing)
      /usr/bin/printf '%s\t%s\t\n' \
        "$EXPECTED_ASSET_NAME" "$EXPECTED_ASSET_SIZE"
      ;;
    digest-malformed)
      /usr/bin/printf '%s\t%s\t%s\n' \
        "$EXPECTED_ASSET_NAME" "$EXPECTED_ASSET_SIZE" "sha256:not-a-digest"
      ;;
    duplicate)
      /usr/bin/printf '%s\t%s\t%s\n%s\t%s\t%s\n' \
        "$EXPECTED_ASSET_NAME" "$EXPECTED_ASSET_SIZE" "$EXPECTED_ASSET_DIGEST" \
        "$EXPECTED_ASSET_NAME" "$EXPECTED_ASSET_SIZE" "$EXPECTED_ASSET_DIGEST"
      ;;
    upload-failure)
      /usr/bin/printf '%s\t%s\t%s\n' \
        "other.pkg" "$EXPECTED_ASSET_SIZE" "$EXPECTED_ASSET_DIGEST"
      ;;
    create-failure)
      exit 97
      ;;
  esac
  exit 0
fi
exit 97
MOCK_GH
/bin/chmod 755 "$MOCK_BIN/gh"

run_release_fixture() {
  local mode="$1"
  local expected_result="$2"
  : > "$MOCK_GH_LOG"
  : > "$MOCK_GH_VIEW_STATE"
  : > "$MOCK_GH_API_STATE"
  if (
    cd "$RELEASE_WORKSPACE"
    PATH="$MOCK_BIN:/usr/bin:/bin" \
      MOCK_RELEASE_MODE="$mode" \
      MOCK_GH_LOG="$MOCK_GH_LOG" \
      MOCK_GH_VIEW_STATE="$MOCK_GH_VIEW_STATE" \
      MOCK_GH_API_STATE="$MOCK_GH_API_STATE" \
      EXPECTED_ASSET_NAME="$EXPECTED_ASSET_NAME" \
      EXPECTED_ASSET_SIZE="$EXPECTED_ASSET_SIZE" \
      EXPECTED_ASSET_DIGEST="$EXPECTED_ASSET_DIGEST" \
      GITHUB_REF_NAME="v1.2.3" \
      GITHUB_REPOSITORY="Halewwang/Simultaneous-interpretation" \
      EMKE_VERSION="1.2.3" \
      /bin/bash "$RELEASE_BLOCK"
  ) > "$TEMP/release-$mode.stdout" 2> "$TEMP/release-$mode.stderr"; then
    if test "$expected_result" != pass; then
      echo "unsafe release publication fixture passed: $mode" >&2
      exit 1
    fi
  elif test "$expected_result" = pass; then
    echo "release publication fixture failed: $mode" >&2
    /bin/cat "$TEMP/release-$mode.stderr" >&2
    exit 1
  fi
}

assert_log_order() {
  local first
  local second
  first="$(/usr/bin/grep -nF -- "$1" "$MOCK_GH_LOG" | /usr/bin/head -n 1)"
  second="$(/usr/bin/grep -nF -- "$2" "$MOCK_GH_LOG" | /usr/bin/head -n 1)"
  test -n "$first" && test -n "$second"
  test "${first%%:*}" -lt "${second%%:*}"
}

run_release_fixture asset-identical pass
assert_not_called 'release create v1.2.3' "$MOCK_GH_LOG"
assert_not_called 'release upload v1.2.3' "$MOCK_GH_LOG"
test "$(/usr/bin/grep -Fc 'api repos/' "$MOCK_GH_LOG")" -eq 2

run_release_fixture asset-missing pass
/usr/bin/grep -Fq 'release upload v1.2.3' "$MOCK_GH_LOG"
assert_not_called '--clobber' "$MOCK_GH_LOG"
test "$(/usr/bin/grep -Fc 'api repos/' "$MOCK_GH_LOG")" -eq 2
assert_log_order 'api repos/' 'release upload v1.2.3'

run_release_fixture create-race pass
test "$(/usr/bin/grep -Fc -- '--json id' "$MOCK_GH_LOG")" -eq 2
assert_log_order 'release create v1.2.3' 'release upload v1.2.3'
assert_not_called '--clobber' "$MOCK_GH_LOG"

run_release_fixture create-failure fail
test "$(/usr/bin/grep -Fc -- '--json id' "$MOCK_GH_LOG")" -eq 2
assert_not_called 'release upload v1.2.3' "$MOCK_GH_LOG"

run_release_fixture absent-release pass
/usr/bin/grep -Fq 'release create v1.2.3' "$MOCK_GH_LOG"
/usr/bin/grep -Fq 'release upload v1.2.3' "$MOCK_GH_LOG"

for unsafe_mode in asset-different asset-size-mismatch \
  digest-missing digest-malformed duplicate; do
  run_release_fixture "$unsafe_mode" fail
  assert_not_called 'release upload v1.2.3' "$MOCK_GH_LOG"
done

run_release_fixture upload-failure fail
/usr/bin/grep -Fq 'release upload v1.2.3' "$MOCK_GH_LOG"
assert_not_called '--clobber' "$MOCK_GH_LOG"

APPCAST_BLOCK="$TEMP/appcast-publication.sh"
/usr/bin/awk '
  $0 == "      - name: Publish Appcast to gh-pages" {
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
' "$WORKFLOW" > "$APPCAST_BLOCK"
test -s "$APPCAST_BLOCK"
/bin/bash -n "$APPCAST_BLOCK"

APPCAST_FIXTURES="$TEMP/appcast-fixtures"
/bin/mkdir -p "$APPCAST_FIXTURES"
FIXTURE_SIGNATURE_A='WVyVJpOx+a5+vNWJVY79TRjFKveNk+VhGJf2iti4CZtJsJewIUGvh/1AKKEAFbH1qUwx+vro1ECuzOsMmumoBA=='
FIXTURE_SIGNATURE_B='pNFd7KbcQSu+Mq7UYrbQXTPq82luht2ACXm/r2utp1u/Uv/5hWqctdT2jwQgMejW7DRoeV/hVr6J4VdZYdwWDw=='
CANDIDATE_APPCAST="$APPCAST_FIXTURES/candidate.xml"
CANDIDATE_COPY="$APPCAST_FIXTURES/candidate-copy.xml"
LOWER_APPCAST="$APPCAST_FIXTURES/lower.xml"
EQUAL_DIFFERENT_APPCAST="$APPCAST_FIXTURES/equal-different.xml"
HIGHER_APPCAST="$APPCAST_FIXTURES/higher.xml"
SOURCE_DATE_EPOCH=1700000000 /bin/bash \
  "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "1002003" \
  "https://github.com/Halewwang/Simultaneous-interpretation/releases/download/v1.2.3/$EXPECTED_ASSET_NAME" \
  "$FIXTURE_SIGNATURE_A" "$EXPECTED_ASSET_SIZE" "$CANDIDATE_APPCAST"
SOURCE_DATE_EPOCH=1700000000 /bin/bash \
  "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "1002003" \
  "https://github.com/Halewwang/Simultaneous-interpretation/releases/download/v1.2.3/$EXPECTED_ASSET_NAME" \
  "$FIXTURE_SIGNATURE_A" "$EXPECTED_ASSET_SIZE" "$CANDIDATE_COPY"
/usr/bin/cmp "$CANDIDATE_APPCAST" "$CANDIDATE_COPY"
SOURCE_DATE_EPOCH=1699999999 /bin/bash \
  "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.2" "1002002" \
  "https://github.com/Halewwang/Simultaneous-interpretation/releases/download/v1.2.2/EMKE-Translation-1.2.2-internal.pkg" \
  "$FIXTURE_SIGNATURE_A" "$EXPECTED_ASSET_SIZE" "$LOWER_APPCAST"
SOURCE_DATE_EPOCH=1700000000 /bin/bash \
  "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "1002003" \
  "https://github.com/Halewwang/Simultaneous-interpretation/releases/download/v1.2.3/$EXPECTED_ASSET_NAME?mismatch=1" \
  "$FIXTURE_SIGNATURE_B" "$EXPECTED_ASSET_SIZE" "$EQUAL_DIFFERENT_APPCAST"
SOURCE_DATE_EPOCH=1700000001 /bin/bash \
  "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.4" "1002004" \
  "https://github.com/Halewwang/Simultaneous-interpretation/releases/download/v1.2.4/EMKE-Translation-1.2.4-internal.pkg" \
  "$FIXTURE_SIGNATURE_A" "$EXPECTED_ASSET_SIZE" "$HIGHER_APPCAST"

MOCK_GIT_LOG="$TEMP/mock-git.log"
/bin/cat > "$MOCK_BIN/git" <<'MOCK_GIT'
#!/bin/bash
set -euo pipefail
if test "${1:-}" = clone; then
  target=""
  for argument in "$@"; do
    target="$argument"
  done
  /usr/bin/printf 'clone %s\n' "$target" >> "$MOCK_GIT_LOG"
  exec /usr/bin/git clone --no-checkout "$MOCK_GIT_ORIGIN" "$target"
fi
for argument in "$@"; do
  if test "$argument" = push; then
    /usr/bin/printf '%s\n' "$*" >> "$MOCK_GIT_LOG"
    break
  fi
done
exec /usr/bin/git "$@"
MOCK_GIT
/bin/chmod 755 "$MOCK_BIN/git"

prepare_appcast_origin() {
  local mode="$1"
  local existing_appcast="$2"
  local scenario="$TEMP/appcast-$mode"
  local seed="$scenario/seed"
  MOCK_GIT_ORIGIN="$scenario/origin.git"
  /bin/mkdir -p "$seed"
  /usr/bin/git -C "$seed" init -q -b main
  /usr/bin/git -C "$seed" config user.name fixture
  /usr/bin/git -C "$seed" config user.email fixture@example.com
  /usr/bin/printf '%s\n' fixture > "$seed/README.md"
  /usr/bin/git -C "$seed" add README.md
  /usr/bin/git -C "$seed" commit -q -m main
  /usr/bin/git init -q --bare "$MOCK_GIT_ORIGIN"
  /usr/bin/git -C "$seed" remote add origin "$MOCK_GIT_ORIGIN"
  /usr/bin/git -C "$seed" push -q origin main
  if test -n "$existing_appcast"; then
    /usr/bin/git -C "$seed" checkout -q --orphan gh-pages
    /usr/bin/git -C "$seed" rm -q -rf .
    /bin/cp "$existing_appcast" "$seed/appcast.xml"
    /usr/bin/git -C "$seed" add appcast.xml
    /usr/bin/git -C "$seed" commit -q -m appcast
    /usr/bin/git -C "$seed" push -q origin gh-pages
  fi
}

run_appcast_fixture() {
  local mode="$1"
  local existing_appcast="$2"
  local expected_result="$3"
  local runner_temp="$TEMP/appcast-runner-$mode"
  prepare_appcast_origin "$mode" "$existing_appcast"
  /bin/mkdir -p "$runner_temp"
  /bin/cp "$CANDIDATE_APPCAST" "$runner_temp/appcast.xml"
  : > "$MOCK_GIT_LOG"
  if PATH="$MOCK_BIN:/usr/bin:/bin" \
    MOCK_GIT_LOG="$MOCK_GIT_LOG" \
    MOCK_GIT_ORIGIN="$MOCK_GIT_ORIGIN" \
    RUNNER_TEMP="$runner_temp" \
    GITHUB_TOKEN="offline-token" \
    GITHUB_REPOSITORY="Halewwang/Simultaneous-interpretation" \
    EMKE_VERSION="1.2.3" \
    EMKE_BUILD_NUMBER="1002003" \
    /bin/bash "$APPCAST_BLOCK" \
    > "$TEMP/appcast-$mode.stdout" 2> "$TEMP/appcast-$mode.stderr"; then
    if test "$expected_result" != pass; then
      echo "unsafe Appcast publication fixture passed: $mode" >&2
      exit 1
    fi
  elif test "$expected_result" = pass; then
    echo "Appcast publication fixture failed: $mode" >&2
    /bin/cat "$TEMP/appcast-$mode.stderr" >&2
    exit 1
  fi
}

run_appcast_fixture absent "" pass
/usr/bin/git --git-dir="$MOCK_GIT_ORIGIN" show gh-pages:appcast.xml \
  > "$TEMP/published-appcast.xml"
/usr/bin/cmp "$CANDIDATE_APPCAST" "$TEMP/published-appcast.xml"
/usr/bin/grep -Fq 'push origin gh-pages' "$MOCK_GIT_LOG"

run_appcast_fixture lower "$LOWER_APPCAST" pass
/usr/bin/git --git-dir="$MOCK_GIT_ORIGIN" show gh-pages:appcast.xml \
  > "$TEMP/published-appcast.xml"
/usr/bin/cmp "$CANDIDATE_APPCAST" "$TEMP/published-appcast.xml"
/usr/bin/grep -Fq 'push origin gh-pages' "$MOCK_GIT_LOG"

run_appcast_fixture equal-identical "$CANDIDATE_APPCAST" pass
assert_not_called 'push origin gh-pages' "$MOCK_GIT_LOG"
/usr/bin/git --git-dir="$MOCK_GIT_ORIGIN" show gh-pages:appcast.xml \
  > "$TEMP/published-appcast.xml"
/usr/bin/cmp "$CANDIDATE_APPCAST" "$TEMP/published-appcast.xml"

run_appcast_fixture equal-different "$EQUAL_DIFFERENT_APPCAST" fail
assert_not_called 'push origin gh-pages' "$MOCK_GIT_LOG"
/usr/bin/git --git-dir="$MOCK_GIT_ORIGIN" show gh-pages:appcast.xml \
  > "$TEMP/published-appcast.xml"
/usr/bin/cmp "$EQUAL_DIFFERENT_APPCAST" "$TEMP/published-appcast.xml"

run_appcast_fixture higher "$HIGHER_APPCAST" fail
assert_not_called 'push origin gh-pages' "$MOCK_GIT_LOG"
/usr/bin/git --git-dir="$MOCK_GIT_ORIGIN" show gh-pages:appcast.xml \
  > "$TEMP/published-appcast.xml"
/usr/bin/cmp "$HIGHER_APPCAST" "$TEMP/published-appcast.xml"
echo "PASS: release metadata"
