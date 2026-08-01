# EMKE Translation Windows Setup EXE Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce `EMKE-Translation-Setup-0.2.0-internal-x64.exe` as the normal tester entry point, requiring no manual certificate import and at most one narrowly scoped UAC elevation while safely installing the exact application MSIX and Microsoft-signed virtual-audio driver.

**Architecture:** A self-contained unelevated WPF/console bootstrapper embeds an immutable payload inventory, extracts verified files to a newly created version-scoped directory, records existing machine/user state, and launches one elevated helper with a signed request. The helper re-verifies the request and performs only exact certificate/driver machine changes. The original unelevated parent installs the per-user MSIX, verifies identity/endpoints, and owns transaction recovery. Rollback removes only state created by the current attempt.

**Tech Stack:** .NET 10 self-contained win-x64, C# 14, WPF, MSTest, Windows App Package APIs/PowerShell-free package deployment API, SetupAPI/NewDev, X509Store, WinVerifyTrust, UAC `runas`, SHA-256

## Global Constraints

- Bundle only the exact accepted `0.2.0.0` MSIX and Microsoft-signed driver
  hashes from prior plans.
- Internal self-signed application trust may add only the pinned public cert to
  `LocalMachine\TrustedPeople`; never bundle a PFX or private key.
- The elevated helper accepts no arbitrary command, path, certificate,
  publisher, package identity, hardware ID, or uninstall target.
- The parent stays unelevated so per-user MSIX installation targets the
  invoking user even when another administrator approves UAC.
- Never execute payloads from Downloads or an unverified temporary path.
- Reject reparse points, path escape, hash mismatch, signature mismatch,
  unsupported OS/architecture/product type, and incompatible pre-existing
  drivers before mutation.
- Rollback removes only exact state created by the current attempt.
- The Setup EXE may show an unknown-publisher warning until an application
  code-signing identity is available; it must not hide that boundary.

---

### Task 1: Scaffold the Setup Domain and State Machine

**Files:**

- Create: `Windows/src/EMKE.Setup/EMKE.Setup.csproj`
- Create: `Windows/src/EMKE.Setup/Program.cs`
- Create: `Windows/src/EMKE.Setup/SetupState.cs`
- Create: `Windows/src/EMKE.Setup/SetupManifest.cs`
- Create: `Windows/src/EMKE.Setup/SetupTransaction.cs`
- Create: `Windows/src/EMKE.Setup/SetupResult.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj`
- Create: `Windows/tests/EMKE.Setup.Tests/SetupManifestTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/SetupTransactionTests.cs`
- Modify: `Windows/EMKE.Windows.slnx`

- [ ] **Step 1: Write RED state-machine tests**

Define the only legal forward states:

```csharp
public enum SetupState
{
    Preflight,
    Verified,
    MachineChangesStarted,
    DriverReady,
    UserPackageReady,
    EndpointVerified,
    Complete,
    RollbackRequired,
}
```

Tests must reject skipped/reversed states and double mutation. Cancellation is
legal before `MachineChangesStarted`; afterward it transitions through
`RollbackRequired` unless Windows reports a resumable reboot requirement.

```bash
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release
```

Expected: FAIL because the Setup project does not exist.

- [ ] **Step 2: Define an immutable manifest**

Use constructor-validated records:

```csharp
public sealed record SetupPayload(
    string LogicalName,
    string FileName,
    long Length,
    string Sha256,
    SetupPayloadKind Kind);

public sealed record SetupManifest(
    string Channel,
    Version ProductVersion,
    string PackageFamilyName,
    string Publisher,
    int MinimumWindowsBuild,
    Architecture Architecture,
    string DriverHardwareId,
    Version DriverVersion,
    IReadOnlyList<SetupPayload> Payloads);
```

The manifest must require exactly one MSIX, one public CER only for self-signed
Internal trust, and one INF/SYS/CAT driver set. Reject duplicate logical names,
duplicate filenames, path separators, invalid hashes, zero lengths, wrong
identity, and unknown payload kinds.

- [ ] **Step 3: Implement pure transaction bookkeeping**

`SetupTransaction` records pre-existing certificate, driver package, device,
and MSIX booleans plus created-by-attempt flags. It yields rollback actions in
reverse order and never yields an action for a pre-existing component.

- [ ] **Step 4: Run and commit domain scaffolding**

```bash
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release
dotnet build Windows/EMKE.Windows.slnx --configuration Release
git diff --check
git add Windows/src/EMKE.Setup Windows/tests/EMKE.Setup.Tests Windows/EMKE.Windows.slnx
git commit -m "feat: define Windows Setup transaction"
```

### Task 2: Implement Preflight and Safe Payload Extraction

**Files:**

- Create: `Windows/src/EMKE.Setup/SetupPreflight.cs`
- Create: `Windows/src/EMKE.Setup/SetupPayloadVerifier.cs`
- Create: `Windows/src/EMKE.Setup/SetupExtractionDirectory.cs`
- Create: `Windows/src/EMKE.Setup/Platform/ISetupHostProbe.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/SetupPreflightTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/SetupPayloadVerifierTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/SetupExtractionDirectoryTests.cs`

- [ ] **Step 1: Write RED host and path-safety tests**

Prove rejection of build `19044`, non-x64, Windows Server, manifest/hash/length
tamper, duplicate file, reparse point at any path component, `..`, rooted path,
hard-link substitution, existing extraction directory, and an output path
outside the created root. Prove build `19045` workstation x64 is admitted.

- [ ] **Step 2: Create a unique, version-scoped extraction root**

Create a new directory below a fixed Setup-owned base using cryptographically
random bytes and `CreateNew` semantics. Open/verify files with handles that
disallow write/delete sharing, validate final resolved paths stay below the
root, and hash while copying embedded resources. Mark extracted files read-only
after verification.

- [ ] **Step 3: Verify signatures before any mutation**

Verify:

```text
MSIX Authenticode valid and Publisher equals manifest
CER SHA-256, subject, validity, thumbprint equal pinned manifest values
driver INF/SYS hashes equal Hardware Dev Center submission
driver CAT kernel trust valid and contains exact INF/SYS members
```

Return structured failures without printing secret or full local paths.

- [ ] **Step 4: Run and commit preflight**

```bash
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release --filter "FullyQualifiedName~SetupPreflightTests|FullyQualifiedName~SetupPayloadVerifierTests|FullyQualifiedName~SetupExtractionDirectoryTests"
git diff --check
git add Windows/src/EMKE.Setup/SetupPreflight.cs Windows/src/EMKE.Setup/SetupPayloadVerifier.cs Windows/src/EMKE.Setup/SetupExtractionDirectory.cs Windows/src/EMKE.Setup/Platform/ISetupHostProbe.cs Windows/tests/EMKE.Setup.Tests
git commit -m "feat: verify Setup host and immutable payloads"
```

### Task 3: Define and Authenticate the One-Shot Elevation Request

**Files:**

- Create: `Windows/src/EMKE.Setup/Elevated/SetupElevationRequest.cs`
- Create: `Windows/src/EMKE.Setup/Elevated/SetupElevationRequestCodec.cs`
- Create: `Windows/src/EMKE.Setup/Elevated/ElevatedHelperLauncher.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/SetupElevationRequestTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/ElevatedHelperLauncherTests.cs`

- [ ] **Step 1: Write RED request-confusion tests**

The request contains only manifest hash, transaction ID, absolute verified
extraction-root identity, expiry, nonce, allowed certificate thumbprint,
allowed driver hardware ID/version, and exact payload hashes. Tests must reject
unknown fields, duplicates, alternate encodings, expired/future timestamps,
replay, different root file identity, changed hash, changed command, or extra
path.

- [ ] **Step 2: Use a canonical authenticated request**

Serialize a versioned canonical binary format. Transfer it through a randomly
named one-shot pipe whose ACL permits only the invoking SID, Administrators,
and SYSTEM. After `runas`, bind the channel to the exact returned helper PID
with `GetNamedPipeClientProcessId`; both sides verify the peer image is the same
Setup EXE before the parent sends the request and a per-launch 256-bit MAC key.
Put no request data or key in command-line arguments, environment variables, or
disk. Clear the key after the authenticated result is verified.

- [ ] **Step 3: Launch exactly one constrained UAC helper**

Use `ProcessStartInfo.Verb="runas"`, `UseShellExecute=true`, the same Setup EXE,
and only a fixed `--elevated-helper-v1` switch plus random pipe name and nonce.
No generic subcommand/parser may be available in elevated mode. Parent waits
with a bounded timeout and validates helper PID, peer image, transaction ID,
nonce, and result MAC.

- [ ] **Step 4: Run and commit elevation request handling**

```bash
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release --filter "FullyQualifiedName~SetupElevationRequestTests|FullyQualifiedName~ElevatedHelperLauncherTests"
git diff --check
git add Windows/src/EMKE.Setup/Elevated Windows/tests/EMKE.Setup.Tests
git commit -m "feat: constrain Setup elevation boundary"
```

### Task 4: Implement Exact Machine-Scope Certificate and Driver Changes

**Files:**

- Create: `Windows/src/EMKE.Setup/Elevated/ElevatedMachineInstaller.cs`
- Create: `Windows/src/EMKE.Setup/Platform/CertificateInstaller.cs`
- Create: `Windows/src/EMKE.Setup/Platform/DriverInstaller.cs`
- Create: `Windows/src/EMKE.Setup/Platform/SetupApiNativeMethods.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/ElevatedMachineInstallerTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/CertificateInstallerTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/DriverInstallerTests.cs`

- [ ] **Step 1: Write RED existing-state and rollback tests**

Cover certificate absent/exact/different, driver absent/exact/older/newer/
wrong signer/wrong hardware ID, device absent/exact/unrelated, install success,
reboot required, partial failure, rollback success, and rollback failure.
Unrelated/incompatible existing drivers must block; they are never removed.

- [ ] **Step 2: Install only the pinned public certificate when necessary**

Use `X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine)`. Re-open
the CER from the verified handle, re-check hash/subject/thumbprint/validity,
then add only when the exact thumbprint is absent. Record `createdByAttempt`.

- [ ] **Step 3: Install only the Microsoft-signed driver**

Use SetupAPI/NewDev with the verified INF and exact hardware ID. Re-verify CAT
kernel trust and membership in the elevated process, install/update only the
declared root device, and query the resulting published INF/version/signer.
Return explicit success/reboot-required/failure; never parse localized console
text from `pnputil` as the primary contract.

- [ ] **Step 4: Implement exact rollback**

Rollback removes the created device/package only when its published INF,
hardware ID, version, and catalog hash still match the transaction. Remove the
certificate only when it was created by the attempt and is still the exact
thumbprint. Otherwise preserve state and emit a durable recovery record.

- [ ] **Step 5: Run and commit machine changes**

```bash
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release --filter "FullyQualifiedName~ElevatedMachineInstallerTests|FullyQualifiedName~CertificateInstallerTests|FullyQualifiedName~DriverInstallerTests"
git diff --check
git add Windows/src/EMKE.Setup/Elevated/ElevatedMachineInstaller.cs Windows/src/EMKE.Setup/Platform Windows/tests/EMKE.Setup.Tests
git commit -m "feat: install exact machine trust and driver"
```

### Task 5: Install the MSIX for the Invoking User and Verify Readiness

**Files:**

- Create: `Windows/src/EMKE.Setup/Platform/PackageInstaller.cs`
- Create: `Windows/src/EMKE.Setup/Platform/EndpointVerifier.cs`
- Create: `Windows/src/EMKE.Setup/SetupOrchestrator.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/PackageInstallerTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/EndpointVerifierTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/SetupOrchestratorTests.cs`

- [ ] **Step 1: Write RED per-user and alternate-admin tests**

Prove the package installer runs only in the original parent, checks the current
user SID before/after UAC, installs exact package identity/version, preserves a
compatible pre-existing package, blocks an unexpected publisher, and removes
only a package created or upgraded by this transaction during rollback.

- [ ] **Step 2: Use Windows package deployment APIs**

Call `PackageManager.AddPackageAsync`/supported deployment API from the parent;
do not launch `Add-AppxPackage` under the elevated administrator. Verify
package family name, full name, publisher, version, architecture, install
location ownership, and signature after installation.

- [ ] **Step 3: Verify driver and endpoints before Launch**

Query the installed driver through the same production trust path as the app,
then discover exactly these active roles:

```text
meetingSpeakerRender
appSpeakerCapture
appMicrophoneRender
meetingMicrophoneCapture
```

Controlled startup may load settings/diagnostics but must not auto-connect to
the Translation service. Offer Launch only after endpoint verification.

- [ ] **Step 4: Implement resumable reboot and recovery record**

When driver installation requires reboot, persist a signed, user-readable
recovery record with transaction ID, exact created state, payload hashes, and
next step. On resume, re-verify all state and payload identities before
continuing. Never persist the request MAC key.

- [ ] **Step 5: Run and commit orchestration**

```bash
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release --filter "FullyQualifiedName~PackageInstallerTests|FullyQualifiedName~EndpointVerifierTests|FullyQualifiedName~SetupOrchestratorTests"
git diff --check
git add Windows/src/EMKE.Setup/Platform/PackageInstaller.cs Windows/src/EMKE.Setup/Platform/EndpointVerifier.cs Windows/src/EMKE.Setup/SetupOrchestrator.cs Windows/tests/EMKE.Setup.Tests
git commit -m "feat: complete per-user Setup orchestration"
```

### Task 6: Package, Sign, Tamper-Test, and Hand Off the EXE

**Files:**

- Create: `Windows/tools/package-setup.ps1`
- Create: `Windows/tools/verify-setup.ps1`
- Create: `Windows/tools/tests/setup-packaging.contract.test.mjs`
- Create: `Windows/tools/tests/setup-packaging.validation.test.ps1`
- Create: `.github/workflows/windows-setup.yml`
- Create: `docs/quality/windows-setup-evidence.md`

- [ ] **Step 1: Write RED artifact-inventory tests**

Require output:

```text
EMKE-Translation-Setup-0.2.0-internal-x64.exe
SHA256SUMS.txt
setup-provenance.json
uninstall/recovery helper
diagnostic-only engineering ZIP
```

The embedded manifest must name exact MSIX, CER if applicable, driver INF/SYS/
CAT hashes, source commits, workflow runs, and signer identities. Reject stale
or extra payloads.

- [ ] **Step 2: Publish one self-contained x64 executable**

Use deterministic Release publish and single-file packaging. Embed payloads as
resources with the generated manifest. Sign the EXE with the configured
application signer when available; for self-signed Internal, verify the pinned
signature and report the unknown-publisher boundary honestly.

- [ ] **Step 3: Run tamper and elevation-boundary tests**

Mutate every embedded payload, manifest field, request field, helper result,
and recovery record. Verify rejection happens before mutation or results in
exact rollback. Run static guards proving no PFX/private key/API key and no
test-mode/BCD operation is present.

- [ ] **Step 4: Build and independently verify the exact EXE**

```powershell
$candidateRoot = 'artifacts/windows-0.2.0-candidate'
$msix = Join-Path $candidateRoot 'EMKE-Translation-Windows-0.2.0-internal-x64.msix'
$driver = 'artifacts/windows-driver-microsoft-signed'
$certificate = Join-Path $candidateRoot 'EMKE-Translation-Internal.cer'
$setup = Join-Path $candidateRoot 'EMKE-Translation-Setup-0.2.0-internal-x64.exe'
pwsh -NoProfile -File Windows/tools/package-setup.ps1 -MsixPath $msix -DriverDirectory $driver -CertificatePath $certificate
pwsh -NoProfile -File Windows/tools/verify-setup.ps1 -SetupPath $setup
Get-FileHash $setup -Algorithm SHA256
Get-AuthenticodeSignature $setup | Format-List Status,SignerCertificate
```

- [ ] **Step 5: Run the complete automated gate and commit**

```bash
node --test Windows/tools/tests/setup-packaging.contract.test.mjs
pwsh -NoProfile -File Windows/tools/tests/setup-packaging.validation.test.ps1
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release
dotnet test Windows/EMKE.Windows.slnx --configuration Release
git diff --check
git add Windows/src/EMKE.Setup Windows/tests/EMKE.Setup.Tests Windows/tools/package-setup.ps1 Windows/tools/verify-setup.ps1 Windows/tools/tests/setup-packaging.contract.test.mjs Windows/tools/tests/setup-packaging.validation.test.ps1 .github/workflows/windows-setup.yml docs/quality/windows-setup-evidence.md
git commit -m "build: produce Windows 0.2.0 Setup EXE"
```

The evidence file must separate build/signature/tamper proof from actual clean
install, upgrade, repair, rollback, uninstall, endpoint, translation, and
meeting acceptance.
