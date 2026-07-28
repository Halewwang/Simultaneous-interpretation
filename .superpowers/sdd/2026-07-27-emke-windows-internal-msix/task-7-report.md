# Internal MSIX Task 7 implementation report

## Scope

Implemented only the exact Internal MSIX install/uninstall lifecycle helpers
and their portable contract/behavior tests:

- `Windows/packaging/App/Install-EMKE-Translation-Internal.ps1`
- `Windows/packaging/App/Uninstall-EMKE-Translation-Internal.ps1`
- `Windows/tools/tests/internal-msix-lifecycle.contract.test.mjs`
- `Windows/tools/tests/internal-msix-lifecycle.behavior.test.ps1`

No WPF source, macOS source, package builder, workflow, or driver lifecycle
file was changed.

## TDD evidence

RED was observed before either production script existed:

```text
node --test Windows/tools/tests/internal-msix-lifecycle.contract.test.mjs
tests 6; pass 0; fail 6; exit 1

pwsh -NoLogo -NoProfile -File \
  Windows/tools/tests/internal-msix-lifecycle.behavior.test.ps1
cases 13; pass 0; fail 13; exit 1
```

The expected failure was the absence of both lifecycle scripts. The
PowerShell behavior suite imports only production function ASTs, replaces the
Windows-only external mutation seams, and never performs elevation, AppX
mutation, or certificate-store mutation.

GREEN after the minimal implementation and focused fixes:

```text
node --test Windows/tools/tests/internal-msix-lifecycle.contract.test.mjs
tests 6; pass 6; fail 0; exit 0

pwsh -NoLogo -NoProfile -File \
  Windows/tools/tests/internal-msix-lifecycle.behavior.test.ps1
cases 13; pass 13; fail 0; exit 0
```

## Implemented safety boundary

The installer:

- rejects dot-source invocation;
- requires explicit `-ConfirmTrust`;
- rejects non-absolute, remote/mapped, missing, wrong-extension, or reparse
  inputs and requires all inputs to share one exact bundle directory;
- parses exact leaf entries from `SHA256SUMS.txt`, rejects malformed,
  traversing, or duplicate requested entries, and verifies the MSIX and CER
  before elevation;
- validates the public CER subject `CN=EMKE Internal Test`, fixed SHA-256, and
  fixed thumbprint;
- rejects an already elevated parent;
- uses `Start-Process ... -Verb RunAs -Wait -PassThru` only for the constrained
  certificate-import child;
- re-resolves and re-verifies the CER bytes, subject, and thumbprint in that
  child, importing only into `LocalMachine\TrustedPeople`;
- returns to the original non-elevated process, re-verifies both inputs, then
  calls `Add-AppxPackage` for that user;
- verifies exact Name, Publisher, Version, and Architecture;
- records only the exact certificate subject, thumbprint, and hash under the
  invoking user's Internal installation registry key.

The uninstaller:

- queries and removes only the exact current-user
  `EMKE.Translation.Internal` package full name;
- retains certificate trust by default;
- requires both `-RemoveCertificate` and
  `-ConfirmRemoveCertificate`, the original CER, and `SHA256SUMS.txt`;
- validates the CER against the exact invoking-user install record before
  removing the package;
- elevates only the constrained certificate-removal child;
- re-verifies the CER bytes, subject, thumbprint, and exact
  `LocalMachine\TrustedPeople` certificate before removal;
- never matches or removes a certificate by subject alone.

Both helpers contain no driver install, driver removal, root-store,
execution-policy, or all-users AppX capability.

## Additional checks

```text
PowerShell AST parse: PASS for both helpers and the behavior suite
forbidden capability scan: PASS
git diff --check: PASS
```

Windows UAC, real Local Machine certificate-store mutation, and real AppX
installation remain hosted/physical Windows evidence gates; this portable
suite proves the constrained orchestration and command seams without mutating
the development host.
