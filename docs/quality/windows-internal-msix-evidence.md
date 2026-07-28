# Windows Internal MSIX Evidence

## Target

- Product: EMKE Translation
- Channel: Internal
- Version: `0.1.0`
- Package identity: `EMKE.Translation.Internal`
- Architecture: `x64`
- Minimum supported Windows build: `26200`
- Hosted build/sign image: `windows-2025-vs2026` (Windows build `26100`)
- Optional install runner:
  `[self-hosted, Windows, X64, emke-win11-25h2]`, build `26200+`

## Repository gates

The Internal MSIX workflow separates three evidence levels.

The `build-test` job runs for pull requests, selected pushes, and manual runs:

1. shared schema and golden-vector validation;
2. all portable Windows packaging and driver contracts;
3. locked .NET restore;
4. native Release configure, build, and CTest;
5. managed Release build and tests.

It has no signing secret, package-signing, certificate-store, or AppX access.

The `sign-package-bundle` job runs only for a manual dispatch or a push to
`main`, after `build-test`. It is bound to the
`windows-internal-signing` GitHub environment and:

1. performs a fresh locked restore and native Release package-input build;
2. reconstructs the PFX only inside the bounded signing step;
3. verifies the certificate, packs/signs the MSIX, and verifies the package;
4. deletes the reconstructed PFX in `finally`;
5. constructs and uploads the five-file handoff ZIP plus provenance.

Before secrets are provisioned, repository administrators must configure
`windows-internal-signing` with required reviewers, prevent self-review, and
store both signing values as environment secrets rather than repository
secrets. `main` must remain protected. Pull-request jobs never reference the
signing environment or secrets.

The `install-25h2` job is default-off and is available only through the manual
`run_25h2_install_validation` input. It requires a clean, disposable 25H2
runner with the exact labels above. It:

1. rejects OS builds below `26200`;
2. downloads the already uploaded exact signed artifact;
3. imports only its exact public certificate into Local Machine Trusted
   People;
4. installs and queries only `EMKE.Translation.Internal`;
5. runs the one-record `driverMissing` smoke with explicitly typed zero
   network/audio counters;
6. removes the exact package and certificate in `finally`, with an
   `always()` exact-thumbprint cleanup marker as a fail-safe.

The hosted artifact is intentionally available without running the optional
25H2 job. A successful hosted build/sign job is not installation acceptance.

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

The following remain pending until a successful protected hosted signing run
is downloaded and independently checked:

- signed MSIX bytes;
- SignTool verification;
- workflow run, artifact ID, final size, hashes, and certificate thumbprint.

The following are a separate pending gate until the optional disposable
Windows 11 25H2 runner job succeeds:

- Local Machine Trusted People import;
- `Add-AppxPackage` and installed identity query;
- packaged `driverMissing` process smoke;
- `Remove-AppxPackage` and certificate cleanup.

The packaged WPF application does not yet implement the production
`--hosted-driver-missing-smoke` entry point. The workflow and validator reserve
that contract, but no install-smoke result may be claimed until the WPF task
implements it and the 25H2 runner executes it.

Neither hosted package proof nor the optional install gate establishes
physical interactive Windows 11 25H2 UI, end-user elevation UX, signed-driver
installation, live endpoint routing, meeting interoperability, listening
quality, or public-release readiness.
