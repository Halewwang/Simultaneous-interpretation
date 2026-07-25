#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
PKG="$ROOT/.build/distribution/EMKE-Translation-0.2.1-internal.pkg"
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

AUTH_EXPANDED="$TEMP/auth-expanded"
/usr/sbin/pkgutil --expand "$PKG" "$AUTH_EXPANDED"
/usr/bin/sed -i '' 's#auth="root"#auth="admin"#' \
  "$AUTH_EXPANDED/PackageInfo"
test "$(/usr/bin/xmllint --xpath 'string(/pkg-info/@auth)' \
  "$AUTH_EXPANDED/PackageInfo")" = admin
/usr/sbin/pkgutil --flatten "$AUTH_EXPANDED" "$TEMP/auth.pkg"
expect_rejected wrong-package-auth "$TEMP/auth.pkg" \
  'unexpected package auth; expected root'

BOM_EXPANDED="$TEMP/bom-expanded"
/usr/sbin/pkgutil --expand "$PKG" "$BOM_EXPANDED"
/usr/bin/printf '.\t40755\t501/20\n' > "$TEMP/non-root-bom-listing"
/usr/bin/mkbom -i "$TEMP/non-root-bom-listing" "$BOM_EXPANDED/Bom"
/usr/bin/lsbom -p ug "$BOM_EXPANDED/Bom" > "$TEMP/bom-ownership"
if ! /usr/bin/grep -Fqx $'501\t20' "$TEMP/bom-ownership"; then
  echo "failed to create non-root BOM fixture" >&2; exit 1
fi
/usr/sbin/pkgutil --flatten "$BOM_EXPANDED" "$TEMP/non-root-bom.pkg"
expect_rejected non-root-bom "$TEMP/non-root-bom.pkg" \
  'non-root package BOM ownership'

CONTROL_EXPANDED="$TEMP/control-expanded"
/usr/sbin/pkgutil --expand-full "$PKG" "$CONTROL_EXPANDED"
CONTROL_APP="$CONTROL_EXPANDED/Payload/Applications/EMKE Translation.app"
CONTROL_FILE="$CONTROL_APP/Contents/Resources/control"$'\n'"name.dat"
/usr/bin/printf '%s\n' 'safe fixture' > "$CONTROL_FILE"
/bin/chmod 0644 "$CONTROL_FILE"
/usr/bin/xattr -cr "$CONTROL_APP" 2>/dev/null || true
/usr/bin/codesign --force --sign - --options runtime --timestamp=none \
  "$CONTROL_APP"
/usr/bin/pkgbuild --root "$CONTROL_EXPANDED/Payload" \
  --scripts "$CONTROL_EXPANDED/Scripts" \
  --identifier com.emke.translation.internal --version 0.2.1 \
  --install-location / "$TEMP/control.pkg"
expect_rejected control-character-path "$TEMP/control.pkg" \
  'control character found in package path'

WORLD_WRITABLE_EXPANDED="$TEMP/world-writable-expanded"
/usr/sbin/pkgutil --expand "$PKG" "$WORLD_WRITABLE_EXPANDED"
/bin/chmod 0777 "$WORLD_WRITABLE_EXPANDED/Scripts/postinstall"
/usr/sbin/pkgutil --flatten "$WORLD_WRITABLE_EXPANDED" \
  "$TEMP/world-writable.pkg"
/usr/sbin/pkgutil --expand-full "$TEMP/world-writable.pkg" \
  "$TEMP/world-writable-audit"
test "$(/usr/bin/stat -f '%Lp' \
  "$TEMP/world-writable-audit/Scripts/postinstall")" = 777
expect_rejected world-writable-postinstall "$TEMP/world-writable.pkg" \
  'world-writable package path found'
echo "PASS: verifier rejects hardened negative fixtures"
