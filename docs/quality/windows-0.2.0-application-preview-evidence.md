# Windows 0.2.0 application-preview evidence

## Scope and source

- Baseline: `origin/main` v0.2.4, commit `3d81733`; this Windows work is an
  independent application-preview stream.
- Tested workflow source before the hosted fallback: `85d06da57f8397b96c7af7af94685565ef33d591` on
  `codex/task4-workflow-evidence-green`.
- Current permanent-workflow fix: `0c4eb23` (`ci: verify hosted MSIX after
  certificate trust`), retained locally on `codex/windows-internal-msix`.
  The product branch was not pushed.
- Current lifecycle-bundle correction source:
  `222382c49425f66709715e4c25fd827b3da33b99`, retained locally on the same
  product branch. The product branch was not pushed.
- Package contract: `EMKE.Translation.Internal`, `0.2.0.0`, x64,
  `CN=EMKE Internal Test`.

## Remote workflow evidence

The protected workflow run [30705339053](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30705339053)
completed its `build-test` and protected `sign-package-bundle` jobs successfully
for source `85d06da57f8397b96c7af7af94685565ef33d591`.

- Build job: `91383366372` passed the portable Node contracts, locked managed
  restore, native CMake/CTest, and Release managed build with no reported build
  warnings or errors.
- Signing job: `91383761641` passed after the `windows-internal-signing`
  approval. Signing protections, certificate/thumbprint checks, and package
  verification were not weakened.
- Uploaded artifact: `8820221805`, named
  `emke-translation-windows-0.2.0-internal-x64-85d06da57f8397b96c7af7af94685565ef33d591`,
  reported size `161272661` bytes.

The requested self-hosted `install-25h2` job (`91384141887`) did not start:
the repository had no runner matching
`[self-hosted, Windows, X64, emke-win11-25h2]`. It therefore supplies no
install, smoke, uninstall, or cleanup evidence.

`84ff725` replaces that unavailable queue with an opt-in
`install-hosted-preview` job on `windows-2025-vs2026`. The job prints the
actual OS version/build, architecture, and product type; records the exact
MSIX SHA-256 and Authenticode status; and accepts `unsupportedWindowsProductType`
only when the hosted machine is a non-workstation Windows product type. It
does not describe that result as Windows 11 25H2 or physical acceptance.

Workflow dispatching was blocked by GitHub API rate limiting (HTTP 403, request
`D416:17F00F:4E1283E:500063E:6A6E1040`, 2026-08-01 15:26:57 UTC). To preserve
the protected-signing route without waiting for the API reset, the
evidence-only branch received temporary harness commit `f27c08b`: it adds only
that branch to the workflow push filter and permits the existing protected
signing and hosted-preview jobs for that push. The product branch does not
contain this harness. API rate limiting prevented immediate run-ID lookup; no
hosted-install result is claimed until the resulting run is inspected.

The temporary harness produced run `30706008991`, whose hosted-preview job
`91386120107` reached the signed MSIX check and exposed a trust-order defect:
it reported SHA-256
`698291A59614CE9DB75197C9442ED43F50ACA54107B963617656C3048A1FCF0F`, signer
`CN=EMKE Internal Test`, and thumbprint
`33E9992B08919BA6522F8A16B95CC2AA5DA6BB98`, but its pre-trust Authenticode
status was `UnknownError`. The workflow incorrectly required `Valid` before
the helper had temporarily trusted the exact public certificate, so it stopped
before installation. Commit `0c4eb23` moves the required `Valid` check into
the helper after exact certificate bytes/subject/thumbprint validation and
temporary TrustedPeople import; it verifies the post-trust signer subject and
thumbprint before `Add-AppxPackage`, while preserving both cleanup paths.
Evidence-only commit `26d7eae` carries that fix plus the temporary harness;
it is not a product commit and must not be merged into the product branch.

## Final hosted-preview result

Run [30706709568](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30706709568)
completed the permanent build/sign path for evidence source
`26d7eaeaacc6b2759407a993349154fb8e1232e6`.

- `build-test` job `91386972863`: **SUCCESS**.
- Protected `sign-package-bundle` job `91387310561`: **SUCCESS**.
- Artifact `8820627964`:
  `emke-translation-windows-0.2.0-internal-x64-26d7eaeaacc6b2759407a993349154fb8e1232e6`,
  `161272655` bytes, service digest SHA-256
  `5acce9bc503fa286276e89224d5f6d6fb14cbd229bf9825ac2b2e077999dc802`,
  expiring `2026-08-15T15:55:21Z`.
- `install-hosted-preview` job `91387702087` recorded pre-trust MSIX SHA-256
  `5BCB5D8D7BCF436381F5A4AF022FA6AFD0497ACA378C666F17644913B3BB958E` with
  Authenticode `UnknownError`. After importing the exact temporary public
  certificate, Authenticode was `Valid`; signer was `CN=EMKE Internal Test`
  with thumbprint `33E9992B08919BA6522F8A16B95CC2AA5DA6BB98`.
- The helper therefore passed `Add-AppxPackage` and exact installed
  identity/version/architecture checks before it invoked the smoke process.
  Its `finally` successfully uninstalled the exact package, and the workflow
  exact-certificate cleanup step also succeeded.
- Smoke did **not** pass: `Windows/tools/test-hosted-msix-install.ps1:237`
  reported `Driver-missing smoke exited with code -532462766.` The packaged
  WPF application has no supported `--hosted-driver-missing-smoke` contract;
  no runtime change was made in Task 4.

Artifact `8820627964` is **withdrawn as a user handoff**. Although its hosted
helper proved the signed MSIX install/query/uninstall and certificate-cleanup
facts above, the artifact's bundled lifecycle scripts independently hard-coded
expected version `0.1.0.0` and old `0.1.0` MSIX/CER names. They therefore
rejected the artifact's own signed `0.2.0` package. The hosted helper evidence
remains valid only for the operations it directly performed; it did not make
those stale bundled scripts usable.

## Lifecycle-bundle correction and replacement artifact

The lifecycle correction was developed RED-first and keeps the existing
install, uninstall, TrustedPeople, non-elevated-parent, rollback, and cleanup
safety behavior intact.

- RED run [30727638046](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30727638046),
  build job `91442179223`, failed because the rendered bundle still carried
  `0.1.0` metadata and malformed lifecycle placeholders were accepted.
- After the renderer correction, RED run
  [30728045398](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30728045398),
  build job `91443290639`, proved that the permanent workflow did not yet run
  the rendered-bundle behavior harness and that the harness still used an old
  independent fixture.
- Product source scripts now use strict lifecycle placeholders. The bundle
  builder resolves `Windows/version.json`, requires exact placeholder counts,
  rejects missing, duplicate, unexpected, or residual placeholders, writes
  the rendered scripts before hashing, and records those final bytes in the
  checksum and provenance files. The permanent portable gate runs the behavior
  harness against a real rendered bundle.
- Run [30728341177](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30728341177)
  passed build job `91444046367` and protected signing job `91444381743` for
  the corrected source. Hosted job `91444675018` again proved exact temporary
  trust, Authenticode `Valid`, package install/query/uninstall, and certificate
  cleanup. It then failed only at the same unsupported WPF smoke contract with
  exit `-532462766`; no smoke success is claimed.
- Final replacement run
  [30728655901](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30728655901)
  is overall **SUCCESS** for source
  `222382c49425f66709715e4c25fd827b3da33b99`: build job `91444861771` passed
  all portable contracts (including the rendered lifecycle behavior suite),
  native Release tests, and managed Release build; protected signing/bundle job
  `91445225554` passed; hosted job `91445500466` was explicitly skipped by the
  dispatch input.

The current user handoff is artifact `8827292982`, named
`emke-translation-windows-0.2.0-internal-x64-222382c49425f66709715e4c25fd827b3da33b99`,
`161272661` bytes, service digest SHA-256
`f6e6755620e873900414f296fcd622dacdcacb699f823a8ff7cb88686a49ae9a`,
expiring `2026-08-16T02:27:31Z`. Artifact `8820627964` is superseded by this
artifact and must not be distributed.

Independent download and extraction of artifact `8827292982` verified exactly
five files in the inner handoff ZIP. Both lifecycle scripts contain
`0.2.0.0`, `x64`, and the current `0.2.0` MSIX/CER names; neither contains a
residual lifecycle placeholder nor `0.1.0`; and their bytes match the copies
inside the ZIP. The provenance records source commit `222382c49425f66709715e4c25fd827b3da33b99`,
workflow run `30728655901`, identity `EMKE.Translation.Internal`, and certificate
thumbprint `33E9992B08919BA6522F8A16B95CC2AA5DA6BB98`.

- MSIX SHA-256:
  `EF9B5C1D06690363A65B20B515A3432255F90DB85659E7D2B62CF41845C1AA85`.
- Certificate SHA-256:
  `05BE411CEE43528C7B8CCC216EEEC6A904A8940DDA7B669BC8A00197CAF0CA1C`.
- Install script SHA-256:
  `E6DEE404E6FC9AF2D8DF5C50A8A7EFCB872A9CFBF262CE8E7CD08E9F6E7E82EF`.
- Uninstall script SHA-256:
  `3A37C7462ED3241B91F4DD1135155A29E9B4D7CC3E2FC88208209253DA951A17`.
- `SHA256SUMS.txt` SHA-256:
  `12E1A2647458778AA069D4294A5689D302E5557511E1501EA32CA5F04EC44057`.
- Inner handoff ZIP SHA-256:
  `782832FFB2781B7FF9F49A49F70376B95821F058BEE0853AF8F41D7CB8C861AA`.
- Provenance JSON SHA-256:
  `2C0C04675597F59A7F3228045E321C6EE08EE21290F924EEC3FF59ACE331139D`.

## Local and contract checks

- `git diff --check` passed before committing `84ff725`.
- Only the static, non-PowerShell assertions in
  `Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs` passed
  locally. The complete contract suite exited nonzero on this macOS checkout
  because `pwsh` is unavailable, so no full local contract-suite pass is
  claimed. PowerShell parsing and execution paths use the Windows CI build job
  above as their execution evidence.
- The permanent workflows now resolve
  `Windows/tools/resolve-version.ps1` rather than independently carrying
  `26200`, `0.1.0`, package-name, or architecture floors.
- Final Windows run `30728655901` executed the complete portable contract and
  rendered lifecycle behavior suites; this supersedes the earlier local-only
  static-check limitation for the corrected workflow source.
- For the final correction source, `node --check` passed for both modified
  contract files; the focused lifecycle mutation-boundary tests passed `2/2`;
  the focused permanent-workflow gate passed `1/1`; the behavior harness has no
  independent `0.1.0` fixture; and `git diff --check` passed. `pwsh` remains
  unavailable on this macOS host, so rendered PowerShell execution evidence is
  the successful Windows job `91444861771`.

## Acceptance boundary

This is application-preview CI evidence only. It does **not** prove a
Microsoft-signed driver, four endpoints, live translation, Setup EXE, physical
Windows 10 22H2 (19045) acceptance, or physical Windows 11 25H2 (26200)
acceptance. The smoke failure means it also does not prove packaged-process
smoke, live translation, driver behavior, or four endpoints. The current
Windows runtime rejects Windows Server and non-x64 by contract; a hosted
Server result is compatibility evidence, not a supported runtime result.
