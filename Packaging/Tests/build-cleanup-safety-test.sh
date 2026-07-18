#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
VALIDATOR="$ROOT/Packaging/Scripts/validate-build-cleanup.sh"
TEMP="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/emke-cleanup-test.XXXXXX")"
TEMP="$(cd "$TEMP" && pwd -P)"
trap '/usr/bin/find "$TEMP" -depth -delete 2>/dev/null || true' EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

make_repo() {
  local repo="$1"
  /bin/mkdir -p "$repo"
  /usr/bin/git -C "$repo" init -q
}

expect_rejected_without_mutation() {
  local label="$1"
  local repo="$2"
  local expected="$3"
  local output="$TEMP/$label.output"
  shift 3
  if bash "$VALIDATOR" "$repo" "$@" > "$output" 2>&1; then
    fail "cleanup validator accepted $label"
  fi
  if ! /usr/bin/grep -Fq "$expected" "$output"; then
    /usr/bin/sed -n '1,80p' "$output" >&2
    fail "cleanup validator rejected $label for the wrong reason"
  fi
  test -f "$TEMP/outside/$label.sentinel" || \
    fail "cleanup validator mutated the external $label sentinel"
}

BUILD_LINK_REPO="$TEMP/build-link-repo"
make_repo "$BUILD_LINK_REPO"
/bin/mkdir -p "$TEMP/outside/build-link"
: > "$TEMP/outside/build-root-symlink.sentinel"
/bin/ln -s "$TEMP/outside/build-link" "$BUILD_LINK_REPO/.build"
expect_rejected_without_mutation build-root-symlink "$BUILD_LINK_REPO" \
  'symlink in build cleanup path' \
  "$BUILD_LINK_REPO/.build/distribution/staging-root" \
  "$BUILD_LINK_REPO/.build/distribution/components" \
  "$BUILD_LINK_REPO/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"

# Exercise the real builder entry point from a safe temporary Git repository.
# Its symlink guard must fire before tool discovery or any cleanup command.
BUILDER_REPO="$TEMP/builder-integration-repo"
make_repo "$BUILDER_REPO"
/bin/mkdir -p "$BUILDER_REPO/Packaging/Scripts" \
  "$TEMP/outside/builder-integration/distribution/staging-root"
/bin/cp "$ROOT/Packaging/build-internal-pkg.sh" \
  "$BUILDER_REPO/Packaging/build-internal-pkg.sh"
/bin/cp "$VALIDATOR" \
  "$BUILDER_REPO/Packaging/Scripts/validate-build-cleanup.sh"
: > "$TEMP/outside/builder-integration.sentinel"
: > "$TEMP/outside/builder-integration/distribution/staging-root/sentinel"
/bin/ln -s "$TEMP/outside/builder-integration" "$BUILDER_REPO/.build"
if bash "$BUILDER_REPO/Packaging/build-internal-pkg.sh" \
  > "$TEMP/builder-integration.output" 2>&1; then
  fail "builder accepted a symlinked .build root"
fi
if ! /usr/bin/grep -Fq 'symlink in build cleanup path' \
  "$TEMP/builder-integration.output"; then
  /usr/bin/sed -n '1,80p' "$TEMP/builder-integration.output" >&2
  fail "builder rejected a symlinked .build root for the wrong reason"
fi
test -f "$TEMP/outside/builder-integration/distribution/staging-root/sentinel" || \
  fail "builder deleted the external staging sentinel"

DIST_LINK_REPO="$TEMP/distribution-link-repo"
make_repo "$DIST_LINK_REPO"
/bin/mkdir -p "$DIST_LINK_REPO/.build" "$TEMP/outside/distribution-link"
: > "$TEMP/outside/distribution-root-symlink.sentinel"
/bin/ln -s "$TEMP/outside/distribution-link" \
  "$DIST_LINK_REPO/.build/distribution"
expect_rejected_without_mutation distribution-root-symlink "$DIST_LINK_REPO" \
  'symlink in build cleanup path' \
  "$DIST_LINK_REPO/.build/distribution/staging-root" \
  "$DIST_LINK_REPO/.build/distribution/components" \
  "$DIST_LINK_REPO/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"

CHILD_LINK_REPO="$TEMP/child-link-repo"
make_repo "$CHILD_LINK_REPO"
/bin/mkdir -p "$CHILD_LINK_REPO/.build/distribution" \
  "$TEMP/outside/owned-child-link"
: > "$TEMP/outside/owned-child-symlink.sentinel"
/bin/ln -s "$TEMP/outside/owned-child-link" \
  "$CHILD_LINK_REPO/.build/distribution/components"
expect_rejected_without_mutation owned-child-symlink "$CHILD_LINK_REPO" \
  'symlink in build cleanup path' \
  "$CHILD_LINK_REPO/.build/distribution/staging-root" \
  "$CHILD_LINK_REPO/.build/distribution/components" \
  "$CHILD_LINK_REPO/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"

EXACT_REPO="$TEMP/exact-repo"
make_repo "$EXACT_REPO"
: > "$TEMP/outside/non-exact-target.sentinel"
expect_rejected_without_mutation non-exact-target "$EXACT_REPO" \
  'unexpected build cleanup target' \
  "$EXACT_REPO/.build/distribution" \
  "$EXACT_REPO/.build/distribution/components" \
  "$EXACT_REPO/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"

SAFE_REPO="$TEMP/safe-repo"
make_repo "$SAFE_REPO"
bash "$VALIDATOR" "$SAFE_REPO" \
  "$SAFE_REPO/.build/distribution/staging-root" \
  "$SAFE_REPO/.build/distribution/components" \
  "$SAFE_REPO/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"
test ! -e "$SAFE_REPO/.build"

echo "PASS: build cleanup validation is canonical and non-mutating"
