# EMKE Translation Coordinator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect the local four-endpoint audio engine to two independent Realtime Translation WebSocket sessions, select original or translated inbound audio once per utterance, expose transient subtitles, and let the menu-bar user configure and test API compatibility without storing secrets outside Keychain.

**Architecture:** A new `EMKECoordinator` actor owns the audio-engine event loop and independent inbound/outbound session receive loops. Pure PCM batching, voice-activity, and utterance-buffer reducers remain deterministic and testable; only the coordinator performs async I/O. The menu app persists public settings in `UserDefaults`, stores only the API key in `KeychainSecretStore`, and reports protocol capabilities separately so Chat Completions compatibility is never mistaken for Translation WebSocket compatibility.

**Tech Stack:** Swift 6.2, Swift Package Manager, Swift Testing 6.2.3, Foundation WebSocket, NaturalLanguage, Core Audio, SwiftUI `MenuBarExtra`, macOS 14+, Apple Silicon.

## Global Constraints

- Never put an API key in source, fixtures, plans, process arguments, logs, `UserDefaults`, or Git.
- Keep the product default endpoint `https://api.openai.com/v1` and model `gpt-realtime-translate`; custom Base URL and model values remain user-editable local preferences.
- Use dedicated `/realtime/translations?model=...` WebSockets, not Chat Completions, Responses, or standard voice-agent Realtime sessions.
- Configure source transcription explicitly with `gpt-realtime-whisper` for the inbound session; a gateway that rejects it is not fully compatible with mother-language gating.
- Send and receive mono signed little-endian PCM16 at 24,000 Hz in 200 ms frames (9,600 bytes) while keeping silence in the stream.
- Run one inbound session targeting the mother language and, unless both selected languages match, one outbound session targeting the meeting language.
- Inbound failures fail open to original audio; outbound failures fail closed to silence unless the user explicitly enables original bypass.
- Buffer only in memory, cap every audio/text/event queue, and clear all buffers when an utterance or translation run ends.
- Do not package/sign the app or claim compatibility with a custom Base URL until a live capability probe succeeds.

## File Structure

```text
Package.swift
Sources/
  EMKERealtime/
    TranslationSessionConfiguration.swift # output language, transcription and noise reduction
    TranslationClientEvent.swift           # exact Translation client-event encoder
    TranslationServerEvent.swift           # typed handshake/audio/transcript decoder
    TranslationSession.swift               # one-reader socket lifecycle and graceful drain
  EMKECoordinator/
    PCMFrameBatcher.swift                   # 10 ms engine chunks -> exact 200 ms network frames
    PCMVoiceActivityDetector.swift          # deterministic bounded energy VAD
    InboundUtteranceBuffer.swift            # original/translation two-candidate gate
    TranslationCoordinatorState.swift       # channel, subtitle and diagnostic snapshots
    TranslationCoordinator.swift            # dual-session orchestration and fail safety
    TranslationConnectionProbe.swift        # per-capability Translation compatibility test
  EMKEAudioEngine/
    LocalAudioEngine.swift                  # generic selected inbound PCM playback entry
  EMKEMenuBarApp/
    AppSettingsStore.swift                  # non-secret UserDefaults persistence
    MenuBarModel.swift                      # coordinator and Keychain UI adapter
    EMKEMenuBarApp.swift                    # API/language/status/subtitle controls
Tests/
  EMKERealtimeTests/
    TranslationEventCodecTests.swift
    TranslationSessionTests.swift
  EMKECoordinatorTests/
    PCMFrameBatcherTests.swift
    PCMVoiceActivityDetectorTests.swift
    InboundUtteranceBufferTests.swift
    TranslationCoordinatorTests.swift
    TranslationConnectionProbeTests.swift
  EMKEAudioEngineTests/LocalAudioEngineTests.swift
  EMKEAudioEngineTests/MenuBarModelTests.swift
docs/translation-coordinator-contract.md
```

---

### Task 1: Correct the Realtime Translation Protocol and Socket Lifecycle

**Files:**
- Modify: `Package.swift`
- Create: `Sources/EMKERealtime/TranslationSessionConfiguration.swift`
- Modify: `Sources/EMKERealtime/TranslationClientEvent.swift`
- Modify: `Sources/EMKERealtime/TranslationServerEvent.swift`
- Modify: `Sources/EMKERealtime/TranslationSession.swift`
- Modify: `Tests/EMKERealtimeTests/TranslationEventCodecTests.swift`
- Modify: `Tests/EMKERealtimeTests/TranslationSessionTests.swift`

**Interfaces:**
- Consumes: `SupportedLanguage`, `APIConfiguration`, `TranslationSocket`, and the official Translation client/server event contract.
- Produces: `TranslationSessionConfiguration`, `TranslationNoiseReduction`, typed `TranslationAudioDelta`/`TranslationTranscriptDelta`, and a `TranslationSession` with exactly one socket reader.

- [x] **Step 1: Write failing codec tests for source transcription and metadata**

```swift
@Test func inboundSessionUpdateEnablesSourceTranscription() throws {
    let value = TranslationSessionConfiguration(
        targetLanguage: .chinese,
        inputTranscriptionModel: "gpt-realtime-whisper",
        noiseReduction: .farField
    )
    let object = try jsonObject(
        TranslationClientEvent.sessionUpdate(configuration: value).encoded()
    )
    let session = try #require(object["session"] as? [String: Any])
    let audio = try #require(session["audio"] as? [String: Any])
    let input = try #require(audio["input"] as? [String: Any])
    #expect((input["transcription"] as? [String: Any])?["model"] as? String == "gpt-realtime-whisper")
    #expect((input["noise_reduction"] as? [String: Any])?["type"] as? String == "far_field")
}

@Test func outputAudioDecodesAndValidatesTransportMetadata() throws {
    let event = try TranslationServerEvent.decode(Data(#"{"type":"session.output_audio.delta","delta":"AAEC","sample_rate":24000,"channels":1,"format":"pcm16","elapsed_ms":400}"#.utf8))
    #expect(event == .outputAudio(.init(data: Data([0, 1, 2]), sampleRate: 24_000, channels: 1, format: "pcm16", elapsedMilliseconds: 400)))
}
```

- [x] **Step 2: Run the focused tests and confirm RED**

Run: `swift test --filter TranslationEventCodecTests`

Expected: compilation fails because the configuration and typed delta interfaces do not exist.

- [x] **Step 3: Implement exact client/server event models**

```swift
public enum TranslationNoiseReduction: String, Codable, Sendable {
    case nearField = "near_field"
    case farField = "far_field"
}

public struct TranslationSessionConfiguration: Equatable, Sendable {
    public let targetLanguage: SupportedLanguage
    public let inputTranscriptionModel: String?
    public let noiseReduction: TranslationNoiseReduction?
}

public struct TranslationAudioDelta: Equatable, Sendable {
    public let data: Data
    public let sampleRate: Int
    public let channels: Int
    public let format: String
    public let elapsedMilliseconds: Int?
}
```

Encode `audio.output.language` for every session. Encode `audio.input.transcription.model` and `audio.input.noise_reduction.type` only when configured. Decode `session.created`, `session.updated`, `session.closed`, typed transcript/audio deltas, and server errors; reject invalid Base64 or non-24-kHz/mono/PCM16 audio instead of converting it to empty data.

- [x] **Step 4: Write failing lifecycle tests for handshake ordering and tail drain**

```swift
@Test func connectWaitsForCreatedThenUpdatedBeforeReturning() async throws {
    let socket = FakeSocket(incoming: [createdEvent, updatedEvent])
    let session = makeSession(socket: socket)
    try await session.connect()
    #expect(await socket.receivedCount == 2)
    #expect(String(decoding: await socket.sent.first!, as: UTF8.self).contains("session.update"))
}

@Test func closeLetsTheSingleReaderDeliverTailAudioBeforeClosed() async throws {
    let socket = FakeSocket(incoming: [createdEvent, updatedEvent, audioEvent, closedEvent])
    let session = makeSession(socket: socket)
    try await session.connect()
    async let next = session.nextEvent()
    async let closed: Void = session.close()
    #expect(try await next == expectedAudioEvent)
    try await closed
    #expect(await socket.wasCancelled)
}
```

- [x] **Step 5: Implement a single-reader `TranslationSession`**

`connect()` receives `session.created`, sends `session.update`, receives `session.updated`, then launches one read loop. The read loop alone calls `socket.receive()`, queues events for `nextEvent()`, fulfills close waiters on `session.closed`, and broadcasts terminal errors. `close()` sends `session.close`, prevents new audio appends, waits for the read loop to observe `session.closed`, and only then cancels the socket.

- [x] **Step 6: Run Realtime tests and commit**

Run: `swift test --filter EMKERealtimeTests`

Expected: all Realtime tests pass.

Commit: `feat: harden realtime translation sessions`

---

### Task 2: Add Bounded PCM Framing, VAD, and Utterance Selection

**Files:**
- Modify: `Package.swift`
- Create: `Sources/EMKECoordinator/PCMFrameBatcher.swift`
- Create: `Sources/EMKECoordinator/PCMVoiceActivityDetector.swift`
- Create: `Sources/EMKECoordinator/InboundUtteranceBuffer.swift`
- Create: `Tests/EMKECoordinatorTests/PCMFrameBatcherTests.swift`
- Create: `Tests/EMKECoordinatorTests/PCMVoiceActivityDetectorTests.swift`
- Create: `Tests/EMKECoordinatorTests/InboundUtteranceBufferTests.swift`

**Interfaces:**
- Consumes: arbitrary even-byte PCM16 chunks and `InboundLanguageGate` decisions.
- Produces: exact 9,600-byte network frames, speech boundary events, and selected playback chunks that can never mix original and translated candidates in one utterance.

- [ ] **Step 1: Write failing 200 ms batching tests**

```swift
@Test func emitsOnlyExactTwoHundredMillisecondFrames() throws {
    var batcher = PCMFrameBatcher()
    #expect(try batcher.append(Data(repeating: 1, count: 4_800)).isEmpty)
    #expect(try batcher.append(Data(repeating: 2, count: 14_400)).map(\.count) == [9_600, 9_600])
    #expect(batcher.bufferedByteCount == 0)
}

@Test func rejectsOddLengthPCM() {
    var batcher = PCMFrameBatcher()
    #expect(throws: PCMFrameBatcherError.invalidPCM16ByteCount) {
        try batcher.append(Data([0]))
    }
}
```

- [ ] **Step 2: Run batcher tests and confirm RED**

Run: `swift test --filter PCMFrameBatcherTests`

Expected: compilation fails because `PCMFrameBatcher` does not exist.

- [ ] **Step 3: Implement the bounded batcher and make tests GREEN**

Use `frameByteCount = 24_000 / 5 * 2 = 9_600`. Retain only the incomplete tail, emit all complete frames in order, and reject odd byte counts. Run the focused tests until green.

- [ ] **Step 4: Write failing deterministic VAD tests**

```swift
@Test func speechStartsOnVoicedPCMAndEndsAfterConfiguredSilence() throws {
    var vad = PCMVoiceActivityDetector(silenceFrameLimit: 3)
    #expect(try vad.observe(pcm16(amplitude: 8_000)) == .speechStarted)
    #expect(try vad.observe(pcm16(amplitude: 0)) == .none)
    #expect(try vad.observe(pcm16(amplitude: 0)) == .none)
    #expect(try vad.observe(pcm16(amplitude: 0)) == .speechEnded)
}
```

- [ ] **Step 5: Implement bounded energy VAD and make tests GREEN**

Compute normalized RMS directly from little-endian PCM16 without allocating a sample array. Default speech threshold is `0.015`; emit one start edge, wait 300 ms of silence (30 engine chunks) for the end edge, and expose `isSpeaking` for deadline decisions.

- [ ] **Step 6: Write failing two-candidate utterance tests**

```swift
@Test func motherLanguageFlushesOnlyBufferedOriginalAndLocksRoute() {
    var buffer = InboundUtteranceBuffer(motherLanguage: .chinese)
    buffer.begin()
    #expect(buffer.appendOriginal(Data([1, 1])).isEmpty)
    #expect(buffer.appendTranslation(Data([2, 2])).isEmpty)
    #expect(buffer.observe(LanguageHypotheses(["zh": 0.9])) == [Data([1, 1])])
    #expect(buffer.appendOriginal(Data([3, 3])) == [Data([3, 3])])
    #expect(buffer.appendTranslation(Data([4, 4])).isEmpty)
}

@Test func foreignLanguageFlushesOnlyTranslation() {
    var buffer = InboundUtteranceBuffer(motherLanguage: .chinese)
    buffer.begin()
    _ = buffer.appendOriginal(Data([1, 1]))
    _ = buffer.appendTranslation(Data([2, 2]))
    #expect(buffer.observe(LanguageHypotheses(["de": 0.8])) == [Data([2, 2])])
}
```

- [ ] **Step 7: Implement selection, bounds, reset, and make tests GREEN**

Before selection, hold both candidates. After `.original`, stream only original; after `.translated`, stream only translated. `resolveDeadline(isSpeech:)` delegates to `InboundLanguageGate`. If undecided original audio reaches 5 seconds, fail open to original. `finish()` flushes the selected tail, clears transcript/audio, resets the gate, and never carries bytes into the next utterance.

- [ ] **Step 8: Run coordinator primitive tests and commit**

Run: `swift test --filter EMKECoordinatorTests`

Expected: batching, VAD and utterance tests pass.

Commit: `feat: buffer inbound translation utterances`

---

### Task 3: Expose Generic Selected Inbound Playback

**Files:**
- Modify: `Sources/EMKEAudioEngine/LocalAudioEngine.swift`
- Modify: `Tests/EMKEAudioEngineTests/LocalAudioEngineTests.swift`

**Interfaces:**
- Consumes: selected 24 kHz mono PCM16, regardless of whether it originated from meeting audio or model audio.
- Produces: `enqueueInboundOutput(_:)` while preserving the old translated-mode safety boundary.

- [ ] **Step 1: Write a failing audio-engine test**

```swift
@Test func selectedInboundPCMCanBeOriginalOrTranslated() async throws {
    let harness = makeHarness()
    try await start(harness)
    await harness.engine.setRouting(inbound: .translated, outbound: .mutedFailClosed)
    try await harness.engine.enqueueInboundOutput(Data([0xff, 0x7f]))
    #expect(harness.factory.physicalOutput.writes == [[1, 1, 1, 1]])
    await harness.engine.stop()
}
```

- [ ] **Step 2: Run the focused test and confirm RED**

Run: `swift test --filter selectedInboundPCMCanBeOriginalOrTranslated`

Expected: compilation fails because `enqueueInboundOutput` does not exist.

- [ ] **Step 3: Implement the generic entry and preserve compatibility**

Move the current decoder/write body to `enqueueInboundOutput(_:)`; keep `enqueueInboundTranslation(_:)` as a forwarding compatibility method until all coordinator call sites migrate.

- [ ] **Step 4: Run audio-engine tests and commit**

Run: `swift test --filter EMKEAudioEngineTests`

Expected: all audio-engine tests pass.

Commit: `feat: play coordinator-selected inbound audio`

---

### Task 4: Build the Dual-Session Translation Coordinator

**Files:**
- Create: `Sources/EMKECoordinator/TranslationCoordinatorState.swift`
- Create: `Sources/EMKECoordinator/TranslationCoordinator.swift`
- Create: `Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift`
- Modify: `Package.swift`

**Interfaces:**
- Consumes: `AudioEngineConfiguration`, `APIConfiguration`, `TranslationPreferences`, API key, `TranslationAudioEngine`, `TranslationSessionBuilding`, `NaturalLanguageClassifier`, and Task-based timing.
- Produces: `TranslationCoordinator`, `TranslationCoordinatorConfiguration`, `TranslationCoordinatorState`, `TranslationChannelState`, `SubtitleSnapshot`, and bounded `TranslationCoordinatorEvent` delivery.

- [ ] **Step 1: Write failing configuration and channel-isolation tests**

```swift
@Test func startCreatesIndependentInboundAndOutboundSessions() async throws {
    let harness = CoordinatorHarness(preferences: .init(motherLanguage: .chinese, meetingOutputLanguage: .german))
    try await harness.coordinator.start(configuration: harness.configuration)
    #expect(await harness.sessions.requests.map(\.targetLanguage) == [.chinese, .german])
    #expect(await harness.sessions.requests.first?.inputTranscriptionModel == "gpt-realtime-whisper")
    #expect(await harness.sessions.requests.last?.inputTranscriptionModel == nil)
}

@Test func inboundFailureFailsOpenWithoutStoppingOutbound() async throws {
    let harness = CoordinatorHarness(inboundFailure: TestError.disconnected)
    try await harness.coordinator.start(configuration: harness.configuration)
    let routing = await harness.audio.lastRouting
    #expect(routing?.inbound == .originalFailOpen)
    #expect(routing?.outbound == .translated)
    #expect(await harness.outboundSession.appendCount > 0)
}

@Test func matchingLanguagesUseLocalOutboundBypassWithoutSecondSession() async throws {
    let harness = CoordinatorHarness(preferences: .init(motherLanguage: .english, meetingOutputLanguage: .english))
    try await harness.coordinator.start(configuration: harness.configuration)
    #expect(await harness.sessions.requests.count == 1)
    #expect(await harness.audio.lastRouting?.outbound == .originalBypass)
}
```

- [ ] **Step 2: Run coordinator tests and confirm RED**

Run: `swift test --filter TranslationCoordinatorTests`

Expected: compilation fails because coordinator interfaces do not exist.

- [ ] **Step 3: Implement startup, independent session loops, and safe routing**

Start the local engine first. Create inbound and outbound sessions independently. A failed inbound connect sends `.inboundConnectionFailed`; a failed outbound connect sends `.outboundConnectionFailed`; one failure never cancels the other. Matching languages select `.originalBypass` outbound and skip that network session.

- [ ] **Step 4: Write failing dataflow and utterance tests**

```swift
@Test func audioEventsAreBatchedAndSentToTheirOwnSessions() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)
    await harness.audio.emit(.inboundNetworkAudio(Data(repeating: 1, count: 9_600)))
    await harness.audio.emit(.outboundNetworkAudio(Data(repeating: 2, count: 9_600)))
    await harness.drain()
    #expect(await harness.inboundSession.appended == [Data(repeating: 1, count: 9_600)])
    #expect(await harness.outboundSession.appended == [Data(repeating: 2, count: 9_600)])
}

@Test func transcriptSelectsExactlyOneInboundCandidate() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)
    await harness.beginInboundSpeech(original: Data([1, 1]))
    await harness.inboundSession.emit(.outputAudio(audioDelta(Data([2, 2]))))
    await harness.inboundSession.emit(.inputTranscript(transcriptDelta("Deutsch")))
    await harness.drain()
    #expect(await harness.audio.inboundPlayback == [Data([2, 2])])
}
```

- [ ] **Step 5: Implement dataflow, transcript classification, deadlines, and subtitles**

Feed every complete inbound frame to the inbound session and every complete outbound frame to the outbound session. Feed inbound source transcript deltas to `NaturalLanguageClassifier`; route selected chunks from `InboundUtteranceBuffer` into `enqueueInboundOutput`. Start the 250 ms decision deadline on the first translated-audio delta. Keep only the current source/translation text for each direction, cap each text field at 4,096 characters, emit subtitle snapshots in memory, and clear them on stop.

- [ ] **Step 6: Implement bounded reconnect and graceful stop**

On a terminal channel error, switch routing immediately, recreate only that session with delays of 250 ms, 500 ms, 1 s, 2 s, and a 5 s cap. Inbound recovery changes from fail-open to translated only after the next `.utteranceEnded`; outbound recovery can leave muted fail-closed immediately. Stop audio appends, call `session.close()` while receive loops continue draining tail events, then stop the local engine and clear all buffers/tasks.

- [ ] **Step 7: Run coordinator and full tests, then commit**

Run: `swift test --filter EMKECoordinatorTests && swift test --parallel`

Expected: all tests pass and one channel's injected failure does not fail the other.

Commit: `feat: coordinate dual translation sessions`

---

### Task 5: Add Structured Translation Compatibility Probing

**Files:**
- Create: `Sources/EMKECoordinator/TranslationConnectionProbe.swift`
- Create: `Tests/EMKECoordinatorTests/TranslationConnectionProbeTests.swift`

**Interfaces:**
- Consumes: the same session factory/configuration as runtime plus an optional 24 kHz PCM16 speech sample supplied by an interactive audio test.
- Produces: `TranslationCompatibilityReport` with independent handshake, target-language, source-transcript, audio-output, dual-session, and graceful-close results.

- [ ] **Step 1: Write failing structured-result tests**

```swift
@Test func chatOnlyGatewayIsReportedAsTranslationHandshakeFailure() async {
    let probe = TranslationConnectionProbe(factory: FailingFactory(error: TestError.http404))
    let report = await probe.run(configuration: probeConfiguration)
    #expect(report.handshake == .failed(.translationEndpointUnavailable))
    #expect(report.targetLanguage == .notRun)
    #expect(report.isFullyCompatible == false)
}

@Test func missingSourceTranscriptIsNotMisreportedAsInvalidKey() async {
    let probe = TranslationConnectionProbe(factory: scriptedFactoryWithoutInputTranscript)
    let report = await probe.run(configuration: probeConfiguration, speechSample: speechPCM)
    #expect(report.authentication == .passed)
    #expect(report.sourceTranscript == .failed(.sourceTranscriptionUnavailable))
}
```

- [ ] **Step 2: Run probe tests and confirm RED**

Run: `swift test --filter TranslationConnectionProbeTests`

Expected: compilation fails because probe/report types do not exist.

- [ ] **Step 3: Implement capability-by-capability probing**

Classify endpoint/handshake, authorization, session update, two concurrent sessions, optional sample audio output, optional source transcript, and graceful close separately. Without a speech sample, mark audio/transcript as `.requiresInteractiveAudio`; never mark the entire gateway fully compatible. Redact error messages by stripping authorization values and never include the API key in the report.

- [ ] **Step 4: Run probe tests and commit**

Run: `swift test --filter TranslationConnectionProbeTests`

Expected: all probe tests pass.

Commit: `feat: probe translation gateway capabilities`

---

### Task 6: Integrate Local Settings, Keychain, Languages, and Status into the Menu Bar

**Files:**
- Create: `Sources/EMKEMenuBarApp/AppSettingsStore.swift`
- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- Modify: `Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift`
- Modify: `Tests/EMKEAudioEngineTests/MenuBarModelTests.swift`
- Modify: `Package.swift`

**Interfaces:**
- Consumes: `KeychainSecretStore`, public settings persistence, `TranslationCoordinator`, and `TranslationConnectionProbe`.
- Produces: user-editable API Key/Base URL/Model ID, mother/meeting languages, explicit channel statuses, bypass controls, connection-test report, and transient subtitle text.

- [ ] **Step 1: Write failing persistence and readiness tests**

```swift
@Test @MainActor func apiReadinessRequiresKeyBaseURLModelAndDevices() async {
    let model = makeMenuModel(secret: nil)
    await model.loadConfiguration()
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"
    #expect(model.readiness == .apiKeyRequired)
    model.apiKeyDraft = "replacement-key"
    #expect(model.readiness == .ready)
}

@Test func publicSettingsNeverPersistAPIKey() throws {
    let defaults = isolatedDefaults()
    let store = UserDefaultsAppSettingsStore(defaults: defaults)
    try store.save(.fixture(baseURL: "https://gateway.example/v1", modelID: "translate-model"))
    #expect(defaults.string(forKey: "apiKey") == nil)
}
```

- [ ] **Step 2: Run menu tests and confirm RED**

Run: `swift test --filter MenuBarModelTests`

Expected: compilation fails because settings/API readiness interfaces do not exist.

- [ ] **Step 3: Implement non-secret settings and Keychain-backed start**

Persist Base URL, model ID, languages, and device UIDs in `UserDefaults`. `apiKeyDraft` is transient and masked; saving writes it to `SecretStore`, then clears the draft. Starting loads the key from Keychain, validates HTTPS/Base URL/model, resolves devices, and passes a complete `TranslationCoordinatorConfiguration` to the coordinator.

- [ ] **Step 4: Replace local-audio-only controls with translation controls**

Add editable `SecureField("API Key")`, Base URL, Model ID, mother-language and meeting-language pickers, Test Connection, Start/Stop Translation, inbound original bypass, outbound explicit original bypass, channel state labels, and current source/translated subtitle text. Remove the statement that the model is not connected. Never display, log, or restore the plaintext key into a visible text field.

- [ ] **Step 5: Run UI model tests and build the app**

Run: `swift test --filter MenuBarModelTests && swift build --product EMKEMenuBarApp`

Expected: tests pass and the executable builds without warnings.

- [ ] **Step 6: Commit the menu integration**

Commit: `feat: configure translation from the menu bar`

---

### Task 7: Document Contracts and Verify the Installed-Driver Build

**Files:**
- Create: `docs/translation-coordinator-contract.md`
- Modify: `docs/superpowers/specs/2026-07-18-emke-translation-macos-mvp-design.md`
- Modify: `docs/superpowers/plans/2026-07-18-emke-translation-coordinator.md`

**Interfaces:**
- Consumes: implemented runtime behavior and verification output.
- Produces: exact session, buffering, privacy, compatibility, and remaining-packaging boundaries.

- [ ] **Step 1: Write the runtime contract**

Document exact endpoint derivation, 200 ms frame size, inbound/outbound target languages, source-transcription requirement, utterance selection thresholds, buffer caps, fail-open/fail-closed behavior, reconnect schedule, Keychain/UserDefaults separation, probe result meanings, and the fact that Chat Completions success does not establish Translation compatibility.

- [ ] **Step 2: Run complete deterministic verification**

Run:

```bash
swift test --parallel
swift build -c release
swift build --product EMKEMenuBarApp
xcrun clang -std=c11 -arch arm64 -mmacosx-version-min=14.0 -Wall -Wextra -Werror \
  -ISources/EMKEAudioBridge/include -ISources/EMKEAudioHAL/include \
  -fsyntax-only Sources/EMKEAudioHAL/EMKEAudioHAL.c
git diff --check
```

Expected: all tests and builds pass, the C bridge is warning-free, and no whitespace errors exist.

- [ ] **Step 3: Run installed-driver integration without a provider secret**

Run: `EMKE_RUN_LIVE_AUDIO_TESTS=1 swift test --filter liveVirtualEndpointsStartAndStop`

Expected: both installed virtual AUHAL endpoints start and stop successfully. This validates only local audio; do not claim the supplied custom Base URL works until a rotated key is entered through the app and the live probe passes.

- [ ] **Step 4: Perform secret and artifact scans**

Run:

```bash
git grep -nE 'sk-[A-Za-z0-9_-]{12,}|Authorization: Bearer [^<]' -- . ':!docs/superpowers/plans/2026-07-18-emke-translation-core-foundation.md'
find . -type f \( -name '*.pcm' -o -name '*.wav' -o -name '*.aiff' \) -not -path './.build/*'
git status --short
```

Expected: no API key, Authorization value, recording, subtitle artifact, or uncommitted build output is present.

- [ ] **Step 5: Mark plan complete and commit**

Set every completed checkbox to `[x]`, add a verification summary and the remaining signed-installer/meeting-app/live-gateway boundary.

Commit: `docs: complete translation coordinator plan`

## Self-Review Result

- Spec coverage: dual sessions, source-transcript mother-language gate, exact one-of-two playback, same-language bypass, subtitles, Keychain, custom Base URL testing, independent failure safety, graceful close, and reconnect behavior each map to a task.
- Placeholder scan: the plan contains no deferred implementation markers; live provider compatibility is explicitly an external verification boundary rather than an implementation placeholder.
- Type consistency: `TranslationSessionConfiguration`, typed delta models, coordinator configuration/state, audio/session protocols, and probe results are introduced before later consumers use them.
