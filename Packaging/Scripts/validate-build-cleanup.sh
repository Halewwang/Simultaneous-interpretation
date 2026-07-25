#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 4 ]]; then
  echo "usage: validate-build-cleanup.sh ROOT STAGING_ROOT COMPONENTS PACKAGE" >&2
  exit 64
fi

REQUESTED_ROOT="$1"
STAGE="$2"
COMPONENTS="$3"
PKG="$4"

fail() {
  echo "$1" >&2
  exit 65
}

[[ "$REQUESTED_ROOT" = /* ]] || fail "repository root must be absolute"
[[ -d "$REQUESTED_ROOT" ]] || fail "repository root must be an existing directory"
[[ ! -L "$REQUESTED_ROOT" ]] || fail "repository root must not be a symlink"
ROOT="$(cd "$REQUESTED_ROOT" 2>/dev/null && pwd -P)" || \
  fail "cannot canonicalize repository root"
[[ "$ROOT" != "/" ]] || fail "repository root cannot be /"
[[ "$REQUESTED_ROOT" = "$ROOT" ]] || \
  fail "repository root must be a physical canonical path"

GIT_ROOT="$(/usr/bin/git -C "$ROOT" rev-parse --show-toplevel 2>/dev/null)" || \
  fail "repository root is not a Git worktree"
GIT_ROOT="$(cd "$GIT_ROOT" 2>/dev/null && pwd -P)" || \
  fail "cannot canonicalize Git worktree root"
[[ "$GIT_ROOT" = "$ROOT" ]] || fail "repository root does not match Git worktree root"

DIST="$ROOT/.build/distribution"
EXPECTED_STAGE="$DIST/staging-root"
EXPECTED_COMPONENTS="$DIST/components"
EMKE_VERSION="${EMKE_VERSION:-0.2.1}"
[[ "$EMKE_VERSION" =~ ^[0-9]+(\.[0-9]+)*$ ]] || \
  fail "invalid EMKE_VERSION"
[[ "${#EMKE_VERSION}" -le 64 ]] || fail "invalid EMKE_VERSION"
EXPECTED_PKG="$DIST/EMKE-Translation-$EMKE_VERSION-internal.pkg"
[[ "$STAGE" = "$EXPECTED_STAGE" ]] || fail "unexpected build cleanup target: $STAGE"
[[ "$COMPONENTS" = "$EXPECTED_COMPONENTS" ]] || \
  fail "unexpected build cleanup target: $COMPONENTS"
[[ "$PKG" = "$EXPECTED_PKG" ]] || fail "unexpected build cleanup target: $PKG"

require_physical_directory_or_absent() {
  local path="$1"
  local canonical
  if [[ -L "$path" ]]; then
    fail "symlink in build cleanup path: $path"
  fi
  if [[ -e "$path" ]]; then
    [[ -d "$path" ]] || fail "non-directory build cleanup ancestor: $path"
    canonical="$(cd "$path" 2>/dev/null && pwd -P)" || \
      fail "cannot canonicalize build cleanup path: $path"
    [[ "$canonical" = "$path" ]] || fail "build cleanup path escapes repository: $path"
    case "$canonical" in
      "$ROOT"/*) ;;
      *) fail "build cleanup path escapes repository: $path" ;;
    esac
  fi
}

require_owned_child_or_absent() {
  local path="$1"
  local expected_type="$2"
  if [[ -L "$path" ]]; then
    fail "symlink in build cleanup path: $path"
  fi
  if [[ -e "$path" ]]; then
    case "$expected_type" in
      directory) [[ -d "$path" ]] || fail "unexpected owned cleanup object: $path" ;;
      file) [[ -f "$path" ]] || fail "unexpected owned cleanup object: $path" ;;
    esac
  fi
}

require_physical_directory_or_absent "$ROOT/.build"
require_physical_directory_or_absent "$DIST"
require_owned_child_or_absent "$STAGE" directory
require_owned_child_or_absent "$COMPONENTS" directory
require_owned_child_or_absent "$PKG" file
