# Task 4 report — CI alignment and Windows 0.2.0 application preview

## Status

DONE_WITH_CONCERNS: permanent metadata-driven workflows and the hosted-preview
fallback are implemented, but a final hosted install run is not yet available.
The original 25H2 job had no eligible runner; a temporary evidence-only push
harness has triggered the replacement path but its run is not yet inspectable
while the GitHub API is rate-limited. No install or smoke success is claimed.

## Changes

- `217b175` — RED-first workflow contract requiring checked-in release metadata
  and rejecting stale independent version/build literals.
- `ad40879` — permanent build/sign/install workflows resolve release metadata;
  artifact names and package inputs are metadata-derived.
- `33fe656`, `c25197f`, `85d06da` — focused Windows contract-fixture fixes
  exposed by the real remote gate.
- `84ff725` — replaces the nonexistent self-hosted 25H2 queue with an opt-in
  `windows-2025-vs2026` hosted preview. It reports actual host facts, keeps
  server rejection explicit, removes the helper's stale `0.1.0.0` restriction,
  and preserves exact identity/signing/cleanup validation.

No application runtime, audio, driver, Setup EXE, macOS code, signing policy,
or product branch remote state was changed.

## RED and GREEN evidence

- RED remote run `30704734281` failed the newly added workflow contract against
  the old literal workflows, before signing.
- Follow-up remote failures `30704878090`, `30704971651`, and `30705158192`
  identified stale test fixtures; each was corrected narrowly and covered by
  the same contract suite.
- GREEN remote run `30705339053`, source `85d06da57f8397b96c7af7af94685565ef33d591`:
  `build-test` job `91383366372` and protected signing job `91383761641`
  succeeded. Artifact `8820221805` was uploaded with the metadata-derived
  name and reported size `161272661` bytes.
- The optional self-hosted install job remained queued because no matching
  runner existed. `84ff725` is pushed only to
  `codex/task4-workflow-evidence-green`. API dispatch was blocked by HTTP 403
  at 2026-08-01 15:26:57 UTC, so evidence-only commit `f27c08b` temporarily
  adds that branch to the push filter and permits the existing protected
  signing and hosted-preview jobs for that push. It is not on the product
  branch and must not be merged.

Local `git diff --check` and the static workflow gate passed. PowerShell
execution tests are not runnable on the macOS checkout (`pwsh` absent), so the
remote Windows gate is the relevant execution proof.

## Self-review and acceptance boundary

The hosted job is deliberately named preview rather than 25H2 acceptance and
prints actual OS/build/product type. It only accepts the app's explicit
Windows-Server rejection when appropriate; it does not fabricate workstation
acceptance. Existing repository evidence states that the packaged WPF app does
not yet implement `--hosted-driver-missing-smoke`; therefore a subsequent
hosted install run may expose that as a real smoke gap and must not be reported
as passed without the executable entry point.

Still unproven: Microsoft-signed driver, four endpoints, live translation,
Setup EXE, physical Windows 10 22H2, physical Windows 11 25H2, device/meeting,
and audio acceptance. Exact final MSIX hash/signer/thumbprint/install evidence
for `84ff725` awaits a successfully dispatched hosted run.
