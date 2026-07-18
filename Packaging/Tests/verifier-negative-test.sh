#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
PKG="$ROOT/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"
VERIFIER="$ROOT/Packaging/verify-internal-pkg.sh"
SIGNATURE_HELPER="$ROOT/Packaging/Scripts/verify-codesign-metadata.sh"
test -s "$PKG" || bash "$ROOT/Packaging/build-internal-pkg.sh"
TEMP="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/emke-verifier-negative.XXXXXX")"
trap '/usr/bin/find "$TEMP" -depth -delete 2>/dev/null || true' EXIT

expect_rejected() {
  local label="$1"
  local fixture="$2"
  local expected="$3"
  local output="$TEMP/$label.output"
  if bash "$VERIFIER" "$fixture" > "$output" 2>&1; then
    echo "verifier accepted $label fixture" >&2; exit 1
  fi
  if ! /usr/bin/grep -Fq "$expected" "$output"; then
    /usr/bin/sed -n '1,80p' "$output" >&2
    echo "verifier rejected $label for the wrong reason" >&2; exit 1
  fi
}

/usr/bin/printf '%s\n' \
  'Identifier=com.emke.translation.app' \
  'Signature=adhoc' \
  'TeamIdentifier=not set' > "$TEMP/adhoc-metadata"
bash "$SIGNATURE_HELPER" com.emke.translation.app "$TEMP/adhoc-metadata"
/usr/bin/printf '%s\n' \
  'Identifier=com.emke.translation.app' \
  'Signature size=9055' \
  'Authority=Developer ID Application: Example' \
  'TeamIdentifier=EXAMPLE123' > "$TEMP/developer-id-metadata"
if bash "$SIGNATURE_HELPER" com.emke.translation.app \
  "$TEMP/developer-id-metadata" > "$TEMP/signature-output" 2>&1; then
  echo "signature helper accepted Developer ID metadata" >&2; exit 1
fi

CREDENTIAL_EXPANDED="$TEMP/credential-expanded"
/usr/sbin/pkgutil --expand-full "$PKG" "$CREDENTIAL_EXPANDED"
CREDENTIAL_APP="$CREDENTIAL_EXPANDED/Payload/Applications/EMKE Translation.app"
CREDENTIAL_FILE="$CREDENTIAL_APP/Contents/Resources/credential-fixture.dat"
{
  /usr/bin/printf 'sk-'
  i=0; while test "$i" -lt 24; do /usr/bin/printf A; i=$((i + 1)); done
  /usr/bin/printf '\n'
  i=0; while test "$i" -lt 8192; do
    /usr/bin/printf 'safe-filler-line-%08d\n' "$i"; i=$((i + 1))
  done
} > "$CREDENTIAL_FILE"
/bin/chmod 644 "$CREDENTIAL_FILE"
/usr/bin/xattr -cr "$CREDENTIAL_APP" 2>/dev/null || true
/usr/bin/codesign --force --sign - --options runtime --timestamp=none \
  "$CREDENTIAL_APP"
/usr/sbin/pkgutil --flatten "$CREDENTIAL_EXPANDED" "$TEMP/credential.pkg"
expect_rejected early-credential "$TEMP/credential.pkg" \
  'credential-like value found in package content'

DECOY_EXPANDED="$TEMP/decoy-expanded"
/usr/sbin/pkgutil --expand-full "$PKG" "$DECOY_EXPANDED"
/bin/mkdir "$DECOY_EXPANDED/Decoy"
/bin/cp "$DECOY_EXPANDED/PackageInfo" "$DECOY_EXPANDED/Decoy/PackageInfo"
/usr/sbin/pkgutil --flatten "$DECOY_EXPANDED" "$TEMP/decoy.pkg"
expect_rejected decoy-outside-payload "$TEMP/decoy.pkg" \
  'unexpected expanded package entry'

MISSING_EXPANDED="$TEMP/missing-expanded"
/usr/sbin/pkgutil --expand-full "$PKG" "$MISSING_EXPANDED"
/usr/bin/find \
  "$MISSING_EXPANDED/Payload/Library/Application Support/EMKE Translation/uninstall-emke.sh" \
  -delete
/usr/sbin/pkgutil --flatten "$MISSING_EXPANDED" "$TEMP/missing.pkg"
expect_rejected missing-required-entry "$TEMP/missing.pkg" \
  'missing required payload entry'

LOCATION_EXPANDED="$TEMP/location-expanded"
/usr/sbin/pkgutil --expand-full "$PKG" "$LOCATION_EXPANDED"
/usr/bin/sed -i '' 's#install-location="/"#install-location="/tmp"#' \
  "$LOCATION_EXPANDED/PackageInfo"
/usr/sbin/pkgutil --flatten "$LOCATION_EXPANDED" "$TEMP/location.pkg"
expect_rejected wrong-install-location "$TEMP/location.pkg" \
  'unexpected package install-location'
echo "PASS: verifier rejects hardened negative fixtures"
