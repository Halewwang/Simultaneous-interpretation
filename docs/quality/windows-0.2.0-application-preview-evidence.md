# Windows 0.2.0 application-preview evidence

## Scope and source

- Baseline: `origin/main` v0.2.4, commit `3d81733`; this Windows work is an
  independent application-preview stream.
- Tested workflow source before the hosted fallback: `85d06da57f8397b96c7af7af94685565ef33d591` on
  `codex/task4-workflow-evidence-green`.
- Current permanent-workflow fix: `0c4eb23` (`ci: verify hosted MSIX after
  certificate trust`), retained locally on `codex/windows-internal-msix`.
  The product branch was not pushed.
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

## Local and contract checks

- `git diff --check` passed before committing `84ff725`.
- The workflow/metadata gate in
  `Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs` passed
  locally. PowerShell-executed portions cannot run on this macOS checkout
  because `pwsh` is unavailable; their authoritative proof is the Windows CI
  build job above.
- The permanent workflows now resolve
  `Windows/tools/resolve-version.ps1` rather than independently carrying
  `26200`, `0.1.0`, package-name, or architecture floors.

## Acceptance boundary

This is application-preview CI evidence only. It does **not** prove a
Microsoft-signed driver, four endpoints, live translation, Setup EXE, physical
Windows 10 22H2 (19045) acceptance, or physical Windows 11 25H2 (26200)
acceptance. The smoke failure means it also does not prove packaged-process
smoke, live translation, driver behavior, or four endpoints. The current
Windows runtime rejects Windows Server and non-x64 by contract; a hosted
Server result is compatibility evidence, not a supported runtime result.
