# EMKE Translation Windows Runtime Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every pending Windows production adapter so the WPF app creates authenticated inbound/outbound Translation sessions, discovers real physical and virtual audio endpoints, preserves the approved protocol and audio safety contracts, and reports stable actionable diagnostics.

**Architecture:** Runtime settings carry only public base-address, model, language, and device choices. A production session factory owns `ISecretStore`, loads a short-lived API-key lease for every initial or reconnecting socket, configures authentication, and disposes the lease immediately after header setup. `TranslationSession` keeps protocol state. A native-backed device catalog exposes stable physical defaults and the four virtual endpoint roles. `TranslationRuntime` remains the sole serialized coordinator of two sessions and native audio; missing driver/endpoints fail before secrets or network are used.

**Tech Stack:** .NET 10, C# 14, ClientWebSocket, MSTest, MMDevice/WASAPI through `EMKE.NativeAudio`, deterministic loopback WebSocket integration tests

## Global Constraints

- Preserve the macOS `v0.2.4` Translation protocol names exactly:
  `session.audio.output.language`, `session.input_audio_buffer.append`,
  `session.output_audio.delta`, `session.input_transcript.delta`, and
  `session.output_transcript.delta`.
- Keep network audio at 24 kHz mono PCM16 and local audio at 48 kHz.
- Keep independent inbound and outbound sessions.
- Preserve inbound original-audio fail-open, outbound microphone fail-closed,
  explicit bypass, reconnect, and bounded shutdown behavior.
- Never place API keys in settings, logs, exceptions, evidence, command lines,
  or test snapshots.
- Driver-missing/incomplete states may open settings and diagnostics but must
  not start audio or connect a socket.
- Use TDD and focused commits; do not broaden this plan into Setup or driver
  installation.

---

### Task 1: Define an Authenticated Translation Session Request Contract

**Files:**

- Modify: `Windows/src/EMKE.Core/RuntimeInterfaces.cs`
- Modify: `Windows/src/EMKE.Application/TranslationRuntime.cs`
- Modify: `Windows/src/EMKE.Application/ChannelSupervisor.cs`
- Modify: `Windows/src/EMKE.Windows.App/Diagnostics/SettingsTranslationCapabilityTester.cs`
- Modify: `Windows/tests/EMKE.Application.Tests/TranslationRuntimeTests.cs`
- Modify: `Windows/tests/EMKE.Application.Tests/TranslationConnectionProbeTests.cs`

- [ ] **Step 1: Write RED tests for secret use and disposal**

Introduce this public-settings-only boundary:

```csharp
public sealed record TranslationSessionRequest(
    Uri BaseAddress,
    TranslationSessionConfiguration Configuration);

public interface ITranslationSessionFactory
{
    ValueTask<ITranslationSession> CreateAsync(
        TranslationSessionRequest request,
        CancellationToken cancellationToken);
}
```

The production factory, not the request, owns `ISecretStore`. Use the existing
zeroizable `ISecretBuffer` lease for every socket creation. Tests must prove
two independent initial sessions and every reconnect load their own usable
lease, each lease is disposed immediately after header configuration, and no
factory call occurs for a missing driver or incomplete endpoints. A missing key
must fail session creation with a stable authentication error.

```bash
dotnet test Windows/tests/EMKE.Application.Tests/EMKE.Application.Tests.csproj --configuration Release --filter "FullyQualifiedName~TranslationRuntimeTests|FullyQualifiedName~TranslationConnectionProbeTests"
```

Expected: FAIL because the runtime currently disposes the key before calling a
factory that cannot accept authentication or base address.

- [ ] **Step 2: Carry public endpoint settings without leaking secrets**

Extend runtime composition settings to retain `BaseUri` and `ModelId` from
`WindowsProductSettings`. Keep endpoint IDs in audio configuration, not in the
session protocol request. `ChannelSupervisor` stores an immutable
`TranslationSessionRequest`, so every reconnect asks the factory for a fresh
authenticated socket and observes current Credential Manager state.

- [ ] **Step 3: Prove missing-prerequisite ordering**

Add ordered fakes and assert:

```text
host -> settings -> driver -> endpoint catalog -> session factory/secret ->
session handshake -> audio
```

No later dependency may be invoked when an earlier prerequisite fails.

- [ ] **Step 4: Run and commit the contract change**

```bash
dotnet test Windows/tests/EMKE.Application.Tests/EMKE.Application.Tests.csproj --configuration Release
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~Settings"
git diff --check
git add Windows/src/EMKE.Core/RuntimeInterfaces.cs Windows/src/EMKE.Application/TranslationRuntime.cs Windows/src/EMKE.Application/ChannelSupervisor.cs Windows/src/EMKE.Windows.App/Diagnostics/SettingsTranslationCapabilityTester.cs Windows/tests/EMKE.Application.Tests/TranslationRuntimeTests.cs Windows/tests/EMKE.Application.Tests/TranslationConnectionProbeTests.cs
git commit -m "refactor: pass authenticated translation connection safely"
```

### Task 2: Implement the Production Authenticated WebSocket Factory

**Files:**

- Modify: `Windows/src/EMKE.Realtime/TranslationSocket.cs`
- Modify: `Windows/src/EMKE.Realtime/TranslationSession.cs`
- Create: `Windows/src/EMKE.Realtime/TranslationSessionFactory.cs`
- Modify: `Windows/tests/EMKE.Realtime.Tests/TranslationSocketTests.cs`
- Modify: `Windows/tests/EMKE.Realtime.Tests/TranslationSessionTests.cs`
- Create: `Windows/tests/EMKE.Realtime.Tests/TranslationSessionFactoryTests.cs`
- Modify: `Windows/tests/EMKE.Integration.Tests/MockTranslationServer.cs`
- Modify: `Windows/tests/EMKE.Integration.Tests/TranslationRuntimeIntegrationTests.cs`

- [ ] **Step 1: Write RED authentication and endpoint tests**

Require the factory to build:

```text
wss://api.example.test/realtime/translations?model=gpt-realtime-translate
Authorization: Bearer [owned secret supplied by the test fixture]
```

Tests must prove header injection occurs before connect, the key is absent from
the URI and exceptions, an invalid non-HTTPS/WSS base is rejected, and inbound
and outbound factory calls return distinct sessions.

```bash
dotnet test Windows/tests/EMKE.Realtime.Tests/EMKE.Realtime.Tests.csproj --configuration Release --filter "FullyQualifiedName~TranslationSessionFactoryTests|FullyQualifiedName~TranslationSocketTests"
```

Expected: FAIL because `ClientWebSocketAdapter` has no authenticated
configuration path.

- [ ] **Step 2: Add a narrow socket configurator**

Extend the internal adapter, not the public protocol interface:

```csharp
internal interface IClientWebSocket
{
    void SetRequestHeader(string name, string value);
    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);
    // existing send/receive members
}
```

`ClientWebSocketAdapter.SetRequestHeader` delegates to
`ClientWebSocket.Options.SetRequestHeader`. Reject CR/LF and empty secrets
before setting the header. Do not log header values.

- [ ] **Step 3: Implement the production factory**

`TranslationSessionFactory` is constructed with `ISecretStore` and the fixed
credential name `translationApiKey`. `CreateAsync` validates the request,
loads one secret lease, creates the endpoint via `TranslationEndpoint.Create`,
configures one fresh socket with authorization, constructs
`TranslationSession`, disposes the secret lease in `finally`, and disposes
partial socket state if construction fails. `ClientWebSocket` requires a
transient managed header string; scope it only to that socket, never expose or
log it, and release it when the session/socket is disposed. Connection remains
lazy until the runtime invokes `ConnectAsync`.

- [ ] **Step 4: Prove exact official event names end to end**

The loopback server test must observe one inbound and one outbound handshake
using `session.audio.output.language`, receive
`session.input_audio_buffer.append`, and send all four delta event types:

```text
session.output_audio.delta
session.input_transcript.delta
session.output_transcript.delta
session.completed
```

Assert audio/captions reach the correct runtime consumers and a legacy or
invented alias fails the codec.

- [ ] **Step 5: Run and commit the production factory**

```bash
dotnet test Windows/tests/EMKE.Realtime.Tests/EMKE.Realtime.Tests.csproj --configuration Release
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~TranslationRuntimeIntegrationTests"
git diff --check
git add Windows/src/EMKE.Realtime Windows/tests/EMKE.Realtime.Tests Windows/tests/EMKE.Integration.Tests/MockTranslationServer.cs Windows/tests/EMKE.Integration.Tests/TranslationRuntimeIntegrationTests.cs
git commit -m "feat: compose authenticated translation sessions"
```

### Task 3: Expose a Real Native-Backed Audio Device Catalog

**Files:**

- Modify: `Windows/native/EMKE.NativeAudio/include/emke_native_audio.h`
- Modify: `Windows/native/EMKE.NativeAudio/src/device_catalog.hpp`
- Modify: `Windows/native/EMKE.NativeAudio/src/device_catalog.cpp`
- Modify: `Windows/native/EMKE.NativeAudio/src/native_audio_api.cpp`
- Modify: `Windows/native/EMKE.NativeAudio.Tests/src/device_catalog_tests.cpp`
- Modify: `Windows/native/EMKE.NativeAudio.Tests/src/endpoint_snapshot_tests.cpp`
- Modify: `Windows/src/EMKE.Platform/Native/NativeAudioTypes.cs`
- Modify: `Windows/src/EMKE.Platform/Native/NativeAudioMethods.cs`
- Create: `Windows/src/EMKE.Platform/Native/WindowsAudioDeviceCatalog.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/WindowsAudioDeviceCatalogTests.cs`

- [ ] **Step 1: Write RED catalog mapping tests**

The managed catalog must return immutable descriptors for available physical
inputs/outputs plus the four virtual roles, with stable endpoint IDs,
direction, active/default flags, and user-facing labels. It must reject
truncated native strings, duplicate IDs, unknown roles, and a native ABI-size
mismatch.

```bash
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~WindowsAudioDeviceCatalogTests"
```

Expected: FAIL because `PendingAudioDeviceCatalog` returns an empty snapshot
and the current native snapshot exposes only default physical IDs.

- [ ] **Step 2: Extend the C ABI without exposing COM to managed code**

Add a versioned enumeration API with caller-owned fixed buffers:

```c
typedef struct emke_audio_endpoint_descriptor_v1 {
  uint32_t size;
  uint32_t direction;
  uint32_t flags;
  wchar_t id[512];
  wchar_t name[256];
  wchar_t role[64];
} emke_audio_endpoint_descriptor_v1;

emke_audio_status emke_audio_enumerate_endpoints_v1(
  emke_audio_endpoint_descriptor_v1* items,
  uint32_t capacity,
  uint32_t* required_count);
```

Use a two-call required-count pattern. Enumerate active MMDevice endpoints on
an MTA worker, preserve exact endpoint IDs, and mark physical defaults and the
four EMKE roles. Keep all allocation/COM lifetime native.

- [ ] **Step 3: Implement managed validation and mapping**

`WindowsAudioDeviceCatalog` performs the count call, applies a sane maximum,
allocates the fixed array, retries once on count growth, validates every size
and terminator, then maps to `AudioDeviceSnapshot`. Return a stable
`RuntimeError` from the caller boundary for native failure; never substitute
an empty successful snapshot.

- [ ] **Step 4: Prove native and managed behavior**

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release --output-on-failure
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~WindowsAudioDeviceCatalogTests|FullyQualifiedName~NativeAudio"
```

Expected: native fakes and managed tests pass; a real-Windows diagnostic run
lists stable physical defaults and exactly four virtual roles when the driver
is installed.

- [ ] **Step 5: Commit the catalog adapter**

```bash
git add Windows/native/EMKE.NativeAudio Windows/native/EMKE.NativeAudio.Tests Windows/src/EMKE.Platform/Native Windows/tests/EMKE.Integration.Tests/WindowsAudioDeviceCatalogTests.cs
git commit -m "feat: expose Windows audio device catalog"
```

### Task 4: Replace Pending Production Composition

**Files:**

- Modify: `Windows/src/EMKE.Windows.App/Bootstrap/ProductionAppAdapterFactory.cs`
- Modify: `Windows/src/EMKE.Routing/OfflineLanguageClassifier.cs`
- Modify: `Windows/tests/EMKE.Windows.App.Tests/ProductionCompositionTests.cs`
- Modify: `Windows/tests/EMKE.Routing.Tests/OfflineLanguageClassifierTests.cs`
- Modify: `Windows/tests/EMKE.Windows.App.Tests/DiagnosticsProductionIntegrationTests.cs`

- [ ] **Step 1: Write a RED no-pending-adapters test**

The production-composition test must instantiate adapters with injectable
native/socket/secret fakes and assert concrete types:

```text
WindowsAudioDeviceCatalog
TranslationSessionFactory
OfflineLanguageClassifier
NativeAudioEngine
WindowsDriverManager
WindowsSettingsStore
CredentialManagerSecretStore
```

Add a source guard that fails for `PendingAudioDeviceCatalog`,
`PendingTranslationSessionFactory`, `PendingLanguageClassifier`, or the
message `composition is not available`.

- [ ] **Step 2: Compose real adapters and preserve disposal order**

Remove the three nested pending classes. Ensure lifetime shutdown order is:

```text
stop runtime -> close sessions -> stop native audio -> release diagnostics -> dispose secrets/settings
```

The language classifier must use the checked-in approved profile resource and
fail closed to the existing route-lock policy when the profile is missing or
invalid; it must not return fabricated equal probabilities.

- [ ] **Step 3: Prove settings diagnostics use the same factory**

`SettingsTranslationCapabilityTester` must load the same base URI/model/key
contract as runtime, create a disposable probe session, complete the
Translation handshake, close within the bounded timeout, and map auth,
network, protocol, and cancellation failures to stable codes.

- [ ] **Step 4: Run and commit production composition**

```bash
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~ProductionCompositionTests|FullyQualifiedName~DiagnosticsProductionIntegrationTests"
dotnet test Windows/tests/EMKE.Routing.Tests/EMKE.Routing.Tests.csproj --configuration Release
rg -n "Pending(AudioDeviceCatalog|TranslationSessionFactory|LanguageClassifier)|composition is not available" Windows/src
git diff --check
```

Expected: tests pass and `rg` returns no production matches.

```bash
git add Windows/src/EMKE.Windows.App/Bootstrap/ProductionAppAdapterFactory.cs Windows/src/EMKE.Routing/OfflineLanguageClassifier.cs Windows/tests/EMKE.Windows.App.Tests Windows/tests/EMKE.Routing.Tests/OfflineLanguageClassifierTests.cs
git commit -m "feat: complete Windows production runtime composition"
```

### Task 5: Verify Safety, Reconnect, and Failure Diagnostics

**Files:**

- Modify: `Windows/src/EMKE.Core/RuntimeError.cs`
- Modify: `Windows/src/EMKE.Application/ChannelSupervisor.cs`
- Modify: `Windows/src/EMKE.Application/TranslationRuntime.cs`
- Modify: `Windows/src/EMKE.Platform/Diagnostics/WindowsAudioDiagnostics.cs`
- Modify: `Windows/tests/EMKE.Integration.Tests/FailureSafetyTests.cs`
- Modify: `Windows/tests/EMKE.Application.Tests/ChannelSupervisorTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/ProductionFailureMatrixTests.cs`
- Create: `docs/quality/windows-runtime-completion-evidence.md`

- [ ] **Step 1: Write deterministic RED failure-matrix tests**

Cover OS, product type, driver missing/signature/ABI/version/endpoints, device
loss, missing key, HTTP auth failure, DNS/TLS/socket failure, protocol alias,
server close, audio queue backpressure, reconnect exhaustion, and shutdown
timeout. For each case assert safe route, user recovery action, and sanitized
log fields.

- [ ] **Step 2: Stabilize diagnostic categories**

Use separate error codes for `host`, `driver`, `device`, `authentication`,
`network`, `protocol`, and `audio`. Safe fields may contain build, driver
version, endpoint role, error code, retry count, and duration; they may not
contain API key, complete URI query, raw endpoint ID, transcript, or PCM.

- [ ] **Step 3: Prove safety contracts under repetition**

Run at least 100 deterministic iterations each for:

```text
inbound session loss -> original audio fail-open
outbound session loss -> virtual microphone silent fail-closed
explicit bypass before loss -> bypass remains explicit
reconnect success -> translated route resumes only after handshake
shutdown -> completes within configured bound with no resource leak
```

- [ ] **Step 4: Run the full runtime gate**

```bash
node Scripts/validate-shared-contracts.mjs
dotnet build Windows/EMKE.Windows.slnx --configuration Release
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build
rg -n "session\.audio\.output\.language|session\.input_audio_buffer\.append|session\.output_audio\.delta|session\.input_transcript\.delta|session\.output_transcript\.delta" Windows/src/EMKE.Realtime Windows/tests
git diff --check
```

Expected: all managed tests pass and all five required protocol names remain
present in production codec and tests.

- [ ] **Step 5: Record proof boundaries and commit**

Document automated results and state separately whether a live provider,
installed driver, physical device, meeting app, and listening test were run.

```bash
git add Windows/src/EMKE.Core/RuntimeError.cs Windows/src/EMKE.Application Windows/src/EMKE.Platform/Diagnostics Windows/tests/EMKE.Application.Tests Windows/tests/EMKE.Integration.Tests docs/quality/windows-runtime-completion-evidence.md
git commit -m "test: verify Windows runtime safety matrix"
```
