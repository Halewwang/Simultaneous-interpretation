# EMKE Translation Windows 10-11 Application Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a signed `0.2.0-internal` x64 MSIX whose package, managed runtime, embedded metadata, tooling, and CI consistently accept Windows 10 build `19045` through current Windows 11 while rejecting older builds, Server product types, and non-x64 hosts.

**Architecture:** `Windows/version.json` is the release source of truth. Packaging scripts copy its values into the MSIX manifest and embedded compatibility resource; managed code parses the embedded values and evaluates a dedicated workstation/x64/build gate before driver, audio, or network startup. `19041` is the compile-time API contract, `19045` is the product runtime floor, and `26200` is evidence-only `MaxVersionTested`.

**Tech Stack:** .NET 10, C# 14, WPF, MSTest, MSIX, PowerShell 7, Node.js contract tests, GitHub Actions

## Global Constraints

- Work only in `.worktrees/windows-internal-msix` on `codex/windows-internal-msix`.
- Preserve macOS `v0.2.4` / `3d81733` behavior and do not modify macOS product code.
- Keep Windows version `0.2.0`, MSIX version `0.2.0.0`, architecture `x64`, channel `internal`.
- Use `net10.0-windows10.0.19041.0` for compilation and build `19045` for product admission.
- Treat `MaxVersionTested=10.0.26200.0` as evidence, never as an upper install bound.
- Reject Windows Server even when its build number is high enough.
- Run each stated RED test before its production change and each GREEN test after it.
- Do not claim ordinary Windows 10 as Microsoft-supported .NET 10 evidence; record it only as EMKE internal compatibility after real-machine acceptance.

---

### Task 1: Make Release Metadata the Single Compatibility Source

**Files:**

- Modify: `Windows/version.json`
- Modify: `Windows/packaging/compatibility.internal.json`
- Modify: `Windows/packaging/channels.json`
- Modify: `Windows/tools/resolve-version.ps1`
- Modify: `Windows/tools/tests/windows-version.contract.test.mjs`
- Modify: `Windows/tests/EMKE.Integration.Tests/VersionMetadataTests.cs`

- [ ] **Step 1: Write the failing metadata contract**

Change the Node and managed tests to require exactly:

```json
{
  "productVersion": "0.2.0",
  "packageVersion": "0.2.0.0",
  "minimumWindowsBuild": 19045,
  "minimumWindowsApiContract": "10.0.19041.0",
  "maximumVersionTested": "10.0.26200.0",
  "architecture": "x64",
  "channel": "internal"
}
```

Also require `driverAbiVersion=1`, `minimumDriverVersion=1.0.0.2`,
`recommendedDriverVersion=1.0.0.2`, and reject string/fractional build values.

Run:

```bash
node --test Windows/tools/tests/windows-version.contract.test.mjs
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~VersionMetadataTests"
```

Expected: FAIL because the repository still declares version `0.1.0`, build
`26200`, and driver `0.1.0`.

- [ ] **Step 2: Update metadata and remove the resolver's independent floor**

Make `resolve-version.ps1` validate relationships instead of a literal
`26200`:

```powershell
if ($minimumWindowsBuild -ne 19045) {
    throw 'minimumWindowsBuild must be 19045 for Windows 0.2.0.'
}
if ($minimumWindowsApiContract -ne '10.0.19041.0') {
    throw 'minimumWindowsApiContract must be 10.0.19041.0.'
}
if ($maximumVersionTested -ne '10.0.26200.0') {
    throw 'maximumVersionTested must be 10.0.26200.0.'
}
```

Return all three values in the resolver result so downstream scripts never
reconstruct them.

- [ ] **Step 3: Prove one-source metadata behavior**

```bash
node --test Windows/tools/tests/windows-version.contract.test.mjs
pwsh -NoProfile -File Windows/tools/resolve-version.ps1 -VersionFile Windows/version.json
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~VersionMetadataTests"
git diff --check
```

Expected: PASS; the resolver reports `19045`, `10.0.19041.0`, and
`10.0.26200.0` from metadata.

- [ ] **Step 4: Commit the metadata boundary**

```bash
git add Windows/version.json Windows/packaging/compatibility.internal.json Windows/packaging/channels.json Windows/tools/resolve-version.ps1 Windows/tools/tests/windows-version.contract.test.mjs Windows/tests/EMKE.Integration.Tests/VersionMetadataTests.cs
git commit -m "build: define Windows 10-11 compatibility metadata"
```

### Task 2: Retarget the Managed Application and Runtime Gate

**Files:**

- Modify: `Windows/Directory.Build.props`
- Modify: `Windows/src/EMKE.Core/CompatibilityManifest.cs`
- Modify: `Windows/src/EMKE.Application/TranslationRuntime.cs`
- Modify: `Windows/src/EMKE.Windows.App/Bootstrap/ProductionAppAdapterFactory.cs`
- Create: `Windows/src/EMKE.Platform/Compatibility/WindowsHostCompatibilityProbe.cs`
- Modify: `Windows/tests/EMKE.Application.Tests/CompatibilityGateTests.cs`
- Modify: `Windows/tests/EMKE.Windows.App.Tests/CompatibilityManifestResourceTests.cs`
- Modify: `Windows/tests/EMKE.Windows.App.Tests/ProductionCompositionTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/WindowsHostCompatibilityProbeTests.cs`

- [ ] **Step 1: Add boundary and product-type RED tests**

Specify a platform-neutral evidence record and probe interface:

```csharp
public sealed record WindowsHostEvidence(
    bool IsWindows,
    int Build,
    Architecture Architecture,
    byte ProductType);

public interface IWindowsHostEvidenceSource
{
    WindowsHostEvidence Read();
}
```

Tests must prove:

```text
build 19044 -> unsupportedWindowsBuild
build 19045 -> allowed to continue to driver check
x86/arm64 -> unsupportedWindowsArchitecture
VER_NT_WORKSTATION (1) -> architecture/build evaluation continues
VER_NT_DOMAIN_CONTROLLER (2) -> unsupportedWindowsProductType
VER_NT_SERVER (3) -> unsupportedWindowsProductType
non-Windows -> unsupportedWindowsPlatform
```

Run:

```bash
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~WindowsHostCompatibilityProbeTests"
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~ProductionCompositionTests"
```

Expected: FAIL because `Windows25H2BuildGate` only checks build `26200` and
does not inspect architecture or product type.

- [ ] **Step 2: Implement the neutral host probe and metadata-backed gate**

Implement `WindowsHostCompatibilityProbe` using `RtlGetVersion` (build) and
`GetProductInfo`/`OSVERSIONINFOEX.wProductType` (workstation). The public gate
must accept `CompatibilityManifest` and `IWindowsHostEvidenceSource`; it must
not embed `19045`.

Replace `Windows25H2BuildGate` with composition of:

```csharp
new WindowsHostBuildGate(
    compatibilityManifest,
    new WindowsHostCompatibilityProbe())
```

Ensure `TranslationRuntime.StartAsync` calls the host gate before
`DriverManager.CheckCompatibilityAsync`, secret loading, session creation, or
`Audio.StartAsync`.

- [ ] **Step 3: Parse the minimum from embedded compatibility JSON**

Remove `InternalMinimumWindowsBuild` from `CompatibilityManifest`. Parse
`minimumWindowsBuild` as a required positive integer and reject a missing,
string, fractional, zero, or negative value. Add the compile contract and
maximum-tested values only if consumers need them; do not duplicate release
constants in C#.

- [ ] **Step 4: Retarget all managed projects**

Set:

```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<PlatformTarget>x64</PlatformTarget>
<EnableWindowsTargeting>true</EnableWindowsTargeting>
```

Run platform compatibility analysis as errors for new unsupported API usage.

- [ ] **Step 5: Prove the runtime admission boundary**

```bash
dotnet test Windows/tests/EMKE.Application.Tests/EMKE.Application.Tests.csproj --configuration Release --filter "FullyQualifiedName~Compatibility"
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~CompatibilityManifestResourceTests|FullyQualifiedName~ProductionCompositionTests"
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~WindowsHostCompatibilityProbeTests"
dotnet build Windows/EMKE.Windows.slnx --configuration Release
git diff --check
```

Expected: PASS; tests prove `19044` rejection, `19045` admission, and Server
rejection before any driver/audio/network action.

- [ ] **Step 6: Commit the managed compatibility gate**

```bash
git add Windows/Directory.Build.props Windows/src/EMKE.Core/CompatibilityManifest.cs Windows/src/EMKE.Application/TranslationRuntime.cs Windows/src/EMKE.Windows.App/Bootstrap/ProductionAppAdapterFactory.cs Windows/src/EMKE.Platform/Compatibility Windows/tests/EMKE.Application.Tests/CompatibilityGateTests.cs Windows/tests/EMKE.Windows.App.Tests/CompatibilityManifestResourceTests.cs Windows/tests/EMKE.Windows.App.Tests/ProductionCompositionTests.cs Windows/tests/EMKE.Integration.Tests/WindowsHostCompatibilityProbeTests.cs
git commit -m "feat: admit supported Windows 10 and 11 workstations"
```

### Task 3: Generate and Verify the Windows 10-Compatible MSIX Manifest

**Files:**

- Modify: `Windows/packaging/App/AppxManifest.internal.xml`
- Modify: `Windows/tools/package-msix.ps1`
- Modify: `Windows/tools/verify-msix.ps1`
- Modify: `Windows/tools/tests/msix-packaging.contract.test.mjs`
- Modify: `Windows/tests/EMKE.Integration.Tests/MsixMetadataTests.cs`

- [ ] **Step 1: Add RED package tests for the install floor**

Require the staged/generated manifest to contain:

```xml
<TargetDeviceFamily
  Name="Windows.Desktop"
  MinVersion="10.0.19045.0"
  MaxVersionTested="10.0.26200.0" />
```

Also require identity version `0.2.0.0`, processor architecture `x64`, and
publisher equality with the signing certificate subject.

```bash
node --test Windows/tools/tests/msix-packaging.contract.test.mjs
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~MsixMetadataTests"
```

Expected: FAIL on the existing `10.0.26200.0` minimum.

- [ ] **Step 2: Generate both version fields from resolver output**

Remove literal version/build assertions from `package-msix.ps1` and
`verify-msix.ps1`. They must compare the generated manifest with the resolver
object and fail if template, embedded JSON, or packaged manifest diverges.

- [ ] **Step 3: Add negative tamper cases**

The Node contract must mutate each value separately and prove rejection for:

```text
MinVersion 19044
MinVersion 26200
MaxVersionTested 26100
Architecture neutral/arm64/x86
Version 0.1.0.0
Publisher mismatch
embedded compatibility minimum 26200
```

- [ ] **Step 4: Build and independently verify the MSIX on Windows CI**

```powershell
$pfxPath = $env:EMKE_INTERNAL_PFX_PATH
$passwordVariable = 'EMKE_INTERNAL_PFX_PASSWORD'
$msix = 'Windows/artifacts/msix/EMKE-Translation-Windows-0.2.0-internal-x64.msix'
pwsh -NoProfile -File Windows/tools/package-msix.ps1 -Configuration Release -PfxPath $pfxPath -PasswordEnvironmentVariable $passwordVariable
pwsh -NoProfile -File Windows/tools/verify-msix.ps1 -PackagePath $msix
Get-AuthenticodeSignature $msix | Format-List Status,SignerCertificate
```

Expected: verifier reports exact identity/version/architecture, `19045`
minimum, `26200` maximum-tested, embedded metadata agreement, and a valid
signature.

- [ ] **Step 5: Commit the MSIX targeting change**

```bash
git add Windows/packaging/App/AppxManifest.internal.xml Windows/tools/package-msix.ps1 Windows/tools/verify-msix.ps1 Windows/tools/tests/msix-packaging.contract.test.mjs Windows/tests/EMKE.Integration.Tests/MsixMetadataTests.cs
git commit -m "build: package Windows 10 compatible internal MSIX"
```

### Task 4: Align CI and Produce the Milestone-1 Preview

**Files:**

- Modify: `.github/workflows/windows-internal-msix.yml`
- Modify: `.github/workflows/windows-runtime.yml`
- Create: `docs/quality/windows-0.2.0-application-preview-evidence.md`

- [ ] **Step 1: Add RED workflow source checks**

Extend `Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs`
to forbid an independent `26200` install floor and require the workflow to
read `minimumWindowsBuild` from `resolve-version.ps1`.

```bash
node --test Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs
```

Expected: FAIL on the workflow's current literal build check.

- [ ] **Step 2: Make hosted install checks metadata-driven**

Hosted runners may build and inspect the package even when their build differs
from the real acceptance matrix. The install job must print actual build,
architecture, product type, exact MSIX SHA-256, signature status, install
result, package identity, smoke result, and uninstall result.

- [ ] **Step 3: Run the complete milestone gate**

```bash
node --test Windows/tools/tests/windows-version.contract.test.mjs Windows/tools/tests/msix-packaging.contract.test.mjs Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs
dotnet build Windows/EMKE.Windows.slnx --configuration Release
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build
git diff --check
rg -n "Minimum(Build|WindowsBuild)\s*=\s*26200|minimumWindowsBuild[^\n]*26200|MinVersion=\"10\.0\.26200" Windows .github/workflows
```

Expected: all tests pass and the final search returns no independent
install/runtime floor. References to `maximumVersionTested` or the Windows 11
25H2 acceptance lane are allowed.

- [ ] **Step 4: Record exact preview evidence and limitations**

The evidence file must include source commit, workflow run, artifact name,
SHA-256, signing subject/thumbprint, package identity/version, and install
proof. State explicitly that this milestone does not prove a Microsoft-signed
driver, four endpoints, live translation, Setup EXE, or physical Windows 10
acceptance.

- [ ] **Step 5: Commit the application preview gate**

```bash
git add .github/workflows/windows-internal-msix.yml .github/workflows/windows-runtime.yml Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs docs/quality/windows-0.2.0-application-preview-evidence.md
git commit -m "ci: verify Windows 10 application preview"
```
