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
cases 23; pass 23; fail 0; exit 0
```

Round-one security review added a second RED/GREEN cycle. RED proved that the
original helper trusted a thumbprint derived from the same replaceable CER and
that UAC cancellation did not yet enter certificate recovery. GREEN proves an
externally pinned thumbprint, an exact four-file checksum inventory, a fixed
encoded elevation child, protected request tamper detection, and exact rollback
after UAC or Add-Appx failure.

Round-two review added five failing behavior cases before implementation. RED
proved that the elevated import had no stable added-versus-preexisting result,
rollback could remove persistent preexisting trust, identity mismatch prevented
capturing the new package full name, strict identity validation blocked
rollback, and the uninstaller could not recover that exact-name mismatch.
GREEN proves stable `Added`/`AlreadyPresent` outcomes, rollback ownership, and
raw exact-name package cleanup without touching unrelated packages.

## Hardened safety boundary

The installer:

- rejects dot-source invocation;
- requires explicit `-ConfirmTrust` and a mandatory externally supplied
  `-ExpectedCertificateThumbprint`;
- rejects non-absolute, remote/mapped, missing, wrong-extension, or reparse
  inputs and requires all inputs to share one exact bundle directory;
- accepts exactly four nonblank checksum entries: the fixed MSIX, CER, install
  helper, and uninstall helper names; it rejects every missing, extra,
  duplicate, malformed, or traversing entry and verifies all four files;
- validates the public CER subject `CN=EMKE Internal Test`, fixed SHA-256, and
  the independently supplied fixed thumbprint, so a same-subject replacement
  CER remains rejected even if an attacker updates `SHA256SUMS.txt`;
- rejects an already elevated parent;
- never re-executes `$PSCommandPath`; it launches only a fixed encoded child
  source with `-Verb RunAs -Wait -PassThru`;
- sends no caller-controlled path in the elevated argv. A random per-operation
  JSON request carries exact operation/path/hash/thumbprint/subject data; its
  directory ACL is restricted, the file is read-only, its digest is bound
  through inherited process environment, and parent file handles prevent
  replacement while the elevated child reads it and the CER;
- independently parses and validates the request schema, request digest, local
  non-reparse CER path, CER bytes, subject, fixed thumbprint, and exact
  `LocalMachine\TrustedPeople` postcondition inside the encoded child;
- maps fixed elevated-child exit codes to the only two import outcomes,
  `Added` and `AlreadyPresent`, without returning certificate data;
- returns to the original non-elevated process, re-verifies both inputs, then
  calls `Add-AppxPackage` for that user;
- captures the newly appeared exact-name package and its `PackageFullName`
  before verifying Publisher, Version, and Architecture;
- writes the invoking-user install record only after Add-Appx succeeds and the
  exact package identity is observed;
- on UAC cancellation, child failure, Add-Appx failure, identity mismatch, or
  record failure, removes only the captured new package full name and removes
  certificate trust only when the stable result proves this run added it;
- preserves an `AlreadyPresent` certificate across every rollback, preserves
  trust when the import outcome is unknown, and emits explicit
  complete-versus-recovery-required guidance.

The uninstaller:

- queries and removes only the exact current-user
  `EMKE.Translation.Internal` package full name;
- retains certificate trust by default;
- requires both `-RemoveCertificate` and
  `-ConfirmRemoveCertificate`, the original CER, `SHA256SUMS.txt`, and the
  externally supplied fixed thumbprint;
- applies the same exact four-file inventory and fixed encoded-child boundary;
- validates the CER against the exact invoking-user install record when one
  exists, while allowing explicit exact-certificate cleanup after a failed
  install left no package or record;
- accepts the raw current-user package only after an ordinal exact Name match,
  so a Publisher/Version/Architecture mismatch cannot deadlock cleanup;
- removes only that returned exact `PackageFullName`, verifies the exact Name
  absent, and elevates only the constrained exact-certificate-removal child;
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

The behavior suite specifically covers same-subject CER replacement with
updated checksums, extra/duplicate inventory entries, paths containing spaces
and single quotes, caller-controlled data exclusion from elevated argv,
protected-request tampering, UAC cancellation recovery, Add-Appx failure
rollback, preexisting certificate preservation across Add/identity/record
failures, identity-mismatch removal by captured exact `PackageFullName`,
unrelated-package preservation, no-record cleanup, and install-record ordering.

## Task 8 handoff

Workflow or release automation must pass
`-ExpectedCertificateThumbprint` from the trusted certificate/provenance input;
the lifecycle helper will not infer trust from the bundled CER or
`SHA256SUMS.txt`. The package builder and CI workflow remain outside this task.

Windows UAC, real Local Machine certificate-store mutation, and real AppX
installation remain hosted/physical Windows evidence gates; this portable
suite proves the constrained orchestration and command seams without mutating
the development host.
