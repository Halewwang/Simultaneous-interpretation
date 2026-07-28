# Task 8 Implementation Report

## Scope

Implemented only:

- `.github/workflows/windows-internal-msix.yml`
- `Windows/tools/build-internal-msix-bundle.ps1`
- `Windows/tools/test-hosted-msix-install.ps1`
- `Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs`
- `docs/quality/windows-internal-msix-evidence.md`

No WPF source, Task 6 package/verification script, Task 7 lifecycle helper,
driver script, or macOS file was modified.

## TDD evidence

RED:

```text
node --test Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs
tests 3; pass 0; fail 3
```

All failures were the expected missing-production-file boundary:

- missing `.github/workflows/windows-internal-msix.yml`;
- missing `Windows/tools/test-hosted-msix-install.ps1`;
- missing `Windows/tools/build-internal-msix-bundle.ps1`.

GREEN:

```text
node --test Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs
tests 3; pass 3; fail 0
```

The bundle behavior test uses four test-owned fixture inputs, runs the actual
PowerShell builder, expands the resulting ZIP, proves the exact five-file
inventory, verifies the non-recursive SHA256 list, and checks provenance
fields, file sizes, and hashes.

## Implemented behavior

- The secret-free `build-test` job uses `windows-2025-vs2026`, Node 24, and
  .NET 10 for pull requests, selected pushes, and manual runs.
- It validates shared/portable contracts, performs locked restore, runs native
  Release build plus CTest, and runs managed Release build/tests.
- The `sign-package-bundle` job is limited to manual dispatch or `main`, runs
  only after `build-test`, and is bound to the protected
  `windows-internal-signing` environment.
- The PFX bytes and password are step-scoped encrypted secrets. Reconstruction,
  validation, signing input, byte clearing, and exact PFX cleanup are bounded
  to the signing step.
- The build-26100 hosted job only builds, signs, verifies, bundles, and uploads;
  it never imports a certificate or invokes `Add-AppxPackage`.
- The default-off `install-25h2` job requires
  `[self-hosted, Windows, X64, emke-win11-25h2]`, rejects builds below `26200`,
  downloads the exact signed artifact, and performs install/query/smoke/remove
  separately.
- The smoke requires one JSON record with `status=driverMissing`,
  `translationStartAllowed=false`, `networkOpenCount=0`, and
  `audioStartCount=0`; all four properties and their JSON-derived types are
  checked explicitly.
- Package and certificate cleanup are nested so certificate cleanup still runs
  if package removal fails. The optional install job adds an `always()` cleanup
  guarded by a run-unique exact-thumbprint marker.
- Artifact upload follows every hosted build/sign/verify gate and precedes the
  optional install job, so installation is not silently claimed as hosted
  evidence.
- No driver build/install/uninstall command is present.
- Bundle and hosted-install inputs reject reparse points across the complete
  existing parent chain. Bundle output must be under an explicit allowed root
  and is revalidated after creation.
- The handoff ZIP contains exactly MSIX, CER, install helper, uninstall helper,
  and `SHA256SUMS.txt`; the Actions artifact also contains the ZIP and
  provenance JSON.

## Review fix TDD evidence

Observed RED failures before production changes:

```text
workflow split:
  tests 3; pass 2; fail 1
  missing run_25h2_install_validation and split jobs

strict smoke:
  tests 1; pass 0; fail 1
  missing networkOpenCount was accepted

controlled output root:
  tests 1; pass 0; fail 1
  AllowedOutputRoot parameter unavailable
  then outside-root bundle write was accepted

reparse parent chains:
  tests 1; pass 0; fail 1
  linked input ancestor was accepted
```

Focused GREEN after the fixes:

```text
tests 7; pass 7; fail 0
```

The Task 6 integration review also first observed RED because the signing step
re-read the exported CER and overwrote `certificate_thumbprint`. The workflow
now treats `steps.package.outputs.certificate_thumbprint` as the sole source
for both bundle construction and the hosted-install job. The contract forbids
the removed CER thumbprint recomputation and verifies both downstream
consumers.

## Verification

Passed:

```text
Node Task 8 contract/behavior:
  tests 7; pass 7; fail 0

PowerShell parser:
  build-internal-msix-bundle.ps1 = passed
  test-hosted-msix-install.ps1 = passed

YAML parser:
  .github/workflows/windows-internal-msix.yml = parsed

git diff --check = passed
```

## Integration dependency and proof boundary

The workflow calls the planned Task 6 interface:

```text
package-msix.ps1
  -Configuration Release
  -PfxPath <ephemeral absolute PFX>
  -PasswordEnvironmentVariable WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD
```

Task 6 must retain or reconcile that interface before hosted execution.
Task 7 now provides the two named handoff helpers. The packaged app must provide
the non-interactive `--hosted-driver-missing-smoke` contract; this Task 8 fix
does not claim that production WPF entry point exists or has run.

No Windows MSIX was installed in this macOS implementation session. A protected
hosted run is still required for MakeAppx, SignTool, and downloadable artifact
evidence. A separate disposable Windows 11 25H2 runner run is required for
certificate-store, AppX, packaged-process smoke, and cleanup evidence.
