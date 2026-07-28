# Windows Internal MSIX Evidence

## Target

- Product: EMKE Translation
- Channel: Internal
- Version: `0.1.0`
- Package identity: `EMKE.Translation.Internal`
- Architecture: `x64`
- Minimum supported Windows build: `26200`
- Hosted image: `windows-2025-vs2026`

## Repository gates

The Internal MSIX workflow is required to run, in order:

1. shared schema and golden-vector validation;
2. all portable Windows packaging and driver contracts;
3. locked .NET restore;
4. native Release configure, build, and CTest;
5. managed Release build and tests;
6. bounded reconstruction of the encrypted-secret PFX input;
7. certificate verification, MSIX pack, sign, and signature verification;
8. exact Local Machine Trusted People import on the ephemeral runner;
9. exact package install and identity query;
10. non-interactive `driverMissing` smoke proving zero network opens and zero
    audio starts;
11. exact package removal and exact certificate cleanup;
12. construction and upload of the five-file handoff ZIP plus provenance.

The PFX bytes and password are scoped only to the signing step. The runner
deletes the reconstructed PFX in `finally`; the installation validator removes
only the exact package full name and exact certificate thumbprint in `finally`.
The workflow also runs an `always()` exact-thumbprint cleanup as a fail-safe.

## Artifact contract

The ZIP inventory is exactly:

```text
EMKE-Translation-Windows-0.1.0-internal-x64.msix
EMKE-Translation-Windows-0.1.0-internal-x64.cer
Install-EMKE-Translation-Internal.ps1
Uninstall-EMKE-Translation-Internal.ps1
SHA256SUMS.txt
```

The Actions artifact additionally carries the ZIP and a machine-readable
provenance JSON with source commit, workflow run ID, package identity,
certificate thumbprint, file sizes, and SHA-256 hashes.

## Current proof boundary

Local portable workflow-contract and bundle-construction tests can prove the
repository shape and deterministic inventory without mutating this macOS
machine. They do not prove a Windows installation.

The following remain pending until a successful hosted workflow run is
downloaded and independently checked:

- signed MSIX bytes;
- SignTool verification;
- Local Machine Trusted People import;
- `Add-AppxPackage` and installed identity query;
- packaged `driverMissing` process smoke;
- `Remove-AppxPackage` and certificate cleanup;
- workflow run, artifact ID, final size, hashes, and certificate thumbprint.

Hosted Windows proof still does not establish physical Windows 11 25H2 UI,
elevation UX, signed-driver installation, live endpoint routing, meeting
interoperability, listening quality, or public-release readiness.
