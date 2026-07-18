#!/bin/bash
set -euo pipefail
EXPECTED_IDENTIFIER="${1:?missing expected identifier}"
METADATA="${2:?missing codesign metadata path}"
test -f "$METADATA"

test "$(/usr/bin/grep -Fxc "Identifier=$EXPECTED_IDENTIFIER" "$METADATA")" = 1 || {
  echo "unexpected codesign identifier" >&2; exit 1; }
test "$(/usr/bin/grep -Fxc 'Signature=adhoc' "$METADATA")" = 1 || {
  echo "expected ad-hoc code signature" >&2; exit 1; }
test "$(/usr/bin/grep -Fxc 'TeamIdentifier=not set' "$METADATA")" = 1 || {
  echo "unexpected codesign team identity" >&2; exit 1; }
if /usr/bin/grep -q '^Authority=' "$METADATA"; then
  echo "unexpected certificate authority" >&2; exit 1
fi
