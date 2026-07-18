# EMKE Menu-Bar Audio Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the native macOS menu-bar audio subsystem that discovers physical and EMKE virtual devices, converts audio between the HAL and Translation formats, and safely moves audio through four Core Audio endpoints without changing system defaults.

**Architecture:** The app uses stable Core Audio device UIDs to open virtual-speaker capture, physical-microphone capture, physical-headphone render, and virtual-microphone render endpoints. A C AUHAL bridge owns every real-time callback and moves only fixed 48 kHz stereo Float32 frames through bounded SPSC buffers; Swift polls and writes those buffers from a dedicated audio worker. Network-ready 24 kHz mono PCM16 conversion and routing decisions remain outside the HAL callbacks.

**Tech Stack:** Swift 6.2, Swift Package Manager, Swift Testing 6.2.3, CoreAudio, AudioUnit AUHAL, Accelerate-free deterministic PCM conversion, C11 atomics, SwiftUI `MenuBarExtra`, macOS 14+, Apple Silicon.

## Global Constraints

- Target Apple Silicon and macOS 14 or later.
- Never change the macOS system default input or output device.
- Select devices by stable UID; names are display-only except for diagnostics.
- Require `com.emke.translation.virtual-speaker` and `com.emke.translation.virtual-microphone` before the engine can become ready.
- Use fixed 48,000 Hz, two-channel, interleaved Float32 PCM between Swift and every AUHAL endpoint.
- Use 24,000 Hz, mono, signed little-endian PCM16 for Translation WebSocket audio.
- HAL callbacks may only render, perform bounded copies, update atomics, and zero-fill; they must not allocate, lock, call Swift, access network or disk, or emit logs.
- Virtual-microphone underruns always render silence.
- Stopping the app clears every in-memory audio buffer.
- This plan does not connect to a Base URL, store audio, install the driver, or package/sign a distributable `.app`.

## Plan Boundary

This plan implements subsystem 3a from the approved product design:

1. Core foundation — complete.
2. Core Audio virtual driver — complete and installed for local integration.
3. Local device/audio engine and menu-bar shell — this document.
4. Dual Translation-session coordinator, utterance buffering, subtitles, and connection test — next plan.
5. Signed installer and meeting-app compatibility/latency/privacy validation — later integration plan.

## File Structure

```text
Package.swift
Sources/
  EMKEAudioHAL/
    include/EMKEAudioHAL.h             # opaque AUHAL input/output endpoint C API
    EMKEAudioHAL.c                     # real-time callbacks and bounded ring-buffer transport
  EMKEAudioEngine/
    AudioDevice.swift                  # stable app-facing device value type and role rules
    AudioDeviceProvider.swift          # injectable provider contract
    CoreAudioDeviceProvider.swift      # Core Audio property queries and UID resolution
    AudioDeviceCatalog.swift           # physical/virtual filtering and readiness result
    NetworkPCMConverter.swift          # 48 kHz stereo Float32 <-> 24 kHz mono PCM16
    HALAudioEndpoint.swift             # Swift ownership wrapper over the C endpoints
    LocalAudioEngine.swift             # four-endpoint lifecycle and safe local routing
    AudioEngineState.swift             # observable state and explicit failure categories
  EMKEMenuBarApp/
    EMKEMenuBarApp.swift               # SwiftUI MenuBarExtra executable entry point
    MenuBarModel.swift                 # main-actor UI state adapter
Tests/
  EMKEAudioEngineTests/
    AudioDeviceCatalogTests.swift
    NetworkPCMConverterTests.swift
    LocalAudioEngineTests.swift
    AudioEngineStateTests.swift
docs/
  local-audio-engine-contract.md
```

---

### Task 1: Device Inventory and Driver Readiness

**Files:**
- Modify: `Package.swift`
- Create: `Sources/EMKEAudioEngine/AudioDevice.swift`
- Create: `Sources/EMKEAudioEngine/AudioDeviceProvider.swift`
- Create: `Sources/EMKEAudioEngine/CoreAudioDeviceProvider.swift`
- Create: `Sources/EMKEAudioEngine/AudioDeviceCatalog.swift`
- Create: `Tests/EMKEAudioEngineTests/AudioDeviceCatalogTests.swift`

**Interfaces:**
- Consumes: Core Audio system device list and exact virtual-device UIDs from `docs/audio-driver-contract.md`.
- Produces: `AudioDevice`, `AudioDeviceProviding`, `CoreAudioDeviceProvider`, `AudioDeviceCatalog`, `AudioDeviceSelection`, and `AudioDeviceCatalogError`.

- [x] **Step 1: Add the target and write failing catalog tests**

Add an `EMKEAudioEngine` library/target linked with `CoreAudio`, plus `EMKEAudioEngineTests`. Test with an in-memory `AudioDeviceProviding` implementation and these exact cases:

```swift
@Test func selectionUsesUIDsAndExcludesVirtualDevicesFromPhysicalChoices() throws {
    let provider = DeviceProviderStub(devices: [
        .fixture(id: 10, uid: AudioDevice.virtualSpeakerUID, input: 2, output: 2),
        .fixture(id: 11, uid: AudioDevice.virtualMicrophoneUID, input: 2, output: 2),
        .fixture(id: 20, uid: "physical.mic", input: 1, output: 0),
        .fixture(id: 21, uid: "physical.headphones", input: 0, output: 2),
    ])
    let catalog = AudioDeviceCatalog(provider: provider)
    let selection = try catalog.resolve(
        physicalInputUID: "physical.mic",
        physicalOutputUID: "physical.headphones"
    )

    #expect(selection.virtualSpeaker.id == 10)
    #expect(selection.virtualMicrophone.id == 11)
    #expect(selection.physicalInput.id == 20)
    #expect(selection.physicalOutput.id == 21)
    #expect(try catalog.physicalInputs().map(\.uid) == ["physical.mic"])
    #expect(try catalog.physicalOutputs().map(\.uid) == ["physical.headphones"])
}
```

Add separate tests for a missing driver, a missing saved physical UID, and a saved input/output that has zero channels in the requested direction.

- [x] **Step 2: Run the focused tests and verify RED**

Run: `swift test --filter AudioDeviceCatalogTests`

Expected: compilation fails because `EMKEAudioEngine`, `AudioDevice`, and `AudioDeviceCatalog` do not exist.

- [x] **Step 3: Implement the device model and injectable catalog**

Use these public contracts:

```swift
public struct AudioDevice: Equatable, Sendable, Identifiable {
    public static let virtualSpeakerUID = "com.emke.translation.virtual-speaker"
    public static let virtualMicrophoneUID = "com.emke.translation.virtual-microphone"
    public let id: AudioObjectID
    public let uid: String
    public let name: String
    public let inputChannelCount: Int
    public let outputChannelCount: Int
    public let nominalSampleRate: Double
}

public protocol AudioDeviceProviding: Sendable {
    func devices() throws -> [AudioDevice]
}

public struct AudioDeviceSelection: Equatable, Sendable {
    public let virtualSpeaker: AudioDevice
    public let virtualMicrophone: AudioDevice
    public let physicalInput: AudioDevice
    public let physicalOutput: AudioDevice
}
```

`AudioDeviceCatalog` excludes both EMKE UIDs from physical choices, sorts choices by localized name then UID, and resolves saved selections strictly by UID. `CoreAudioDeviceProvider` reads the system list, UID, name, nominal rate, and input/output channel totals through `AudioObjectGetPropertyData`; it must not mutate any Core Audio property.

- [x] **Step 4: Run focused and full tests and verify GREEN**

Run: `swift test --filter AudioDeviceCatalogTests && swift test --parallel`

Expected: all catalog tests and the existing 43 tests pass.

- [x] **Step 5: Add a live read-only enumeration assertion**

Run:

```bash
swift test --filter installedDriverAppearsInCoreAudio
```

The test is skipped when the driver is absent. On the development Mac it must find both exact UIDs, report 48,000 Hz, and confirm at least one input and output channel on each virtual device.

- [x] **Step 6: Commit**

```bash
git add Package.swift Sources/EMKEAudioEngine Tests/EMKEAudioEngineTests
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: discover local audio devices"
```

---

### Task 2: Streaming Translation PCM Conversion

**Files:**
- Create: `Sources/EMKEAudioEngine/NetworkPCMConverter.swift`
- Create: `Tests/EMKEAudioEngineTests/NetworkPCMConverterTests.swift`

**Interfaces:**
- Consumes: fixed 48 kHz stereo interleaved Float32 transport frames and 24 kHz mono PCM16 network bytes.
- Produces: stateful `NetworkPCMEncoder` and `NetworkPCMDecoder` with chunk-boundary continuity.

- [x] **Step 1: Write failing conversion tests**

Tests must prove silence remains silence, stereo downmix uses `(left + right) / 2`, Float32 values clamp to `[-1, 1]`, encoded bytes are little-endian signed PCM16, every two 48 kHz frames produce one 24 kHz sample, every 24 kHz sample produces two stereo 48 kHz frames, and splitting input across odd-sized chunks produces the same bytes/samples as one contiguous call.

Use the exact API:

```swift
var encoder = NetworkPCMEncoder()
let first = try encoder.append48kStereo([1, -1, 0.5, 0.5, 0.25, 0.25])
let second = try encoder.append48kStereo([0, 0])

var decoder = NetworkPCMDecoder()
let frames = try decoder.append24kMonoPCM16(Data([0x00, 0x40]))
```

- [x] **Step 2: Run the focused tests and verify RED**

Run: `swift test --filter NetworkPCMConverterTests`

Expected: compilation fails because the encoder and decoder are absent.

- [x] **Step 3: Implement deterministic bounded conversion**

The encoder rejects an odd Float count with `NetworkPCMError.misalignedStereoSamples`, retains at most one pending stereo frame between calls, averages adjacent 48 kHz mono frames for a two-tap low-pass/downsample step, clamps, rounds to `Int16`, and emits little-endian bytes. The decoder rejects an odd byte count with `NetworkPCMError.misalignedPCM16`, converts each signed sample to Float32, duplicates it into two 48 kHz frames and two channels, and never accesses audio hardware.

- [x] **Step 4: Run focused and full tests and verify GREEN**

Run: `swift test --filter NetworkPCMConverterTests && swift test --parallel`

Expected: all conversion and existing tests pass without warnings.

- [x] **Step 5: Commit**

```bash
git add Sources/EMKEAudioEngine/NetworkPCMConverter.swift Tests/EMKEAudioEngineTests/NetworkPCMConverterTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: convert realtime translation audio"
```

---

### Task 3: Real-Time-Safe AUHAL Endpoint Bridge

**Files:**
- Modify: `Package.swift`
- Create: `Sources/EMKEAudioHAL/include/EMKEAudioHAL.h`
- Create: `Sources/EMKEAudioHAL/EMKEAudioHAL.c`
- Create: `Tests/EMKEAudioEngineTests/HALAudioEndpointTests.swift`

**Interfaces:**
- Consumes: `EMKEAudioRingBuffer` and a Core Audio `AudioObjectID` with the required input or output scope.
- Produces: opaque `EMKEHALInput`/`EMKEHALOutput` instances and create/start/stop/read/write/diagnostic functions.

- [x] **Step 1: Add the C target and write failing validation tests**

Add `EMKEAudioHAL` as a C target depending on `EMKEAudioBridge`, linked to `CoreAudio` and `AudioUnit`; make `EMKEAudioEngine` depend on it. Write tests that invalid object ID `0` is rejected, zero capacity is rejected, null handles return safe zero/error values, and stopped outputs expose zero queued frames.

- [x] **Step 2: Run the focused tests and verify RED**

Run: `swift test --filter HALAudioEndpointTests`

Expected: compilation fails because `EMKEAudioHAL.h` and its functions do not exist.

- [x] **Step 3: Implement opaque AUHAL input/output endpoints**

Expose this C contract:

```c
typedef struct EMKEHALInput EMKEHALInput;
typedef struct EMKEHALOutput EMKEHALOutput;

OSStatus EMKEHALInputCreate(AudioObjectID deviceID, uint32_t capacityFrames, EMKEHALInput **outInput);
OSStatus EMKEHALInputStart(EMKEHALInput *input);
OSStatus EMKEHALInputStop(EMKEHALInput *input);
uint32_t EMKEHALInputRead(EMKEHALInput *input, float *frames, uint32_t frameCount);
uint32_t EMKEHALInputReadableFrames(const EMKEHALInput *input);
void EMKEHALInputDestroy(EMKEHALInput *input);

OSStatus EMKEHALOutputCreate(AudioObjectID deviceID, uint32_t capacityFrames, EMKEHALOutput **outOutput);
OSStatus EMKEHALOutputStart(EMKEHALOutput *output);
OSStatus EMKEHALOutputStop(EMKEHALOutput *output);
uint32_t EMKEHALOutputWrite(EMKEHALOutput *output, const float *frames, uint32_t frameCount);
uint32_t EMKEHALOutputQueuedFrames(const EMKEHALOutput *output);
void EMKEHALOutputDestroy(EMKEHALOutput *output);
```

Each unit uses `kAudioUnitSubType_HALOutput`, selects only its supplied device with `kAudioOutputUnitProperty_CurrentDevice`, exposes 48 kHz stereo interleaved Float32 on the app side, and preallocates its scratch/ring storage in `Create`. Input callbacks call `AudioUnitRender` then bounded-write the ring. Output callbacks bounded-read and zero-fill every underrun. Start/stop are idempotent and reset buffers outside the real-time callback.

- [x] **Step 4: Compile C with warnings as errors and verify GREEN**

Run:

```bash
swift test --filter HALAudioEndpointTests
xcrun clang -std=c11 -arch arm64 -mmacosx-version-min=14.0 -Wall -Wextra -Werror \
  -ISources/EMKEAudioBridge/include -ISources/EMKEAudioHAL/include \
  -fsyntax-only Sources/EMKEAudioHAL/EMKEAudioHAL.c
```

Expected: tests pass and C compilation emits no warnings.

- [x] **Step 5: Commit**

```bash
git add Package.swift Sources/EMKEAudioHAL Tests/EMKEAudioEngineTests/HALAudioEndpointTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: add local AUHAL audio endpoints"
```

---

### Task 4: Swift Endpoint Ownership and Test Doubles

**Files:**
- Create: `Sources/EMKEAudioEngine/HALAudioEndpoint.swift`
- Create: `Tests/EMKEAudioEngineTests/HALAudioEndpointOwnershipTests.swift`

**Interfaces:**
- Consumes: Task 3 C endpoint handles.
- Produces: `AudioInputEndpoint`, `AudioOutputEndpoint`, `HALAudioInputEndpoint`, `HALAudioOutputEndpoint`, and `AudioEndpointError`.

- [ ] **Step 1: Write failing lifecycle tests**

Use protocol-backed fakes to prove start/stop idempotence, start failure leaves the endpoint stopped, destroying a running endpoint stops it first, reads never exceed caller capacity, and partial writes report backpressure rather than discarding silently.

- [ ] **Step 2: Run the tests and verify RED**

Run: `swift test --filter HALAudioEndpointOwnershipTests`

Expected: compilation fails because the Swift endpoint contracts are absent.

- [ ] **Step 3: Implement the wrappers**

Use synchronous, non-`async` endpoint protocols because all network/UI work is above this boundary:

```swift
public protocol AudioInputEndpoint: AnyObject {
    func start() throws
    func stop()
    func read(into frames: UnsafeMutableBufferPointer<Float>) -> Int
}

public protocol AudioOutputEndpoint: AnyObject {
    func start() throws
    func stop()
    func write(_ frames: UnsafeBufferPointer<Float>) -> Int
}
```

The concrete wrappers own and destroy exactly one C handle and translate nonzero `OSStatus` into `AudioEndpointError.coreAudio(OSStatus)`.

- [ ] **Step 4: Run focused and full tests and verify GREEN**

Run: `swift test --filter HALAudioEndpointOwnershipTests && swift test --parallel`

- [ ] **Step 5: Commit**

```bash
git add Sources/EMKEAudioEngine/HALAudioEndpoint.swift Tests/EMKEAudioEngineTests/HALAudioEndpointOwnershipTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: own HAL audio endpoint lifecycles"
```

---

### Task 5: Four-Endpoint Local Audio Engine and Safety Routing

**Files:**
- Create: `Sources/EMKEAudioEngine/AudioEngineState.swift`
- Create: `Sources/EMKEAudioEngine/LocalAudioEngine.swift`
- Create: `Tests/EMKEAudioEngineTests/LocalAudioEngineTests.swift`
- Create: `Tests/EMKEAudioEngineTests/AudioEngineStateTests.swift`

**Interfaces:**
- Consumes: `AudioDeviceSelection`, four endpoint protocols, `RoutingStateMachine`, `NetworkPCMEncoder`, and `NetworkPCMDecoder`.
- Produces: `LocalAudioEngine`, `AudioEngineConfiguration`, `AudioEngineState`, and `AudioEngineEvent`.

- [ ] **Step 1: Write failing safety-routing tests**

With in-memory endpoints, prove:

- starting creates/starts virtual-speaker input, physical-microphone input, physical-output render, and virtual-microphone render exactly once;
- inbound original bypass copies only virtual-speaker capture to physical output;
- outbound original bypass copies only physical-microphone capture to virtual-microphone output;
- outbound translated mode writes only explicitly supplied translated frames;
- outbound fail-closed mode never writes captured microphone frames;
- inbound fail-open mode routes original frames;
- stop clears queued input/output and returns `.stopped` even when one endpoint stop reports an error.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `swift test --filter LocalAudioEngineTests`

Expected: compilation fails because `LocalAudioEngine` is absent.

- [ ] **Step 3: Implement a serial worker-owned engine**

Use this app-facing boundary:

```swift
public actor LocalAudioEngine {
    public func start(configuration: AudioEngineConfiguration) async throws
    public func stop() async
    public func setRouting(inbound: InboundOutputMode, outbound: OutboundOutputMode) async
    public func nextEvent() async -> AudioEngineEvent
    public func enqueueInboundTranslation(_ pcm16: Data) async throws
    public func enqueueOutboundTranslation(_ pcm16: Data) async throws
}
```

The actor owns a single bounded worker loop. Each cycle polls capture endpoints into preallocated 10 ms chunks, emits network PCM events from both capture sources, and routes only the audio selected by the current modes. Event delivery uses a bounded continuation queue; audio overload increments counters instead of allowing unbounded memory growth. Stopping cancels the worker, stops outputs before inputs, clears converters and buffered events, and publishes `.stopped`.

- [ ] **Step 4: Run focused and full tests and verify GREEN**

Run: `swift test --filter LocalAudioEngineTests && swift test --filter AudioEngineStateTests && swift test --parallel`

- [ ] **Step 5: Commit**

```bash
git add Package.swift Sources/EMKEAudioEngine Tests/EMKEAudioEngineTests
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: route local translation audio safely"
```

---

### Task 6: Minimal Menu-Bar Application Shell

**Files:**
- Modify: `Package.swift`
- Create: `Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift`
- Create: `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- Create: `Tests/EMKEAudioEngineTests/MenuBarModelTests.swift`

**Interfaces:**
- Consumes: `AudioDeviceCatalog`, `AudioEngineState`, `TranslationPreferences`, and the existing Keychain/configuration modules.
- Produces: a runnable `EMKEMenuBarApp` executable with device readiness and safe start/stop controls.

- [ ] **Step 1: Write failing model-state tests**

Test that an absent driver disables Start with a repair message, missing physical selections disable Start, ready selections enable Start, active translation makes selected devices immutable, and stopped/error states never display “Translating”.

- [ ] **Step 2: Run the tests and verify RED**

Run: `swift test --filter MenuBarModelTests`

Expected: compilation fails because `MenuBarModel` is absent.

- [ ] **Step 3: Implement the main-actor model and SwiftUI shell**

Add an executable target depending on `EMKECore`, `EMKESecurity`, and `EMKEAudioEngine`. `MenuBarExtra("EMKE Translation", systemImage: model.systemImage)` shows engine status, real microphone and output selectors, Start/Stop, and Quit. This plan intentionally keeps API credentials, languages, subtitles, and model-session controls out of the shell until the coordinator plan.

- [ ] **Step 4: Build and verify GREEN**

Run: `swift test --filter MenuBarModelTests && swift build --product EMKEMenuBarApp && swift test --parallel`

Expected: model tests pass and the executable links against AppKit/SwiftUI without warnings.

- [ ] **Step 5: Commit**

```bash
git add Package.swift Sources/EMKEMenuBarApp Tests/EMKEAudioEngineTests/MenuBarModelTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: add EMKE menu bar shell"
```

---

### Task 7: Installed-Driver Integration and Contract Documentation

**Files:**
- Create: `docs/local-audio-engine-contract.md`
- Modify: `docs/superpowers/specs/2026-07-18-emke-translation-macos-mvp-design.md`
- Modify: `docs/superpowers/plans/2026-07-18-emke-menu-bar-audio-engine.md`

**Interfaces:**
- Consumes: all prior tasks plus the locally installed driver.
- Produces: repeatable system integration evidence and the exact boundary for the Translation coordinator plan.

- [ ] **Step 1: Document ownership, formats, and safety states**

Document the four device roles, exact UIDs, 48 kHz stereo app transport, 24 kHz mono PCM16 network boundary, buffer limits, stop order, fail-open/fail-closed behavior, and the rule that the engine never changes system defaults.

- [ ] **Step 2: Run an installed-device start/stop smoke test**

Add an opt-in test gated by `EMKE_RUN_LIVE_AUDIO_TESTS=1`. It resolves both EMKE UIDs, opens their companion directions, starts them for 250 ms, stops them, and verifies no Core Audio error. It must not select a physical device or mutate defaults.

Run: `EMKE_RUN_LIVE_AUDIO_TESTS=1 swift test --filter liveVirtualEndpointsStartAndStop`

Expected: both installed virtual endpoints start and stop successfully.

- [ ] **Step 3: Run final automated verification**

Run:

```bash
swift test --parallel
swift build -c release
swift build --product EMKEMenuBarApp
git diff --check
rg -n "malloc|calloc|realloc|pthread_mutex|dispatch_|NSLog|printf|open\(|write\(" \
  Sources/EMKEAudioHAL Sources/EMKEAudioBridge
```

Expected: all tests/builds pass; allocation appears only in endpoint create/destroy paths; no lock, logging, file, network, or Swift call appears in AUHAL callbacks.

- [ ] **Step 4: Mark the plan complete and commit**

```bash
git add docs
git -c user.name='Codex' -c user.email='codex@local' commit -m "docs: complete local audio engine plan"
```

## Manual Integration Gate After This Plan

Launch `EMKEMenuBarApp`, select a physical microphone and headphones, and run local level/route checks before selecting EMKE devices in a real meeting. Do not use a real meeting until the next coordinator plan connects inbound/outbound Translation sessions; translated modes remain silent unless test PCM is explicitly injected.
