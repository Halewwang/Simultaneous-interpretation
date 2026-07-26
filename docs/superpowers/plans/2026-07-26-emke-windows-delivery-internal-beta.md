# EMKE Windows Delivery and Internal Beta Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce signed, independently versioned Windows application and driver artifacts, prove clean install/update/recovery/uninstall, and complete the Windows Internal Beta hardware and real-meeting exit matrix.

**Architecture:** The WPF application ships as an x64 self-contained signed MSIX with a channel-specific `.appinstaller` feed. The virtual audio driver ships as a separate signed package plus an explicit elevated installer/recovery tool. A compatibility gate binds app version, contract version, settings schema, driver ABI/version, package hash, and release channel. Windows releases use `windows-vMAJOR.MINOR.PATCH` and never depend on macOS release promotion.

**Tech Stack:** .NET 10 self-contained x64, MSIX, App Installer, MakeAppx, SignTool, C# elevated driver installer, SetupAPI/NewDev, PowerShell 7, GitHub Actions, Windows 11 25H2 physical lab

## Global Constraints

- Windows release tags are `windows-vMAJOR.MINOR.PATCH`; macOS keeps `vMAJOR.MINOR.PATCH`.
- Windows application, driver, feed, package identity, certificate, and release notes are independent of macOS.
- Channels are `internal`, `beta`, and `stable`; identities and feeds never auto-cross channels.
- Initial delivery target is `internal`, version `0.1.0`, x64, Windows build 26200+.
- App package and driver package are separate artifacts and separate update operations.
- MSIX installation/update is per user and does not install or mutate the driver.
- Driver installation/update/uninstall is explicit, displays impact/version, safely stops translation, and requires UAC.
- The app validates embedded compatibility data before translation start.
- Remote feeds cannot lower embedded minimum driver ABI/version or replace an untrusted package hash.
- Every downloadable artifact uses HTTPS, SHA-256, and Authenticode/catalog signature verification.
- Signing keys, certificate passwords, private certificates, API keys, endpoint IDs, recordings, and user data are never committed or uploaded as unrestricted CI artifacts.
- Internal test/attestation signing is not described as Windows Certified. Stable waits for HLK/WHCP.
- CI/build proof, install proof, endpoint proof, meeting proof, and listening proof remain separate.
- A release is not complete until the exact artifact paths, hashes, versions, signatures, feed URLs, and evidence bundle are recorded.

---

### Task 1: Establish Independent Windows Version and Channel Metadata

**Files:**
- Create: `Windows/version.json`
- Create: `Windows/packaging/channels.json`
- Create: `Windows/packaging/compatibility.internal.json`
- Create: `Windows/tools/resolve-version.ps1`
- Create: `Windows/tests/EMKE.Integration.Tests/VersionMetadataTests.cs`

**Interfaces:**
- One version file drives assemblies, MSIX, driver installer, compatibility, and release filenames.
- Driver version remains separately updatable but is bound by compatibility metadata.

- [ ] **Step 1: Write failing metadata tests**

Assert:

```text
productVersion is SemVer without prerelease for Internal 0.1.0
packageVersion is four numeric components
tag is exactly windows-v + productVersion
contractVersion == 1
settingsSchemaVersion == 1
driverAbiVersion == 1
minimum OS build == 26200
architecture == x64
channel exists in channels.json
macOS v* tag is rejected as a Windows release tag
```

- [ ] **Step 2: Create version metadata**

`Windows/version.json`:

```json
{
  "productVersion": "0.1.0",
  "packageVersion": "0.1.0.0",
  "contractVersion": 1,
  "settingsSchemaVersion": 1,
  "driverAbiVersion": 1,
  "minimumWindowsBuild": 26200,
  "architecture": "x64",
  "channel": "internal"
}
```

`Windows/packaging/channels.json` defines:

```text
internal -> package identity EMKE.Translation.Internal
beta -> package identity EMKE.Translation.Beta
stable -> package identity EMKE.Translation
```

Each channel has its own application ID, credential target suffix, mutex/pipe suffix, display-name suffix, update-feed path, and driver-feed path.

- [ ] **Step 3: Create embedded compatibility metadata**

`compatibility.internal.json` contains:

```json
{
  "appVersion": "0.1.0",
  "contractVersion": 1,
  "settingsSchemaVersion": 1,
  "driverAbiVersion": 1,
  "minimumDriverVersion": "0.1.0",
  "recommendedDriverVersion": "0.1.0",
  "driverPackageVersion": "0.1.0",
  "driverPackageSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "channel": "internal"
}
```

The all-zero example hash must cause packaging to fail. The package script replaces it in a generated staging copy after the signed driver bundle exists; it never edits the committed template.

- [ ] **Step 4: Implement version resolver**

`resolve-version.ps1` validates the JSON and emits an object with:

```text
ProductVersion
PackageVersion
ExpectedTag
PackageIdentity
Channel
CredentialTarget
AppInstallerPath
DriverFeedPath
```

`-RequireTag` compares `GITHUB_REF_NAME` or an explicit `-Tag` with `ExpectedTag`.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~VersionMetadata"
pwsh Windows/tools/resolve-version.ps1 -VersionFile Windows/version.json
git add Windows/version.json Windows/packaging Windows/tools/resolve-version.ps1 Windows/tests/EMKE.Integration.Tests
git commit -m "build: define independent Windows release metadata"
```

### Task 2: Implement Runtime CompatibilityGate

**Files:**
- Create: `Windows/src/EMKE.Application/Compatibility/CompatibilityGate.cs`
- Create: `Windows/src/EMKE.Core/CompatibilityManifest.cs`
- Create: `Windows/src/EMKE.Platform/Driver/WindowsDriverManager.cs`
- Create: `Windows/src/EMKE.Platform/Driver/DriverPackageVerifier.cs`
- Create: `Windows/tests/EMKE.Application.Tests/CompatibilityGateTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/DriverPackageVerifierTests.cs`
- Modify: `Windows/src/EMKE.Windows.App/EMKE.Windows.App.csproj`

**Interfaces:**
- Gate input: embedded manifest plus installed driver evidence.
- Gate output: allowed, stable reason, and recommended repair/update action.

- [ ] **Step 1: Write failing shared compatibility tests**

Drive every case in `Shared/TestVectors/Settings/compatibility-gate.json`. Exact stable reasons:

```text
compatible
compatibleUpdateRecommended
driverMissing
driverSignatureInvalid
driverAbiMismatch
driverBelowMinimum
virtualEndpointsIncomplete
unsupportedWindowsBuild
```

- [ ] **Step 2: Implement the pure gate**

Evaluation order:

```text
OS build
driver present
signature valid
ABI equal
version >= minimum
four technical endpoint roles present
version < recommended warning
```

The gate has no network access and does not install anything.

- [ ] **Step 3: Embed generated compatibility metadata**

The packaging build supplies a generated `compatibility.json` as a WPF resource. On non-packaged developer builds, use an explicit debug resource generated from the same template and driver hash; never fall back to “allow any driver.”

- [ ] **Step 4: Implement driver/package verification**

`WindowsDriverManager` reports:

```text
root devnode hardware ID
driver file version
driver ABI property
catalog signer and chain status
four endpoint-role presence
current endpoint states
```

`DriverPackageVerifier` checks:

```text
HTTPS origin
expected exact byte length when feed supplies it
SHA-256 equals embedded manifest
installer Authenticode chain valid
catalog Microsoft/test signer matches current channel policy
INF hardware ID and ABI match
```

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Application.Tests/EMKE.Application.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~CompatibilityGate"
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~DriverPackageVerifier"
git add Windows/src Windows/tests
git commit -m "feat: enforce Windows app driver compatibility"
```

### Task 3: Build the Signed Driver Installer and Recovery Tool

**Files:**
- Create: `Windows/packaging/DriverInstaller/EMKE.DriverInstaller.csproj`
- Create: `Windows/packaging/DriverInstaller/app.manifest`
- Create: `Windows/packaging/DriverInstaller/Program.cs`
- Create: `Windows/packaging/DriverInstaller/DriverOperations.cs`
- Create: `Windows/packaging/DriverInstaller/PackageVerification.cs`
- Create: `Windows/packaging/DriverInstaller/InstallerJournal.cs`
- Create: `Windows/packaging/DriverInstaller/Strings.resx`
- Create: `Windows/packaging/DriverInstaller/Strings.zh-CN.resx`
- Create: `Windows/tests/EMKE.DriverInstaller.Tests/EMKE.DriverInstaller.Tests.csproj`
- Create: `Windows/tests/EMKE.DriverInstaller.Tests/DriverInstallerTests.cs`
- Create: `Windows/tools/package-driver-installer.ps1`

**Interfaces:**
- Commands: `install`, `upgrade`, `repair`, `restore`, `uninstall`, `verify`.
- Exit codes are stable and documented; output contains no user path or device ID.

- [ ] **Step 1: Write failing negative installer tests**

Use a fake `IDriverOperations` and synthetic packages:

```text
wrong SHA -> reject before elevation operation
invalid Authenticode -> reject
wrong hardware ID -> reject
wrong ABI -> reject
downgrade without restore intent -> reject
running translation -> request safe stop before driver mutation
compatible old driver remains installed if new install fails
uninstall requires explicit command and confirmation
```

- [ ] **Step 2: Implement elevated manifest and commands**

Set:

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

Before mutation, print/show:

```text
current driver version
requested driver version
reason
expected endpoint interruption
whether restart may be required
```

Require `--confirm` for install/upgrade/repair/restore/uninstall. `verify` remains read-only.

- [ ] **Step 3: Implement SetupAPI/NewDev operations**

Use Windows APIs rather than parsing localized `pnputil` output:

```text
DiInstallDriverW
SetupCopyOEMInfW when staging is required
SetupUninstallOEMInfW for the exact published OEM INF
Configuration Manager device rescan
WinVerifyTrust for installer
CryptCATAdmin/Catalog verification for driver files
```

Identify only `ROOT\EMKEVIRTUALAUDIO` and the exact OEM INF published by this package. Never remove a broad class of audio drivers.

- [ ] **Step 4: Implement rollback journal**

Before upgrade, copy the last compatible signed package into:

```text
%ProgramData%\EMKE Translation\DriverRecovery\0.1.0\
```

Write an atomic journal containing only version, package hash, OEM INF name, ABI, and UTC time. On failed new-driver activation:

```text
leave old compatible driver active if still present
otherwise reinstall the saved signed package
verify devnode and four endpoint roles
report restart required if activation cannot complete
```

- [ ] **Step 5: Package**

`package-driver-installer.ps1`:

```text
builds self-contained win-x64 single-file installer
copies signed INF/CAT/SYS beside it into a versioned bundle
signs installer executable
verifies every signature
computes bundle SHA-256
emits DriverInstaller-0.1.0-internal-x64.zip
emits driver-feed-entry.json
```

- [ ] **Step 6: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.DriverInstaller.Tests/EMKE.DriverInstaller.Tests.csproj --configuration Release
git add Windows/packaging/DriverInstaller Windows/tests/EMKE.DriverInstaller.Tests Windows/tools/package-driver-installer.ps1
git commit -m "feat: add signed Windows driver lifecycle tool"
```

### Task 4: Package the WPF Application as Signed MSIX

**Files:**
- Create: `Windows/packaging/App/AppxManifest.internal.xml`
- Create: `Windows/packaging/App/Assets/`
- Create: `Windows/packaging/App/EMKE.Translation.internal.appinstaller`
- Create: `Windows/tools/package-msix.ps1`
- Create: `Windows/tools/verify-msix.ps1`
- Create: `Windows/tests/EMKE.Integration.Tests/MsixMetadataTests.cs`

**Interfaces:**
- Output: `EMKE-Translation-Windows-0.1.0-internal-x64.msix`.
- Feed: channel-specific HTTPS `.appinstaller`.

- [ ] **Step 1: Write failing manifest tests**

Assert:

```text
Identity Name == EMKE.Translation.Internal
ProcessorArchitecture == x64
MinVersion build component is at least 26100
application runtime gate still requires 26200
Executable points to EMKE.Windows.App.exe
no runFullTrust capability unless proven necessary
no driver extension appears in MSIX
DisplayName includes Internal
Publisher is supplied by build and matches signing certificate subject
```

- [ ] **Step 2: Publish the self-contained app**

Run inside `package-msix.ps1`:

```powershell
dotnet publish Windows/src/EMKE.Windows.App/EMKE.Windows.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishReadyToRun=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

Copy only runtime output, localized resources, native audio DLL, assets, and generated compatibility metadata into a clean staging directory.

- [ ] **Step 3: Generate compatibility from signed driver bundle**

Read the exact driver bundle version and hash, generate the staging-only `compatibility.json`, validate it against `Shared/Contracts/v1/compatibility.schema.json`, and embed it before MakeAppx.

Fail if:

```text
hash is the committed example value
driver ABI differs from Windows/version.json
channel differs
minimum driver exceeds package driver version
contract version differs
```

- [ ] **Step 4: Pack and sign**

Use `MakeAppx.exe pack` and `SignTool.exe sign /fd SHA256`. Signing material arrives only through the protected CI certificate store or signing service; the script accepts a certificate thumbprint, never a PFX password argument.

- [ ] **Step 5: Generate `.appinstaller`**

The file:

```text
uses HTTPS URI from WINDOWS_UPDATE_BASE_URL
points to exact signed MSIX version and URI
checks for updates on launch
allows manual update checks through the same URI
does not reference the driver installer
```

- [ ] **Step 6: Verify**

`verify-msix.ps1` fails unless:

```text
MSIX signature chain is valid for the channel
publisher matches manifest
version/architecture/identity match Windows/version.json
compatibility resource exists and hash is non-example
native DLL is x64
PDB, test assembly, settings, credentials, and recordings are absent
.appinstaller URI/version match the MSIX
```

- [ ] **Step 7: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~MsixMetadata"
git add Windows/packaging/App Windows/tools/package-msix.ps1 Windows/tools/verify-msix.ps1 Windows/tests/EMKE.Integration.Tests
git commit -m "build: package signed Windows MSIX"
```

### Task 5: Implement App Update and Driver Repair UX

**Files:**
- Create: `Windows/src/EMKE.Platform/Updates/AppInstallerUpdateService.cs`
- Create: `Windows/src/EMKE.Platform/Updates/DriverUpdateFeed.cs`
- Create: `Windows/src/EMKE.Windows.App/Updates/UpdateViewModel.cs`
- Create: `Windows/src/EMKE.Windows.App/Updates/UpdateWindow.xaml`
- Create: `Windows/tests/EMKE.Integration.Tests/AppInstallerUpdateServiceTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/DriverUpdateFeedTests.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/UpdateViewModelTests.cs`

**Interfaces:**
- App update is non-elevated and independent.
- Driver repair/update launches the separately verified elevated tool only after user confirmation.

- [ ] **Step 1: Write failing app-update tests**

Prove:

```text
automatic and manual checks use the same service
wrong channel identity is rejected
lower/equal app version is ignored
available app update never forces driver update
running translation is not interrupted by background app check
```

- [ ] **Step 2: Write failing driver-feed tests**

Prove:

```text
feed must use HTTPS
feed channel must equal embedded channel
feed cannot lower minimum ABI/version
feed package version/hash must equal embedded allowed package
unknown extra package is ignored
signature/hash checked before offering UAC
```

- [ ] **Step 3: Implement user flow**

For driver repair/update:

```text
show reason/current/target version
request TranslationRuntime.StopAsync
verify stopped snapshot
verify package again
launch signed DriverInstaller with verb=runas and exact command
wait outside UI thread
re-enumerate driver and endpoints
run CompatibilityGate
offer recovery if failed
```

Cancel leaves current compatible driver untouched.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Update"
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Update"
git add Windows/src Windows/tests
git commit -m "feat: add independent Windows update channels"
```

### Task 6: Prove Clean Install, Upgrade, Recovery, and Uninstall

**Files:**
- Create: `Windows/tools/test-clean-install.ps1`
- Create: `Windows/tools/test-upgrade.ps1`
- Create: `Windows/tools/test-driver-recovery.ps1`
- Create: `Windows/tools/test-uninstall.ps1`
- Create: `docs/quality/windows-install-lifecycle-evidence.md`

**Interfaces:**
- Tests run on restorable Windows 11 25H2 physical/VM images.
- Evidence binds every result to artifact hashes.

- [ ] **Step 1: Prepare two signed test versions**

Build:

```text
app 0.1.0 -> app 0.1.1
driver 0.1.0 -> compatible driver 0.1.1
one intentionally invalid driver package with wrong hash
```

Use internal identities and signing policy only. Invalid package never leaves the restricted lab.

- [ ] **Step 2: Test clean install**

On a clean snapshot:

```text
install MSIX per user without UAC
launch app and observe driverMissing
download/verify driver bundle
confirm UAC
install driver
verify root devnode, ABI, signature, and four roles
complete onboarding
reboot and verify app/driver readiness
```

- [ ] **Step 3: Test independent app update**

Update app 0.1.0 → 0.1.1 through `.appinstaller`:

```text
no UAC
settings and credential retained
compatible driver untouched
app starts with same channel and contract
rollback/reinstall documented if App Installer rejects downgrade
```

- [ ] **Step 4: Test compatible driver update and failed update**

Update driver 0.1.0 → 0.1.1:

```text
translation safely stops
explicit UAC
new signature/version/ABI/endpoints verified
settings and app untouched
```

Then attempt the wrong-hash package. Expected: rejected before driver mutation; 0.1.1 remains active.

- [ ] **Step 5: Test recovery**

Inject activation failure after staging a new package. Expected: recovery restores the saved signed compatible driver or reports restart-required while preserving a recoverable package and journal.

- [ ] **Step 6: Test uninstall**

```text
uninstall MSIX leaves driver until explicit driver uninstall
explicit driver uninstall targets only ROOT\EMKEVIRTUALAUDIO and exact OEM INF
two meeting endpoints disappear
physical audio devices remain unchanged
reinstall succeeds
```

- [ ] **Step 7: Record evidence and commit**

`windows-install-lifecycle-evidence.md` records OS image, source commit, app/driver versions and hashes, signature result, each transition, restart, and rollback result.

```powershell
git add Windows/tools/test-*.ps1 docs/quality/windows-install-lifecycle-evidence.md
git commit -m "test: verify Windows install and recovery lifecycle"
```

### Task 7: Add Independent Windows Release Workflow

**Files:**
- Create: `.github/workflows/windows-release.yml`
- Create: `Windows/tools/write-release-manifest.ps1`
- Create: `Windows/packaging/release-manifest.schema.json`
- Create: `docs/releases/windows-internal-release-template.md`

**Interfaces:**
- Trigger: `windows-v*` only.
- Produces one restricted Internal GitHub release and two independent feeds.

- [ ] **Step 1: Add strict tag gate**

Workflow trigger:

```yaml
on:
  push:
    tags:
      - "windows-v*"
```

First job runs:

```powershell
pwsh Windows/tools/resolve-version.ps1 `
  -VersionFile Windows/version.json `
  -Tag $env:GITHUB_REF_NAME `
  -RequireTag
```

A macOS `v*` tag must never start this workflow.

- [ ] **Step 2: Build from a clean checkout**

Jobs:

```text
shared contract validation
.NET restore/build/test
C++ build/test
WDK driver build/static verification
driver signing and bundle
compatibility generation
MSIX publish/pack/sign
MSIX and driver verification
release manifest
```

Signing jobs use protected `windows-internal` environment and non-exportable certificate/signing-service credentials.

- [ ] **Step 3: Generate release manifest**

The manifest contains:

```text
source commit
tag
channel
app version/package identity/MSIX hash/signature signer
contract/settings versions
driver version/ABI/bundle hash/catalog signer
minimum Windows build/architecture
appinstaller URL
driver feed URL
evidence document hashes
```

Validate against `release-manifest.schema.json` and sign the manifest.

- [ ] **Step 4: Publish atomically**

Order:

```text
upload immutable versioned artifacts
verify downloaded hashes
publish driver feed entry
publish .appinstaller
create restricted Internal release
mark release manifest complete
```

If any verification fails, do not update current feed pointers.

- [ ] **Step 5: Add release notes template**

Sections:

```text
Windows scope and minimum OS
app and driver versions
contract version
installation steps
meeting device routing
known limitations
verification evidence by level
rollback/recovery
artifact hashes
```

- [ ] **Step 6: Dry-run and commit**

Run the workflow scripts locally with test certificates and a local HTTPS test feed. Do not publish a release.

```powershell
git add .github/workflows/windows-release.yml Windows/tools/write-release-manifest.ps1 Windows/packaging/release-manifest.schema.json docs/releases/windows-internal-release-template.md
git commit -m "ci: add independent Windows release pipeline"
```

### Task 8: Execute the Windows 11 25H2 Hardware Lab Matrix

**Files:**
- Create: `docs/quality/windows-internal-hardware-matrix.md`
- Create: `Windows/tools/run-endurance.ps1`
- Create: `Windows/tools/inject-runtime-fault.ps1`
- Create: `Windows/tools/collect-runtime-counters.ps1`

**Interfaces:**
- Test artifact is the exact signed Internal candidate.
- Hardware evidence is not generated from developer Debug builds.

- [ ] **Step 1: Prepare the matrix**

At least two physical machines with different audio chipset/vendor combinations. Across them cover:

```text
internal audio
USB microphone
USB output
Bluetooth headset
Secure Boot enabled
Windows build >= 26200
```

Record anonymized hardware class/vendor IDs, not serial numbers.

- [ ] **Step 2: Exercise device lifecycle**

For each relevant device:

```text
start/stop
hot unplug/replug
default device change with follow-default off
default device change with follow-default on
saved device missing before start
saved device disappears while running
sleep/resume
driver endpoint disable/enable
```

- [ ] **Step 3: Exercise failures**

Inject:

```text
network disconnect
DNS failure
authentication failure
endpoint/model failure
server error
blocked close
inbound queue full
outbound queue full
native device HRESULT
application crash
```

Run 100 outbound failure injections. Required: zero microphone leakage when explicit bypass is off.

- [ ] **Step 4: Run endurance**

Run:

```powershell
pwsh Windows/tools/run-endurance.ps1 -Hours 1 -Mode Normal
pwsh Windows/tools/run-endurance.ps1 -Hours 8 -Mode Normal
```

Collect every minute:

```text
working set
private bytes
managed heap
thread count
handle count
ring fill levels
queue depth
underrun/overflow/drop counters
snapshot version
```

Required: bounded queues/buffers and no sustained unexplained one-way working-set growth.

- [ ] **Step 5: Verify latency and 400 ms chunks**

Using the same service, device route, and synthetic markers as the macOS baseline:

```text
measure capture-to-output P50/P95
verify complete 400 ms translated chunks
compare Windows P95 with macOS
```

Required: Windows P95 is no more than 100 ms slower. State measurement uncertainty and sample count.

- [ ] **Step 6: Record and commit lab evidence**

```powershell
git add docs/quality/windows-internal-hardware-matrix.md Windows/tools/run-endurance.ps1 Windows/tools/inject-runtime-fault.ps1 Windows/tools/collect-runtime-counters.ps1
git commit -m "test: record Windows Internal hardware evidence"
```

### Task 9: Execute the Real Meeting and Human Listening Matrix

**Files:**
- Create: `docs/quality/windows-internal-meeting-matrix.md`
- Create: `docs/quality/windows-internal-listening-review.md`
- Create: `docs/quality/windows-internal-beta-exit.md`

**Interfaces:**
- Meeting apps: Feishu, DingTalk, Microsoft Teams.
- Each evidence row binds the exact application/driver build and recording bundle hash.

- [ ] **Step 1: Verify routing before every meeting**

For each app:

```text
meeting speaker = EMKE Virtual Speaker
meeting microphone = EMKE Virtual Microphone
EMKE input = real microphone
EMKE output = real headphones/speaker
```

An API connection check does not satisfy this gate.

- [ ] **Step 2: Run the complete scenario set**

In each meeting app verify:

```text
inbound translation
outbound translation
same-language local pass-through
inbound original bypass
outbound explicit original bypass
network failure
server error
physical input disconnect
physical output disconnect
stop
exit
```

Use at least zh↔en and de↔zh across the total matrix.

- [ ] **Step 3: Conduct blinded listening review**

Reviewers rate:

```text
completeness
no source/translation overlap
no clipping at chunk boundaries
speech intelligibility
latency acceptability
correct fail-open/fail-closed perception
```

Record whether the sample is Windows or macOS only after ratings are submitted. Do not commit recordings; commit artifact hashes and secure evidence locations.

- [ ] **Step 4: Complete exit criteria**

`windows-internal-beta-exit.md` must explicitly decide every criterion:

```text
100/100 outbound failure injections silent
8-hour bounded resource behavior
complete 400 ms translated chunks
no persistent normal-load underrun/overflow
P95 <= macOS P95 + 100 ms
Feishu passed
DingTalk passed
Teams passed
install passed
app upgrade passed
driver upgrade passed
recovery passed
bilingual recovery actions reviewed
automation/install/meeting/listening evidence separated
```

Any unmet item blocks Internal Beta promotion; it does not block continued Windows development.

- [ ] **Step 5: Commit observed evidence**

```powershell
git add docs/quality/windows-internal-meeting-matrix.md docs/quality/windows-internal-listening-review.md docs/quality/windows-internal-beta-exit.md
git commit -m "docs: record Windows Internal Beta acceptance"
```

### Task 10: Publish or Hold the Internal Candidate

**Files:**
- Modify: `docs/quality/windows-internal-beta-exit.md`
- Create: `docs/releases/windows-0.1.0-internal.md`

**Interfaces:**
- Publishes only if every required criterion is satisfied by the same candidate hashes.

- [ ] **Step 1: Run the final source/build/package gates**

```powershell
node Scripts/validate-shared-contracts.mjs
dotnet restore Windows/EMKE.Windows.slnx --locked-mode
dotnet build Windows/EMKE.Windows.slnx --configuration Release --no-restore
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release
pwsh Windows/tools/verify-driver-package.ps1 Windows/artifacts/driver/x64/Release
pwsh Windows/tools/verify-msix.ps1 Windows/artifacts/app/EMKE-Translation-Windows-0.1.0-internal-x64.msix
git diff --check
git status --short
```

- [ ] **Step 2: Reconcile artifact identity**

Verify that MSIX, driver bundle, compatibility manifest, release manifest, install evidence, lab evidence, and meeting evidence all reference the same:

```text
source commit
app version/hash
driver version/hash
contract version
channel
architecture
```

- [ ] **Step 3: Hold if any proof is missing**

If a criterion is missing, set the exit document to `HOLD`, list the exact missing evidence, and do not tag or update feeds.

- [ ] **Step 4: Publish if all proof is complete**

Only on `PASS`:

```powershell
git tag -s windows-v0.1.0 -m "EMKE Translation Windows 0.1.0 Internal"
git push origin HEAD
git push origin windows-v0.1.0
```

The tag starts the protected release workflow. Verify the workflow-produced hashes and feeds before distributing the Internal link.

- [ ] **Step 5: Record final release**

`docs/releases/windows-0.1.0-internal.md` records the immutable artifact URLs, hashes, signatures, exact installation sequence, routing instructions, proof boundaries, and recovery instructions.

```powershell
git add docs/quality/windows-internal-beta-exit.md docs/releases/windows-0.1.0-internal.md
git commit -m "docs: finalize Windows 0.1.0 Internal release"
git status --porcelain
```

Expected: empty status. macOS development and releases remain unaffected by this Windows decision.
