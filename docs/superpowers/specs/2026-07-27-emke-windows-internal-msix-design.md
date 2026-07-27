# EMKE Translation Windows Internal MSIX Design

**Date:** 2026-07-27
**Status:** Approved, including elevated Internal certificate trust
**Implementation baseline:** `dd9d3cf` (`docs: record Task 7B hosted evidence`)
**Target:** Windows 11 25H2+, x64, Internal channel

## 1. Purpose

Produce the first real Windows application installation file for EMKE
Translation:

```text
EMKE-Translation-Windows-0.1.0-internal-x64.msix
```

The package is an independently versioned Windows deliverable. It does not
replace, alter, or couple its release cadence to the macOS application.

This milestone packages a Windows-native WPF client, the C# translation
runtime, and the existing native audio DLL. The virtual audio driver remains a
separate package because MSIX cannot install a Windows driver. Until the driver
has a trusted test or Microsoft signature and passes physical-machine
acceptance, the installed application must report `driverMissing` and block
translation rather than pretending that four-endpoint audio is ready.

## 2. Deliverables

The build produces:

1. `EMKE-Translation-Windows-0.1.0-internal-x64.msix`
2. `EMKE-Translation-Windows-0.1.0-internal-x64.cer`
3. `Install-EMKE-Translation-Internal.ps1`
4. `Uninstall-EMKE-Translation-Internal.ps1`
5. `SHA256SUMS.txt`
6. `EMKE-Translation-Windows-0.1.0-internal-x64.zip`

The `.msix` is the application installation file. The ZIP is the handoff
bundle containing the installation file, public test certificate, exact
install/uninstall helpers, and hashes. No private certificate, certificate
password, API key, endpoint identifier, recording, transcript, or local path
may enter any artifact.

The package identity is:

```text
Name: EMKE.Translation.Internal
Publisher: CN=EMKE Internal Test
Version: 0.1.0.0
Architecture: x64
Minimum Windows version: 10.0.26200.0
Application executable: EMKE.Windows.App.exe
```

Internal, Beta, and Stable identities remain separate and never update across
channels.

## 3. Product Scope

### 3.1 Included application behavior

The package contains:

- one self-contained .NET 10 x64 WPF application;
- one process and one `TranslationRuntime`;
- Windows system-tray lifetime and single-instance activation;
- dashboard, settings, onboarding, and floating status surfaces;
- Simplified Chinese, English, and Follow System interface language;
- API-key storage through Windows Credential Manager;
- local settings for Base URL, model, translation languages, and physical
  devices;
- independent inbound and outbound Translation WebSocket sessions;
- the existing 24 kHz mono PCM16 network contract;
- the existing 48 kHz local native-audio contract;
- inbound original-audio fail-open and outbound microphone fail-closed
  behavior;
- explicit inbound and outbound bypass controls;
- native audio discovery and four-role endpoint compatibility checks;
- bounded shutdown and actionable stable error categories.

The app uses the already approved shared schemas, golden vectors, status codes,
route rules, and privacy boundaries. It does not share WPF UI or Windows audio
drivers with macOS.

### 3.2 Driver-missing behavior

The MSIX never installs, updates, removes, or trusts a driver. At startup the
application evaluates the embedded compatibility manifest:

```text
minimumWindowsBuild = 26200
architecture = x64
contractVersion = 1
settingsSchemaVersion = 1
driverAbiVersion = 1
minimumDriverVersion = 0.1.0
channel = internal
```

If the driver is absent, unsigned, incompatible, or does not expose exactly
the four technical endpoint roles, the app:

- remains installable and launchable;
- shows a localized Driver status and repair explanation;
- permits settings, onboarding, and non-mutating diagnostics;
- disables translation start;
- emits no audio and opens no Translation WebSocket session;
- never relabels the state as ready.

The currently built unsigned INF/SYS/CAT artifact is not embedded in the MSIX
and is not offered to normal users as an installable driver.

## 4. Architecture

```text
WPF UI
  -> AppPresentation / immutable snapshots
  -> EMKE.Application TranslationRuntime
  -> EMKE.Realtime + EMKE.Routing
  -> EMKE.Platform adapters
       -> Credential Manager
       -> atomic local settings
       -> WebSocket
       -> native audio C ABI
            -> EMKE.NativeAudio.dll
                 -> WASAPI / MMDevice
                 -> independently installed EMKE virtual-audio driver
```

Only `EMKE.Windows.App` references WPF. Core, protocol, routing, and
application state are deterministic class libraries with no UI dependency.
The C# runtime owns orchestration and network state; the C++ DLL owns audio
threads, WASAPI, endpoint discovery, buffering, and PCM conversion.

The UI consumes one versioned immutable snapshot. Opening or closing windows
does not create or stop another runtime. UI code never reads audio devices,
performs network I/O, hashes a package, or waits synchronously for shutdown.

## 5. Runtime Data Flow

On application startup:

1. enforce one Internal-channel process;
2. load local non-secret settings;
3. open Windows Credential Manager only through the secret-store adapter;
4. load and validate embedded compatibility metadata;
5. query native host/endpoint evidence without mutating devices;
6. publish a stopped or driver-blocked immutable snapshot;
7. show onboarding when incomplete, otherwise remain available from the tray.

On translation start:

1. validate OS, architecture, configuration, permissions, driver, ABI, and
   four endpoint roles;
2. create independent inbound and outbound Translation sessions;
3. start native audio only after the relevant session handshake is ready;
4. route audio under the approved fail-open/fail-closed rules;
5. publish bounded state, levels, captions, and errors through snapshots.

On stop or exit:

1. reject duplicate start commands;
2. stop diagnostics;
3. signal both runtime directions;
4. close local audio and network sessions within bounded deadlines;
5. force only local safe shutdown after a close timeout;
6. remove the tray icon and release the single-instance coordinator.

No audio, transcript, translation text, or endpoint identifier is persisted.

## 6. Internal Signing

The Internal package uses one persistent self-signed code-signing certificate:

```text
Subject: CN=EMKE Internal Test
EKU: Code Signing
Key: RSA 3072 or stronger
Hash: SHA-256
```

The private PFX and its random password are generated outside the repository
and stored only as encrypted GitHub Actions secrets:

```text
WINDOWS_INTERNAL_SIGNING_PFX_BASE64
WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD
```

The public certificate is exported into the delivery bundle. CI reconstructs
the PFX only inside the ephemeral runner, signs with SignTool, verifies the
signature, and deletes runner-local signing files during cleanup. Logs and
artifacts must not contain the private key or password.

The helper script requires an explicit confirmation before importing the
public certificate into the Local Machine Trusted People store. This exact
certificate-trust action requires elevation and produces one UAC prompt. The
script verifies the fixed certificate thumbprint and MSIX SHA-256 before
elevation, re-verifies both after elevation, imports only the supplied
certificate, and then calls `Add-AppxPackage` for the invoking user. It never
imports the certificate into a root store, installs the driver, or weakens
Windows execution policy.

The uninstall helper removes only the exact
`EMKE.Translation.Internal` package for the invoking user. With a separate
explicit confirmation and elevation, it may also remove only the exact
matching Internal certificate thumbprint from Local Machine Trusted People.
It never removes another certificate with only a similar subject.

This certificate is for Internal testing only. It is not Microsoft Store,
Trusted Signing, EV, attestation, WHQL, or Windows Certified evidence.

## 7. Packaging

The Windows CI workflow:

1. validates version/channel metadata;
2. runs shared contract and C# unit tests;
3. builds the C# runtime and WPF app in Release;
4. builds and tests `EMKE.NativeAudio.dll`;
5. publishes the app self-contained for `win-x64`;
6. stages only required application files, native DLLs, resources, and the
   generated compatibility manifest;
7. creates a classic packaged desktop manifest with
   `EntryPoint="Windows.FullTrustApplication"`,
   `uap10:RuntimeBehavior="packagedClassicApp"`,
   `uap10:TrustLevel="mediumIL"`, and the required
   `rescap:Capability Name="runFullTrust"`;
8. creates the MSIX with MakeAppx;
9. signs it with SignTool using the Internal certificate and an explicit
   SHA-256 file digest;
10. verifies manifest identity, publisher, version, architecture, minimum OS,
   entry executable, content hashes, and signature;
11. imports the public certificate into the ephemeral runner's Local Machine
    Trusted People store;
12. installs the exact MSIX with `Add-AppxPackage`;
13. verifies package identity and a non-interactive `driverMissing` smoke
    result;
14. removes only that package and exact test certificate from the ephemeral
    runner;
15. emits the handoff files, SHA-256 list, and exact provenance metadata.

The package build fails closed when:

- Windows source, tests, or Release build fail;
- expected signing secrets are absent;
- certificate subject, thumbprint, EKU, or validity is wrong;
- manifest identity or publisher differs from the certificate;
- an unexpected architecture or minimum OS is present;
- native ABI and embedded compatibility metadata disagree;
- signing verification fails;
- the package includes private material or an unexpected driver file;
- hosted install, identity, smoke, or uninstall verification fails.

## 8. Installation Experience

The internal tester:

1. downloads the delivery ZIP;
2. verifies `SHA256SUMS.txt`;
3. runs `Install-EMKE-Translation-Internal.ps1`;
4. reviews the explicit Internal certificate warning;
5. confirms one elevated Local Machine Trusted People certificate import;
6. approves the UAC prompt;
7. receives the normal per-user MSIX installation;
8. launches EMKE Translation from Start;
9. sees onboarding and the truthful driver-missing state until a separately
   authorized signed driver is installed.

Direct double-click installation of the MSIX is supported after the public
certificate has already been trusted.

## 9. Testing and Evidence

### 9.1 Automated

- shared JSON schema and golden-vector tests;
- C# core, realtime, routing, runtime, localization, persistence, and WPF
  presentation tests;
- Credential Manager and WebSocket adapter seam tests;
- native C++ tests and process-level `driverMissing` checks;
- package manifest and forbidden-content tests;
- certificate and Authenticode verification;
- MSIX install/query/non-interactive smoke/uninstall on the hosted Windows
  runner;
- deterministic SHA-256 and exact artifact inventory reporting.

### 9.2 Manual boundaries

Hosted automation does not prove:

- Windows 11 25H2 physical-machine UI behavior;
- the elevated certificate-trust prompt on a physical test machine;
- signed driver installation or UAC behavior;
- live four-endpoint routing;
- real microphone/headphone behavior;
- Feishu, DingTalk, Teams, or other meeting interoperability;
- crash silence on the real driver;
- human listening quality or latency.

Those remain separate signed-driver and physical-lab gates.

## 10. Acceptance Criteria

This milestone is complete when:

1. the Internal MSIX, CER, install/uninstall helpers, hashes, and ZIP exist;
2. the MSIX is self-contained, x64, version `0.1.0.0`, and targets build
   26200+;
3. the signature is valid against the included Internal certificate;
4. CI installs and removes the exact package successfully;
5. the installed executable reports the controlled `driverMissing` state
   without opening audio or network sessions;
6. application, native, contract, and packaging tests pass;
7. downloaded artifact bytes and SHA-256 values are independently verified;
8. the final handoff records the exact path, size, hash, source commit,
   certificate thumbprint, CI run, and artifact ID;
9. no claim is made that the unsigned driver, live endpoints, meetings, or
   public distribution are ready.

## 11. Non-goals

- Embedding or installing the virtual driver through MSIX.
- Public Stable distribution.
- Microsoft Store submission.
- Microsoft Trusted Signing, driver attestation, HLK, WHCP, or WHQL.
- Automatic cross-channel updates.
- ARM64 or Windows builds below 26200.
- Changing macOS code, package identity, or release cadence.
- Claiming real meeting functionality from hosted package evidence.

## 12. Technical Basis

- [Microsoft: generating MSIX package components](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-manual-conversion)
  requires `runFullTrust` for a package containing a classic full-trust
  desktop application.
- [Microsoft: SignTool MSIX signing](https://learn.microsoft.com/windows/msix/package/sign-app-package-using-signtool)
  requires the package Publisher to match the signing certificate subject and
  requires an explicit digest algorithm.
- [Microsoft: package-signing certificate](https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing)
  places a self-signed test certificate in Local Machine Trusted People before
  deployment.
