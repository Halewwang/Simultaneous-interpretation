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

The shared default release inputs are app version `0.2.2` and build number
`2002`. Override them for a release without editing source metadata:

```bash
EMKE_VERSION=0.2.2 EMKE_BUILD_NUMBER=2002 \
  bash Packaging/build-internal-pkg.sh
```

Both inputs accept numeric components only. The app bundle receives both
values, while the component package uses `EMKE_VERSION` as its package
version. The versioned delivery artifact is
`.build/distribution/EMKE-Translation-$EMKE_VERSION-internal.pkg`.

The only delivery artifact is
`.build/distribution/EMKE-Translation-0.2.2-internal.pkg` after the default
command's
verifier passes. `.build/distribution/components` and `staging-root` are local
scratch/intermediate trees, not handoff artifacts; FileProvider may add xattrs
to them after generation. The pipeline still verifies fresh app/driver bundles
before packaging, verifies the sanitized package root immediately before
`pkgbuild`, then strictly verifies the extracted package payload. The verifier
also requires `PackageInfo auth="root"` and numeric BOM ownership `0:0` for
every payload entry.

Before cleaning old scratch, the builder requires a physical canonical Git
root and rejects symlinked `.build`, distribution, or owned cleanup children.
Cleanup is limited to its exact descendants and never follows an external
symlink target.

## Sparkle update metadata

The app embeds the resolved `Sparkle.framework`, enables automatic checks and
downloads, and uses this HTTPS feed:

`https://raw.githubusercontent.com/Halewwang/Simultaneous-interpretation/gh-pages/appcast.xml`

`Packaging/App/Info.plist` contains only the application-specific public EdDSA
key. The matching private key remains in the local login Keychain under account
`com.emke.translation.app`; it must never be exported, logged, committed,
copied into `.build`, or placed in an Appcast/report.

After signing a release artifact with Sparkle's Keychain-backed `sign_update`
tool, render deterministic metadata with:

```bash
SOURCE_DATE_EPOCH=0 bash Packaging/Scripts/render-appcast.sh \
  VERSION BUILD HTTPS_URL EDDSA_SIGNATURE BYTE_LENGTH /physical/output/appcast.xml
```

The renderer XML-escapes inputs and rejects malformed numeric values,
non-HTTPS URLs, empty signatures, control characters, relative/traversing
paths, symlink destinations, and non-canonical output parents.

Exact `vMAJOR.MINOR.PATCH` tag pushes (each component `0...999`, without
leading zeroes) run `.github/workflows/release.yml` on `macos-26`. The workflow
reruns the Swift and packaging suites, passes the resolved version and build
number into a fresh internal-package build, creates the GitHub Release with the
versioned PKG asset, and only then publishes `appcast.xml` to `gh-pages`.
Release publication is immutable and retry-safe: an existing tag Release is
reused only when its one matching PKG asset has the exact local byte length and
GitHub-reported SHA-256 digest. A missing asset is uploaded once without
replacement; a different, duplicate, or unverifiable asset fails closed and is
never deleted or overwritten.

The tagged commit timestamp is passed to the Appcast renderer as
`SOURCE_DATE_EPOCH`, so rerunning the same tag does not change `pubDate`.
Appcast publication is monotonic by numeric build: a lower existing build may
advance, a higher existing build refuses rollback, and an equal build succeeds
only when the complete Appcast bytes are already identical. Equal-build
metadata differences fail closed instead of rewriting history.

The repository Actions secret `SPARKLE_PRIVATE_KEY` is required for this
automation. It is materialized only in a mode-`600` file below
`RUNNER_TEMP`, removed by the signing step's exit trap, and never printed or
stored in the checkout, release notes, Appcast, or Git history. GitHub's
ephemeral workflow token is supplied through step environments and is not
embedded in a remote URL or committed.

The current app and embedded Sparkle code are ad-hoc signed. The
`com.apple.security.cs.disable-library-validation` entitlement is restricted
to this internal build. A public Developer ID/notarized package must remove
that entitlement, sign all nested code with the production identity, notarize,
staple, and pass clean-Mac update acceptance.

The tag workflow does not change that distribution boundary: its GitHub
Release asset remains an unsigned, unnotarized, arm64-only internal test
package, and installation or update still requires the existing macOS
administrator authorization. Publishing the signed Sparkle metadata is not
public-release approval.

## Install

```bash
sudo installer \
  -pkg .build/distribution/EMKE-Translation-0.2.2-internal.pkg \
  -target /
```

Installation still requires administrator authorization and installs both the
app and the virtual audio driver with root-owned package payload entries.

## Uninstall while preserving settings and Keychain

`bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh"`

## Uninstall and explicitly purge settings and Keychain

`bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh" --purge-user-data`

## Reinstall acceptance

Follow `docs/packaging/internal-install-test-2026-07-19.md`. Public release
still requires Developer ID signatures, notarization, stapling, and clean-Mac
acceptance.
