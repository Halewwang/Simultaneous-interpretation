# Windows Microsoft Driver Signing Evidence

## Current status

**BLOCKED — Microsoft Hardware Dev Center has not returned signed driver
bytes.**

The repository now contains the fail-closed importer and production catalog
trust policy needed to validate a future portal result. This is implementation
readiness only. No portal submission ID, portal status, returned CAT hash,
Microsoft signer chain, verification-host build, or promoted driver directory
exists yet.

Hosted CI uses the unsigned Task 3 WDK package only as a mutation-test fixture.
Its simulated trust evidence is test-scoped and is not a substitute for
Microsoft portal bytes, Windows kernel-policy verification, or release
acceptance.

## Required external evidence

The protected release operator must upload the exact Task 3 archive, retain the
Hardware Dev Center submission ID and status, and download the returned package
without renaming or editing it. The returned directory must contain exactly one
INF, SYS, and CAT. The importer requires the returned INF and SYS hashes to
match the submitted manifest, permits the Microsoft-returned CAT to differ,
and promotes bytes only after exact catalog-membership, certificate-chain,
Microsoft publisher, and kernel-signing-policy validation.

The importer first copies the returned directory into an isolated snapshot and
performs every hash, catalog-membership, Microsoft publisher, kernel-policy,
and online whole-chain revocation check against that snapshot only. Manifest,
returned package, promoted output, and evidence paths must be pairwise
disjoint and cannot traverse reparse-point ancestors. Evidence is flushed to
an owned temporary file and exclusively published as `pending` before the
output move. After the promoted directory is re-hashed against the original
snapshot baseline, a second flushed record is atomically replaced as
`committed`. Every record carries a unique transaction ID. Consumers must
accept only `promotionState: committed`; a crash-visible `pending` record is
not promotion success. Any recoverable write, evidence publish, output publish,
commit publish, or final re-hash failure removes only outputs explicitly owned
by that transaction and never deletes another transaction's evidence.

After the portal result exists, run:

```powershell
pwsh -NoProfile -File Windows/tools/import-microsoft-signed-driver.ps1 `
  -SubmissionManifest artifacts/windows-driver-submission/driver-submission.json `
  -ReturnedPackageDirectory artifacts/windows-driver-portal-result `
  -OutputDirectory artifacts/windows-driver-microsoft-signed `
  -EvidencePath artifacts/windows-driver-microsoft-signed-evidence.json `
  -PortalSubmissionId '<actual portal submission ID>' `
  -PortalStatus '<actual portal status>'
```

The evidence JSON will record the source commit, submitted and returned CAT
SHA-256 values, exact promoted file hashes, signer subject and chain, portal
submission ID/status, and verification-host Windows build.

## Boundary

Do not fabricate a portal response, replace Microsoft signing with the internal
test certificate, bypass `/kp`, enable test mode, disable Secure Boot, or
disable Memory Integrity. Task 5 installation, endpoint, Secure Boot, and
Memory Integrity acceptance has not started and cannot be claimed from this
code or hosted test evidence.
