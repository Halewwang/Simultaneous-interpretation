# EMKE Translation Windows Internal MSIX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build, test, sign, install-check, download, and independently verify `EMKE-Translation-Windows-0.1.0-internal-x64.msix` plus its Internal certificate and exact install/uninstall handoff bundle.

**Architecture:** Complete the approved C# headless runtime and Windows-native WPF product on top of the existing C++ audio/driver foundation, then package the self-contained x64 application as a classic full-trust MSIX. The MSIX remains separate from the virtual driver and reports a controlled `driverMissing` state until a separately signed compatible driver is installed.

**Tech Stack:** .NET 10, C# 14, WPF, MSTest, C++/CMake/MSVC, MSIX, MakeAppx, SignTool, PowerShell 7, OpenSSL, GitHub Actions `windows-2025-vs2026`

---

## Plan Set and Precedence

This execution plan uses the exact per-file code and tests already specified in
the following complete plans:

1. `docs/superpowers/plans/2026-07-26-emke-windows-translation-runtime.md`
2. `docs/superpowers/plans/2026-07-26-emke-windows-wpf-product.md`
3. `docs/superpowers/plans/2026-07-26-emke-windows-delivery-internal-beta.md`

Those files are implementation detail documents, not optional background.
Execute their referenced task steps where this plan selects them. This plan
takes precedence for the following Internal `0.1.0` differences:

- the Windows audio foundation through Task 7B already exists at `dd9d3cf`;
- no driver package is embedded, downloaded, installed, or removed;
- no `.appinstaller` feed or public release is required for this artifact;
- no self-hosted interactive UI or physical hardware result is required;
- `compatibility.internal.json` records
  `driverPackageAvailable=false` and has no downloadable driver hash;
- classic WPF MSIX declares the required `runFullTrust` capability;
- the test certificate is imported into Local Machine Trusted People with one
  explicit elevated operation;
- hosted CI must install, query, smoke, and uninstall the exact MSIX;
- real driver, endpoint, UI, meeting, and listening gates remain unverified.

The approved design is
`docs/superpowers/specs/2026-07-27-emke-windows-internal-msix-design.md`.

## Global Constraints

- Work only in `.worktrees/windows-internal-msix` on
  `codex/windows-internal-msix`.
- Preserve the dirty `main` checkout and every other worktree.
- Use TDD for all production code and packaging behavior.
- Keep Windows and macOS projects independently releasable.
- Do not change macOS product code or packaging.
- Do not install or mutate a driver.
- Do not create a public GitHub release or tag.
- Do not expose PFX bytes, passwords, API keys, endpoint IDs, recordings,
  transcripts, local paths, or raw device logs.
- Every commit must have fresh focused tests and `git diff --check`.
- Push only after implementation and review gates pass.
- Hosted Windows proof is not physical Windows 11 25H2 acceptance.

---

### Task 1: Freeze the Internal Package Metadata

**Files:**

- Create: `Windows/version.json`
- Create: `Windows/packaging/channels.json`
- Create: `Windows/packaging/compatibility.internal.json`
- Create: `Windows/tools/resolve-version.ps1`
- Create: `Windows/tools/tests/windows-version.contract.test.mjs`

- [ ] **Step 1: Write the portable failing metadata contract**

Add a Node test that requires:

```text
productVersion = 0.1.0
packageVersion = 0.1.0.0
contractVersion = 1
settingsSchemaVersion = 1
driverAbiVersion = 1
minimumWindowsBuild = 26200
architecture = x64
channel = internal
package identity = EMKE.Translation.Internal
publisher = CN=EMKE Internal Test
driverPackageAvailable = false
driverPackageSha256 is absent
```

Run:

```bash
node --test Windows/tools/tests/windows-version.contract.test.mjs
```

Expected: FAIL because the metadata files do not exist.

- [ ] **Step 2: Implement metadata and the resolver**

Execute Task 1 Steps 2 and 4 from
`2026-07-26-emke-windows-delivery-internal-beta.md`, with this exact
compatibility object:

```json
{
  "appVersion": "0.1.0",
  "contractVersion": 1,
  "settingsSchemaVersion": 1,
  "driverAbiVersion": 1,
  "minimumDriverVersion": "0.1.0",
  "recommendedDriverVersion": "0.1.0",
  "driverPackageAvailable": false,
  "channel": "internal"
}
```

The resolver must fail if `driverPackageAvailable=false` is paired with a
driver URL or hash.

- [ ] **Step 3: Verify and commit the portable metadata boundary**

```powershell
node --test Windows/tools/tests/windows-version.contract.test.mjs
pwsh Windows/tools/resolve-version.ps1 -VersionFile Windows/version.json
git diff --check
git add Windows/version.json Windows/packaging/channels.json `
  Windows/packaging/compatibility.internal.json `
  Windows/tools/resolve-version.ps1 Windows/tools/tests
git commit -m "build: define Windows Internal package metadata"
```

Expected: focused Node and PowerShell checks pass.

### Task 2: Implement the Complete Managed Translation Runtime

**Files:**

- Modify: `Windows/EMKE.Windows.slnx`
- Create/modify: `Windows/src/EMKE.Core/**`
- Create/modify: `Windows/src/EMKE.Realtime/**`
- Create/modify: `Windows/src/EMKE.Routing/**`
- Create/modify: `Windows/src/EMKE.Application/**`
- Create/modify: `Windows/src/EMKE.Platform/**`
- Create/modify: `Windows/tests/EMKE.Core.Tests/**`
- Create/modify: `Windows/tests/EMKE.Contract.Tests/**`
- Create/modify: `Windows/tests/EMKE.Application.Tests/**`
- Create/modify: `Windows/tests/EMKE.Integration.Tests/**`
- Create: `.github/workflows/windows-runtime.yml`

- [ ] **Step 1: Scaffold and prove RED**

Execute Translation Runtime Tasks 1–3 exactly. Run each specified focused test
before implementation and retain the observed failing result. Once
`EMKE.Integration.Tests` exists, add `VersionMetadataTests.cs` with the exact
Delivery Task 1 assertions plus:

```text
driverPackageAvailable=false
driverPackageSha256 absent
driver package URL absent
```

Run the focused managed metadata test before proceeding:

```powershell
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~VersionMetadata"
```

- [ ] **Step 2: Implement protocol and session behavior**

Execute Translation Runtime Tasks 4–6 exactly:

```text
endpoint construction
ClientWebSocket text transport
session.created -> session.update -> session.updated handshake
9600-byte PCM16 append batches
caption/audio delta decoding
bounded close
VAD, language gate, levels, and routing
```

- [ ] **Step 3: Bind the native audio ABI**

Execute Translation Runtime Task 7 exactly. The managed layer may poll or write
the public C ABI only from background tasks; it must not register a managed
realtime callback.

- [ ] **Step 4: Implement the serialized runtime and failure matrix**

Execute Translation Runtime Tasks 8–9 exactly. Require 100 deterministic
outbound failure iterations with virtual microphone silence unless explicit
bypass was already active.

- [ ] **Step 5: Add and run runtime CI gates**

Execute Translation Runtime Task 10, except do not claim a real service,
installed driver, live endpoint, meeting, or listening result.

```powershell
node Scripts/validate-shared-contracts.mjs
dotnet restore Windows/EMKE.Windows.slnx --locked-mode
dotnet build Windows/EMKE.Windows.slnx --configuration Release --no-restore
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build
git diff --check
```

- [ ] **Step 6: Commit each runtime task**

Use the ten commit boundaries in the imported runtime plan. Do not collapse the
runtime into one review-sized commit.

### Task 3: Build the Windows-native WPF Product

**Files:**

- Create/modify: `Windows/src/EMKE.Windows.App/**`
- Create/modify: `Windows/tests/EMKE.Windows.App.Tests/**`
- Create: `.github/workflows/windows-app.yml`
- Create: `docs/quality/windows-wpf-product-evidence.md`

- [ ] **Step 1: Create the composition root**

Execute WPF Product Task 1 exactly. One process owns one
`TranslationRuntime`, one snapshot store, one tray host, and one
single-instance coordinator.

- [ ] **Step 2: Implement localized presentation and Windows surfaces**

Execute WPF Product Tasks 2–3 exactly. Use the existing approved EMKE icon
asset; do not invent a different Windows brand.

- [ ] **Step 3: Implement settings and secrets**

Execute WPF Product Task 4 exactly. Settings use atomic local JSON; API keys
use Windows Credential Manager and never enter settings, logs, screenshots, or
package staging.

- [ ] **Step 4: Implement onboarding and diagnostics**

Execute WPF Product Tasks 5–6 exactly. Driver installation actions are
presentation-only in this milestone: show `driverMissing` and the signed-driver
boundary, but do not download or launch the unsigned driver package.

- [ ] **Step 5: Add accessibility and headless product gates**

Execute WPF Product Task 7. Execute the hosted build/test portion of Task 8.
Do not require or claim the unavailable `interactive-desktop` self-hosted job;
record UI Automation, 200% DPI, high contrast, and physical visual review as
pending.

- [ ] **Step 6: Verify and commit each WPF task**

```powershell
dotnet restore Windows/EMKE.Windows.slnx --locked-mode
dotnet build Windows/EMKE.Windows.slnx --configuration Release --no-restore
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build
rg -n "MessageBox\\.Show|\\.Result\\b|\\.Wait\\(|Thread\\.Sleep|NotImplementedException|TODO|FIXME" `
  Windows/src/EMKE.Windows.App Windows/src/EMKE.Platform
git diff --check
```

Expected: build/test exit `0`; hygiene scan has no prohibited production
matches. Use the eight commit boundaries from the imported WPF plan.

### Task 4: Implement the Driver Compatibility Gate

**Files:**

- Create: `Windows/src/EMKE.Application/Compatibility/CompatibilityGate.cs`
- Create: `Windows/src/EMKE.Core/CompatibilityManifest.cs`
- Create: `Windows/src/EMKE.Platform/Driver/WindowsDriverManager.cs`
- Create: `Windows/tests/EMKE.Application.Tests/CompatibilityGateTests.cs`
- Modify: `Windows/src/EMKE.Windows.App/EMKE.Windows.App.csproj`

- [ ] **Step 1: Write the failing gate matrix**

Execute Delivery Task 2 Step 1. Add:

```text
driverPackageAvailable=false -> repair action is unavailable
driverMissing -> Start disabled
driverMissing -> zero WebSocket opens
driverMissing -> zero native stream starts
```

- [ ] **Step 2: Implement the pure gate**

Execute Delivery Task 2 Steps 2–3. Embed the exact generated
`compatibility.internal.json`; never fall back to “allow any driver.”

- [ ] **Step 3: Implement read-only installed-driver evidence**

Implement only the read-only `WindowsDriverManager` portion of Delivery Task 2
Step 4. Do not create the downloadable `DriverPackageVerifier` in this
milestone because no signed driver package is offered.

- [ ] **Step 4: Verify and commit**

```powershell
dotnet test Windows/tests/EMKE.Application.Tests/EMKE.Application.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~CompatibilityGate"
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~WindowsDriverManager"
git diff --check
git add Windows/src Windows/tests
git commit -m "feat: gate Windows runtime on compatible driver evidence"
```

### Task 5: Add Test-certificate Contracts and Provisioning

**Files:**

- Create: `Windows/packaging/InternalSigning/README.md`
- Create: `Windows/tools/verify-internal-signing-certificate.ps1`
- Create: `Windows/tools/tests/internal-signing.contract.test.mjs`
- Create: `Windows/tools/tests/internal-signing.validation.test.ps1`

- [ ] **Step 1: Write failing certificate contracts**

Require:

```text
subject = CN=EMKE Internal Test
RSA key size >= 3072
signature algorithm uses SHA-256 or stronger
EKU contains 1.3.6.1.5.5.7.3.3
Key Usage contains Digital Signature
certificate is currently valid
PFX private key is present only in the runner input
public CER contains no private key
```

Run:

```bash
node --test Windows/tools/tests/internal-signing.contract.test.mjs
```

Expected: FAIL because verifier and documentation do not exist.

- [ ] **Step 2: Implement the read-only certificate verifier**

`verify-internal-signing-certificate.ps1` accepts:

```text
-PfxPath
-PasswordEnvironmentVariable
-ExpectedSubject
-ExportPublicCertificatePath
```

It reads the password only from the named environment variable, validates the
contract above, exports DER CER bytes, prints only subject, validity, EKU, key
size, and public thumbprint, and never prints the password or private key.

- [ ] **Step 3: Document one-time secret provisioning**

The README records this controller-only macOS command shape:

```bash
signing_temp="$(mktemp -d /tmp/emke-msix-signing.XXXXXX)"
openssl rand -base64 48 > "$signing_temp/password"
openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 730 \
  -subj "/CN=EMKE Internal Test" \
  -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=codeSigning" \
  -keyout "$signing_temp/key.pem" \
  -out "$signing_temp/cert.pem"
openssl pkcs12 -export \
  -out "$signing_temp/app.pfx" \
  -inkey "$signing_temp/key.pem" \
  -in "$signing_temp/cert.pem" \
  -passout "file:$signing_temp/password"
base64 < "$signing_temp/app.pfx" > "$signing_temp/app.pfx.base64"
gh secret set WINDOWS_INTERNAL_SIGNING_PFX_BASE64 \
  < "$signing_temp/app.pfx.base64"
gh secret set WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD \
  < "$signing_temp/password"
```

After `gh secret list` confirms both names, remove only these exact temporary
files and the now-empty generated directory. Never commit, upload as an
artifact, or print their contents.

- [ ] **Step 4: Verify and commit**

Use a synthetic test PFX in a test-owned temporary directory to run:

```powershell
node --test Windows/tools/tests/internal-signing.contract.test.mjs
pwsh Windows/tools/tests/internal-signing.validation.test.ps1
git diff --check
git add Windows/packaging/InternalSigning Windows/tools
git commit -m "build: define Internal MSIX signing contract"
```

### Task 6: Build and Verify the Classic WPF MSIX

**Files:**

- Create: `Windows/packaging/App/AppxManifest.internal.xml`
- Create: `Windows/packaging/App/Assets/**`
- Create: `Windows/tools/package-msix.ps1`
- Create: `Windows/tools/verify-msix.ps1`
- Create: `Windows/tools/tests/msix-packaging.contract.test.mjs`
- Create/modify: `Windows/tests/EMKE.Integration.Tests/MsixMetadataTests.cs`

- [ ] **Step 1: Write failing package and manifest tests**

Start from Delivery Task 4 Step 1, with these exact required manifest values:

```xml
<Identity
  Name="EMKE.Translation.Internal"
  Publisher="CN=EMKE Internal Test"
  Version="0.1.0.0"
  ProcessorArchitecture="x64" />
<TargetDeviceFamily
  Name="Windows.Desktop"
  MinVersion="10.0.26200.0"
  MaxVersionTested="10.0.26200.0" />
<Application
  Id="EMKETranslation"
  Executable="EMKE.Windows.App.exe"
  EntryPoint="Windows.FullTrustApplication"
  uap10:RuntimeBehavior="packagedClassicApp"
  uap10:TrustLevel="mediumIL">
<rescap:Capability Name="runFullTrust" />
```

The contract rejects every INF, CAT, SYS, PFX, PEM, key, password, test
assembly, PDB, user setting, credential, recording, transcript, and raw
endpoint fixture in staging.

- [ ] **Step 2: Publish the self-contained app**

Use Delivery Task 4 Step 2 exactly:

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

- [ ] **Step 3: Stage and package**

`package-msix.ps1` must:

```text
resolve MakeAppx and SignTool from the installed Windows SDK
create a new guarded repository-owned staging directory
copy only verified publish output, native DLL, assets, and compatibility JSON
render the exact manifest from validated metadata
call MakeAppx pack without /nv
import the PFX temporarily into CurrentUser\My
sign by exact certificate thumbprint with SignTool /fd SHA256
export the public CER
remove the temporary CurrentUser\My certificate and PFX file in finally
```

The signing password never appears as a command-line argument.

- [ ] **Step 4: Verify the exact MSIX**

`verify-msix.ps1` must:

```text
run SignTool verify /pa
unpack with MakeAppx
validate manifest identity/publisher/version/architecture/OS/entry/capability
validate compatibility metadata
validate EMKE.Windows.App.exe and EMKE.NativeAudio.dll are x64
reject forbidden files and private material
hash every extracted file and the final MSIX
prove verification did not change the MSIX bytes
```

- [ ] **Step 5: Run and commit**

```powershell
node --test Windows/tools/tests/msix-packaging.contract.test.mjs
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~MsixMetadata"
pwsh Windows/tools/package-msix.ps1 -Configuration Release
pwsh Windows/tools/verify-msix.ps1 `
  -Package Windows/artifacts/msix/EMKE-Translation-Windows-0.1.0-internal-x64.msix
git diff --check
git add Windows/packaging/App Windows/tools Windows/tests
git commit -m "build: package signed Windows Internal MSIX"
```

### Task 7: Add Exact Install and Uninstall Helpers

**Files:**

- Create: `Windows/packaging/App/Install-EMKE-Translation-Internal.ps1`
- Create: `Windows/packaging/App/Uninstall-EMKE-Translation-Internal.ps1`
- Create: `Windows/tools/tests/internal-msix-lifecycle.contract.test.mjs`
- Create: `Windows/tools/tests/internal-msix-lifecycle.behavior.test.ps1`

- [ ] **Step 1: Write failing lifecycle contracts**

Require the installer to:

```text
reject dot-source
require explicit -ConfirmTrust
accept only exact absolute local non-reparse paths
verify SHA256SUMS before any elevation
verify certificate subject and fixed thumbprint
self-elevate only a child certificate-import operation
re-verify certificate bytes and thumbprint after elevation
import only into LocalMachine\TrustedPeople
return to the original user token for Add-AppxPackage
verify exact installed Name, Publisher, Version, and Architecture
never invoke driver tools
```

Require the uninstaller to:

```text
remove only Name=EMKE.Translation.Internal for the invoking user
retain certificate unless -RemoveCertificate and -ConfirmRemoveCertificate
remove only the exact recorded thumbprint from LocalMachine\TrustedPeople
never match by subject alone
never remove a driver or another AppX package
```

- [ ] **Step 2: Implement the installer**

Use `Start-Process pwsh -Verb RunAs -Wait` only for the exact certificate
import child mode. Pass verified paths and thumbprint as discrete
`ArgumentList` values. The elevated child returns a stable exit code and no
free-form certificate data.

- [ ] **Step 3: Implement the uninstaller**

Run `Remove-AppxPackage` non-elevated for the exact package full name. Use the
same constrained elevated child pattern only when exact certificate removal
was separately confirmed.

- [ ] **Step 4: Run and commit**

```powershell
node --test Windows/tools/tests/internal-msix-lifecycle.contract.test.mjs
pwsh Windows/tools/tests/internal-msix-lifecycle.behavior.test.ps1
git diff --check
git add Windows/packaging/App Windows/tools/tests
git commit -m "feat: add guarded Internal MSIX lifecycle helpers"
```

### Task 8: Add Hosted Build, Install, Smoke, and Artifact Workflow

**Files:**

- Create: `.github/workflows/windows-internal-msix.yml`
- Create: `Windows/tools/build-internal-msix-bundle.ps1`
- Create: `Windows/tools/test-hosted-msix-install.ps1`
- Create: `Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs`
- Create: `docs/quality/windows-internal-msix-evidence.md`

- [ ] **Step 1: Write the failing workflow contract**

Require:

```text
windows-2025-vs2026 runner
.NET 10 setup
locked restore
shared contract validation
managed build/test
native Release build and CTest
PFX reconstructed only from masked secrets
MSIX pack/sign/verify
LocalMachine TrustedPeople import on the ephemeral runner
Add-AppxPackage exact package
installed identity query
driverMissing smoke with no network/audio start
Remove-AppxPackage exact package
exact certificate cleanup in always()
artifact upload only after every gate passes
```

The workflow must not invoke driver install/uninstall scripts or expose
certificate environment values.

- [ ] **Step 2: Implement the bundle builder**

The bundle contains exactly:

```text
EMKE-Translation-Windows-0.1.0-internal-x64.msix
EMKE-Translation-Windows-0.1.0-internal-x64.cer
Install-EMKE-Translation-Internal.ps1
Uninstall-EMKE-Translation-Internal.ps1
SHA256SUMS.txt
```

It then emits
`EMKE-Translation-Windows-0.1.0-internal-x64.zip`, hashes the ZIP, and writes a
machine-readable provenance JSON containing source commit, workflow run,
package identity, certificate thumbprint, file sizes, and hashes.

- [ ] **Step 3: Implement hosted installation validation**

`test-hosted-msix-install.ps1` imports the exact CER, installs the exact MSIX,
asserts package identity/version/publisher/architecture, runs the
non-interactive driver-missing smoke, and in `finally` removes only the exact
package and thumbprint.

- [ ] **Step 4: Add the workflow**

Trigger on `workflow_dispatch` and relevant Windows/shared source paths. Pass
secrets as environment values only to the signing step. Upload one restricted
Actions artifact named:

```text
emke-translation-windows-0.1.0-internal-x64-${{ github.sha }}
```

- [ ] **Step 5: Run local static gates and commit**

```bash
node --test Windows/driver/tests/*.test.mjs Windows/tools/tests/*.test.mjs
git diff --check
git add .github/workflows/windows-internal-msix.yml Windows/tools `
  docs/quality/windows-internal-msix-evidence.md
git commit -m "ci: build and install-check Windows Internal MSIX"
```

### Task 9: Provision Secrets and Produce the Installer

**Files:**

- No repository source file is modified while generating private material.
- Update after CI: `docs/quality/windows-internal-msix-evidence.md`

- [ ] **Step 1: Review the complete branch**

Run focused spec and quality review for:

```text
runtime safety
WPF state ownership
Credential Manager/privacy
manifest/full-trust minimum scope
certificate handling
path/reparse safety
MSIX lifecycle exact targeting
workflow secret exposure
artifact boundary
```

Resolve all Critical and Important findings before push.

- [ ] **Step 2: Provision the persistent Internal certificate secrets**

Execute the exact controller-only procedure in
`Windows/packaging/InternalSigning/README.md`. The GitHub Environment
`windows-internal-signing` must already have required reviewers configured,
and only the signing workflow job may declare
`environment: windows-internal-signing`. Confirm only Environment secret
names:

```bash
gh secret list --env windows-internal-signing | rg \
  'WINDOWS_INTERNAL_SIGNING_PFX_BASE64|WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD'
```

Do not print values.

- [ ] **Step 3: Push and monitor Windows CI**

```bash
git push -u origin codex/windows-internal-msix
gh run list --branch codex/windows-internal-msix --limit 5
```

Follow the selected `windows-internal-msix.yml` run until every job is
terminal. On failure, use systematic debugging and do not download or report a
partial artifact as an installer.

- [ ] **Step 4: Download and independently verify**

Download the successful Actions artifact into a new `mktemp -d` directory.
Verify:

```text
artifact inventory is exact
ZIP inventory is exact
MSIX signature verifies
CER thumbprint equals provenance
SHA256SUMS matches every handoff file
MSIX hash equals workflow provenance
source commit equals the tested branch commit
no private material is present
```

- [ ] **Step 5: Record evidence and commit**

Write exact run ID, job IDs, artifact ID/name/expiry, source commit, package
identity, certificate thumbprint, sizes, hashes, test counts, and proof
boundaries to `docs/quality/windows-internal-msix-evidence.md`.

```bash
git add docs/quality/windows-internal-msix-evidence.md
git commit -m "docs: record Windows Internal MSIX evidence"
git push origin codex/windows-internal-msix
```

- [ ] **Step 6: Final fresh verification**

```bash
git status --short --branch
git rev-parse HEAD
git rev-parse origin/codex/windows-internal-msix
final_run_id="$(
  gh run list \
    --branch codex/windows-internal-msix \
    --workflow windows-internal-msix.yml \
    --limit 1 \
    --json databaseId \
    --jq '.[0].databaseId'
)"
test -n "$final_run_id"
gh run view "$final_run_id" --json headSha,status,conclusion,url,jobs
```

Report the real local download path, artifact URL, `.msix` and ZIP SHA-256,
certificate thumbprint, required first-install UAC, and the explicit unsigned
driver/physical meeting boundary.
