# EMKE Translation Windows 10-11 Driver and A1 Signing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build driver package `1.0.0.2` for Windows 10 build `19045` through Windows 11, explicitly target KMDF `1.31`, submit the exact INF/SYS/CAT package to Microsoft Hardware Dev Center, and verify the returned Microsoft-signed bytes without test mode or Secure Boot changes.

**Architecture:** The existing SYSVAD-derived WaveRT driver and four-endpoint ABI stay unchanged. The source INF declares the `19045` model floor and the project pins KMDF `1.31`; CI stages a flat three-file submission package and records immutable hashes. A separate import job accepts only the Hardware Dev Center result that matches the submitted INF and SYS hashes, validates the Microsoft catalog signature and membership, then promotes those exact bytes for Setup bundling.

**Tech Stack:** C++20, WDK/MSBuild, KMDF 1.31, INF2CAT, SignTool/WinTrust, PowerShell 7, Node.js contract tests, GitHub Actions, Microsoft Hardware Dev Center

## Global Constraints

- Driver ABI stays `1`; package version becomes `1.0.0.2`.
- Keep exactly four endpoint roles and the current hardware ID
  `ROOT\EMKEVIRTUALAUDIO`.
- Keep the pinned WDK build toolchain even though runtime KMDF is `1.31`.
- Do not enable `TESTSIGNING`, edit BCD, disable Secure Boot, or disable Memory
  Integrity in any script, workflow, documentation, or acceptance procedure.
- Do not call a self-signed/test-signed driver A1-installable.
- Never put portal credentials, EV private keys, tokens, or signing secrets in
  repository files or artifacts.
- The Hardware Dev Center response is an external release gate; no local or
  hosted simulation can replace it.
- All package verification operates on resolved/stamped INF, SYS, and CAT
  bytes, not source text alone.

---

### Task 1: Retarget INF, Version, and KMDF Contract

**Files:**

- Modify: `Windows/driver/EMKE.VirtualAudio/EMKE.VirtualAudio.inf`
- Modify: `Windows/driver/EMKE.VirtualAudio/EMKE.VirtualAudio.vcxproj`
- Modify: `Windows/driver/tests/driver-contract.test.mjs`
- Modify: `Windows/driver/tests/package-boundary.test.mjs`
- Modify: `Windows/tools/validate-driver-contract.mjs`
- Modify: `Windows/version.json`
- Modify: `Windows/packaging/compatibility.internal.json`

- [ ] **Step 1: Write the RED source and stamped-package contract**

Require:

```text
DriverVer = 08/01/2026,1.0.0.2
model decoration = NTamd64.10.0...19045
resolved KmdfLibraryVersion = 1.31
driver ABI = 1
hardware ID = ROOT\EMKEVIRTUALAUDIO
exact endpoint role count = 4
```

The boundary test must continue to reject `$KMDFVERSION$` in a staged package
and must reject `1.32`, `1.33`, or later.

```bash
node --test Windows/driver/tests/driver-contract.test.mjs Windows/driver/tests/package-boundary.test.mjs
```

Expected: FAIL on version `1.0.0.1`, build `26200`, and stamped KMDF `1.33`.

- [ ] **Step 2: Set explicit runtime targeting**

Change the INF manufacturer/model section to:

```ini
%ManufacturerName%=EMKE,NTamd64.10.0...19045

[EMKE.NTamd64.10.0...19045]
%EMKE.VirtualAudio.DeviceDesc%=EMKE.VirtualAudio,ROOT\EMKEVIRTUALAUDIO
```

Set an explicit KMDF project/INF stamp input that resolves
`KmdfLibraryVersion=1.31`; do not rely on the installed WDK default. Preserve
the current `WindowsTargetPlatformVersion` only as compiler/tool selection.

- [ ] **Step 3: Prove version and ABI agreement**

`validate-driver-contract.mjs` must compare driver metadata to
`Windows/version.json` and compatibility JSON, including package version,
minimum Windows build, ABI, hardware ID, and endpoint roles.

- [ ] **Step 4: Run and commit the source contract**

```bash
node --test Windows/driver/tests/driver-contract.test.mjs Windows/driver/tests/package-boundary.test.mjs
node Windows/tools/validate-driver-contract.mjs
git diff --check
git add Windows/driver/EMKE.VirtualAudio/EMKE.VirtualAudio.inf Windows/driver/EMKE.VirtualAudio/EMKE.VirtualAudio.vcxproj Windows/driver/tests/driver-contract.test.mjs Windows/driver/tests/package-boundary.test.mjs Windows/tools/validate-driver-contract.mjs Windows/version.json Windows/packaging/compatibility.internal.json
git commit -m "build: target Windows 10 with KMDF 1.31"
```

### Task 2: Make Driver Lifecycle and Evidence Tools Metadata-Driven

**Files:**

- Modify: `Windows/tools/install-test-driver.ps1`
- Modify: `Windows/tools/uninstall-test-driver.ps1`
- Modify: `Windows/tools/collect-audio-evidence.ps1`
- Modify: `Windows/tools/verify-toolchain.ps1`
- Modify: `Windows/tools/tests/lab-driver-lifecycle.contract.test.mjs`
- Modify: `Windows/tools/tests/lab-driver-lifecycle.behavior.test.ps1`
- Modify: `Windows/tools/tests/lab-driver-lifecycle.validation.test.ps1`
- Modify: `Windows/tools/tests/audio-evidence-collector.contract.test.mjs`
- Modify: `Windows/tools/tests/audio-evidence-collector.behavior.test.ps1`
- Modify: `Windows/tools/tests/audio-evidence-collector.validation.test.ps1`

- [ ] **Step 1: Add RED boundary cases**

Tests must prove build `19044` is rejected, build `19045` is admitted, x86 and
ARM64 are rejected, and Server product types are rejected. INF parsing must
select `EMKE.NTamd64.10.0...19045` and version `1.0.0.2` from the trusted staged
file, not from caller input.

```powershell
pwsh -NoProfile -File Windows/tools/tests/lab-driver-lifecycle.behavior.test.ps1
pwsh -NoProfile -File Windows/tools/tests/lab-driver-lifecycle.validation.test.ps1
pwsh -NoProfile -File Windows/tools/tests/audio-evidence-collector.behavior.test.ps1
pwsh -NoProfile -File Windows/tools/tests/audio-evidence-collector.validation.test.ps1
```

Expected: FAIL because the tools embed `26200` and `1.0.0.1`.

- [ ] **Step 2: Load release metadata once per tool**

Dot-source or invoke `resolve-version.ps1` at process start and pass its
immutable values into validation functions. Remove script-scoped independent
minimums. Keep test seams by injecting a resolved metadata object, not by
overriding production constants.

- [ ] **Step 3: Add forbidden-operation guards**

Contract tests must fail if lifecycle scripts or workflows contain executable
uses of:

```text
bcdedit /set testsigning
TESTSIGNING ON
Disable-ComputerRestore
SecureBoot disabled
Memory Integrity disabled
```

Documentation may mention these only as forbidden behavior.

- [ ] **Step 4: Run and commit the tooling change**

```bash
node --test Windows/tools/tests/lab-driver-lifecycle.contract.test.mjs Windows/tools/tests/audio-evidence-collector.contract.test.mjs
pwsh -NoProfile -File Windows/tools/tests/lab-driver-lifecycle.behavior.test.ps1
pwsh -NoProfile -File Windows/tools/tests/lab-driver-lifecycle.validation.test.ps1
pwsh -NoProfile -File Windows/tools/tests/audio-evidence-collector.behavior.test.ps1
pwsh -NoProfile -File Windows/tools/tests/audio-evidence-collector.validation.test.ps1
git diff --check
git add Windows/tools/install-test-driver.ps1 Windows/tools/uninstall-test-driver.ps1 Windows/tools/collect-audio-evidence.ps1 Windows/tools/verify-toolchain.ps1 Windows/tools/tests
git commit -m "test: align driver tools with Windows 10 floor"
```

### Task 3: Build and Verify the Exact Hardware Dev Center Submission Package

**Files:**

- Modify: `Windows/tools/build-driver.ps1`
- Modify: `Windows/tools/stage-driver-package.mjs`
- Modify: `Windows/tools/verify-driver-package.ps1`
- Create: `Windows/tools/create-driver-submission.ps1`
- Create: `Windows/tools/tests/driver-submission.contract.test.mjs`
- Create: `Windows/tools/tests/driver-submission.validation.test.ps1`
- Modify: `.github/workflows/windows-audio.yml`
- Create: `docs/quality/windows-driver-submission-evidence.md`

- [ ] **Step 1: Write RED immutable-inventory tests**

Define the submission inventory as exactly one INF, one SYS, and one CAT, with
no PDB or nested directory. Require `driver-submission.json`:

```json
{
  "sourceCommitPattern": "^[0-9a-f]{40}$",
  "driverVersion": "1.0.0.2",
  "driverAbiVersion": 1,
  "minimumWindowsBuild": 19045,
  "kmdfLibraryVersion": "1.31",
  "files": [
    {"name": "EMKE.VirtualAudio.inf", "sha256Pattern": "^[0-9a-f]{64}$"},
    {"name": "EMKE.VirtualAudio.sys", "sha256Pattern": "^[0-9a-f]{64}$"},
    {"name": "EMKE.VirtualAudio.cat", "sha256Pattern": "^[0-9a-f]{64}$"}
  ]
}
```

Mutation tests must reject extra files, changed hashes, unresolved INF tokens,
wrong model decoration, wrong KMDF, and a CAT that does not contain the exact
INF/SYS members.

- [ ] **Step 2: Build with WDK and validate the resolved package**

On Windows CI:

```powershell
$stagedDriver = 'Windows/artifacts/driver/x64/Release'
$submission = 'artifacts/windows-driver-submission'
pwsh -NoProfile -File Windows/tools/build-driver.ps1 -Configuration Release -Platform x64
pwsh -NoProfile -File Windows/tools/verify-driver-package.ps1 $stagedDriver
pwsh -NoProfile -File Windows/tools/create-driver-submission.ps1 -PackageDirectory $stagedDriver -OutputDirectory $submission
```

`create-driver-submission.ps1` must copy verified bytes to a new directory,
write the inventory/provenance, then re-hash the destination.

- [ ] **Step 3: Produce a portal-ready archive without signing secrets**

Create one deterministic archive containing the verified package and its
manifest. Record the archive hash, workflow run ID, source commit, WDK version,
and package hashes in the evidence file. Do not include local test certificates
or private signing material.

- [ ] **Step 4: Run and commit the submission gate**

```bash
node --test Windows/driver/tests/driver-contract.test.mjs Windows/driver/tests/package-boundary.test.mjs Windows/tools/tests/driver-submission.contract.test.mjs
pwsh -NoProfile -File Windows/tools/tests/driver-submission.validation.test.ps1
git diff --check
git add Windows/tools/build-driver.ps1 Windows/tools/stage-driver-package.mjs Windows/tools/verify-driver-package.ps1 Windows/tools/create-driver-submission.ps1 Windows/tools/tests/driver-submission.contract.test.mjs Windows/tools/tests/driver-submission.validation.test.ps1 .github/workflows/windows-audio.yml docs/quality/windows-driver-submission-evidence.md
git commit -m "build: create Hardware Dev Center driver submission"
```

### Task 4: Import and Verify the Microsoft-Signed Result

**Files:**

- Create: `Windows/tools/import-microsoft-signed-driver.ps1`
- Create: `Windows/tools/tests/microsoft-signed-driver.contract.test.mjs`
- Create: `Windows/tools/tests/microsoft-signed-driver.validation.test.ps1`
- Modify: `Windows/src/EMKE.Platform/Driver/WindowsDriverManager.cs`
- Modify: `Windows/tests/EMKE.Integration.Tests/WindowsDriverManagerTests.cs`
- Create: `docs/quality/windows-microsoft-driver-signing-evidence.md`

- [ ] **Step 1: Write RED returned-package verification tests**

The importer accepts the original `driver-submission.json` and a portal result
directory. It must require the returned INF and SYS hashes to match the
submitted hashes exactly, allow the CAT to change, and require the returned CAT
to contain those exact members. It must reject missing/extra files, a changed
SYS/INF, expired/untrusted catalog chain, test signer, and non-Microsoft kernel
signing evidence.

- [ ] **Step 2: Define production trust policy explicitly**

Replace the current hard-coded `CN=EMKE Internal Test` driver signer check with
an injected policy:

```csharp
public interface IDriverCatalogTrustPolicy
{
    DriverCatalogTrustDecision Evaluate(
        string signerSubject,
        bool kernelPolicyValid,
        bool catalogMembersValid);
}
```

Production policy accepts the Microsoft-signed catalog under Windows kernel
trust and exact membership. Test policy remains test-only and cannot be
composed by `ProductionAppAdapterFactory`.

- [ ] **Step 3: Execute the external Hardware Dev Center gate**

From the protected release environment:

1. Upload the exact archive from Task 3.
2. Select targets covering Windows 10 22H2 x64 and required Windows 11 x64.
3. Complete attestation when accepted; otherwise complete the applicable
   HLK/WHQL path.
4. Download the portal result without renaming or editing its contents.
5. Run the importer and retain portal submission ID/status plus returned hashes.

Expected: this step remains BLOCKED until Hardware Dev Center returns signed
bytes. Do not fabricate or bypass it.

- [ ] **Step 4: Verify and promote exact returned bytes**

```powershell
$submissionManifest = 'artifacts/windows-driver-submission/driver-submission.json'
$portalResult = 'artifacts/windows-driver-portal-result'
$promotedDriver = 'artifacts/windows-driver-microsoft-signed'
pwsh -NoProfile -File Windows/tools/import-microsoft-signed-driver.ps1 -SubmissionManifest $submissionManifest -ReturnedPackageDirectory $portalResult -OutputDirectory $promotedDriver
pwsh -NoProfile -File Windows/tools/verify-driver-package.ps1 $promotedDriver
```

Record the exact CAT SHA-256, signer chain, submission ID, source commit,
INF/SYS hashes, and verification host build. The promoted directory becomes
the sole driver input to Setup packaging.

- [ ] **Step 5: Run and commit verification code and completed evidence**

```bash
node --test Windows/tools/tests/microsoft-signed-driver.contract.test.mjs
pwsh -NoProfile -File Windows/tools/tests/microsoft-signed-driver.validation.test.ps1
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~WindowsDriverManagerTests"
git diff --check
git add Windows/tools/import-microsoft-signed-driver.ps1 Windows/tools/tests/microsoft-signed-driver.contract.test.mjs Windows/tools/tests/microsoft-signed-driver.validation.test.ps1 Windows/src/EMKE.Platform/Driver/WindowsDriverManager.cs Windows/tests/EMKE.Integration.Tests/WindowsDriverManagerTests.cs docs/quality/windows-microsoft-driver-signing-evidence.md
git commit -m "feat: trust Microsoft-signed EMKE driver package"
```

### Task 5: Prove Secure Boot and Memory Integrity Installation

**Files:**

- Modify: `Windows/tools/collect-audio-evidence.ps1`
- Modify: `Windows/tools/tests/audio-evidence-collector.validation.test.ps1`
- Create: `docs/quality/windows-driver-real-machine-evidence.md`

- [ ] **Step 1: Extend evidence collection**

Collect only non-secret evidence for OS edition/product type/build,
architecture, Secure Boot, Memory Integrity, driver package identity/version,
Microsoft catalog signer, kernel trust result, root devnode, service state,
KMDF runtime evidence, and four endpoint roles.

- [ ] **Step 2: Execute exact-byte install matrix**

Install the promoted driver on clean Windows 10 `19045` x64 and representative
Windows 11 hosts while Secure Boot and Memory Integrity remain on. Reboot only
when Windows reports it is required. Verify load, four endpoints, uninstall,
and reinstall using the exact promoted hashes.

- [ ] **Step 3: Fail release on any trust or endpoint deviation**

The evidence validator must reject test mode, Secure Boot off, Memory Integrity
off, non-Microsoft catalog trust, mismatched hash/version/ABI, missing endpoint,
or non-workstation host.

- [ ] **Step 4: Record proof boundary and commit**

```bash
pwsh -NoProfile -File Windows/tools/tests/audio-evidence-collector.validation.test.ps1
git diff --check
git add Windows/tools/collect-audio-evidence.ps1 Windows/tools/tests/audio-evidence-collector.validation.test.ps1 docs/quality/windows-driver-real-machine-evidence.md
git commit -m "test: verify A1 driver on Windows 10 and 11"
```

Do not mark this plan complete until the evidence file names the exact signed
artifact hashes and real machines used.
