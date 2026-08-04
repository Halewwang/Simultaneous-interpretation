# Windows Setup EXE evidence ledger

This ledger separates source proof from packaged-artifact and real-Windows
acceptance. A higher evidence level is never inferred from a lower one.

## Evidence level A - source and ordinary automated tests

Status: PASS for this evidence level.

- Task 5 source orchestration is implemented on the Setup branch, including
  unelevated per-user package deployment, exact identity checks, endpoint
  readiness, rollback contracts, and authenticated reboot records.
- The Setup project passes 160/160 ordinary tests. The ordinary full solution
  gate passes 780 tests with one designed Windows-only skip.
- Nine Node packaging contracts pass, including an actual deterministic
  self-contained `win-x64` single-file publish and embedded-resource self-check.
- Seven PowerShell validation cases pass for exact inventory, byte/manifest
  tamper, stale/extra files, secret-bearing extensions, and reparse paths.
- The authenticated helper keeps one UAC session alive through the user phase
  and accepts only an authenticated final commit or exact rollback.
- This level does not prove that a final signed Setup candidate exists.

## Evidence level B - exact packaged bytes and signatures

Status: PENDING.

Required evidence is the exact five-file candidate inventory, Setup SHA-256,
valid Authenticode signer, embedded payload inventory, exact signed MSIX, and
the non-expired Microsoft Hardware Dev Center driver package. The accepted
Internal MSIX and CER are present and pinned, but the required Microsoft-signed
driver artifact is not currently available locally. A real packaging attempt
rejects that absence before candidate publication, and the candidate path
remains absent.

## Evidence level C - CI workflow evidence

Status: PENDING.

The `Windows Setup EXE` workflow runs portable contracts and ordinary Setup
tests on pull requests. Its protected `windows-setup-signing` job is manual and
requires explicit MSIX and Microsoft-signed-driver artifact provenance. A green
build-test job alone is not Setup packaging or installation evidence.

## Evidence level D - real Windows install and rollback

Status: PENDING.

Still required on clean Windows 10 22H2 and Windows 11 workstations: one narrow
UAC prompt, certificate/driver install, four active endpoint roles, per-user
MSIX install under the invoking SID, controlled startup, repair, upgrade,
failure rollback, reboot resume, uninstall, and removal of only state created
by the transaction. Hosted build runners do not replace this evidence.

## Evidence level E - end-to-end tester acceptance

Status: PENDING.

Still required: actual translation audio, meeting-platform listening, long-run
behavior, and tester acceptance of the exact candidate hash. Until levels B-D
are complete, the project must not state that a final Setup EXE was produced.
