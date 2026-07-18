# EMKE Internal macOS Package

This package is for the current development Mac only. Its app and driver
payloads are ad-hoc signed; the package is unsigned at the package level, not
notarized, arm64-only, and not suitable for public distribution. The
reference-derived icon also requires a separate brand-rights and originality
review before any public release.

Before install or uninstall, quit EMKE and close Feishu, DingTalk, Teams,
recorders, and other active audio apps because Core Audio will restart.

## Build and verify without installation

`bash Packaging/build-internal-pkg.sh`

## Install

`sudo installer -pkg .build/distribution/EMKE-Translation-0.1.0-internal.pkg -target /`

## Uninstall while preserving settings and Keychain

`bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh"`

## Uninstall and explicitly purge settings and Keychain

`bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh" --purge-user-data`

## Reinstall acceptance

Follow `docs/packaging/internal-install-test-2026-07-19.md`. Public release
still requires Developer ID signatures, notarization, stapling, and clean-Mac
acceptance.
