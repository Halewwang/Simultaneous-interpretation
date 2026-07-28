# EMKE Translation Realtime Audio Stability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变 Provider、凭据和出站安全合同的前提下，实现入站 12% 原声预览、80 ms 原译交叉淡化、自适应 VAD、通道 epoch、可配置分帧、匿名延迟指标和明确的运行能力状态。

**Architecture:** 新增纯 Swift 入站试听状态机和独立 PCM renderer，由 `TranslationCoordinator` 编排，`LocalAudioEngine` 继续只拥有端点生命周期。入站正常路径和 fail-open 的 PCM 由协调器写入现有 translated output；显式原声旁路仍由音频引擎直接执行。网络会话、VAD、播放和 UI 状态通过配置、epoch 与确定性测试保持隔离。

**Tech Stack:** Swift 6.2、Swift Concurrency、Swift Testing、SwiftUI/AppKit、现有 EMKECoordinator / EMKEAudioEngine / EMKERouting / EMKERealtime 模块。

## Global Constraints

- 实施基线固定为 `4892578`，工作分支为 `codex/emke-audio-stability`。
- 目标平台固定为 Apple Silicon、macOS 14 及以上。
- Translation 音频保持 24,000 Hz、单声道、signed little-endian PCM16。
- 原声预览增益固定为 `0.12`，母语恢复和原译交叉淡化固定为 80 ms，即 1,920 个 24 kHz 样本。
- 自适应 VAD 使用 10 ms 块、20 ms attack、300 ms release、500 ms 译音尾部。
- 公开发布的网络分帧默认保持 200 ms；只有真实有声 Provider 探测通过后才切换为 40 ms。
- 入站失败必须 fail-open；出站失败必须 fail-closed；显式旁路语义不得改变。
- 音频、字幕、API Key、Authorization 头、用户和设备身份不得写入延迟指标、磁盘日志或测试产物。
- 时间测试使用注入时钟或样本计数，不使用真实 sleep；真实 Provider 探测除外且默认跳过。
- 不实现固定音色、自定义提示词、多模型级联、Windows 音频链路或驱动环形缓冲重写。

---

## File Structure

### New production files

- `Sources/EMKECoordinator/AudioStabilityConfiguration.swift`：内部功能开关与 40/200 ms 配置。
- `Sources/EMKECoordinator/PCM16GainRamp.swift`：PCM16 增益包络和饱和混音。
- `Sources/EMKECoordinator/InboundAuditionController.swift`：话语路由状态机和播放指令。
- `Sources/EMKECoordinator/InboundAuditionRenderer.swift`：执行原声预览、恢复和双流交叉淡化。
- `Sources/EMKECoordinator/AdaptivePCMVoiceActivityDetector.swift`：动态噪声底 VAD。
- `Sources/EMKECoordinator/TranslationLatencyTracker.swift`：匿名、内存内分段延迟。

### New test files

- `Tests/EMKECoordinatorTests/PCM16GainRampTests.swift`
- `Tests/EMKECoordinatorTests/InboundAuditionControllerTests.swift`
- `Tests/EMKECoordinatorTests/InboundAuditionRendererTests.swift`
- `Tests/EMKECoordinatorTests/AdaptivePCMVoiceActivityDetectorTests.swift`
- `Tests/EMKECoordinatorTests/TranslationLatencyTrackerTests.swift`
- `Tests/EMKECoordinatorTests/LiveTranslationProbeTests.swift`

### Modified files

- `Sources/EMKECoordinator/PCMFrameBatcher.swift`
- `Sources/EMKECoordinator/TranslationCoordinator.swift`
- `Sources/EMKECoordinator/TranslationCoordinatorState.swift`
- `Sources/EMKEMenuBarApp/AppLocalization.swift`
- `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- `Sources/EMKEMenuBarApp/TranslationChannelRow.swift`
- `Tests/EMKECoordinatorTests/PCMFrameBatcherTests.swift`
- `Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift`
- `Tests/EMKEAudioEngineTests/TranslationChannelPresentationTests.swift`
- `Tests/EMKEAudioEngineTests/TranslationDashboardPresentationTests.swift`
- `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
- `docs/translation-coordinator-contract.md`

### Preserved for release fallback

- `Sources/EMKECoordinator/InboundUtteranceBuffer.swift`：`AudioStabilityConfiguration.legacy`
  关闭原声预览时继续提供 v0.2.3 候选缓冲行为。
- `Tests/EMKECoordinatorTests/InboundUtteranceBufferTests.swift`：保持回退路径的既有覆盖。

---

### Task 1: Internal Stability Configuration and Configurable Network Frames

**Files:**
- Create: `Sources/EMKECoordinator/AudioStabilityConfiguration.swift`
- Modify: `Sources/EMKECoordinator/PCMFrameBatcher.swift:3-35`
- Modify: `Sources/EMKECoordinator/TranslationCoordinator.swift:62-81`
- Test: `Tests/EMKECoordinatorTests/PCMFrameBatcherTests.swift`

**Interfaces:**
- Consumes: 24 kHz mono PCM16 from `LocalAudioEngine`.
- Produces: `AudioStabilityConfiguration`, `PCMFrameBatcher.init(frameDurationMilliseconds:)`, and instance `frameByteCount`.

- [ ] **Step 1: Write failing configuration and frame-size tests**

```swift
@Test
func fortyMillisecondBatcherEmitsExactFrames() throws {
    var batcher = PCMFrameBatcher(frameDurationMilliseconds: 40)
    #expect(batcher.frameByteCount == 1_920)
    let frames = try batcher.append(Data(repeating: 7, count: 3_840))
    #expect(frames.map(\.count) == [1_920, 1_920])
}

@Test
func productionConfigurationKeepsProviderSafeTwoHundredMilliseconds() {
    #expect(AudioStabilityConfiguration.production.inputFrameDurationMilliseconds == 200)
    #expect(AudioStabilityConfiguration.providerProbe40ms.inputFrameDurationMilliseconds == 40)
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
swift test --filter 'fortyMillisecondBatcherEmitsExactFrames|productionConfigurationKeepsProviderSafeTwoHundredMilliseconds'
```

Expected: FAIL because the configuration type and configurable initializer do not exist.

- [ ] **Step 3: Add the internal configuration**

```swift
public struct AudioStabilityConfiguration: Equatable, Sendable {
    public let inboundAuditionEnabled: Bool
    public let adaptiveVADEnabled: Bool
    public let inputFrameDurationMilliseconds: Int

    public init(
        inboundAuditionEnabled: Bool,
        adaptiveVADEnabled: Bool,
        inputFrameDurationMilliseconds: Int
    ) {
        precondition(inputFrameDurationMilliseconds > 0)
        self.inboundAuditionEnabled = inboundAuditionEnabled
        self.adaptiveVADEnabled = adaptiveVADEnabled
        self.inputFrameDurationMilliseconds = inputFrameDurationMilliseconds
    }

    public static let production = Self(
        inboundAuditionEnabled: true,
        adaptiveVADEnabled: true,
        inputFrameDurationMilliseconds: 200
    )

    public static let providerProbe40ms = Self(
        inboundAuditionEnabled: true,
        adaptiveVADEnabled: true,
        inputFrameDurationMilliseconds: 40
    )

    public static let legacy = Self(
        inboundAuditionEnabled: false,
        adaptiveVADEnabled: false,
        inputFrameDurationMilliseconds: 200
    )
}
```

Add `audioStability: AudioStabilityConfiguration = .production` to
`TranslationCoordinatorConfiguration`.

- [ ] **Step 4: Make `PCMFrameBatcher` calculate its instance frame size**

```swift
public struct PCMFrameBatcher: Sendable {
    public let frameByteCount: Int
    private var buffer = Data()

    public init(frameDurationMilliseconds: Int = 200) {
        precondition(frameDurationMilliseconds > 0)
        let bytesTimesMilliseconds = 24_000 * 2 * frameDurationMilliseconds
        precondition(bytesTimesMilliseconds.isMultiple(of: 1_000))
        frameByteCount = bytesTimesMilliseconds / 1_000
    }
}
```

Replace `Self.frameByteCount` uses with the instance property. Preserve odd-byte rejection and `reset()`.

- [ ] **Step 5: Run focused and coordinator compilation tests**

Run:

```bash
swift test --filter PCMFrameBatcherTests
swift test --filter TranslationCoordinatorTests
```

Expected: both commands PASS; existing default 9,600-byte behavior remains green.

- [ ] **Step 6: Commit**

```bash
git add Sources/EMKECoordinator/AudioStabilityConfiguration.swift Sources/EMKECoordinator/PCMFrameBatcher.swift Sources/EMKECoordinator/TranslationCoordinator.swift Tests/EMKECoordinatorTests/PCMFrameBatcherTests.swift
git commit -m "feat: make realtime audio framing configurable"
```

---

### Task 2: PCM16 Gain Ramps and Saturating Mixer

**Files:**
- Create: `Sources/EMKECoordinator/PCM16GainRamp.swift`
- Create: `Tests/EMKECoordinatorTests/PCM16GainRampTests.swift`

**Interfaces:**
- Consumes: aligned PCM16 `Data`, current gain, target gain, and ramp sample count.
- Produces: `PCM16GainRamp`, `PCM16Mixer`, and `PCM16ProcessingError`.

- [ ] **Step 1: Write failing ramp and mixer tests**

```swift
private func constantPCM16(_ amplitude: Int16, samples: Int) -> Data {
    let bits = UInt16(bitPattern: amplitude)
    var data = Data(capacity: samples * 2)
    for _ in 0..<samples {
        data.append(UInt8(truncatingIfNeeded: bits))
        data.append(UInt8(truncatingIfNeeded: bits >> 8))
    }
    return data
}

private func decodePCM16(_ data: Data) -> [Int16] {
    stride(from: 0, to: data.count, by: 2).map { index in
        Int16(bitPattern:
            UInt16(data[index]) | UInt16(data[index + 1]) << 8
        )
    }
}

@Test
func eightyMillisecondRampReachesTargetAcrossChunks() throws {
    var ramp = PCM16GainRamp(initialGain: 0.12)
    ramp.setTarget(1.0, overSamples: 1_920)

    let first = try ramp.process(constantPCM16(10_000, samples: 960))
    let second = try ramp.process(constantPCM16(10_000, samples: 960))

    #expect(decodePCM16(first).first == 1_200)
    #expect(abs(Int(decodePCM16(second).last ?? 0) - 10_000) <= 1)
    #expect(ramp.currentGain == 1.0)
}

@Test
func mixerSaturatesInsteadOfWrapping() throws {
    let mixed = try PCM16Mixer.mix(
        constantPCM16(30_000, samples: 2),
        constantPCM16(30_000, samples: 2)
    )
    #expect(decodePCM16(mixed) == [Int16.max, Int16.max])
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
swift test --filter PCM16GainRampTests
```

Expected: FAIL because gain and mixer types do not exist.

- [ ] **Step 3: Implement the exact public surface**

```swift
public enum PCM16ProcessingError: Error, Equatable, Sendable {
    case invalidPCM16ByteCount
    case mismatchedSampleCount
    case invalidRampSampleCount
}

public struct PCM16GainRamp: Sendable {
    public private(set) var currentGain: Double
    private var startGain: Double
    private var targetGain: Double
    private var totalSamples = 0
    private var processedSamples = 0

    public init(initialGain: Double) {
        currentGain = initialGain
        startGain = initialGain
        targetGain = initialGain
    }

    public mutating func setTarget(_ gain: Double, overSamples: Int) {
        precondition(overSamples >= 0)
        startGain = currentGain
        targetGain = gain
        totalSamples = overSamples
        processedSamples = 0
        if overSamples == 0 { currentGain = gain }
    }

    public mutating func process(_ pcm16: Data) throws -> Data
}

public enum PCM16Mixer {
    public static func mix(_ lhs: Data, _ rhs: Data) throws -> Data
}
```

Use little-endian `Int16`, sample-by-sample linear interpolation, and clamped
`Int32` addition. The last sample of a completed ramp must use the exact target gain.

- [ ] **Step 4: Add edge-case tests and make them pass**

Add tests for odd bytes, mismatched mixer lengths, a ramp changed midway, silence,
negative PCM, and zero-sample immediate gain changes.

Run:

```bash
swift test --filter PCM16GainRampTests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Sources/EMKECoordinator/PCM16GainRamp.swift Tests/EMKECoordinatorTests/PCM16GainRampTests.swift
git commit -m "feat: add deterministic PCM gain ramps"
```

---

### Task 3: Inbound Audition State Machine and Crossfade Renderer

**Files:**
- Create: `Sources/EMKECoordinator/InboundAuditionController.swift`
- Create: `Sources/EMKECoordinator/InboundAuditionRenderer.swift`
- Create: `Tests/EMKECoordinatorTests/InboundAuditionControllerTests.swift`
- Create: `Tests/EMKECoordinatorTests/InboundAuditionRendererTests.swift`
- Preserve: `Sources/EMKECoordinator/InboundUtteranceBuffer.swift`
- Preserve: `Tests/EMKECoordinatorTests/InboundUtteranceBufferTests.swift`

**Interfaces:**
- Consumes: original/translated PCM, `LanguageHypotheses`, utterance lifecycle, and fail-open events.
- Produces: `InboundAuditionCommand`, `InboundRenderedChunk`,
  `InboundAuditionController`, and
  `InboundAuditionRenderer.consume(_:) -> [InboundRenderedChunk]`.

- [ ] **Step 1: Write failing state-machine tests**

```swift
@Test
func motherLanguageLocksOriginalWithoutReplayingBufferedPCM() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let id = controller.beginUtterance()

    #expect(controller.appendOriginal(Data([1, 1]), utteranceID: id) == [
        .original(Data([1, 1])),
    ])
    #expect(controller.observe(
        LanguageHypotheses(["zh": 0.9]),
        utteranceID: id
    ) == [.setOriginalGain(1.0, rampSamples: 1_920)])
    #expect(controller.route == .original)
}

@Test
func translationArrivingBeforeLanguageDecisionIsHeldThenCrossfaded() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let id = controller.beginUtterance()
    #expect(controller.appendTranslation(Data([2, 2]), utteranceID: id).isEmpty)
    #expect(controller.observe(
        LanguageHypotheses(["de": 0.9]),
        utteranceID: id
    ) == [.beginCrossfade([Data([2, 2])], rampSamples: 1_920)])
    #expect(controller.route == .translated)
}
```

Also add tests proving a stale `utteranceID` is ignored, route lock cannot switch,
deadline speech prefers available translation, non-speech prefers original, and
`failOpen()` overrides a translated route without replaying stored original PCM.

- [ ] **Step 2: Run controller tests and verify RED**

Run:

```bash
swift test --filter InboundAuditionControllerTests
```

Expected: FAIL because the controller types do not exist.

- [ ] **Step 3: Implement the state-machine interface**

```swift
public enum InboundAuditionCommand: Equatable, Sendable {
    case original(Data)
    case setOriginalGain(Double, rampSamples: Int)
    case beginCrossfade([Data], rampSamples: Int)
    case translation(Data)
    case failOpen(rampSamples: Int)
    case reset
}

public struct InboundAuditionController: Sendable {
    public private(set) var route: InboundRoute = .undecided
    public private(set) var utteranceID: UInt64?

    public init(
        motherLanguage: SupportedLanguage,
        maximumBufferedTranslationBytes: Int = 240_000
    )

    public mutating func beginUtterance() -> UInt64
    public mutating func appendOriginal(
        _ pcm16: Data,
        utteranceID: UInt64
    ) -> [InboundAuditionCommand]
    public mutating func appendTranslation(
        _ pcm16: Data,
        utteranceID: UInt64
    ) -> [InboundAuditionCommand]
    public mutating func observe(
        _ hypotheses: LanguageHypotheses,
        utteranceID: UInt64
    ) -> [InboundAuditionCommand]
    public mutating func resolveDeadline(
        isSpeech: Bool,
        utteranceID: UInt64
    ) -> [InboundAuditionCommand]
    public mutating func failOpen() -> [InboundAuditionCommand]
    public mutating func finish(utteranceID: UInt64) -> [InboundAuditionCommand]
    public mutating func reset() -> [InboundAuditionCommand]
}
```

Reuse `InboundLanguageGate`; buffer only translated PCM before a translated route is
locked. Never buffer original PCM for replay.

- [ ] **Step 4: Write failing renderer tests**

```swift
private func constantPCM16(_ amplitude: Int16, samples: Int) -> Data {
    let bits = UInt16(bitPattern: amplitude)
    var data = Data(capacity: samples * 2)
    for _ in 0..<samples {
        data.append(UInt8(truncatingIfNeeded: bits))
        data.append(UInt8(truncatingIfNeeded: bits >> 8))
    }
    return data
}

private func decodePCM16(_ data: Data) -> [Int16] {
    stride(from: 0, to: data.count, by: 2).map { index in
        Int16(bitPattern:
            UInt16(data[index]) | UInt16(data[index + 1]) << 8
        )
    }
}

@Test
func rendererPreviewsOriginalAtTwelvePercent() throws {
    var renderer = InboundAuditionRenderer()
    let output = try renderer.consume(.original(
        constantPCM16(10_000, samples: 240)
    ))
    #expect(output.count == 1)
    #expect(output[0].source == .original)
    #expect(decodePCM16(output[0].pcm16).allSatisfy { $0 == 1_200 })
}

@Test
func rendererMixesBothStreamsForExactlyEightyMilliseconds() throws {
    var renderer = InboundAuditionRenderer()
    _ = try renderer.consume(.beginCrossfade(
        [constantPCM16(2_000, samples: 1_920)],
        rampSamples: 1_920
    ))
    let output = try renderer.consume(.original(
        constantPCM16(10_000, samples: 1_920)
    ))
    let samples = output.flatMap { decodePCM16($0.pcm16) }
    #expect(output.allSatisfy { $0.source == .crossfade })
    #expect(samples.count == 1_920)
    #expect(abs(Int(samples.first ?? 0) - 1_200) <= 1)
    #expect(abs(Int(samples.last ?? 0) - 2_000) <= 1)
}
```

- [ ] **Step 5: Implement queued crossfade execution**

```swift
public enum InboundRenderedSource: Equatable, Sendable {
    case original
    case crossfade
    case translation
}

public struct InboundRenderedChunk: Equatable, Sendable {
    public let pcm16: Data
    public let source: InboundRenderedSource
}

public enum InboundAuditionRendererError: Error, Equatable, Sendable {
    case bufferLimitExceeded
}

public struct InboundAuditionRenderer: Sendable {
    public static let previewGain = 0.12
    public static let rampSampleCount = 1_920

    public init(maximumQueuedBytesPerSource: Int = 240_000)
    public mutating func consume(
        _ command: InboundAuditionCommand
    ) throws -> [InboundRenderedChunk]
}
```

The renderer keeps independent original and translation ramps plus PCM queues capped at
240,000 bytes per source.
During crossfade it emits only equal-length pairs through `PCM16Mixer`; after 1,920
mixed samples it drops future original PCM and flushes remaining translation at gain
`1.0`. `failOpen` clears translated PCM and ramps the next live original samples from
the current original gain to `1.0`. Exceeding either queue limit throws
`InboundAuditionRendererError.bufferLimitExceeded`, which Task 7 converts to direct
fail-open.

- [ ] **Step 6: Run controller and renderer suites**

Run:

```bash
swift test --filter 'InboundAuditionControllerTests|InboundAuditionRendererTests'
```

Expected: PASS, including no-replay and bounded-buffer tests.

- [ ] **Step 7: Verify the legacy utterance buffer remains green**

Run:

```bash
swift test --filter 'InboundUtteranceBufferTests|InboundAuditionControllerTests|InboundAuditionRendererTests'
```

Expected: PASS; the new and fallback paths both retain deterministic coverage.

- [ ] **Step 8: Commit**

```bash
git add Sources/EMKECoordinator/InboundAuditionController.swift Sources/EMKECoordinator/InboundAuditionRenderer.swift Tests/EMKECoordinatorTests/InboundAuditionControllerTests.swift Tests/EMKECoordinatorTests/InboundAuditionRendererTests.swift
git commit -m "feat: add inbound audition routing and crossfade"
```

---

### Task 4: Adaptive Voice Activity Detection

**Files:**
- Create: `Sources/EMKECoordinator/AdaptivePCMVoiceActivityDetector.swift`
- Create: `Tests/EMKECoordinatorTests/AdaptivePCMVoiceActivityDetectorTests.swift`
- Preserve: `Sources/EMKECoordinator/PCMVoiceActivityDetector.swift`
- Preserve: `Tests/EMKECoordinatorTests/PCMVoiceActivityDetectorTests.swift`

**Interfaces:**
- Consumes: one aligned 10 ms PCM16 block per call.
- Produces: `AdaptivePCMVoiceActivityDetector.observe(_:)`, `isSpeaking`, `noiseFloor`, and `currentThreshold`.

- [ ] **Step 1: Write failing adaptive VAD tests**

```swift
private func pcm16(amplitude: Int16, sampleCount: Int = 240) -> Data {
    let bits = UInt16(bitPattern: amplitude)
    var data = Data(capacity: sampleCount * 2)
    for _ in 0..<sampleCount {
        data.append(UInt8(truncatingIfNeeded: bits))
        data.append(UInt8(truncatingIfNeeded: bits >> 8))
    }
    return data
}

@Test
func adaptiveVADRequiresTwoVoicedFramesToStart() throws {
    var detector = AdaptivePCMVoiceActivityDetector()
    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .none)
    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .speechStarted)
}

@Test
func noiseFloorAdaptsOnlyOutsideSpeech() throws {
    var detector = AdaptivePCMVoiceActivityDetector()
    for _ in 0..<20 {
        _ = try detector.observe(pcm16(amplitude: 120))
    }
    let learned = detector.noiseFloor
    _ = try detector.observe(pcm16(amplitude: 8_000))
    _ = try detector.observe(pcm16(amplitude: 8_000))
    #expect(detector.noiseFloor == learned)
}
```

Add release-after-30-silent-frames, threshold clamp, reset, empty input, odd-byte,
and transient-noise tests.

- [ ] **Step 2: Run adaptive VAD tests and verify RED**

Run:

```bash
swift test --filter AdaptivePCMVoiceActivityDetectorTests
```

Expected: FAIL because the adaptive detector does not exist.

- [ ] **Step 3: Implement exact defaults**

```swift
public struct AdaptivePCMVoiceActivityDetector: Sendable {
    public private(set) var isSpeaking = false
    public private(set) var noiseFloor = 0.002
    public var currentThreshold: Double {
        min(max(noiseFloor * 3.0, 0.006), 0.030)
    }

    public init(
        initialNoiseFloor: Double = 0.002,
        noiseFloorEMA: Double = 0.05,
        thresholdMultiplier: Double = 3.0,
        minimumThreshold: Double = 0.006,
        maximumThreshold: Double = 0.030,
        attackFrameLimit: Int = 2,
        silenceFrameLimit: Int = 30
    )

    public mutating func observe(
        _ pcm16: Data
    ) throws -> PCMVoiceActivityEvent
    public mutating func reset()
}
```

Update the noise-floor EMA only while not speaking and when RMS is below the current
threshold. Freeze the floor during speech.

- [ ] **Step 4: Add a coordinator-selectable wrapper**

```swift
enum InboundVoiceActivityDetector: Sendable {
    case fixed(PCMVoiceActivityDetector)
    case adaptive(AdaptivePCMVoiceActivityDetector)

    var isSpeaking: Bool {
        switch self {
        case .fixed(let detector): detector.isSpeaking
        case .adaptive(let detector): detector.isSpeaking
        }
    }

    mutating func observe(_ pcm16: Data) throws -> PCMVoiceActivityEvent {
        switch self {
        case .fixed(var detector):
            let event = try detector.observe(pcm16)
            self = .fixed(detector)
            return event
        case .adaptive(var detector):
            let event = try detector.observe(pcm16)
            self = .adaptive(detector)
            return event
        }
    }

    mutating func reset() {
        switch self {
        case .fixed(var detector):
            detector.reset()
            self = .fixed(detector)
        case .adaptive(var detector):
            detector.reset()
            self = .adaptive(detector)
        }
    }
}
```

Do not connect the wrapper to the coordinator until Task 7.

- [ ] **Step 5: Run both VAD suites**

Run:

```bash
swift test --filter 'PCMVoiceActivityDetectorTests|AdaptivePCMVoiceActivityDetectorTests'
```

Expected: PASS; legacy fallback remains available.

- [ ] **Step 6: Commit**

```bash
git add Sources/EMKECoordinator/AdaptivePCMVoiceActivityDetector.swift Tests/EMKECoordinatorTests/AdaptivePCMVoiceActivityDetectorTests.swift
git commit -m "feat: add adaptive inbound voice detection"
```

---

### Task 5: Anonymous Translation Latency Tracking

**Files:**
- Create: `Sources/EMKECoordinator/TranslationLatencyTracker.swift`
- Create: `Tests/EMKECoordinatorTests/TranslationLatencyTrackerTests.swift`
- Modify later in Task 7: `Sources/EMKECoordinator/TranslationCoordinatorState.swift`

**Interfaces:**
- Consumes: `UInt64` utterance IDs, monotonic nanoseconds, and fixed milestones.
- Produces: `TranslationLatencyTracker.mark`, `TranslationLatencySnapshot`,
  `TranslationLatencySummary`, and bounded in-memory diagnostics.

- [ ] **Step 1: Write failing milestone and privacy-surface tests**

```swift
@Test
func trackerComputesOnlyFirstOccurrenceOfEachMilestone() {
    var tracker = TranslationLatencyTracker(capacity: 2)
    tracker.mark(.speechStarted, utteranceID: 7, at: 1_000_000)
    tracker.mark(.firstNetworkFrameSent, utteranceID: 7, at: 41_000_000)
    tracker.mark(.firstNetworkFrameSent, utteranceID: 7, at: 99_000_000)

    let value = tracker.snapshot(for: 7)
    #expect(value?.speechToFirstNetworkFrameMilliseconds == 40)
}

@Test
func trackerEvictsOldUtterancesAndResetClearsMemory() {
    var tracker = TranslationLatencyTracker(capacity: 1)
    tracker.mark(.speechStarted, utteranceID: 1, at: 1)
    tracker.mark(.speechStarted, utteranceID: 2, at: 2)
    #expect(tracker.snapshot(for: 1) == nil)
    tracker.reset()
    #expect(tracker.latestSnapshot == nil)
}

@Test
func trackerPublishesNearestRankP95() {
    var tracker = TranslationLatencyTracker(capacity: 20)
    for id in UInt64(1)...20 {
        tracker.mark(.speechStarted, utteranceID: id, at: 0)
        tracker.mark(
            .firstNetworkFrameSent,
            utteranceID: id,
            at: id * 1_000_000
        )
    }
    let value = tracker.diagnostics.summary.speechToFirstNetworkFrame
    #expect(value.sampleCount == 20)
    #expect(value.p50Milliseconds == 10)
    #expect(value.p95Milliseconds == 19)
}
```

- [ ] **Step 2: Run tracker tests and verify RED**

Run:

```bash
swift test --filter TranslationLatencyTrackerTests
```

Expected: FAIL because latency types do not exist.

- [ ] **Step 3: Implement the bounded typed tracker**

```swift
public enum TranslationLatencyMilestone: CaseIterable, Sendable {
    case speechStarted
    case firstNetworkFrameSent
    case firstSourceTranscriptReceived
    case routeDecided
    case firstTranslationAudioReceived
    case firstPlaybackScheduled
}

public struct TranslationLatencySnapshot: Equatable, Sendable {
    public let utteranceID: UInt64
    public let speechToFirstNetworkFrameMilliseconds: Double?
    public let speechToFirstSourceTranscriptMilliseconds: Double?
    public let speechToRouteDecisionMilliseconds: Double?
    public let speechToFirstTranslationAudioMilliseconds: Double?
    public let translationAudioToPlaybackMilliseconds: Double?
}

public struct TranslationLatencyPercentiles: Equatable, Sendable {
    public let sampleCount: Int
    public let p50Milliseconds: Double?
    public let p95Milliseconds: Double?
    public static let empty = Self(
        sampleCount: 0,
        p50Milliseconds: nil,
        p95Milliseconds: nil
    )
}

public struct TranslationLatencySummary: Equatable, Sendable {
    public let speechToFirstNetworkFrame: TranslationLatencyPercentiles
    public let speechToFirstSourceTranscript: TranslationLatencyPercentiles
    public let speechToRouteDecision: TranslationLatencyPercentiles
    public let speechToFirstTranslationAudio: TranslationLatencyPercentiles
    public let translationAudioToPlayback: TranslationLatencyPercentiles
    public static let empty = Self(
        speechToFirstNetworkFrame: .empty,
        speechToFirstSourceTranscript: .empty,
        speechToRouteDecision: .empty,
        speechToFirstTranslationAudio: .empty,
        translationAudioToPlayback: .empty
    )
}

public struct TranslationLatencyDiagnostics: Equatable, Sendable {
    public let latest: TranslationLatencySnapshot?
    public let summary: TranslationLatencySummary
    public static let empty = Self(latest: nil, summary: .empty)
}

public struct TranslationLatencyTracker: Sendable {
    public init(capacity: Int = 128)
    public mutating func mark(
        _ milestone: TranslationLatencyMilestone,
        utteranceID: UInt64,
        at nanoseconds: UInt64
    )
    public func snapshot(for utteranceID: UInt64) -> TranslationLatencySnapshot?
    public var latestSnapshot: TranslationLatencySnapshot? { get }
    public var diagnostics: TranslationLatencyDiagnostics { get }
    public mutating func reset()
}
```

Use only numeric identifiers and times. Do not add generic metadata dictionaries or
string payloads that could later receive subtitles or credentials. Percentiles use the
nearest-rank rule over non-nil completed intervals, with index
`ceil(percentile * count) - 1` after ascending sort.

- [ ] **Step 4: Run tests and scan the API surface**

Run:

```bash
swift test --filter TranslationLatencyTrackerTests
rg -n 'Data|String|URL|Authorization|apiKey|transcript' Sources/EMKECoordinator/TranslationLatencyTracker.swift
```

Expected: tests PASS; the scan returns no payload-bearing stored fields.

- [ ] **Step 5: Commit**

```bash
git add Sources/EMKECoordinator/TranslationLatencyTracker.swift Tests/EMKECoordinatorTests/TranslationLatencyTrackerTests.swift
git commit -m "feat: track anonymous translation latency"
```

---

### Task 6: Independent Inbound and Outbound Epoch Isolation

**Files:**
- Modify: `Sources/EMKECoordinator/TranslationCoordinator.swift:95-118,178-225,324-426,681-790`
- Modify: `Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift:70-243,913-1024`

**Interfaces:**
- Consumes: channel identity and the epoch captured when a session/task is created.
- Produces: guarded connect, receive, append-failure, reconnect, deadline, and stop callbacks.

- [ ] **Step 1: Extend the fake and write a failing stale-event test**

```swift
@Test
func staleInboundAudioFromPreviousEpochCannotReachPlayback() async throws {
    let stale = CoordinatorSessionFake(
        appendErrors: [.disconnected]
    )
    let recovered = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        inbound: stale,
        additionalSessions: [recovered],
        reconnectDelays: [.zero]
    )
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(
        Data(repeating: 1, count: 9_600)
    ))
    #expect(await eventually { await harness.coordinator.state.inbound == .active })

    await stale.emit(.success(.outputAudio(audioDelta(Data([9, 9])))))
    try? await Task.yield()
    #expect(!(await harness.audio.inboundPlayback).contains(Data([9, 9])))
    await harness.coordinator.stop()
}
```

Extend `CoordinatorSessionFake` with
`init(appendErrors: [CoordinatorTestError] = [])`; `appendAudio` removes and throws the
first queued error before recording data. Allow `CoordinatorHarness` to accept an injected
initial inbound or outbound fake. A pending `nextEvent` continuation on the failed session
is intentionally resumed after reconnect to reproduce a late callback.

Add the symmetric outbound test and a test proving inbound reconnect does not invalidate
the outbound epoch.

- [ ] **Step 2: Run the stale-event tests and verify RED**

Run:

```bash
swift test --filter 'staleInboundAudioFromPreviousEpochCannotReachPlayback|staleOutboundAudioFromPreviousEpochCannotReachPlayback'
```

Expected: at least one test FAILS because callbacks are not epoch-tagged.

- [ ] **Step 3: Add channel epoch helpers**

```swift
private var inboundEpoch: UInt64 = 0
private var outboundEpoch: UInt64 = 0

@discardableResult
private func advanceEpoch(for channel: Channel) -> UInt64 {
    switch channel {
    case .inbound:
        inboundEpoch &+= 1
        return inboundEpoch
    case .outbound:
        outboundEpoch &+= 1
        return outboundEpoch
    }
}

private func isCurrent(_ epoch: UInt64, for channel: Channel) -> Bool {
    switch channel {
    case .inbound: epoch == inboundEpoch
    case .outbound: epoch == outboundEpoch
    }
}
```

- [ ] **Step 4: Thread epoch through every asynchronous channel path**

Change signatures to:

```swift
private func connectInitialSession(
    _ session: any TranslationSessionControlling,
    channel: Channel,
    epoch: UInt64
) async

private func startInboundReceiveLoop(
    session: any TranslationSessionControlling,
    epoch: UInt64
)

private func handleInboundEvent(
    _ event: TranslationServerEvent,
    epoch: UInt64
) async -> Bool

private func handleChannelFailure(
    _ channel: Channel,
    error: any Error,
    epoch: UInt64
) async

private func scheduleReconnect(
    channel: Channel,
    attempt: Int,
    expectedEpoch: UInt64
)
```

Guard `isCurrent` before every state mutation, subtitle append, audio enqueue, failure
transition, reconnect and session assignment. Advance both epochs at stop before awaiting
session close.

- [ ] **Step 5: Run focused reconnection tests**

Run:

```bash
swift test --filter 'staleInboundAudioFromPreviousEpochCannotReachPlayback|staleOutboundAudioFromPreviousEpochCannotReachPlayback|failedInboundSessionReconnectsWithoutRestartingOutbound|inboundManualBypassSurvivesRealFailureAndReconnect|outboundManualBypassSurvivesRealFailureAndReconnect'
```

Expected: PASS.

- [ ] **Step 6: Run all coordinator tests**

Run:

```bash
swift test --filter EMKECoordinatorTests
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Sources/EMKECoordinator/TranslationCoordinator.swift Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift
git commit -m "fix: isolate realtime channel generations"
```

---

### Task 7: Integrate Audition, Adaptive VAD, Framing, and Latency

**Files:**
- Modify: `Sources/EMKECoordinator/TranslationCoordinator.swift:84-150,152-263,428-679,799-812`
- Modify: `Sources/EMKECoordinator/TranslationCoordinatorState.swift:45-69`
- Modify: `Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift`

**Interfaces:**
- Consumes: all components produced by Tasks 1-6.
- Produces: integrated 12% preview, mother-language ramp, foreign-language crossfade,
  fail-open recovery, configurable network frames, adaptive VAD, and state latency snapshot.

- [ ] **Step 1: Add failing coordinator acceptance tests**

```swift
private func constantPCM16(_ amplitude: Int16, samples: Int) -> Data {
    let bits = UInt16(bitPattern: amplitude)
    var data = Data(capacity: samples * 2)
    for _ in 0..<samples {
        data.append(UInt8(truncatingIfNeeded: bits))
        data.append(UInt8(truncatingIfNeeded: bits >> 8))
    }
    return data
}

private func decodePCM16(_ data: Data) -> [Int16] {
    stride(from: 0, to: data.count, by: 2).map { index in
        Int16(bitPattern:
            UInt16(data[index]) | UInt16(data[index + 1]) << 8
        )
    }
}

private extension CoordinatorHarness {
    func emitInboundSpeechFrames(
        amplitude: Int16,
        count: Int
    ) async {
        for _ in 0..<count {
            await audio.emit(.inboundNetworkAudio(
                constantPCM16(amplitude, samples: 240)
            ))
        }
    }
}

@Test
func undecidedInboundSpeechPlaysTwelvePercentPreview() async throws {
    let harness = CoordinatorHarness()
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually { !(await harness.audio.inboundPlayback).isEmpty })
    let first = decodePCM16((await harness.audio.inboundPlayback)[0])
    #expect(first.allSatisfy { $0 == 1_200 })
    await harness.coordinator.stop()
}

@Test
func foreignSpeechCrossfadesThenDropsFollowingOriginal() async throws {
    let harness = CoordinatorHarness(classifier: {
        _ in LanguageHypotheses(["de": 0.9])
    })
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    await harness.inbound.emit(.success(.inputTranscript(transcriptDelta("Deutsch"))))
    await harness.inbound.emit(.success(.outputAudio(
        audioDelta(constantPCM16(2_000, samples: 1_920))
    )))
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 8)

    let mixed = (await harness.audio.inboundPlayback).flatMap(decodePCM16)
    #expect(mixed.contains { abs(Int($0) - 2_000) <= 1 })
    await harness.coordinator.stop()
}
```

Add tests for mother-language recovery without replay, translation-before-classification,
fail-open recovery from the live point, 40 ms frames of 1,920 bytes, latency timestamps,
manual inbound bypass avoiding double playback, and stop clearing all state.

- [ ] **Step 2: Run the new acceptance tests and verify RED**

Run:

```bash
swift test --filter 'undecidedInboundSpeechPlaysTwelvePercentPreview|foreignSpeechCrossfadesThenDropsFollowingOriginal'
```

Expected: FAIL because the coordinator still buffers original audio.

- [ ] **Step 3: Construct runtime components from configuration**

In `resetRuntimeBuffers` create:

```swift
inboundBatcher = PCMFrameBatcher(
    frameDurationMilliseconds: configuration.audioStability
        .inputFrameDurationMilliseconds
)
outboundBatcher = PCMFrameBatcher(
    frameDurationMilliseconds: configuration.audioStability
        .inputFrameDurationMilliseconds
)
inboundVAD = configuration.audioStability.adaptiveVADEnabled
    ? .adaptive(AdaptivePCMVoiceActivityDetector())
    : .fixed(PCMVoiceActivityDetector())
inboundAudition = InboundAuditionController(
    motherLanguage: configuration.preferences.motherLanguage
)
inboundRenderer = InboundAuditionRenderer()
inboundBuffer = InboundUtteranceBuffer(
    motherLanguage: configuration.preferences.motherLanguage
)
latencyTracker.reset()
```

When `inboundAuditionEnabled` is false, route original, translation, deadline and finish
events through the preserved `InboundUtteranceBuffer`. When it is true, route the same
events through `InboundAuditionController` and `InboundAuditionRenderer`. Do not execute
both paths for one audio block.

- [ ] **Step 4: Replace buffered candidate playback with audition commands**

Add:

```swift
private func executeInbound(
    _ commands: [InboundAuditionCommand]
) async {
    for command in commands {
        guard let chunks = try? inboundRenderer.consume(command) else {
            await forceDirectInboundFailOpen()
            return
        }
        for chunk in chunks {
            if let id = inboundAudition.utteranceID,
               chunk.source == .crossfade || chunk.source == .translation {
                latencyTracker.mark(
                    .firstPlaybackScheduled,
                    utteranceID: id,
                    at: levelTimeNanoseconds()
                )
            }
            try? await audioEngine.enqueueInboundOutput(chunk.pcm16)
        }
    }
}
```

Normal and fail-open audition keep the engine's effective inbound mode `.translated` so
the coordinator owns the 24 kHz gain ramp. Explicit `.originalBypass` remains a direct
audio-engine route and resets audition state so playback cannot duplicate.

Implement the effective routing explicitly:

```swift
private var effectiveInboundAudioMode: InboundOutputMode {
    guard configuration?.audioStability.inboundAuditionEnabled == true else {
        return routing.inbound
    }
    return routing.inbound == .originalFailOpen
        ? .translated
        : routing.inbound
}
```

`forceDirectInboundFailOpen()` is the renderer-error safety fallback: reset audition,
set the engine directly to `.originalFailOpen`, preserve the current outbound mode, and
publish the local processing failure. It must not schedule translated PCM after fallback.

- [ ] **Step 5: Mark every latency milestone once**

Mark:

- `speechStarted` on VAD start;
- `firstNetworkFrameSent` after the first successful append for the current utterance;
- `firstSourceTranscriptReceived` on the first source delta;
- `routeDecided` when `InboundRoute` first leaves `.undecided`;
- `firstTranslationAudioReceived` on the first translated audio delta;
- `firstPlaybackScheduled` before the first crossfade or full-translation enqueue; 12%
  preview output must not satisfy this translation-playback milestone.

Publish `latencyTracker.diagnostics` as the defaulted `latency` property on
`TranslationCoordinatorState`.

- [ ] **Step 6: Preserve the 500 ms tail and safety contracts**

Keep `scheduleInboundDeadline`, `scheduleInboundFinish`, and late-delta tail extension.
On inbound failure execute `.failOpen(rampSamples: 1_920)` before scheduling reconnect.
On outbound failure do not touch outbound translated playback or routing semantics.

- [ ] **Step 7: Run focused integration tests**

Run:

```bash
swift test --filter TranslationCoordinatorTests
```

Expected: PASS, including legacy handshake, reconnect, tail, fail-open and fail-closed tests.

- [ ] **Step 8: Run coordinator and audio-engine suites**

Run:

```bash
swift test --filter 'EMKECoordinatorTests|LocalAudioEngineTests|NetworkPCMConverterTests|RoutingStateMachineTests'
```

Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Sources/EMKECoordinator/TranslationCoordinator.swift Sources/EMKECoordinator/TranslationCoordinatorState.swift Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift
git commit -m "feat: integrate low-latency inbound audition"
```

---

### Task 8: Expose Audio Engine, Listen, and Speak Readiness

**Files:**
- Modify: `Sources/EMKECoordinator/TranslationCoordinatorState.swift:45-62`
- Modify: `Sources/EMKECoordinator/TranslationCoordinator.swift:152-263`
- Modify: `Sources/EMKEMenuBarApp/AppLocalization.swift:33-171,370-455`
- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift:93-289`
- Modify: `Sources/EMKEMenuBarApp/TranslationChannelRow.swift:40-128`
- Modify: `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationChannelPresentationTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardPresentationTests.swift`

**Interfaces:**
- Consumes: `audioEngineStarted`, inbound channel state, outbound channel state, and bypass state.
- Produces: computed `canListen`, `canSpeak`, and localized capability copy without a layout redesign.

- [ ] **Step 1: Write failing capability-state tests**

```swift
@Test
func runningCapabilitiesSeparateEngineListenAndSpeakReadiness() {
    let connecting = TranslationCoordinatorState(
        audioEngineStarted: true,
        inbound: .connecting,
        outbound: .connecting
    )
    #expect(connecting.audioEngineStarted)
    #expect(!connecting.canListen)
    #expect(!connecting.canSpeak)

    let inboundFailed = TranslationCoordinatorState(
        isRunning: true,
        audioEngineStarted: true,
        inbound: .failed(message: "offline"),
        outbound: .active
    )
    #expect(inboundFailed.canListen)
    #expect(inboundFailed.canSpeak)
}
```

Add tests for inbound reconnect/fail-open, outbound failure, manual bypass, same-language
outbound bypass, stopped state, and English copy.

- [ ] **Step 2: Run presentation tests and verify RED**

Run:

```bash
swift test --filter 'runningCapabilitiesSeparateEngineListenAndSpeakReadiness|TranslationChannelPresentationTests'
```

Expected: FAIL because capability properties and copy do not exist.

- [ ] **Step 3: Add state properties and computed semantics**

```swift
public struct TranslationCoordinatorState: Equatable, Sendable {
    public var isRunning: Bool
    public var audioEngineStarted: Bool
    public var inbound: TranslationChannelState
    public var outbound: TranslationChannelState
    public var subtitles: SubtitleSnapshot
    public var latency: TranslationLatencyDiagnostics

    public var canListen: Bool {
        guard audioEngineStarted else { return false }
        return switch inbound {
        case .active, .bypassed, .reconnecting, .failed: true
        case .stopped, .connecting: false
        }
    }

    public var canSpeak: Bool {
        guard audioEngineStarted else { return false }
        return switch outbound {
        case .active, .bypassed: true
        case .stopped, .connecting, .reconnecting, .failed: false
        }
    }
}
```

Extend the existing initializer with
`audioEngineStarted: Bool = false` and
`latency: TranslationLatencyDiagnostics = .empty` so existing callers keep compiling.
Set `audioEngineStarted = true` immediately after `audioEngine.start` succeeds and false
before publishing `.stopped`.

- [ ] **Step 4: Add exact bilingual copy and reuse current layout**

Add keys:

```swift
case audioEngineStarted
case canListen
case canSpeak
```

Map them to:

```swift
case .audioEngineStarted:
    localized(zhHans: "音频引擎已启动", english: "Audio engine ready")
case .canListen:
    localized(zhHans: "可以收听", english: "Can listen")
case .canSpeak:
    localized(zhHans: "可以发言", english: "Can speak")
```

Use `canListen` for healthy inbound channel status and `canSpeak` for healthy outbound
channel status. During startup, the primary status may show `audioEngineStarted` only
after the local engine actually starts.

- [ ] **Step 5: Run copy, geometry, render, and accessibility tests**

Run:

```bash
swift test --filter 'AppLocalizationTests|TranslationChannelPresentationTests|TranslationDashboardPresentationTests|TranslationDashboardRenderTests|TranslationDashboardAccessibilityTests'
```

Expected: PASS in Chinese and English; existing compact/expanded layout policies remain valid.

- [ ] **Step 6: Commit**

```bash
git add Sources/EMKECoordinator/TranslationCoordinatorState.swift Sources/EMKECoordinator/TranslationCoordinator.swift Sources/EMKEMenuBarApp/AppLocalization.swift Sources/EMKEMenuBarApp/MenuBarModel.swift Sources/EMKEMenuBarApp/TranslationChannelRow.swift Tests/EMKEAudioEngineTests/AppLocalizationTests.swift Tests/EMKEAudioEngineTests/TranslationChannelPresentationTests.swift Tests/EMKEAudioEngineTests/TranslationDashboardPresentationTests.swift
git commit -m "feat: expose runtime listen and speak readiness"
```

---

### Task 9: Live Provider Probe, Contract Documentation, and Full Verification

**Files:**
- Create: `Tests/EMKECoordinatorTests/LiveTranslationProbeTests.swift`
- Modify: `Sources/EMKECoordinator/TranslationConnectionProbe.swift`
- Modify: `Tests/EMKECoordinatorTests/TranslationConnectionProbeTests.swift`
- Modify: `docs/translation-coordinator-contract.md`
- Verify: all source and test files changed in Tasks 1-8

**Interfaces:**
- Consumes: explicit environment variables and a raw 24 kHz mono PCM16 sample.
- Produces: an opt-in live compatibility test and updated current/target contract.

- [ ] **Step 1: Add an opt-in live probe test**

```swift
private let liveTranslationProbeEnabled =
    ProcessInfo.processInfo.environment[
        "EMKE_RUN_LIVE_TRANSLATION_TESTS"
    ] == "1"

@Test(
    .enabled(
        if: liveTranslationProbeEnabled,
        "Set EMKE_RUN_LIVE_TRANSLATION_TESTS=1 and provider inputs"
    )
)
func liveFortyMillisecondTranslationProbe() async throws {
    let environment = ProcessInfo.processInfo.environment
    let apiKey = try #require(environment["EMKE_API_KEY"])
    let baseURL = try #require(URL(string: try #require(environment["EMKE_BASE_URL"])))
    let modelID = try #require(environment["EMKE_MODEL_ID"])
    let sampleURL = URL(fileURLWithPath: try #require(environment["EMKE_SPEECH_SAMPLE"]))
    let speech = try Data(contentsOf: sampleURL)
    #expect(speech.count.isMultiple(of: 1_920))

    let report = await TranslationConnectionProbe().run(
        configuration: TranslationConnectionProbeConfiguration(
            apiConfiguration: APIConfiguration(
                baseURL: baseURL,
                modelID: modelID
            ),
            apiKey: apiKey,
            inboundTargetLanguage: .chinese,
            outboundTargetLanguage: .german,
            speechChunkByteCount: 1_920
        ),
        speechSample: speech
    )
    #expect(report.sourceTranscript == .passed)
    #expect(report.audioOutput == .passed)
}
```

The test must not print the environment or add the sample to git.

- [ ] **Step 2: Make the existing probe stream configured chunks**

Add `speechChunkByteCount: Int? = nil` to
`TranslationConnectionProbeConfiguration`. When present, validate that it is a positive
even number and append `speechSample` to the inbound session in consecutive chunks of that
size. Preserve the current single-append behavior when it is `nil`.

Add this deterministic test:

```swift
@Test
func probeCanSendFortyMillisecondSpeechChunks() async {
    let inbound = ProbeSessionFake(closingEvents: [
        .inputTranscript(TranslationTranscriptDelta(
            text: "hello",
            elapsedMilliseconds: nil
        )),
        .outputAudio(probeAudioDelta(Data([1, 2]))),
    ])
    let probe = TranslationConnectionProbe(
        sessionBuilder: ProbeBuilderFake(
            sessions: [inbound, ProbeSessionFake()]
        )
    )
    let configuration = TranslationConnectionProbeConfiguration(
        apiConfiguration: .default,
        apiKey: "test-key",
        inboundTargetLanguage: .chinese,
        outboundTargetLanguage: .german,
        speechChunkByteCount: 1_920
    )

    _ = await probe.run(
        configuration: configuration,
        speechSample: Data(repeating: 1, count: 3_840)
    )
    #expect(await inbound.appended.map(\.count) == [1_920, 1_920])
}
```

- [ ] **Step 3: Verify deterministic and default-live behavior**

Run:

```bash
swift test --filter TranslationConnectionProbeTests
swift test --filter LiveTranslationProbeTests
```

Expected: deterministic probe tests PASS; live suite PASS with one explicit skip when the
environment switch is absent.

- [ ] **Step 4: Update the runtime contract**

Document:

- 12% preview and its intentional low-volume foreign-original exposure;
- 80 ms mother-language recovery and foreign crossfade;
- adaptive VAD defaults;
- 40 ms probe versus 200 ms release fallback;
- independent channel epochs;
- anonymous latency fields;
- audio-engine/listen/speak readiness;
- real Provider and meeting acceptance remaining unverified until executed.

- [ ] **Step 5: Run static safety scans**

Run:

```bash
git diff --check
rg -n 'sk-[A-Za-z0-9]|Authorization: Bearer [^<]|EMKE_API_KEY=' Sources Tests docs
rg -n 'TB[D]|TO[D]|FIXM[E]|implement[ ]later|fill[ ]in[ ]details' Sources Tests docs/translation-coordinator-contract.md
```

Expected: `git diff --check` is silent; credential and placeholder scans return no
committed secret or incomplete requirement.

- [ ] **Step 6: Run the full deterministic suite**

Run:

```bash
swift test
```

Expected: all deterministic tests PASS; driver/live tests remain explicitly skipped unless
their opt-in environment is configured.

- [ ] **Step 7: Inspect the final branch**

Run:

```bash
git status --short --branch
git log --oneline --decorate origin/main..HEAD
```

Expected: only intended changes exist; every task has one focused commit after the PRD and
plan commits.

- [ ] **Step 8: Commit documentation and live probe**

```bash
git add Sources/EMKECoordinator/TranslationConnectionProbe.swift Tests/EMKECoordinatorTests/TranslationConnectionProbeTests.swift Tests/EMKECoordinatorTests/LiveTranslationProbeTests.swift docs/translation-coordinator-contract.md
git commit -m "test: add realtime audio acceptance boundaries"
```

- [ ] **Step 9: Record proof boundaries in the handoff**

Report exact test counts and skipped tests from the fresh `swift test` output. Mark 40 ms
Provider compatibility, virtual-driver playback quality, real meeting behavior, packaging,
signing and update acceptance as unverified unless those checks were actually run.
