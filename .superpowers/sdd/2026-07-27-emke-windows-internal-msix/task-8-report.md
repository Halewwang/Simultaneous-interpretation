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

- Hosted workflow uses `windows-2025-vs2026`, Node 24, and .NET 10.
- It validates shared/portable contracts, performs locked restore, runs native
  Release build plus CTest, and runs managed Release build/tests.
- The PFX bytes and password are step-scoped encrypted secrets. Reconstruction,
  validation, signing input, byte clearing, and exact PFX cleanup are bounded
  to the signing step.
- The package is verified before installation.
- Hosted validation imports only the exact public certificate into Local
  Machine Trusted People, installs and queries only
  `EMKE.Translation.Internal`, validates version/publisher/architecture, and
  runs `--hosted-driver-missing-smoke`.
- The smoke requires one JSON record with `status=driverMissing`,
  `translationStartAllowed=false`, `networkOpenCount=0`, and
  `audioStartCount=0`.
- Package and certificate cleanup are nested so certificate cleanup still runs
  if package removal fails. The workflow adds an `always()` exact-thumbprint
  cleanup.
- Artifact upload is after every normal success gate and is not marked
  `always()`.
- No driver build/install/uninstall command is present.
- The handoff ZIP contains exactly MSIX, CER, install helper, uninstall helper,
  and `SHA256SUMS.txt`; the Actions artifact also contains the ZIP and
  provenance JSON.

## Verification

Passed:

```text
PowerShell parser:
  build-internal-msix-bundle.ps1 = passed
  test-hosted-msix-install.ps1 = passed

YAML parser:
  .github/workflows/windows-internal-msix.yml = parsed

git diff --check = passed
```

The broader portable Node invocation currently reaches this Task 8 contract
successfully, but the combined command is not green yet because the concurrently
created Task 7 lifecycle contract tests have six expected failures while their
install/uninstall helpers are not present. Those files are outside this task
and were not changed.

## Integration dependency and proof boundary

The workflow calls the planned Task 6 interface:

```text
package-msix.ps1
  -Configuration Release
  -PfxPath <ephemeral absolute PFX>
  -PasswordEnvironmentVariable WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD
```

Task 6 must retain or reconcile that interface before hosted execution.
Task 7 must provide the two named handoff helpers. The packaged app must provide
the non-interactive `--hosted-driver-missing-smoke` contract.

No Windows MSIX was installed in this macOS implementation session. A hosted
run is still required for MakeAppx, SignTool, certificate-store, AppX,
packaged-process smoke, cleanup, and downloadable artifact evidence.
