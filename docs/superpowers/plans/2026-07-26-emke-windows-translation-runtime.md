# EMKE Windows Translation Runtime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a headless Windows Translation runtime that consumes the shared contract, drives two independent Text WebSocket sessions and the native audio ABI, and preserves all routing, reconnection, shutdown, and failure-safety semantics.

**Architecture:** Pure C# projects define domain values, realtime protocol, routing, and a single serialized `TranslationRuntime`. Platform adapters sit behind interfaces. The runtime receives commands through a bounded channel, tags asynchronous work with a generation ID, publishes immutable versioned snapshots, polls native audio from a background task, and never executes managed code on WASAPI callbacks.

**Tech Stack:** .NET 10, C# 14, `ClientWebSocket`, `System.Threading.Channels`, `System.Text.Json`, MSTest, C++ C ABI from `EMKE.NativeAudio`, ASP.NET Core test host for local WebSocket fixtures

## Global Constraints

- Target framework is `net10.0-windows10.0.26100.0`; runtime start additionally gates OS build 26200.
- No WPF reference is allowed in `EMKE.Core`, `EMKE.Realtime`, `EMKE.Routing`, or `EMKE.Application`.
- No third-party MVVM, retry, WebSocket, or JSON package is added.
- Every client protocol event uses `WebSocketMessageType.Text`.
- Normal two-language translation owns two independent sessions; same-language outbound owns no socket.
- Network PCM is 24 kHz mono signed little-endian PCM16; batches are exactly 9,600 bytes.
- Inbound failure is original-audio fail-open; outbound failure is muted fail-closed.
- Only explicit user bypass permits physical microphone audio to reach the virtual microphone.
- Reconnect schedule is exactly 250 ms, 500 ms, 1 s, 2 s, 5 s.
- Close deadline is one second and starts before any potentially blocking close send.
- Audio/control queues are bounded; full queues increment counters and degrade instead of growing.
- API keys and Authorization values never appear in snapshots, exceptions, logs, fixtures, or debugger display.
- All source tests use fake clocks, fake sockets, and fake native audio. Real network/device evidence is a separate gate.

---

### Task 1: Scaffold the Managed Runtime Projects

**Files:**
- Modify: `Windows/EMKE.Windows.slnx`
- Create: `Windows/Directory.Build.props`
- Create: `Windows/src/EMKE.Core/EMKE.Core.csproj`
- Create: `Windows/src/EMKE.Realtime/EMKE.Realtime.csproj`
- Create: `Windows/src/EMKE.Routing/EMKE.Routing.csproj`
- Create: `Windows/src/EMKE.Application/EMKE.Application.csproj`
- Create: `Windows/src/EMKE.Platform/EMKE.Platform.csproj`
- Create: `Windows/tests/EMKE.Core.Tests/EMKE.Core.Tests.csproj`
- Create: `Windows/tests/EMKE.Contract.Tests/EMKE.Contract.Tests.csproj`
- Create: `Windows/tests/EMKE.Realtime.Tests/EMKE.Realtime.Tests.csproj`
- Create: `Windows/tests/EMKE.Routing.Tests/EMKE.Routing.Tests.csproj`
- Create: `Windows/tests/EMKE.Application.Tests/EMKE.Application.Tests.csproj`
- Create: `Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj`

**Interfaces:**
- Dependency direction: Core ← Realtime/Routing ← Application ← Platform.
- Tests reference only the product projects they exercise.

- [ ] **Step 1: Set repository-wide managed build rules**

`Windows/Directory.Build.props` must set:

```xml
<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<PlatformTarget>x64</PlatformTarget>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisLevel>latest-all</AnalysisLevel>
<Deterministic>true</Deterministic>
<ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
```

Allow test projects to override analyzer warnings that are specific to MSTest-generated code, but do not disable nullable or warning-as-error globally.

- [ ] **Step 2: Create projects**

Run:

```powershell
dotnet new classlib -n EMKE.Core -o Windows/src/EMKE.Core
dotnet new classlib -n EMKE.Realtime -o Windows/src/EMKE.Realtime
dotnet new classlib -n EMKE.Routing -o Windows/src/EMKE.Routing
dotnet new classlib -n EMKE.Application -o Windows/src/EMKE.Application
dotnet new classlib -n EMKE.Platform -o Windows/src/EMKE.Platform

dotnet new mstest -n EMKE.Core.Tests -o Windows/tests/EMKE.Core.Tests
dotnet new mstest -n EMKE.Contract.Tests -o Windows/tests/EMKE.Contract.Tests
dotnet new mstest -n EMKE.Realtime.Tests -o Windows/tests/EMKE.Realtime.Tests
dotnet new mstest -n EMKE.Routing.Tests -o Windows/tests/EMKE.Routing.Tests
dotnet new mstest -n EMKE.Application.Tests -o Windows/tests/EMKE.Application.Tests
dotnet new mstest -n EMKE.Integration.Tests -o Windows/tests/EMKE.Integration.Tests
```

Add every project to `Windows/EMKE.Windows.slnx`.

- [ ] **Step 3: Set project references**

Use only:

```text
EMKE.Realtime -> EMKE.Core
EMKE.Routing -> EMKE.Core
EMKE.Application -> EMKE.Core, EMKE.Realtime, EMKE.Routing
EMKE.Platform -> EMKE.Core, EMKE.Application
```

Delete every template `Class1.cs` and `UnitTest1.cs`.

- [ ] **Step 4: Prove the scaffold**

```powershell
dotnet restore Windows/EMKE.Windows.slnx --locked-mode
dotnet build Windows/EMKE.Windows.slnx --configuration Release --no-restore
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build
```

If no lock file exists on the first run, run `dotnet restore --use-lock-file` once, commit all `packages.lock.json` files, then use `--locked-mode`.

- [ ] **Step 5: Commit**

```powershell
git add Windows
git commit -m "build: scaffold Windows translation runtime"
```

### Task 2: Implement Core Domain Values and Immutable Snapshots

**Files:**
- Create: `Windows/src/EMKE.Core/LanguageCode.cs`
- Create: `Windows/src/EMKE.Core/AppSnapshot.cs`
- Create: `Windows/src/EMKE.Core/RuntimeError.cs`
- Create: `Windows/src/EMKE.Core/RuntimeCommand.cs`
- Create: `Windows/src/EMKE.Core/RuntimeInterfaces.cs`
- Create: `Windows/tests/EMKE.Core.Tests/AppSnapshotTests.cs`
- Create: `Windows/tests/EMKE.Core.Tests/RuntimeErrorTests.cs`

**Interfaces:**
- Stable string values match `Shared/Contracts/v1/app-state.schema.json`.
- Snapshot is immutable and contains no secret.

- [ ] **Step 1: Write failing stable-value tests**

Assert exact values for:

```text
RuntimeState: stopped, starting, running, stopping, degraded, failed
ChannelState: inactive, connecting, connected, reconnecting, bypassed, degraded, failed
InboundRoute: stopped, translated, originalFailOpen, originalBypass
OutboundRoute: stopped, translated, mutedFailClosed, originalBypass
ErrorCategory and RecoveryAction: every schema value
LanguageCode: zh, en, de
```

Serialize a stopped snapshot and compare its keys and values with the shared schema fixture.

- [ ] **Step 2: Implement closed value types**

Use enums internally and explicit JSON converters for the stable lowercase/camel-case strings. Do not rely on `enum.ToString()`.

`AppSnapshot` is a sealed immutable record with:

```csharp
public sealed record AppSnapshot(
    int ContractVersion,
    ulong Version,
    RuntimeState RuntimeState,
    ChannelState InboundChannelState,
    ChannelState OutboundChannelState,
    InboundRoute InboundRoute,
    OutboundRoute OutboundRoute,
    double InboundLevel,
    double OutboundLevel,
    string SourceCaption,
    string TranslatedCaption,
    AudioSelection AudioSelection,
    DriverCompatibility DriverCompatibility,
    TranslationCompatibilityReport? ConnectionReport,
    AudioDiagnostics AudioDiagnostics,
    UpdateAvailability UpdateAvailability,
    RuntimeError? Error);
```

Clamp levels to `[0, 1]` at the construction boundary. `Version` must increase for each published mutation.

- [ ] **Step 3: Define stable errors**

`RuntimeError` contains only:

```text
ErrorCategory Category
string Code
IReadOnlyDictionary<string, string> Parameters
RecoveryAction RecoveryAction
```

Its constructor rejects keys named `authorization`, `apiKey`, `token`, and values matching the regular expression `sk-[A-Za-z0-9_-]{16,}`.

- [ ] **Step 4: Define ports**

Add the interfaces:

```text
ITranslationSession
ITranslationSessionFactory
ITranslationAudioEngine
IAudioDeviceCatalog
IAudioDiagnostics
ILanguageClassifier
ISecretStore
ISettingsStore
IOnboardingProgressStore
IDriverManager
IUpdateService
IClock
IRuntimeLog
```

All asynchronous APIs accept `CancellationToken`. Secret loading returns a disposable secret buffer abstraction and never `ToString()`s the key.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Core.Tests/EMKE.Core.Tests.csproj --configuration Release
git add Windows/src/EMKE.Core Windows/tests/EMKE.Core.Tests
git commit -m "feat: define Windows runtime domain contract"
```

### Task 3: Consume Every Shared Contract Fixture

**Files:**
- Create: `Windows/tests/EMKE.Contract.Tests/RepositoryPaths.cs`
- Create: `Windows/tests/EMKE.Contract.Tests/SharedFixtureTests.cs`
- Create: `Windows/tests/EMKE.Contract.Tests/StableValueTests.cs`
- Modify: `Windows/tests/EMKE.Contract.Tests/EMKE.Contract.Tests.csproj`
- Consume: `Shared/Contracts/`
- Consume: `Shared/TestVectors/`

**Interfaces:**
- The test project reads canonical files from `Shared/`; no copied expected data.

- [ ] **Step 1: Write a failing inventory test**

Resolve repository root by walking upward from `AppContext.BaseDirectory` until `Shared/Contracts/contract-manifest.json` exists. Fail after eight parents with a message that does not include the absolute path.

Assert:

```text
contractVersion == 1
schema count == 3
fixture count == 8
every inventory entry exists
every fixture has contractVersion, fixtureId, category
```

- [ ] **Step 2: Add stable-value tests**

Parse `app-state.schema.json` and assert each C# converter emits exactly the declared enum values. Parse `translation-events.schema.json` and assert the protocol event registry has exactly the declared event types.

- [ ] **Step 3: Add fixture dispatch**

Create one test method per fixture category:

```text
Realtime -> EMKE.Realtime fixture adapters
Routing -> EMKE.Routing fixture adapters
Audio -> native-audio fixture adapter
Settings -> EMKE.Core compatibility/migration adapter
```

Initially mark an adapter result as `Assert.Inconclusive` only until its owning task in this same plan is implemented. Remove all inconclusive results before Task 10.

- [ ] **Step 4: Run inventory tests**

```powershell
dotnet test Windows/tests/EMKE.Contract.Tests/EMKE.Contract.Tests.csproj `
  --configuration Release `
  --filter "TestCategory=Inventory"
```

- [ ] **Step 5: Commit**

```powershell
git add Windows/tests/EMKE.Contract.Tests
git commit -m "test: consume shared contract fixtures on Windows"
```

### Task 4: Implement Endpoint Construction and Text WebSocket Transport

**Files:**
- Create: `Windows/src/EMKE.Realtime/TranslationEndpoint.cs`
- Create: `Windows/src/EMKE.Realtime/TranslationSocket.cs`
- Create: `Windows/src/EMKE.Realtime/TranslationEventCodec.cs`
- Create: `Windows/tests/EMKE.Realtime.Tests/TranslationEndpointTests.cs`
- Create: `Windows/tests/EMKE.Realtime.Tests/TranslationSocketTests.cs`
- Create: `Windows/tests/EMKE.Realtime.Tests/TranslationEventCodecTests.cs`

**Interfaces:**
- Accepts HTTPS/WSS base URL with host.
- Sends every JSON event as one or more Text frames ending in `endOfMessage = true`.

- [ ] **Step 1: Write failing endpoint tests**

Cover:

```text
https becomes wss
wss remains wss
http/ws/file/relative/missing host are rejected
existing base path is preserved
one slash is inserted before realtime/translations
model ID is query encoded
existing query/fragment is rejected rather than ambiguously merged
```

- [ ] **Step 2: Implement endpoint construction**

Use `UriBuilder`, preserve the normalized base path, append:

```text
$"/realtime/translations?model={Uri.EscapeDataString(modelId)}"
```

Return a typed configuration error, not `UriFormatException`, to callers.

- [ ] **Step 3: Write failing frame-type tests**

Use an internal `IClientWebSocket` adapter in tests. Assert `session.update`, `input_audio_buffer.append`, and `session.close` all call:

```csharp
SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken)
```

Add a negative test whose fake reports Binary and expect protocol error `binaryTranslationEvent`.

- [ ] **Step 4: Implement socket and codec**

The codec uses source-generated `System.Text.Json` metadata. It rejects:

```text
unknown event types
missing required payload
invalid base64
audio payload with odd byte count
Binary server frames
messages above the fixed receive limit
```

The socket assembles fragmented Text frames into a bounded preallocated buffer and clears it after every complete event.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Realtime.Tests/EMKE.Realtime.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Endpoint|FullyQualifiedName~Socket|FullyQualifiedName~Codec"
git add Windows/src/EMKE.Realtime Windows/tests/EMKE.Realtime.Tests
git commit -m "feat: add Windows Translation text transport"
```

### Task 5: Implement Translation Session Handshake, Batching, and Close

**Files:**
- Create: `Windows/src/EMKE.Realtime/PcmFrameBatcher.cs`
- Create: `Windows/src/EMKE.Realtime/TranslationSession.cs`
- Create: `Windows/src/EMKE.Realtime/SessionCloseCoordinator.cs`
- Create: `Windows/tests/EMKE.Realtime.Tests/PcmFrameBatcherTests.cs`
- Create: `Windows/tests/EMKE.Realtime.Tests/TranslationSessionTests.cs`
- Create: `Windows/tests/EMKE.Realtime.Tests/SessionCloseCoordinatorTests.cs`

**Interfaces:**
- Session state: disconnected → connecting → created → updating → connected → closing → closed/failed.
- Close completion is shared by all callers for one generation.

- [ ] **Step 1: Write failing shared batching tests**

Drive `Shared/TestVectors/Audio/pcm-batching.json`. Enforce:

```text
frameBytes = 9600
odd byte count rejected
partial data retained until a complete frame
stop discards an incomplete remainder
```

- [ ] **Step 2: Implement the bounded batcher**

Use one 9,600-byte frame buffer and one integer offset. When a frame completes, copy it into the caller-provided send buffer or invoke an async send before accepting more input. Do not accumulate an unbounded list.

- [ ] **Step 3: Write failing handshake tests**

Drive `Realtime/text-frame-handshake.json`. Prove:

```text
session.created precedes session.update
session.update is Text
connected requires session.updated
audio before connected is rejected
unexpected event order is protocol failure
same-language policy is handled above session creation
```

- [ ] **Step 4: Implement the session**

One receive loop owns decoding and emits typed events through a bounded channel. Audio deltas remain pooled byte owners and are disposed after the consumer acknowledges them.

- [ ] **Step 5: Write failing close tests**

Use fake clock and fake blocking socket to prove:

```text
deadline starts before close send
deadline is 1000 ms
tail audio before session.closed is delivered
all close callers await one Task
old generation completion cannot mutate a new session
```

- [ ] **Step 6: Implement close coordination**

Create the deadline cancellation source before sending `session.close`. Race receive completion against the fake-clock delay. Dispose socket resources exactly once in a `finally`.

- [ ] **Step 7: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Realtime.Tests/EMKE.Realtime.Tests.csproj --configuration Release
git add Windows/src/EMKE.Realtime Windows/tests/EMKE.Realtime.Tests
git commit -m "feat: add Windows Translation session lifecycle"
```

### Task 6: Implement Routing, VAD, Levels, and Offline Language Classification

**Files:**
- Create: `Windows/src/EMKE.Routing/InboundLanguageGate.cs`
- Create: `Windows/src/EMKE.Routing/InboundUtteranceBuffer.cs`
- Create: `Windows/src/EMKE.Routing/PcmVoiceActivityDetector.cs`
- Create: `Windows/src/EMKE.Routing/PcmLevelMeter.cs`
- Create: `Windows/src/EMKE.Routing/OfflineLanguageClassifier.cs`
- Create: `Windows/src/EMKE.Routing/Resources/language-profile-v1.json`
- Create: `Windows/src/EMKE.Routing/Resources/THIRD_PARTY_NOTICES.md`
- Create: `Windows/tests/EMKE.Routing.Tests/InboundLanguageGateTests.cs`
- Create: `Windows/tests/EMKE.Routing.Tests/InboundUtteranceBufferTests.cs`
- Create: `Windows/tests/EMKE.Routing.Tests/PcmVoiceActivityDetectorTests.cs`
- Create: `Windows/tests/EMKE.Routing.Tests/PcmLevelMeterTests.cs`
- Create: `Windows/tests/EMKE.Routing.Tests/OfflineLanguageClassifierTests.cs`

**Interfaces:**
- Classifier is offline and returns probabilities for only `zh`, `en`, and `de`.
- Gate consumes primary-tag probabilities and shared timing thresholds.

- [ ] **Step 1: Write failing routing fixture tests**

Drive both routing fixtures and assert all expected route states. Use a fake monotonic clock so 250 ms and 500 ms boundaries are exact.

- [ ] **Step 2: Implement primary-tag aggregation and thresholds**

Rules:

```text
lowercase and split BCP-47 tag at "-"
sum regional variants by primary tag
clamp each sum to 1.0
native >= 0.75 -> original
any non-native >= 0.60 -> translated
undecided voiced at 250 ms -> translated
undecided unvoiced at 250 ms -> original
after VAD end wait 500 ms
late audio or transcript restarts the 500 ms window
```

- [ ] **Step 3: Port VAD and level semantics**

Read the macOS source implementations and shared PCM fixtures. Preserve exact:

```text
odd-byte rejection
24 kHz default sample rate
normalized level range
attack/release behavior
speech start/end thresholds
reset behavior
```

Add cross-platform expected values to shared fixtures only if the current contract does not already describe them; such a change requires both platform contract suites before merge.

- [ ] **Step 4: Build the offline language profile**

Create a deterministic character n-gram profile limited to zh/en/de:

```text
zh: Unicode Han ratio plus profile
en/de: normalized 1- to 3-character profile
output: three nonnegative probabilities summing to 1.0
low evidence: all values below decision thresholds
```

Build `language-profile-v1.json` from redistributable public-domain or permissively licensed text. Record source URLs, license, corpus hash, generator version, and generated model hash in `THIRD_PARTY_NOTICES.md`. Do not include raw corpus text in the application package.

- [ ] **Step 5: Compare with macOS decisions**

Create a synthetic/redacted golden corpus with at least:

```text
100 zh utterances
100 en utterances
100 de utterances
60 short/ambiguous utterances
```

Store only redistributable sentences or generated text under `Shared/TestVectors/Routing/LanguageCorpus/`. Compare final route decisions, not raw classifier probabilities. Required agreement with macOS is at least 99%; low-confidence disagreements must remain undecided until the normal cutoff.

- [ ] **Step 6: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Routing.Tests/EMKE.Routing.Tests.csproj --configuration Release
dotnet test Windows/tests/EMKE.Contract.Tests/EMKE.Contract.Tests.csproj --configuration Release
git add Windows/src/EMKE.Routing Windows/tests/EMKE.Routing.Tests Shared/TestVectors/Routing
git commit -m "feat: implement Windows routing semantics"
```

### Task 7: Bind the Native Audio ABI Without Managed Realtime Callbacks

**Files:**
- Create: `Windows/src/EMKE.Platform/Native/NativeAudioMethods.cs`
- Create: `Windows/src/EMKE.Platform/Native/NativeAudioTypes.cs`
- Create: `Windows/src/EMKE.Platform/Native/NativeAudioEngine.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/NativeAudioAbiTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/NativeAudioPollingTests.cs`

**Interfaces:**
- `NativeAudioEngine` implements `ITranslationAudioEngine`.
- C# owns one polling task; C++ owns every realtime callback.

- [ ] **Step 1: Write ABI mismatch and lifetime tests**

With the fake native DLL:

```text
ABI 1 loads
ABI 2 returns driver/native incompatibility
SafeHandle releases exactly once
failed create does not leak
stop is idempotent
dispose cancels and joins poll task
```

- [ ] **Step 2: Generate exact P/Invoke definitions**

Use source-generated `[LibraryImport]`, `CallingConvention.Cdecl`, blittable fixed-layout structs, and `SafeHandle`. Validate `Marshal.SizeOf<T>()` against native exported size constants during tests.

- [ ] **Step 3: Implement polling**

One background task:

```text
polls at bounded cadence when no event exists
copies event metadata immediately
wraps PCM bytes in pooled memory
writes to a bounded Channel with capacity 64
increments dropped event counters if full
never passes managed delegates to native code
```

- [ ] **Step 4: Run with fake and real DLL**

```powershell
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~NativeAudio"
```

Expected: fake tests always pass; real-DLL ABI test passes on Windows x64 builder without opening devices.

- [ ] **Step 5: Commit**

```powershell
git add Windows/src/EMKE.Platform/Native Windows/tests/EMKE.Integration.Tests
git commit -m "feat: bind Windows native audio runtime"
```

### Task 8: Implement the Single Serialized TranslationRuntime

**Files:**
- Create: `Windows/src/EMKE.Application/TranslationRuntime.cs`
- Create: `Windows/src/EMKE.Application/RuntimeStateReducer.cs`
- Create: `Windows/src/EMKE.Application/ChannelSupervisor.cs`
- Create: `Windows/src/EMKE.Application/RuntimeSnapshotPublisher.cs`
- Create: `Windows/tests/EMKE.Application.Tests/TranslationRuntimeTests.cs`
- Create: `Windows/tests/EMKE.Application.Tests/RuntimeStateReducerTests.cs`
- Create: `Windows/tests/EMKE.Application.Tests/ChannelSupervisorTests.cs`

**Interfaces:**
- Commands enter one bounded channel.
- Snapshots leave through `IObservable<AppSnapshot>` or an event that carries a complete immutable snapshot.

- [ ] **Step 1: Write failing serialization tests**

Use controlled tasks to prove:

```text
start and stop never overlap mutation
second start while starting is coalesced
window subscribers receive the same snapshot version
no subscriber can mutate a snapshot
old generation device result cannot overwrite new generation
old generation reconnect cannot reopen a stopped session
```

- [ ] **Step 2: Implement command loop**

Use `Channel.CreateBounded<RuntimeCommand>` with:

```text
capacity = 64
single reader = true
multiple writers = true
full mode = DropWrite
```

Critical commands (`Stop`, `Exit`) use a separate single-slot priority path so a full telemetry queue cannot block safety shutdown.

- [ ] **Step 3: Write failing start/rollback tests**

Prove exact sequence:

```text
OS gate
settings and secret validation
driver compatibility
physical/virtual device validation
native audio start
inbound session connect
outbound session connect unless same-language
running snapshot
```

Failure unwinds only completed steps. Outbound connection failure keeps inbound running and sets outbound `mutedFailClosed`.

- [ ] **Step 4: Implement independent channel supervisors**

Each supervisor owns:

```text
session
send batcher
receive loop
reconnect task
close task
generation
channel state
```

Transient network faults use exact backoff schedule. Authentication, configuration, endpoint/model, protocol, permission, driver, and format errors do not blindly retry.

- [ ] **Step 5: Write failing stop tests**

Prove:

```text
new audio is rejected first
two session closes start concurrently
tail audio is delivered before local engine stops
one-second deadline completes locally
native engine stops after sessions
candidates, captions, converters, queues reset
stopped snapshot contains empty captions and zero levels
```

- [ ] **Step 6: Implement snapshot publication**

Every published snapshot increments an unsigned 64-bit version. Slow UI subscribers receive the latest snapshot without retaining an unbounded history.

- [ ] **Step 7: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Application.Tests/EMKE.Application.Tests.csproj --configuration Release
git add Windows/src/EMKE.Application Windows/tests/EMKE.Application.Tests
git commit -m "feat: implement serialized Windows Translation runtime"
```

### Task 9: Add Mock Translation Server and Failure Matrix

**Files:**
- Create: `Windows/tests/EMKE.Integration.Tests/MockTranslationServer.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/TranslationRuntimeIntegrationTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/FailureSafetyTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/TestAudioEngine.cs`
- Create: `docs/quality/windows-runtime-evidence.md`

**Interfaces:**
- Local loopback WebSocket server speaks the contract-v1 event set.
- Integration tests never use a real API key or external network.

- [ ] **Step 1: Implement deterministic server scenarios**

Support:

```text
normal handshake
two simultaneous sessions
same-language one-session path
fragmented Text events
Binary event injection
401/403 equivalent handshake rejection
unknown model
delayed session.closed
blocked close response
late transcript/audio deltas
disconnect/reconnect
server error event
```

Bind only to loopback on an ephemeral port. Tests receive the resolved URI from the server object.

- [ ] **Step 2: Add complete business-flow tests**

Prove:

```text
two-language start reaches running
same-language skips outbound socket and enables originalBypass
input PCM is sent in 9600-byte Text-frame JSON events
translated output reaches the correct native queue
inbound and outbound captions remain isolated
one channel failure does not stop the other
```

- [ ] **Step 3: Add 100-iteration safety injection**

Run 100 deterministic seeds for:

```text
outbound disconnect
server error
send failure
receive failure
queue full
translated-audio underrun
close timeout
```

For every seed, assert virtual microphone output is zero unless explicit bypass was already active.

- [ ] **Step 4: Record evidence**

Create `docs/quality/windows-runtime-evidence.md` with:

```text
source commit
.NET SDK version
native ABI version
contract version
unit/contract/integration counts
100-iteration safety result
known unverified boundaries
```

Explicitly mark real Translation service, installed driver, live endpoints, real meetings, and human listening as unverified here.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Windows/EMKE.Windows.slnx --configuration Release
git add Windows/tests/EMKE.Integration.Tests docs/quality/windows-runtime-evidence.md
git commit -m "test: verify Windows Translation runtime safety"
```

### Task 10: Add Runtime CI and Close the Headless Gate

**Files:**
- Create: `.github/workflows/windows-runtime.yml`
- Modify: `docs/quality/windows-runtime-evidence.md`

**Interfaces:**
- Windows runtime workflow is independent of macOS release and Windows packaging workflows.

- [ ] **Step 1: Add CI triggers**

Trigger on:

```text
Windows/src/EMKE.Core/**
Windows/src/EMKE.Realtime/**
Windows/src/EMKE.Routing/**
Windows/src/EMKE.Application/**
Windows/src/EMKE.Platform/**
Windows/tests/**
Shared/**
Windows/Directory.Build.props
Windows/EMKE.Windows.slnx
```

Workflow commands:

```powershell
node Scripts/validate-shared-contracts.mjs
dotnet restore Windows/EMKE.Windows.slnx --locked-mode
dotnet build Windows/EMKE.Windows.slnx --configuration Release --no-restore
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build `
  --logger "trx;LogFileName=windows-runtime.trx"
```

- [ ] **Step 2: Remove all incomplete contract adapters**

Run:

```powershell
rg -n "Assert\\.Inconclusive|TODO|FIXME|NotImplementedException" `
  Windows/src Windows/tests
```

Expected: no output. A deliberate runtime error string containing these words is not allowed as an exception.

- [ ] **Step 3: Run final gates**

```powershell
node Scripts/validate-shared-contracts.mjs
dotnet restore Windows/EMKE.Windows.slnx --locked-mode
dotnet build Windows/EMKE.Windows.slnx --configuration Release --no-restore
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build
git diff --check
git status --short
```

- [ ] **Step 4: Update evidence with observed results**

Record exact counts and commit. Keep device/service/meeting proof boundaries unchanged unless those tests actually ran against the same build.

- [ ] **Step 5: Commit**

```powershell
git add .github/workflows/windows-runtime.yml docs/quality/windows-runtime-evidence.md
git commit -m "ci: gate Windows Translation runtime"
git status --porcelain
```

Expected: empty status. The resulting commit is the base for the independent WPF product branch.
