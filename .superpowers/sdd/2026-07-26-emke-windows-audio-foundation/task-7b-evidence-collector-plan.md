# Task 7B2 Windows Audio Evidence Collector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a non-elevated, read-only-input PowerShell 7 collector that
validates and sanitizes supplied Windows audio lab evidence into one private,
atomic JSON artifact.

**Architecture:** One PowerShell script exposes pure validation, hashing,
acceptance, serialization, and orchestration functions behind a pre-definition
dot-source guard. Node freezes the static safety and CI contract. Portable
PowerShell suites import real function bodies through the AST and replace only
Windows host, repository HEAD, Authenticode, clock, and atomic-rename seams.

**Tech Stack:** PowerShell 7, .NET cryptography and JSON APIs, Node 24 test
runner, GitHub Actions.

## Global Constraints

- Never elevate, install, uninstall, sign, enumerate live devices, or mutate
  certificates, drivers, audio, or Git state.
- Runtime requires Windows 11 build 26200 or newer, x64, PowerShell 7, and
  explicit `-ConfirmCollect`.
- All inputs are exact local non-reparse paths; output is a new atomic file.
- No error, console output, or evidence field may expose raw endpoint IDs,
  salt, input paths, observation text, recording bytes, or privacy canaries.
- `driverInstalled` is always `notEstablished`; observation-based proof fields
  never claim collector-independent physical proof.

---

### Task 1: Freeze the entry and CI safety contract

**Files:**
- Create: `Windows/tools/tests/audio-evidence-collector.contract.test.mjs`
- Create: `Windows/tools/collect-audio-evidence.ps1`
- Modify: `.github/workflows/windows-audio.yml`

**Interfaces:**
- Consumes: the approved Task 7B2 brief.
- Produces: script parameter names, pre-definition guard, function names,
  forbidden-capability boundary, and three independent CI test commands.

- [x] Write Node tests that require the collector file, exact parameters,
  PowerShell/host gates, guard ordering, package/hash/privacy constants,
  non-overwriting atomic output API, no forbidden mutation/device/certificate
  capability text, and independent contract/validation/behavior CI gates.
- [x] Run
  `node --test Windows/tools/tests/audio-evidence-collector.contract.test.mjs`
  and record RED because the collector and CI gates are absent.
- [x] Add only the parameter block, dot-source guard, strict-mode boundary,
  named function declarations, safe constants, and CI test commands needed to
  satisfy the contract. CI commands invoke tests only and contain no
  `-ConfirmCollect` or evidence paths.
- [x] Re-run the Node contract and require all tests to pass.

### Task 2: Implement strict validation and sanitization

**Files:**
- Create: `Windows/tools/tests/audio-evidence-collector.validation.test.ps1`
- Modify: `Windows/tools/collect-audio-evidence.ps1`

**Interfaces:**
- Produces:
  - `Resolve-CollectorInputPath` and `Resolve-CollectorOutputPath`
  - `Get-StrictCollectorPackage` and `Get-CollectorPackageSha256`
  - `Get-CollectorInfMetadata` and `Get-CollectorCatalogMetadata`
  - `Read-CollectorObservation`
  - `Get-EndpointRoleSha256`
  - `Get-LabAcceptance` and `New-AudioEvidenceRecord`
  - `Write-AtomicEvidenceFile`

- [x] Build an AST importer that loads production function bodies without
  dot-sourcing and forbids real process execution.
- [x] Add table-driven RED cases for UNC/reparse/type/existing-output paths,
  exact flat package layout and V1 digest, INF and signature metadata,
  observation allowed keys, duplicate/missing roles and scenarios, UTC ordering,
  safe counters, the frozen Smoke ABI table, weak salt, operator digest,
  privacy canaries, acceptance precedence, exact endpoint hashes, field order,
  UTF-8 no BOM, recording/raw byte hashes, and atomic-write failure cleanup.
- [x] Run the portable validation suite and record RED against the declarations.
- [x] Implement the smallest pure functions that satisfy the tables. Public
  errors use fixed categories only. Endpoint hashing uses:

```text
SHA256(
  UTF8("EMKE-ENDPOINT-ROLE-HASH-V1\0" + role + "\0")
  || salt
  || UTF8("\0" + opaqueEndpointId)
)
```

- [x] Re-run validation until every case passes without leaving temp files.

### Task 3: Implement top-level read-only orchestration

**Files:**
- Create: `Windows/tools/tests/audio-evidence-collector.behavior.test.ps1`
- Modify: `Windows/tools/collect-audio-evidence.ps1`

**Interfaces:**
- Produces: `Invoke-CollectAudioEvidence`, which calls only safe functions plus
  injected `Get-CollectorRepositoryHead`, `Get-CollectorHostInfo`,
  `Get-AuthenticodeSignature`, `Get-CollectorUtcNow`, and final rename seams.

- [x] Add real top-level RED cases for missing confirmation, unsupported OS,
  wrong architecture, commit mismatch, package mismatch, invalid signature,
  schema rejection, salt rejection, output collision, atomic failure, privacy
  canary suppression, optional recording digest, and deterministic happy output.
- [x] Verify real dot-source rejection in an isolated caller scope occurs
  before caller strict/error/function changes and leaks no collector functions.
- [x] Run the portable behavior suite and record RED.
- [x] Implement orchestration in this order: confirmation and host gate; exact
  paths; exact HEAD; package/INF/catalog; raw observation and salt; optional
  recording; evidence record; owned same-directory temp; non-overwriting atomic
  rename. On failure remove only the exact owned temp and never the target.
- [x] Re-run behavior and validation until both pass.

### Task 4: Verify all gates and commit

**Files:**
- Verify all files above plus the existing lifecycle suites.

- [x] Run the collector Node contract, collector validation, and collector
  behavior suites.
- [x] Run all repository Node tests, existing lifecycle validation, and existing
  lifecycle behavior with portable PowerShell.
- [x] Run privacy scans, `git diff --check`, inspect the complete diff, and
  confirm only the approved files changed.
- [x] Commit the approved brief, plan, collector, tests, and workflow gates in
  one or a small number of focused commits. Do not push.
