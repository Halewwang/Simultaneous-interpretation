# Task 5 Report — Internal MSIX Signing Contract

Status: `DONE_WITH_CONCERNS`

## Scope

Task 5 defines the Internal certificate contract, a read-only PFX verifier,
the controller-only one-time provisioning procedure, and executable contract
tests. The implementation commit is the commit containing this report with
message `build: define Internal MSIX signing contract`; its resolved hash is
reported in the handoff because a commit cannot contain its own hash.

No persistent certificate was generated. No GitHub secret was created,
changed, read, or printed. Validation used only synthetic certificates and PFX
files inside a test-owned temporary directory, which the test removed.

## TDD evidence

### RED

The Node contract suite was created before the verifier or README existed:

```text
PATH=/tmp/emke-pwsh-7bLsUP:$PATH node --test \
  Windows/tools/tests/internal-signing.contract.test.mjs

tests 4
pass 0
fail 4
```

The public-command test failed because
`verify-internal-signing-certificate.ps1` did not exist. The three
provisioning tests failed with `ENOENT` for
`Windows/packaging/InternalSigning/README.md`.

The synthetic-certificate suite also ran before the verifier existed:

```text
PATH=/tmp/emke-pwsh-7bLsUP:$PATH pwsh -NoLogo -NoProfile -File \
  Windows/tools/tests/internal-signing.validation.test.ps1

PASS 0
FAIL 10
```

All ten cases reached the missing verifier. Before that retained RED, a
test-command construction error and an unsupported macOS SHA-1 generation
path were corrected in test code. The SHA-1 fixture now mutates only the two
certificate signature-algorithm OIDs of a synthetic SHA-256 certificate, so
it deterministically exercises the real verifier branch without external
tools.

### GREEN

Fresh focused verification:

```text
PATH=/tmp/emke-pwsh-7bLsUP:$PATH node --test \
  Windows/tools/tests/internal-signing.contract.test.mjs

tests 4
pass 4
fail 0
```

```text
PATH=/tmp/emke-pwsh-7bLsUP:$PATH pwsh -NoLogo -NoProfile -File \
  Windows/tools/tests/internal-signing.validation.test.ps1

PASS 10
FAIL 0
Internal signing validation passed.
```

The synthetic suite proves:

- exact subject matching;
- RSA 3072-bit minimum;
- SHA-1 rejection and SHA-256 acceptance;
- code-signing EKU;
- Digital Signature key usage;
- current validity;
- a PFX private key;
- password input only through the named environment variable;
- exactly five public metadata output fields with no password disclosure; and
- a DER `.cer` that reloads without a private key.

`git diff --check` passed.

## Implementation boundaries

On Windows, the verifier loads the PFX with `EphemeralKeySet`; it never imports
the certificate into a persistent store. macOS PowerShell does not support
that flag, so the synthetic local validation uses the platform default
process-temporary loader and disposes the certificate immediately. The
production Windows branch still requires explicit ephemeral loading.

The provisioning README records command shapes and cleanup rules only. They
were not executed in this task. It requires confirmation of the two secret
names before deleting exactly the five generated temporary files and their
now-empty directory.

## Proof boundary

This is local contract and synthetic-PFX evidence on macOS. It is not proof of
a Windows hosted runner, a real Internal signing secret, a signed MSIX,
certificate installation, package installation, driver signing, real-device
operation, meeting behavior, or release acceptance. Those remain later plan
gates.

## Cross-task security fix — Environment-scoped secrets

The follow-up commit containing this section uses message
`docs: protect Windows signing environment secrets`. It closes the
repository-level secret provisioning gap without creating or reading any
secret.

TDD RED:

```text
PATH=/tmp/emke-pwsh-7bLsUP:$PATH node --test \
  Windows/tools/tests/internal-signing.contract.test.mjs

tests 5
pass 3
fail 2
```

The README test observed both old `gh secret set NAME` commands instead of the
required `gh secret set --env windows-internal-signing NAME` commands. The
Task 9 test also rejected the old `gh secret list --app actions` command.

TDD GREEN:

```text
tests 5
pass 5
fail 0
```

The provisioning guide now requires an administrator-created
`windows-internal-signing` GitHub Environment with required reviewers. Both
secret writes and the name-only list are scoped with `--env
windows-internal-signing`; repository-level and `--app actions` command shapes
are rejected. The guide and Task 9 require only the signing workflow job to
declare `environment: windows-internal-signing`.

A read-only inspection confirmed that the existing
`sign-package-bundle` workflow job already has that exact Environment binding
and that its two secret references are confined to its signing step. No
workflow file needed modification. `git diff --check` passed.

No GitHub Environment, protection rule, certificate, or secret was created or
changed. Hosted approval behavior and secret availability remain unverified
until the administrator configures the Environment and a real workflow run
passes its required-reviewer gate.

## Review round — reject whitespace and alternate secret scopes

The review found that the first Environment-scope contract enumerated only
commands beginning in column zero. An additional indented repository-level
`gh secret set` or `--org` list command could therefore coexist with the valid
commands without entering the checked command arrays.

The pre-fix mutation added both unsafe lines to the real README while retaining
the three valid Environment-scoped commands. The focused suite incorrectly
reported:

```text
tests 5
pass 5
fail 0
```

This demonstrated the real leak in the previous scan. The README mutation was
then removed before implementing the test fix.

The revised scanner matches every line satisfying
`^\s*gh secret (set|list)`, normalizes leading whitespace, explicitly rejects
`--org` and `--app`, and requires the exact immediate scope
`--env windows-internal-signing` on every detected command. Dedicated malicious
fixtures cover an indented repository-level set, a repository-level list,
an organization-level set, and an Actions-app list.

Replaying the same real-README mutation after the fix produced the required
RED:

```text
tests 6
pass 5
fail 1
actual: gh secret set WINDOWS_INTERNAL_SIGNING_PFX_BASE64
expected: exact --env windows-internal-signing scope
```

After restoring the real README, the focused suite returned 6/6 GREEN and
`git diff --check` passed. The Task 9 plan check applies the same scanner to
its exact provisioning section. No provisioning document, workflow, secret,
Environment, certificate, or private material was changed by this review fix.
