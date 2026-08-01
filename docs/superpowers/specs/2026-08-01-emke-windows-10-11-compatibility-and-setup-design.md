# EMKE Translation Windows 10-11 Compatibility and Setup Design

**Date:** 2026-08-01

**Status:** Approved in conversation; pending written-spec review

**macOS behavior baseline:** `origin/main` `v0.2.4` / `3d81733`

**Windows implementation baseline:** `codex/windows-internal-msix` / `8153cab`

**Windows release target:** `0.2.0-internal`, x64

## 1. Decision

The Windows application, virtual-audio driver, installer, and complete
translation path will target:

- Windows 10 22H2 x64, build `19045` or newer; and
- Windows 11 x64, build `22000` or newer.

The application and driver will remain independently versioned and released
from macOS. Cross-platform reuse remains limited to behavior contracts,
protocol schemas, golden vectors, and acceptance criteria. SwiftUI/AppKit,
WPF, WASAPI, and the Windows driver remain platform-native implementations.

Windows ARM64, Windows Server, Windows 10 builds below `19045`, and Windows
test-signing mode are outside this release.

## 2. Evidence and Root Cause

The first signed Windows MSIX has this manifest requirement:

```text
MinVersion = 10.0.26200.0
```

On the reported target machine:

```text
OS build = 10.0.19045.6466
Certificate store = LocalMachine\TrustedPeople
Authenticode status = Valid
Add-AppxPackage = 0x80073CFD
```

This proves that certificate trust and package signature validation succeeded.
Installation failed because the package required build `26200` while the
machine ran build `19045`. The compatibility floor is also duplicated in the
application TFM, runtime gate, embedded metadata, driver INF, lab tools, and CI,
so changing only the MSIX manifest would create a false compatibility claim.

## 3. Compatibility Contract

One canonical Windows compatibility definition will generate or validate all
platform-specific representations:

```text
architecture = x64
productType = workstation
minimumWindowsBuild = 19045
minimumWindowsApiContract = 10.0.19041.0
maximumVersionTested = 10.0.26200.0
channel = internal
```

The following surfaces must agree with that contract:

- `Windows/version.json`;
- the generated MSIX `TargetDeviceFamily`;
- embedded `compatibility.json`;
- the .NET target framework and runtime build gate;
- driver INF model decoration;
- driver install, uninstall, and evidence tooling;
- package and driver contract tests; and
- Windows CI and real-machine acceptance labels.

`MaxVersionTested` records evidence and does not impose an upper install limit.
The application must fail closed before audio or network startup on builds
below `19045`, on non-x64 architectures, or on non-workstation Windows product
types. A Windows Server build number must never satisfy the client-OS gate.

Ordinary Windows 10 22H2 is outside the current general Microsoft servicing
and .NET 10 support matrix. EMKE therefore records Windows 10 22H2 as an
internally tested compatibility target, not as Microsoft-supported evidence.
Every promoted Windows build requires fresh build-19045 acceptance.

## 4. Application Targeting

The managed target becomes:

```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

The product runtime floor remains build `19045`, even though the compile-time
Windows API contract is `19041`. This permits compile-time API compatibility
analysis while rejecting untested Windows 10 2004 through 21H2 installations.

The `Windows25H2BuildGate` implementation will be replaced with a neutral
Windows build gate that reads the canonical minimum. No production class,
test fixture, packaging script, or workflow may retain an independent `26200`
minimum.

The application must retain these macOS v0.2.4 Translation protocol names:

```text
session.audio.output.language
session.input_audio_buffer.append
session.output_audio.delta
session.input_transcript.delta
session.output_transcript.delta
```

The existing behavior contracts remain unchanged: 24 kHz mono PCM16 network
audio, 48 kHz local audio, independent inbound and outbound sessions, inbound
original-audio fail-open, outbound microphone fail-closed, explicit bypass,
reconnect, and bounded safe shutdown.

## 5. Runtime Completion

The current Internal application composition still contains pending production
adapters for audio-device catalog access and Translation Session creation.
Windows 10-11 full-function acceptance therefore requires more than lowering
the package floor.

The release must replace pending production adapters with:

- a real MMDevice/WASAPI device catalog;
- the production inbound and outbound Translation WebSocket Session factory;
- the approved language-gate and protocol mapping;
- the native four-endpoint routing implementation; and
- stable diagnostics for OS, driver, device, auth, protocol, network, and
  audio failures.

Driver-missing, driver-invalid, or incomplete-endpoint states must keep
settings, onboarding, and non-mutating diagnostics available while blocking
audio startup and network connection.

## 6. Virtual-Audio Driver

The driver source will continue to build with the pinned WDK toolchain, but its
runtime framework and INF contract must support Windows 10 22H2:

```text
KMDF library version = 1.31
INF model decoration = NTamd64.10.0...19045
Driver ABI version = 1
Driver package version = 1.0.0.2
```

KMDF 1.33 is not a Windows 10 runtime target. The build must explicitly select
KMDF 1.31 and fail if code requires a later WDF API. Driver contract tests must
verify the resolved, stamped INF and framework version rather than source text
alone.

The driver package remains separate from the MSIX. The Setup executable may
coordinate both packages, but application package identity, application
signing, driver catalog signing, and driver servicing remain separate trust
and update domains.

## 7. Driver Signing: A1 Normal Mode

The selected A1 path must install with Secure Boot and Memory Integrity left
enabled. It must not enable `TESTSIGNING`, modify boot configuration, request a
Secure Boot change, or use an unsigned driver exception.

The driver package must receive a Microsoft Hardware Dev Center signature that
is valid for the selected Windows 10 and Windows 11 targets. Attestation may be
used when the Hardware Dev Center accepts the target package and policy;
otherwise the release follows the applicable HLK/WHQL submission path.

External prerequisites are explicit release gates:

- Windows Hardware Dev Center access;
- the identity and signing credential required by the current portal policy;
- a submitted driver package containing the exact INF, SYS, and CAT bytes;
- downloaded signed-result verification; and
- Windows 10 and Windows 11 installation evidence for those exact bytes.

Without the returned Microsoft-signed driver bytes, the code may be called
build-ready or submission-ready, but the A1 installer cannot be called
complete.

## 8. Application and Installer Signing

Driver signing does not establish trust for the Setup executable or MSIX.
Application signing remains a separate channel.

For Internal preview, the existing pinned self-signed application certificate
may remain in use. The Setup flow may import only that exact public certificate
into `LocalMachine\TrustedPeople` after hash, subject, validity, and thumbprint
verification. The user performs no manual certificate steps, although Windows
can still display an unknown-publisher warning before the bootstrapper itself
is trusted.

For a verified-publisher handoff, both Setup and MSIX require an appropriate
CA-backed or Azure Artifact Signing identity. That signer must match the MSIX
Publisher contract. A trusted application signer removes the local certificate
import step; it does not replace Microsoft driver signing.

Private keys, passwords, tokens, API keys, and Hardware Dev Center credentials
must remain in protected signing environments and must never enter repository
history or artifacts.

## 9. Setup Executable

The user-facing artifact becomes:

```text
EMKE-Translation-Setup-0.2.0-internal-x64.exe
```

The Setup executable is a two-process bootstrapper:

1. An unelevated parent verifies its embedded manifest, payload inventory,
   hashes, signatures, client product type, OS build, architecture, and
   existing installation state.
2. The parent launches one narrowly scoped elevated helper through UAC.
3. The elevated helper re-verifies the signed request and immutable payload
   hashes, adds only required machine trust, and installs or updates only the
   exact Microsoft-signed driver package.
4. The unelevated parent installs the MSIX for the invoking user.
5. Setup verifies package identity, driver identity, four endpoint roles, and
   controlled startup before offering Launch.

Keeping the parent unelevated preserves the original user identity even when
UAC is approved with another administrator account. The helper accepts no
arbitrary command, path, certificate, publisher, package identity, or driver
hardware ID.

Setup extracts payloads only into a newly created, version-specific directory
with reparse-point and path-containment checks. It never executes payloads from
Downloads or an unverified temporary path.

## 10. Transaction and Recovery

Before mutation, Setup records whether the exact application certificate,
driver package, device instance, and MSIX were already present.

The transaction states are:

```text
Preflight
Verified
MachineChangesStarted
DriverReady
UserPackageReady
EndpointVerified
Complete
RollbackRequired
```

Rules:

- compatible pre-existing components are preserved;
- incompatible or unrelated pre-existing drivers block installation and are
  never removed automatically;
- rollback removes only certificate, driver, device, and MSIX state created by
  the current attempt;
- a reboot-required result is explicit and resumable;
- rollback failure produces a durable recovery record and exact remediation;
- uninstall removes only the selected EMKE channel and exact matching driver;
  and
- no failure path silently reports success or ready audio.

## 11. Versioning and Artifacts

The compatibility and installer change is a Windows minor release:

```text
Windows product version = 0.2.0
MSIX package version = 0.2.0.0
Driver package version = 1.0.0.2
Channel = internal
```

The delivery inventory is:

1. `EMKE-Translation-Setup-0.2.0-internal-x64.exe`;
2. signed application MSIX;
3. public application certificate only when self-signed Internal trust is
   required;
4. Microsoft-signed driver INF, SYS, and CAT;
5. uninstall/recovery helper;
6. `SHA256SUMS.txt`;
7. source, workflow, signing, and artifact provenance JSON; and
8. a diagnostic-only ZIP for engineering recovery.

The EXE is the normal tester entry point. The raw MSIX and driver files are not
presented as the primary installation workflow.

## 12. Delivery Milestones

### Milestone 1: Windows 10 application compatibility

- Centralize build `19045` metadata.
- Retarget managed projects and MSIX.
- Replace the 25H2 runtime gate.
- Build, sign, and install the application on Windows 10 22H2.
- Keep driver-missing behavior truthful.

### Milestone 2: Windows 10 driver compatibility and runtime completion

- Pin KMDF 1.31 and INF build `19045`.
- Complete device catalog, Translation Session, and native routing adapters.
- Validate driver build and package on Windows 10 and Windows 11.
- Produce the exact Hardware Dev Center submission package.

### Milestone 3: Microsoft driver signing and Setup EXE

- Submit and retrieve the Microsoft-signed driver.
- Verify returned bytes and signer chain.
- Build the two-process Setup transaction and recovery flow.
- Sign and bundle exact immutable payloads.

### Milestone 4: Real-machine acceptance

- Execute installation, upgrade, repair, rollback, and uninstall matrices.
- Execute four-endpoint audio and meeting tests.
- Promote only the exact accepted artifact.

Milestone 1 may produce an Internal preview for the reported Windows 10
machine. It must not be presented as the complete A1 release.

## 13. Test and Evidence Matrix

### Automated gates

- contract tests proving one canonical minimum-build value;
- red-green tests for build `19044` rejection and `19045` acceptance;
- .NET build with Windows platform compatibility analysis;
- application, realtime, routing, settings, diagnostics, and WPF tests;
- native audio unit and process tests;
- INF, KMDF, ABI, catalog-inventory, and driver-package validation;
- MSIX identity, version, architecture, minimum-OS, content, and signature
  verification;
- Setup preflight, elevation-boundary, immutable-request, rollback, existing
  component, reboot, and tamper tests; and
- exact artifact inventory, SHA-256, signer, source commit, and workflow
  provenance.

### Real Windows gates

At minimum, exact signed artifacts are tested on:

- Windows 10 22H2 x64, build `19045`, Secure Boot and Memory Integrity on;
- Windows 11 21H2 x64, build `22000`, as the declared Windows 11 floor;
- Windows 11 23H2 x64, build `22631`;
- Windows 11 24H2 x64, build `26100`; and
- Windows 11 25H2 x64, build `26200` or newer.

The matrix covers clean install, upgrade, repair, uninstall, alternate-admin
UAC, reboot-required recovery, four endpoint discovery, microphone/headphone
routing, inbound fail-open, outbound fail-closed, both Translation sessions,
captions, reconnect, and at least one real meeting application.

Hosted CI, synthetic driver evidence, SignTool verification, or MSIX extraction
does not replace real Windows installation, driver load, endpoint, or meeting
evidence.

## 14. Acceptance Criteria

The Windows `0.2.0-internal` A1 release is complete only when:

1. the Setup EXE rejects builds below `19045`, non-x64 machines, and Windows
   Server product types;
2. Windows 10 build `19045` and the selected Windows 11 matrix install without
   test mode or Secure Boot changes;
3. the driver is Microsoft signed and reports KMDF 1.31-compatible behavior;
4. Setup requires no manual certificate import and performs at most one UAC
   elevation sequence;
5. Setup rollback and uninstall remove only exact state owned by the attempt;
6. the application exposes exactly four usable virtual endpoint roles;
7. real inbound and outbound Translation sessions use the approved protocol;
8. audio safety, bypass, reconnect, and bounded shutdown contracts pass;
9. a real meeting test passes on Windows 10 and Windows 11;
10. downloaded artifact hashes, signers, provenance, and source commit match;
    and
11. no evidence boundary is promoted beyond what was actually run.

## 15. Non-goals

- Windows 10 builds below `19045`.
- Windows ARM64 or x86.
- Windows Server certification.
- Test mode, disabled Secure Boot, or boot-configuration modification.
- Sharing Windows UI or driver code with macOS.
- Claiming that a build-only or self-signed driver artifact is installable in
  A1 normal mode.
- Automatic Store publication or Windows Update driver distribution.

## 16. External References

- [Microsoft: INF Manufacturer section and Windows build decorations](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/inf-manufacturer-section)
- [Microsoft: KMDF version history](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/kmdf-version-history)
- [Microsoft: kernel-mode driver signing policy](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-)
- [Microsoft: MSIX certificate and installation troubleshooting](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide)
- [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
