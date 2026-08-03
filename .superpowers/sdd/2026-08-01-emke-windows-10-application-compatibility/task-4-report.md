# Task 4 report — CI alignment and Windows 0.2.0 application preview

## Status

DONE_WITH_CONCERNS: permanent metadata-driven workflows and hosted-preview
install evidence are complete. The signed MSIX installed, its exact identity
was validated, and cleanup succeeded; the WPF smoke process failed because the
package has no supported `--hosted-driver-missing-smoke` contract. No runtime
change was made in Task 4, and no smoke/live/driver/four-endpoint acceptance is
claimed. Artifact `8820627964` is withdrawn from user handoff because its
bundled lifecycle scripts were stale; corrected artifact `8827292982`
supersedes it.

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
- `0c4eb23` — moves required Authenticode `Valid` verification behind exact
  temporary public-certificate trust, verifies signer subject/thumbprint before
  installation, and retains both cleanup paths.

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
- Hosted run `30706008991`, job `91386120107`, reached the signed package and
  exposed the expected internal-certificate trust-order defect: the exact MSIX
  SHA-256 was `698291A59614CE9DB75197C9442ED43F50ACA54107B963617656C3048A1FCF0F`,
  signer `CN=EMKE Internal Test`, and thumbprint
  `33E9992B08919BA6522F8A16B95CC2AA5DA6BB98`, but pre-trust Authenticode was
  `UnknownError` and the workflow incorrectly demanded `Valid` before the
  helper added the exact certificate. Product commit `0c4eb23` centralizes the
  mandatory Valid/signer verification after exact temporary trust and before
  `Add-AppxPackage`; evidence-only commit `26d7eae` carried its replacement
  run and is not part of the product branch.
- Final hosted run `30706709568`: `build-test` job `91386972863` and protected
  signing job `91387310561` both succeeded. Artifact `8820627964`, named
  `emke-translation-windows-0.2.0-internal-x64-26d7eaeaacc6b2759407a993349154fb8e1232e6`,
  was `161272655` bytes with service digest SHA-256
  `5acce9bc503fa286276e89224d5f6d6fb14cbd229bf9825ac2b2e077999dc802` and
  expiration `2026-08-15T15:55:21Z`.
- Final install job `91387702087` recorded pre-trust MSIX SHA-256
  `5BCB5D8D7BCF436381F5A4AF022FA6AFD0497ACA378C666F17644913B3BB958E` with
  `UnknownError`, then post-trust Authenticode `Valid` with signer
  `CN=EMKE Internal Test` and thumbprint
  `33E9992B08919BA6522F8A16B95CC2AA5DA6BB98`. `Add-AppxPackage` and exact
  identity/version/architecture validation completed; helper-finally uninstall
  and workflow exact-certificate cleanup completed. The subsequent smoke
  failed at `test-hosted-msix-install.ps1:237` with exit `-532462766` because
  the WPF package lacks a supported `--hosted-driver-missing-smoke` contract.

The hosted helper did not execute the artifact's bundled lifecycle scripts.
Whole-plan review later found those scripts hard-coded `0.1.0.0` and old
`0.1.0` MSIX/CER names, so artifact `8820627964` rejected its own signed
`0.2.0` package and is not a usable user lifecycle handoff. Its direct
install/query/uninstall and cleanup observations remain evidence only for the
helper operations described above.

Local `git diff --check` and the static workflow gate passed. PowerShell
execution tests are not runnable on the macOS checkout (`pwsh` absent), so the
remote Windows gate is the relevant execution proof.

## Review fix round 1

- Corrected the evidence document: only static, non-PowerShell assertions pass
  locally; the full Node contract suite exits nonzero on macOS without `pwsh`.
- Added a static workflow-contract assertion preventing the hosted-preview job
  from describing its runner as 25H2, and changed the remaining exception text
  to `hosted preview runner`. The job continues to print actual OS/build and
  product type.
- Verification: the new static assertion passes locally; PowerShell-dependent
  tests remain intentionally unproven locally and rely on the completed
  Windows CI evidence above.

## Whole-plan lifecycle correction

- RED run `30727638046`, build `91442179223`, exposed stale rendered metadata
  and missing strict placeholder rejection. RED run `30728045398`, build
  `91443290639`, exposed the missing permanent rendered-behavior gate and stale
  behavior fixture.
- Commits `3ca5438` through `222382c` render lifecycle metadata from the
  checked-in resolver, reject malformed or residual placeholders, hash the
  final rendered bytes, run behavior tests on a real rendered bundle, and
  cover the protected elevation request's write lock and tamper detection.
  Existing install/uninstall/TrustedPeople/elevation/rollback/cleanup safety
  remains under the behavior suite.
- Corrected hosted run `30728341177` passed build `91444046367` and protected
  signing `91444381743`. Hosted job `91444675018` proved temporary trust,
  Authenticode `Valid`, install/query/uninstall, and certificate cleanup, then
  retained the known smoke-only failure `-532462766`.
- Final run `30728655901` is overall SUCCESS for
  `222382c49425f66709715e4c25fd827b3da33b99`: build `91444861771` and
  protected signing/bundle `91445225554` succeeded; hosted `91445500466` was
  explicitly skipped. This separation leaves actual hosted package lifecycle
  evidence in `30728341177` and a clean overall-success deliverable in
  `30728655901`.
- Replacement artifact `8827292982`, name
  `emke-translation-windows-0.2.0-internal-x64-222382c49425f66709715e4c25fd827b3da33b99`,
  size `161272661`, service digest
  `f6e6755620e873900414f296fcd622dacdcacb699f823a8ff7cb88686a49ae9a`,
  expires `2026-08-16T02:27:31Z`. Independent extraction verified exactly five
  inner files, current lifecycle metadata, no placeholder/`0.1.0`, matching
  inner/outer script bytes, and provenance for the exact source/run/identity/
  thumbprint. MSIX SHA-256 is
  `EF9B5C1D06690363A65B20B515A3432255F90DB85659E7D2B62CF41845C1AA85`;
  inner ZIP SHA-256 is
  `782832FFB2781B7FF9F49A49F70376B95821F058BEE0853AF8F41D7CB8C861AA`.

Artifact `8827292982` supersedes withdrawn artifact `8820627964` for user
handoff. No product branch was pushed.

## Self-review and acceptance boundary

The hosted job is deliberately named preview rather than 25H2 acceptance and
prints actual OS/build/product type. It only accepts the app's explicit
Windows-Server rejection when appropriate; it does not fabricate workstation
acceptance. The final WPF smoke failure confirms the known missing
`--hosted-driver-missing-smoke` contract and is reported as a concern, not as
successful smoke evidence.

Still unproven: packaged-process smoke, Microsoft-signed driver, four
endpoints, live translation, Setup EXE, physical Windows 10 22H2, physical
Windows 11 25H2, device/meeting, and audio acceptance. Product commits,
including `0c4eb23`, remain local and unpushed.
