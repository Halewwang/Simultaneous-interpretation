# EMKE Translation Windows 10-11 Acceptance and Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Accept and promote one exact Windows `0.2.0-internal` A1 Setup EXE only after automated provenance checks and real Windows 10/11 installation, driver, four-endpoint, live Translation, audio-safety, and meeting tests all pass for the same immutable payload hashes.

**Architecture:** Release acceptance consumes immutable outputs from the application-compatibility, runtime-completion, driver-signing, and Setup plans. A machine-readable release candidate manifest pins every source commit, workflow run, artifact hash, signer, package identity, driver identity, and protocol contract. Automated tests validate the inventory; controlled real-machine runs append signed evidence for each matrix lane. Promotion copies, but never rebuilds, the accepted candidate.

**Tech Stack:** PowerShell 7, Node.js contract tests, .NET/MSTest, Windows event/audio diagnostics, SHA-256, Authenticode/WinTrust, GitHub Actions artifacts, real Windows 10/11 x64 machines

## Global Constraints

- Execute this plan after the four prerequisite plans produce candidate
  artifacts; never substitute a newer rebuild mid-matrix.
- Use the exact Setup EXE, MSIX, public CER if applicable, and Microsoft-signed
  INF/SYS/CAT hashes in every lane.
- Keep Secure Boot and Memory Integrity on for A1 acceptance.
- Do not use test mode, BCD changes, unsigned-driver exceptions, Windows
  Server, x86, or ARM64.
- Do not collect API keys, PCM, recordings, full endpoint IDs, transcripts,
  meeting content, user names, or unrelated device inventory.
- Automated/hosted proof never substitutes for physical install, driver load,
  endpoint routing, listening, or meeting evidence.
- A failed lane blocks promotion; do not edit the candidate or evidence to make
  a failed result appear passing.
- Windows and macOS releases remain independently versioned and deployable.

---

## Plan Set and Execution Order

Execute and accept these plans in order before final promotion:

1. `2026-08-01-emke-windows-10-application-compatibility.md`
2. `2026-08-01-emke-windows-runtime-completion.md`
3. `2026-08-01-emke-windows-10-driver-and-signing.md`
4. `2026-08-01-emke-windows-setup-exe.md`
5. this acceptance and release plan.

Milestone 1 may produce an application-only preview for build `19045`, but it
is not the A1 release and must use a distinct evidence label.

### Task 1: Freeze the Release Candidate Inventory

**Files:**

- Create: `Windows/release/windows-0.2.0-internal-candidate.json`
- Create: `Windows/tools/resolve-release-candidate.ps1`
- Create: `Windows/tools/tests/windows-release-candidate.contract.test.mjs`
- Create: `Windows/tools/tests/windows-release-candidate.validation.test.ps1`
- Create: `docs/quality/windows-0.2.0-release-candidate.md`

- [ ] **Step 1: Write the RED candidate-schema contract**

Require one canonical document containing:

```json
{
  "productVersion": "0.2.0",
  "packageVersion": "0.2.0.0",
  "driverVersion": "1.0.0.2",
  "driverAbiVersion": 1,
  "channel": "internal",
  "architecture": "x64",
  "minimumWindowsBuild": 19045,
  "sourceCommitPattern": "^[0-9a-f]{40}$",
  "workflowRuns": {
    "applicationPattern": "^[1-9][0-9]*$",
    "driverPattern": "^[1-9][0-9]*$",
    "setupPattern": "^[1-9][0-9]*$"
  },
  "artifacts": [
    {"role": "setup", "name": "EMKE-Translation-Setup-0.2.0-internal-x64.exe", "sha256Pattern": "^[0-9a-f]{64}$"},
    {"role": "msix", "name": "EMKE-Translation-Windows-0.2.0-internal-x64.msix", "sha256Pattern": "^[0-9a-f]{64}$"},
    {"role": "driver-inf", "name": "EMKE.VirtualAudio.inf", "sha256Pattern": "^[0-9a-f]{64}$"},
    {"role": "driver-sys", "name": "EMKE.VirtualAudio.sys", "sha256Pattern": "^[0-9a-f]{64}$"},
    {"role": "driver-cat", "name": "EMKE.VirtualAudio.cat", "sha256Pattern": "^[0-9a-f]{64}$"}
  ]
}
```

Also require Setup/MSIX signer evidence, Microsoft driver catalog signer,
package family/full name, publisher, hardware ID, four endpoint roles, KMDF
`1.31`, macOS behavior baseline `3d81733`, and the five approved protocol event
names.

- [ ] **Step 2: Implement a strict candidate resolver**

`resolve-release-candidate.ps1` validates schema, unique roles/names/hashes,
cross-checks `Windows/version.json`, reads the Setup embedded inventory, hashes
all files from opened handles, and verifies signatures/membership. It returns
an immutable object for acceptance tools; it never downloads or rebuilds.

- [ ] **Step 3: Add mutation cases**

Reject a changed source commit, workflow ID, artifact hash, signer, package
identity/version, driver version/ABI/KMDF/model section, endpoint role,
protocol event, or missing/extra artifact.

- [ ] **Step 4: Run and commit the candidate freeze**

```bash
node --test Windows/tools/tests/windows-release-candidate.contract.test.mjs
pwsh -NoProfile -File Windows/tools/tests/windows-release-candidate.validation.test.ps1
pwsh -NoProfile -File Windows/tools/resolve-release-candidate.ps1 -CandidateFile Windows/release/windows-0.2.0-internal-candidate.json -ArtifactDirectory artifacts/windows-0.2.0-candidate
git diff --check
git add Windows/release/windows-0.2.0-internal-candidate.json Windows/tools/resolve-release-candidate.ps1 Windows/tools/tests/windows-release-candidate.contract.test.mjs Windows/tools/tests/windows-release-candidate.validation.test.ps1 docs/quality/windows-0.2.0-release-candidate.md
git commit -m "release: freeze Windows 0.2.0 candidate"
```

### Task 2: Run the Complete Automated Release Gate

**Files:**

- Create: `Windows/tools/run-windows-release-gate.ps1`
- Create: `Windows/tools/tests/windows-release-gate.contract.test.mjs`
- Create: `.github/workflows/windows-release-candidate.yml`
- Create: `docs/quality/windows-0.2.0-automated-evidence.md`

- [ ] **Step 1: Write a RED orchestration contract**

The release gate must invoke, fail-fast, and persist exit/result evidence for:

```text
shared cross-platform behavior contracts
Windows version/package/workflow contracts
all managed builds and tests
native audio builds and tests
driver source/stamped package tests
Microsoft-signed driver import verification
Setup domain/tamper/elevation tests
MSIX and Setup independent signature/hash verification
candidate manifest verification
secret/private-key/test-mode scans
```

The contract test must fail if any required command is omitted or guarded with
continue-on-error.

- [ ] **Step 2: Implement fail-fast orchestration**

`run-windows-release-gate.ps1` takes only candidate/artifact paths, invokes the
existing focused tools, writes a machine-readable result per gate, and exits
nonzero on the first failure. It must not install a driver on a hosted runner
unless that runner is an explicitly provisioned acceptance machine.

- [ ] **Step 3: Run the exact gate on the frozen candidate**

```powershell
pwsh -NoProfile -File Windows/tools/run-windows-release-gate.ps1 -CandidateFile Windows/release/windows-0.2.0-internal-candidate.json -ArtifactDirectory artifacts/windows-0.2.0-candidate -EvidenceDirectory artifacts/windows-0.2.0-evidence/automated
```

Expected: every result references the same candidate manifest hash and exact
artifact SHA-256 values.

- [ ] **Step 4: Record proof boundaries and commit**

The automated evidence document separates compile/unit/package/signature
proof from real-machine install, endpoint, live service, listening, and meeting
proof, which remain pending until Tasks 3–5.

```bash
git add Windows/tools/run-windows-release-gate.ps1 Windows/tools/tests/windows-release-gate.contract.test.mjs .github/workflows/windows-release-candidate.yml docs/quality/windows-0.2.0-automated-evidence.md
git commit -m "ci: gate frozen Windows release candidate"
```

### Task 3: Execute Install, Upgrade, Repair, Rollback, and Uninstall Matrix

**Files:**

- Create: `Windows/acceptance/windows-0.2.0-machine-matrix.json`
- Create: `Windows/tools/run-setup-acceptance.ps1`
- Create: `Windows/tools/tests/setup-acceptance.contract.test.mjs`
- Create: `Windows/tools/tests/setup-acceptance.validation.test.ps1`
- Create: `docs/quality/windows-0.2.0-setup-acceptance.md`

- [ ] **Step 1: Define exact real-machine lanes**

Require at minimum:

```text
Windows 10 22H2 x64 build 19045, Secure Boot on, Memory Integrity on
Windows 11 21H2 x64 build 22000
Windows 11 23H2 x64 build 22631
Windows 11 24H2 x64 build 26100
Windows 11 25H2 x64 build 26200 or newer
```

Each lane records machine pseudonym, edition/product type/build/architecture,
Secure Boot, Memory Integrity, candidate manifest hash, artifact hashes, start
state, action, result, reboot, rollback result, and end state.

- [ ] **Step 2: Add acceptance-record validation before execution**

Reject non-workstation, wrong build/architecture, disabled security state,
wrong artifact hash, reused transaction ID, missing time bounds, or evidence
that does not name the exact candidate.

- [ ] **Step 3: Execute lifecycle scenarios**

Across the matrix run:

```text
clean install
upgrade from Windows 0.1.0 application-only package
same-version repair
cancel before mutation
forced failure after certificate creation
forced failure after driver installation
forced failure after MSIX installation
alternate-administrator UAC approval
reboot-required resume
normal uninstall
reinstall after uninstall
```

Verify no manual certificate operation, at most one UAC sequence, correct
invoking-user package ownership, exact rollback preservation, and no removal of
unrelated/pre-existing components.

- [ ] **Step 4: Prove negative host admission**

Use non-mutating/fake-host tests for build `19044`, x86, ARM64, and Windows
Server. Setup must reject before extraction/machine mutation/UAC.

- [ ] **Step 5: Validate evidence and commit results**

```bash
node --test Windows/tools/tests/setup-acceptance.contract.test.mjs
pwsh -NoProfile -File Windows/tools/tests/setup-acceptance.validation.test.ps1
pwsh -NoProfile -File Windows/tools/run-setup-acceptance.ps1 -CandidateFile Windows/release/windows-0.2.0-internal-candidate.json -Scenario clean-install -EvidenceDirectory artifacts/windows-0.2.0-evidence/setup
git diff --check
git add Windows/acceptance/windows-0.2.0-machine-matrix.json Windows/tools/run-setup-acceptance.ps1 Windows/tools/tests/setup-acceptance.contract.test.mjs Windows/tools/tests/setup-acceptance.validation.test.ps1 docs/quality/windows-0.2.0-setup-acceptance.md
git commit -m "test: accept Windows Setup lifecycle matrix"
```

### Task 4: Execute Four-Endpoint and Audio-Safety Acceptance

**Files:**

- Create: `Windows/tools/run-audio-acceptance.ps1`
- Create: `Windows/tools/tests/audio-acceptance.contract.test.mjs`
- Create: `Windows/tools/tests/audio-acceptance.validation.test.ps1`
- Create: `docs/quality/windows-0.2.0-audio-acceptance.md`

- [ ] **Step 1: Define sanitized audio evidence**

Record only endpoint roles/states, sample rates, channel counts, frame/sequence
counters, route transitions, underrun/backpressure/error counters, session
states, protocol event-name counters, reconnect timings, and pass/fail. Never
record PCM, captions, transcripts, complete endpoint IDs, or meeting content.

- [ ] **Step 2: Verify physical and virtual routing**

On Windows 10 `19045` and at least Windows 11 `26100`/`26200+`, verify:

```text
physical speaker -> appSpeakerCapture -> inbound 24 kHz session
inbound 24 kHz audio delta -> meetingSpeakerRender at local 48 kHz
physical microphone -> outbound 24 kHz session
outbound 24 kHz audio delta -> appMicrophoneRender -> meetingMicrophoneCapture
exactly four active virtual roles
```

Confirm application device selection honors explicit settings and follow-
default changes without cross-routing.

- [ ] **Step 3: Verify safety and recovery**

Inject inbound socket loss, outbound socket loss, driver endpoint removal,
physical device change, queue saturation, malformed protocol event, service
close, and reconnect. Verify inbound original fail-open, outbound microphone
fail-closed, explicit bypass, route-lock behavior, recovery only after complete
handshake, and bounded shutdown.

- [ ] **Step 4: Verify exact protocol contract live**

Provider-side or sanitized client evidence must show:

```text
session.audio.output.language
session.input_audio_buffer.append
session.output_audio.delta
session.input_transcript.delta
session.output_transcript.delta
```

Do not include API keys or transcript content. Validate two independent session
IDs and correct target language for each direction.

- [ ] **Step 5: Validate and commit audio evidence**

```bash
node --test Windows/tools/tests/audio-acceptance.contract.test.mjs
pwsh -NoProfile -File Windows/tools/tests/audio-acceptance.validation.test.ps1
pwsh -NoProfile -File Windows/tools/run-audio-acceptance.ps1 -CandidateFile Windows/release/windows-0.2.0-internal-candidate.json -EvidenceDirectory artifacts/windows-0.2.0-evidence/audio
git diff --check
git add Windows/tools/run-audio-acceptance.ps1 Windows/tools/tests/audio-acceptance.contract.test.mjs Windows/tools/tests/audio-acceptance.validation.test.ps1 docs/quality/windows-0.2.0-audio-acceptance.md
git commit -m "test: accept Windows audio safety matrix"
```

### Task 5: Execute Real Meeting Acceptance

**Files:**

- Create: `Windows/acceptance/meeting-scenarios.json`
- Create: `Windows/tools/validate-meeting-evidence.ps1`
- Create: `Windows/tools/tests/meeting-evidence.contract.test.mjs`
- Create: `docs/quality/windows-0.2.0-meeting-acceptance.md`

- [ ] **Step 1: Define privacy-safe scenarios**

Use consented test speech with no personal or business content. At minimum run
one supported meeting application on Windows 10 `19045` and one on Windows 11
`26100` or newer. Record app/version, selected virtual speaker/microphone roles,
language pair, direction, duration, candidate hash, and observer result; do not
store recording/transcript.

- [ ] **Step 2: Verify both translation directions**

For each platform verify remote speech reaches local translated output and
local speech reaches the meeting as translated microphone output. Confirm
captions update, mute/bypass behavior is explicit, original/translated audio
does not duplicate unexpectedly, and reconnect recovers after a controlled
network interruption.

- [ ] **Step 3: Perform listening and latency observation**

Record pass/fail plus bounded numerical counters/timestamps for startup,
first-audio, reconnect, underflow, and shutdown. Human observation may state
intelligible/no audible clipping/route correct; it must not reproduce spoken
content.

- [ ] **Step 4: Validate and commit meeting evidence**

```bash
node --test Windows/tools/tests/meeting-evidence.contract.test.mjs
pwsh -NoProfile -File Windows/tools/validate-meeting-evidence.ps1 -CandidateFile Windows/release/windows-0.2.0-internal-candidate.json -EvidenceDirectory artifacts/windows-0.2.0-evidence/meeting
git diff --check
git add Windows/acceptance/meeting-scenarios.json Windows/tools/validate-meeting-evidence.ps1 Windows/tools/tests/meeting-evidence.contract.test.mjs docs/quality/windows-0.2.0-meeting-acceptance.md
git commit -m "test: accept Windows real meeting flow"
```

### Task 6: Promote the Exact Accepted Candidate

**Files:**

- Create: `Windows/tools/promote-windows-release.ps1`
- Create: `Windows/tools/tests/windows-release-promotion.contract.test.mjs`
- Create: `Windows/release/windows-0.2.0-internal-release.json`
- Create: `docs/releases/windows-0.2.0-internal.md`

- [ ] **Step 1: Write a RED no-rebuild promotion contract**

Promotion must require completed automated, Setup, driver, audio, and meeting
evidence for every required lane; each document must reference the same
candidate manifest hash. The script may copy verified bytes but may not invoke
build, publish, package, sign, or portal submission commands.

- [ ] **Step 2: Implement final acceptance evaluation**

Fail unless all 11 approved design acceptance criteria are present and pass.
Also fail on stale source commit, changed signature, changed hash, missing real
Windows 10 evidence, missing Windows 11 evidence, test mode, manual certificate
step, more than one UAC sequence, or any open rollback/recovery record.

- [ ] **Step 3: Generate release inventory and checksums**

Copy the exact accepted files to a new release directory using `CreateNew`,
re-hash afterward, generate `SHA256SUMS.txt`, and write
`windows-0.2.0-internal-release.json` with candidate hash, source/workflow
provenance, signers, acceptance evidence hashes, and promotion timestamp.

- [ ] **Step 4: Write release notes with honest boundaries**

Name supported OS range, x64/workstation scope, exact app/driver versions,
normal Setup EXE workflow, known unknown-publisher state if self-signed, and the
real matrix actually executed. Do not claim Store/Windows Update distribution,
Windows Server, ARM64, or Microsoft .NET support for Windows 10.

- [ ] **Step 5: Run final verification and commit promotion metadata**

```bash
node --test Windows/tools/tests/windows-release-promotion.contract.test.mjs
pwsh -NoProfile -File Windows/tools/promote-windows-release.ps1 -CandidateFile Windows/release/windows-0.2.0-internal-candidate.json -ArtifactDirectory artifacts/windows-0.2.0-candidate -EvidenceDirectory artifacts/windows-0.2.0-evidence -ReleaseDirectory artifacts/windows-0.2.0-internal-release
git diff --check
git add Windows/tools/promote-windows-release.ps1 Windows/tools/tests/windows-release-promotion.contract.test.mjs Windows/release/windows-0.2.0-internal-release.json docs/releases/windows-0.2.0-internal.md
git commit -m "release: promote Windows 0.2.0 internal A1"
```

- [ ] **Step 6: Independently re-download and verify handoff**

From the final handoff location, download the Setup EXE and release inventory
into a clean directory. Verify SHA-256, Authenticode, embedded payload hashes,
MSIX identity/signature, Microsoft driver catalog/membership, release manifest,
and source/workflow provenance. This is the final proof that the distributed
file is the accepted file.
