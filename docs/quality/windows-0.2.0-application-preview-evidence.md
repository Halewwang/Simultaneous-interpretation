# Windows 0.2.0 application-preview evidence

## Scope and source

- Baseline: `origin/main` v0.2.4, commit `3d81733`; this Windows work is an
  independent application-preview stream.
- Tested workflow source before the hosted fallback: `85d06da57f8397b96c7af7af94685565ef33d591` on
  `codex/task4-workflow-evidence-green`.
- Current permanent-workflow source: `84ff725` (`ci: run hosted Windows preview validation`),
  retained locally on `codex/windows-internal-msix` and mirrored only to the
  evidence branch.  The product branch was not pushed.
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
Evidence-only commit `26d7eae` carries that fix plus the temporary harness and
has been pushed for a new protected run. Its result is still pending.

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
acceptance. The packaged WPF application also has no production
`--hosted-driver-missing-smoke` entry point, so a successful build/sign job
must not be read as packaged process-smoke evidence. The current Windows
runtime rejects Windows Server and non-x64 by contract; a hosted Server result
is compatibility evidence, not a supported runtime result.
