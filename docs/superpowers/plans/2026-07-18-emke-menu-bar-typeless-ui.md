# EMKE Typeless-Inspired Menu Bar UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将当前单页原生设置面板改造成已确认的 Typeless 风格双通道翻译工作台，并用本地 24 kHz mono PCM16 的真实电平驱动确定性实时音波。

**Architecture:** 保持现有 `TranslationCoordinator`、双 Realtime 会话和本地 CoreAudio 路由不变，在 coordinator actor 内从已有 PCM 块计算并限频发布可丢弃的音频电平快照。`MenuBarModel` 继续负责编排状态和动作，SwiftUI 层拆成控制台、设置、音波、通道行与视觉令牌；视图只消费不可变展示数据和动作闭包，不接触 Keychain、网络或音频引擎。

**Tech Stack:** Swift 6.2、SwiftUI、AppKit、Combine、Swift Testing、Swift Package Manager、macOS 14+、CoreAudio、现有 EMKE 模块。

## Global Constraints

- 已确认设计规格是唯一产品合同：`docs/superpowers/specs/2026-07-18-emke-menu-bar-typeless-ui-design.md`。
- 不改变入站 fail-open、出站 fail-closed、双会话隔离、Keychain 储存和优雅关闭行为。
- 不新增录音、音频历史、电平历史、字幕历史、遥测、账号、订阅或房间能力。
- 音频电平完全在本机计算，不写盘、不记录、不进入 UserDefaults，也不出现在测试失败输出中。
- UI 快照发布频率不超过 30 Hz；消费者落后时同类快照只保留最新值，不得反压实时音频线程。
- 音波不使用随机数。相同 PCM 输入与状态必须生成相同电平和柱形布局。
- 主控制台在 420 × 620 pt 内完整显示，不使用主页面滚动；设置页可以在同尺寸内滚动。
- 所有代码修改遵循 RED → GREEN → REFACTOR；每个任务完成后运行对应定向测试并单独提交。
- 禁止把真实 API Key、Authorization 头或已保存 Keychain 内容写入源码、测试、截图或提交信息。

---

## Task 1: 添加确定性的本地 PCM 电平计算

**Files:**

- Create: `Sources/EMKECoordinator/PCMLevelMeter.swift`
- Modify: `Sources/EMKECoordinator/TranslationCoordinatorState.swift`
- Create: `Tests/EMKECoordinatorTests/PCMLevelMeterTests.swift`

- [ ] **Step 1: 先写静音、固定振幅、起音、释放和非法数据测试**

在 `Tests/EMKECoordinatorTests/PCMLevelMeterTests.swift` 添加：

```swift
import Foundation
import Testing
@testable import EMKECoordinator

func pcm16(
    amplitude: Int16,
    sampleCount: Int = 240
) -> Data {
    var data = Data(capacity: sampleCount * MemoryLayout<Int16>.size)
    for index in 0..<sampleCount {
        var sample = (index.isMultiple(of: 2) ? amplitude : -amplitude)
            .littleEndian
        withUnsafeBytes(of: &sample) { data.append(contentsOf: $0) }
    }
    return data
}

@Test
func silenceRemainsAtZero() throws {
    var meter = PCMLevelMeter()
    #expect(try meter.observe(pcm16(amplitude: 0)) == 0)
}

@Test
func fixedPCMProducesNormalizedDeterministicLevel() throws {
    var first = PCMLevelMeter()
    var second = PCMLevelMeter()
    let sample = pcm16(amplitude: 12_000)

    let firstLevel = try first.observe(sample)
    let secondLevel = try second.observe(sample)

    #expect(firstLevel > 0)
    #expect(firstLevel <= 1)
    #expect(abs(firstLevel - secondLevel) < 0.000_001)
}

@Test
func attackIsFasterThanRelease() throws {
    var meter = PCMLevelMeter()
    let loud = pcm16(amplitude: 18_000)
    let silence = pcm16(amplitude: 0)

    let attacked = try meter.observe(loud)
    let released = try meter.observe(silence)

    #expect(attacked > 0)
    #expect(released > 0)
    #expect(released < attacked)
}

@Test
func resetClearsSmoothedLevel() throws {
    var meter = PCMLevelMeter()
    _ = try meter.observe(pcm16(amplitude: 18_000))
    meter.reset()
    #expect(meter.level == 0)
}

@Test
func oddByteCountIsRejected() {
    var meter = PCMLevelMeter()
    #expect(throws: PCMLevelMeterError.oddByteCount) {
        try meter.observe(Data([0x01]))
    }
}

@Test
func combinedSnapshotUsesTheLouderChannel() {
    let snapshot = AudioLevelSnapshot(inbound: 0.3, outbound: 0.7)
    #expect(snapshot.combined == 0.7)
}
```

- [ ] **Step 2: 运行定向测试，确认 RED**

Run:

```bash
swift test --filter PCMLevelMeterTests
```

Expected: FAIL，编译器报告 `PCMLevelMeter`、`PCMLevelMeterError` 和 `AudioLevelSnapshot` 尚不存在。

- [ ] **Step 3: 实现 RMS、归一化与 80/220 ms 平滑器**

在 `Sources/EMKECoordinator/PCMLevelMeter.swift` 实现完整的无状态 I/O 算法：

```swift
import Foundation

public enum PCMLevelMeterError: Error, Equatable, Sendable {
    case oddByteCount
    case invalidSampleRate
}

public struct PCMLevelMeter: Sendable {
    public private(set) var level = 0.0

    private let noiseFloor: Double
    private let ceiling: Double
    private let attackSeconds: Double
    private let releaseSeconds: Double

    public init(
        noiseFloor: Double = 0.01,
        ceiling: Double = 0.35,
        attackSeconds: Double = 0.08,
        releaseSeconds: Double = 0.22
    ) {
        self.noiseFloor = noiseFloor
        self.ceiling = ceiling
        self.attackSeconds = attackSeconds
        self.releaseSeconds = releaseSeconds
    }

    @discardableResult
    public mutating func observe(
        _ pcm16: Data,
        sampleRate: Double = 24_000
    ) throws -> Double {
        guard pcm16.count.isMultiple(of: 2) else {
            throw PCMLevelMeterError.oddByteCount
        }
        guard sampleRate > 0 else {
            throw PCMLevelMeterError.invalidSampleRate
        }

        let sampleCount = pcm16.count / MemoryLayout<Int16>.size
        guard sampleCount > 0 else { return level }

        let sumOfSquares = pcm16.withUnsafeBytes { bytes in
            (0..<sampleCount).reduce(into: 0.0) { sum, index in
                let sample = Int16(littleEndian: bytes.loadUnaligned(
                    fromByteOffset: index * MemoryLayout<Int16>.size,
                    as: Int16.self
                ))
                let normalized = Double(sample) / Double(Int16.max)
                sum += normalized * normalized
            }
        }
        let rms = sqrt(sumOfSquares / Double(sampleCount))
        let range = max(ceiling - noiseFloor, .leastNonzeroMagnitude)
        let target = min(max((rms - noiseFloor) / range, 0), 1)
        let duration = Double(sampleCount) / sampleRate
        let timeConstant = target > level ? attackSeconds : releaseSeconds
        let alpha = 1 - exp(-duration / timeConstant)
        level += (target - level) * alpha
        level = min(max(level, 0), 1)
        return level
    }

    public mutating func reset() {
        level = 0
    }
}
```

在 `Sources/EMKECoordinator/TranslationCoordinatorState.swift` 的事件定义前添加快照：

```swift
public struct AudioLevelSnapshot: Equatable, Sendable {
    public var inbound: Double
    public var outbound: Double

    public init(inbound: Double = 0, outbound: Double = 0) {
        self.inbound = min(max(inbound, 0), 1)
        self.outbound = min(max(outbound, 0), 1)
    }

    public var combined: Double {
        max(inbound, outbound)
    }
}
```

- [ ] **Step 4: 运行定向测试，确认 GREEN**

Run:

```bash
swift test --filter PCMLevelMeterTests
```

Expected: PASS。

- [ ] **Step 5: 提交算法基础**

```bash
git add Sources/EMKECoordinator/PCMLevelMeter.swift Sources/EMKECoordinator/TranslationCoordinatorState.swift Tests/EMKECoordinatorTests/PCMLevelMeterTests.swift
git commit -m "feat: add deterministic local audio level meter"
```

## Task 2: 从 TranslationCoordinator 发布限频且可合并的电平快照

**Files:**

- Modify: `Sources/EMKECoordinator/TranslationCoordinatorState.swift`
- Modify: `Sources/EMKECoordinator/TranslationCoordinator.swift`
- Modify: `Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift`

- [ ] **Step 1: 扩展测试时钟和 coordinator 事件测试**

在 `Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift` 增加线程安全单调时钟：

```swift
private final class CoordinatorLevelClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value: UInt64 = 1

    func now() -> UInt64 {
        lock.withLock { value }
    }

    func advance(milliseconds: UInt64) {
        lock.withLock { value += milliseconds * 1_000_000 }
    }
}
```

给 `CoordinatorHarness` 增加 `levelClock`，并把 `levelTimeNanoseconds: levelClock.now` 注入 coordinator。随后添加测试，使用现有 `audio.emit` 和匹配事件 helper 验证：

```swift
@Test
func coordinatorPublishesSeparateInboundAndOutboundLevels() async throws {
    let harness = CoordinatorHarness()
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(
        pcm16(amplitude: 14_000)
    ))
    let inbound = await harness.nextAudioLevelEvent()
    #expect(inbound.inbound > 0)
    #expect(inbound.outbound == 0)

    harness.levelClock.advance(milliseconds: 34)
    await harness.audio.emit(.outboundNetworkAudio(
        pcm16(amplitude: 18_000)
    ))
    let outbound = await harness.nextAudioLevelEvent()
    #expect(outbound.inbound > 0)
    #expect(outbound.outbound > 0)
}

@Test
func audioLevelEventsAreThrottledAndQueuedSnapshotsAreCoalesced() async throws {
    let harness = CoordinatorHarness()
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(pcm16(amplitude: 8_000)))
    harness.levelClock.advance(milliseconds: 34)
    await harness.audio.emit(.inboundNetworkAudio(pcm16(amplitude: 12_000)))
    harness.levelClock.advance(milliseconds: 34)
    await harness.audio.emit(.inboundNetworkAudio(pcm16(amplitude: 18_000)))

    let latest = await harness.nextAudioLevelEvent()
    #expect(latest.inbound > 0.2)
}
```

`pcm16` 使用 Task 1 测试文件中同一测试 target 内的 internal helper。`nextAudioLevelEvent()` 必须跳过启动阶段的 `.stateChanged`，并在 `.audioLevels` 时返回。上面的阈值验证队列返回的是第三个较大振幅产生的最新快照，而不是第一个旧快照；测试不得用真实 sleep 证明限频。

- [ ] **Step 2: 运行定向测试，确认 RED**

Run:

```bash
swift test --filter coordinatorPublishesSeparateInboundAndOutboundLevels
swift test --filter audioLevelEventsAreThrottledAndQueuedSnapshotsAreCoalesced
```

Expected: FAIL，`.audioLevels` 事件和 coordinator 的时钟注入尚不存在。

- [ ] **Step 3: 添加事件类型和 coordinator 电平状态**

在 `TranslationCoordinatorState.swift` 扩展事件：

```swift
public enum TranslationCoordinatorEvent: Equatable, Sendable {
    case stateChanged(TranslationCoordinatorState)
    case audioLevels(AudioLevelSnapshot)
    case audioBackpressure(droppedFrames: Int)
    case stopped
}
```

在 `TranslationCoordinator` 添加以下成员和初始化参数：

```swift
private static let minimumLevelPublishInterval: UInt64 = 33_333_334

private let levelTimeNanoseconds: @Sendable () -> UInt64
private var inboundLevelMeter = PCMLevelMeter()
private var outboundLevelMeter = PCMLevelMeter()
private var audioLevels = AudioLevelSnapshot()
private var lastLevelPublishTime: UInt64?

public init(
    audioEngine: any TranslationAudioEngine = LocalAudioEngine(),
    sessionBuilder: any TranslationSessionBuilding =
        URLSessionTranslationSessionBuilder(),
    languageClassifier: @escaping @Sendable (String) ->
        LanguageHypotheses = { text in
            NaturalLanguageClassifier().hypotheses(for: text)
        },
    reconnectDelays: [Duration] = [
        .milliseconds(250),
        .milliseconds(500),
        .seconds(1),
        .seconds(2),
        .seconds(5),
    ],
    levelTimeNanoseconds: @escaping @Sendable () -> UInt64 = {
        DispatchTime.now().uptimeNanoseconds
    }
) {
    self.audioEngine = audioEngine
    self.sessionBuilder = sessionBuilder
    self.languageClassifier = languageClassifier
    self.reconnectDelays = reconnectDelays
    self.levelTimeNanoseconds = levelTimeNanoseconds
}
```

- [ ] **Step 4: 在现有音频事件路径计算、限频并发布**

在 `handleAudioEvent` 调用既有翻译逻辑前观察对应数据；电平计算失败只丢弃该次 UI 快照，不得触发翻译通道失败：

```swift
case .inboundNetworkAudio(let pcm16):
    observeAudioLevel(pcm16, channel: .inbound)
    await handleInboundAudio(pcm16)
case .outboundNetworkAudio(let pcm16):
    observeAudioLevel(pcm16, channel: .outbound)
    await handleOutboundAudio(pcm16)
```

添加 helpers：

```swift
private func observeAudioLevel(_ pcm16: Data, channel: Channel) {
    do {
        switch channel {
        case .inbound:
            audioLevels.inbound = try inboundLevelMeter.observe(pcm16)
        case .outbound:
            audioLevels.outbound = try outboundLevelMeter.observe(pcm16)
        }
        publishAudioLevelsIfDue(at: levelTimeNanoseconds())
    } catch {
        return
    }
}

private func publishAudioLevelsIfDue(at now: UInt64) {
    if let lastLevelPublishTime,
       now - lastLevelPublishTime < Self.minimumLevelPublishInterval {
        return
    }
    lastLevelPublishTime = now
    publish(.audioLevels(audioLevels))
}
```

在 `resetRuntimeBuffers` 同步清理 meter、快照和节流时间：

```swift
inboundLevelMeter.reset()
outboundLevelMeter.reset()
audioLevels = AudioLevelSnapshot()
lastLevelPublishTime = nil
```

- [ ] **Step 5: 合并排队中的电平事件，保护控制事件**

在 `publish(_:)` 的 waiter 分支之后、普通容量逻辑之前加入：

```swift
if case .audioLevels = event,
   let index = events.lastIndex(where: { queued in
       if case .audioLevels = queued { return true }
       return false
   }) {
    events.remove(at: index)
    events.append(event)
    return
}
```

这保证 UI 未消费时只有最新电平快照留在队列，并让离散状态事件先于之后生成的最新音频快照到达；`.stateChanged`、`.audioBackpressure` 和 `.stopped` 仍按原规则保留。

- [ ] **Step 6: 运行 coordinator 全套测试，确认 GREEN**

Run:

```bash
swift test --filter EMKECoordinatorTests
```

Expected: PASS；现有翻译、旁路、重连和关闭测试不回归。

- [ ] **Step 7: 提交 coordinator 电平事件**

```bash
git add Sources/EMKECoordinator/TranslationCoordinatorState.swift Sources/EMKECoordinator/TranslationCoordinator.swift Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift
git commit -m "feat: publish coalesced translation audio levels"
```

## Task 3: 扩展 MenuBarModel 的页面、运行时长和展示状态

**Files:**

- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- Modify: `Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift`

- [ ] **Step 1: 先写页面切换、运行时长、电平与停止复位测试**

给测试 stub 增加可主动发送事件的方法：

```swift
private var queuedEvents: [TranslationCoordinatorEvent] = []

func nextEvent() async -> TranslationCoordinatorEvent {
    if !queuedEvents.isEmpty {
        return queuedEvents.removeFirst()
    }
    return await withCheckedContinuation { continuation in
        eventWaiters.append(continuation)
    }
}

func emit(_ event: TranslationCoordinatorEvent) {
    if eventWaiters.isEmpty {
        queuedEvents.append(event)
    } else {
        eventWaiters.removeFirst().resume(returning: event)
    }
}
```

同时在测试文件增加可复用启动 helper，避免每个测试遗漏必要配置：

```swift
@MainActor
private func configureAndStart(_ model: MenuBarModel) async {
    await model.loadConfiguration()
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"
    model.baseURLString = "https://gateway.example/v1"
    model.modelID = "translation-model"
    await model.start()
}
```

添加以下模型合同测试：

```swift
@Test @MainActor
func settingsNavigationDoesNotRecreateCoordinator() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(
        secret: "test-key",
        coordinator: coordinator
    )

    model.showSettings()
    #expect(model.screen == .settings)
    model.showDashboard()
    #expect(model.screen == .dashboard)
    #expect(await coordinator.configurations.isEmpty)
}

@Test @MainActor
func elapsedFormatterUsesMinuteSecondContract() {
    #expect(MenuBarModel.formatElapsed(seconds: 0) == "00:00")
    #expect(MenuBarModel.formatElapsed(seconds: 65) == "01:05")
    #expect(MenuBarModel.formatElapsed(seconds: 3_725) == "62:05")
}

@Test @MainActor
func modelConsumesLatestAudioLevelSnapshot() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(
        secret: "test-key",
        coordinator: coordinator
    )
    await configureAndStart(model)

    await coordinator.emit(.audioLevels(
        AudioLevelSnapshot(inbound: 0.25, outbound: 0.75)
    ))
    await Task.yield()

    #expect(model.inboundLevel == 0.25)
    #expect(model.outboundLevel == 0.75)
    #expect(model.combinedLevel == 0.75)
}

@Test @MainActor
func stoppingClearsLevelsAndRunStartDate() async {
    let model = makeTranslationMenuModel(secret: "test-key")
    await configureAndStart(model)
    await model.stop()

    #expect(model.inboundLevel == 0)
    #expect(model.outboundLevel == 0)
    #expect(model.translationStartedAt == nil)
}
```

- [ ] **Step 2: 运行定向测试，确认 RED**

Run:

```bash
swift test --filter settingsNavigationDoesNotRecreateCoordinator
swift test --filter modelConsumesLatestAudioLevelSnapshot
swift test --filter stoppingClearsLevelsAndRunStartDate
```

Expected: FAIL，页面和电平展示状态尚不存在，event switch 也未覆盖 `.audioLevels`。

- [ ] **Step 3: 添加页面与运行展示状态**

在 `MenuBarModel.swift` 添加：

```swift
enum MenuBarScreen: Equatable {
    case dashboard
    case settings
}

@Published private(set) var screen: MenuBarScreen = .dashboard
@Published private(set) var inboundLevel = 0.0
@Published private(set) var outboundLevel = 0.0
@Published private(set) var translationStartedAt: Date?
@Published private(set) var isStopping = false

var combinedLevel: Double {
    max(inboundLevel, outboundLevel)
}

func showSettings() {
    screen = .settings
}

func showDashboard() {
    screen = .dashboard
}

static func formatElapsed(seconds: TimeInterval) -> String {
    let wholeSeconds = max(Int(seconds.rounded(.down)), 0)
    return String(
        format: "%02d:%02d",
        wholeSeconds / 60,
        wholeSeconds % 60
    )
}

func elapsedText(at now: Date) -> String {
    guard let translationStartedAt else { return "00:00" }
    return Self.formatElapsed(
        seconds: now.timeIntervalSince(translationStartedAt)
    )
}

var apiKeyStatusText: String {
    hasStoredAPIKey ? "已存入 Keychain" : "尚未保存"
}
```

启动成功且 `coordinatorState.isRunning` 时设置 `translationStartedAt = Date()`；`stop()` 进入时设置 `isStopping = true`，完成或失败时用 `defer` 恢复为 false。所有失败路径、`stop()` 完成和 `.stopped` 事件统一调用：

```swift
private func resetRuntimePresentation() {
    inboundLevel = 0
    outboundLevel = 0
    translationStartedAt = nil
    inboundBypassEnabled = false
    outboundBypassEnabled = false
    isStopping = false
}
```

- [ ] **Step 4: 消费电平事件且不生成高频 VoiceOver 状态**

在 `startObservingCoordinator()` 的 switch 中添加：

```swift
case .audioLevels(let levels):
    inboundLevel = levels.inbound
    outboundLevel = levels.outbound
```

电平只更新绘图值，不写入 `statusText`、错误文本或辅助功能公告。

- [ ] **Step 5: 增加结构化主状态和通道展示映射测试**

先测试 `dashboardStatusText(at:)` 与两个通道的 fail-open/fail-closed 映射：

```swift
@Test @MainActor
func outboundFailureIsPresentedAsMuted() {
    #expect(MenuBarModel.text(for: .failed(message: "offline"), channel: .outbound) == "已静音")
}

@Test @MainActor
func inboundFailureIsPresentedAsOriginalAudio() {
    #expect(MenuBarModel.text(for: .failed(message: "offline"), channel: .inbound) == "播放原音")
}
```

实现 `MenuBarChannel` 枚举和 channel-aware 映射；不要再让出站失败统一显示“连接失败”。同时提供：

```swift
func dashboardStatusText(at now: Date) -> String {
    if isStarting { return "正在连接" }
    if coordinatorState.isRunning {
        return "翻译中 · \(elapsedText(at: now))"
    }
    return readiness == .ready ? "准备开始" : statusText
}
```

- [ ] **Step 6: 运行模型测试，确认 GREEN**

Run:

```bash
swift test --filter MenuBarTranslationModelTests
```

Expected: PASS。

- [ ] **Step 7: 提交模型展示状态**

```bash
git add Sources/EMKEMenuBarApp/MenuBarModel.swift Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift
git commit -m "feat: add menu bar presentation state"
```

## Task 4: 实现视觉令牌、确定性音波和双通道行

**Files:**

- Create: `Sources/EMKEMenuBarApp/EMKEVisualStyle.swift`
- Create: `Sources/EMKEMenuBarApp/LiveWaveformView.swift`
- Create: `Sources/EMKEMenuBarApp/TranslationChannelRow.swift`
- Create: `Tests/EMKEAudioEngineTests/WaveformBarLayoutTests.swift`
- Create: `Tests/EMKEAudioEngineTests/TranslationChannelPresentationTests.swift`

- [ ] **Step 1: 先写 24 柱形确定性、边界和通道动作测试**

在 `WaveformBarLayoutTests.swift` 添加：

```swift
import Testing
@testable import EMKEMenuBarApp

@Test
func waveformProducesTwentyFourDeterministicBars() {
    let first = WaveformBarLayout.heights(level: 0.65)
    let second = WaveformBarLayout.heights(level: 0.65)
    #expect(first.count == 24)
    #expect(first == second)
}

@Test
func silenceUsesLowStaticBaseline() {
    let heights = WaveformBarLayout.heights(level: 0)
    #expect(heights.allSatisfy { $0 >= 4 && $0 <= 7 })
}

@Test
func waveformClampsOutOfRangeLevels() {
    #expect(WaveformBarLayout.heights(level: -1) ==
        WaveformBarLayout.heights(level: 0))
    #expect(WaveformBarLayout.heights(level: 2) ==
        WaveformBarLayout.heights(level: 1))
}
```

在 `TranslationChannelPresentationTests.swift` 测试：

```swift
@Test
func inboundActiveUsesOriginalAudioAction() {
    let value = TranslationChannelPresentation.make(
        channel: .inbound,
        state: .active,
        bypassEnabled: false
    )
    #expect(value.status == "稳定")
    #expect(value.actionTitle == "播放原音")
    #expect(value.actionAccessibilityLabel == "播放入站原音")
}

@Test
func outboundFailureUsesMutedStatusAndNoFalseStableState() {
    let value = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .failed(message: "offline"),
        bypassEnabled: false
    )
    #expect(value.status == "已静音")
    #expect(value.symbol == "mic.slash")
    #expect(value.isBlockingFailure)
}
```

- [ ] **Step 2: 运行定向测试，确认 RED**

Run:

```bash
swift test --filter WaveformBarLayoutTests
swift test --filter TranslationChannelPresentationTests
```

Expected: FAIL，视觉类型和展示映射尚不存在。

- [ ] **Step 3: 实现集中式视觉令牌**

在 `EMKEVisualStyle.swift` 定义语义值，不硬编码网页品牌：

```swift
import SwiftUI

enum EMKEVisualStyle {
    static let panelWidth: CGFloat = 420
    static let panelHeight: CGFloat = 620
    static let horizontalPadding: CGFloat = 24
    static let primarySpacing: CGFloat = 24
    static let groupSpacing: CGFloat = 16
    static let compactSpacing: CGFloat = 8
    static let primaryButtonHeight: CGFloat = 52
    static let dividerOpacity = 0.14

    static let primaryText = Color.primary
    static let secondaryText = Color.secondary
    static let activityBlue = Color(
        red: 0.25,
        green: 0.45,
        blue: 0.92
    )
}
```

颜色使用 `Color.primary`、`Color.secondary`、系统背景与系统警告色适配深色和 Increase Contrast；蓝色只用于音波活动端点。

- [ ] **Step 4: 实现纯柱形几何与 LiveWaveformView**

`WaveformBarLayout` 使用固定 24 项权重，不使用时间或随机数：

```swift
enum WaveformBarLayout {
    private static let weights: [Double] = [
        0.28, 0.42, 0.58, 0.36, 0.72, 0.50,
        0.84, 0.62, 0.94, 0.70, 1.00, 0.78,
        0.88, 0.66, 0.96, 0.74, 0.82, 0.56,
        0.76, 0.48, 0.64, 0.38, 0.52, 0.30,
    ]

    static func heights(
        level: Double,
        minimum: Double = 4,
        maximum: Double = 72
    ) -> [Double] {
        let clamped = min(max(level, 0), 1)
        return weights.map { weight in
            let baseline = minimum + (weight * 2)
            return min(baseline + clamped * weight * (maximum - baseline), maximum)
        }
    }
}
```

`LiveWaveformView` 只接收值，不拥有 timer：

```swift
struct LiveWaveformView: View {
    let level: Double
    let maximumHeight: CGFloat
    var compact = false

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(alignment: .center, spacing: compact ? 2 : 4) {
            ForEach(
                Array(WaveformBarLayout.heights(
                    level: level,
                    maximum: Double(maximumHeight)
                ).enumerated()),
                id: \.offset
            ) { index, height in
                Capsule()
                    .fill(index == 23 && level > 0.08
                        ? EMKEVisualStyle.activityBlue
                        : EMKEVisualStyle.primaryText.opacity(
                            compact ? 0.64 : 0.82
                        ))
                    .frame(
                        width: compact ? 3 : 6,
                        height: CGFloat(height)
                    )
            }
        }
        .frame(maxWidth: .infinity, minHeight: maximumHeight)
        .animation(
            reduceMotion ? nil : .easeOut(duration: 0.08),
            value: level
        )
        .accessibilityHidden(true)
    }
}
```

因为 coordinator 已限频至 30 Hz 且视图没有自己的 timer，窗口隐藏后 SwiftUI 不会持续触发逐帧 UI 更新。

- [ ] **Step 5: 实现 TranslationChannelPresentation 和行组件**

`TranslationChannelPresentation.make` 必须覆盖 `.stopped`、`.connecting`、`.active`、`.bypassed`、`.reconnecting`、`.failed`，并区分入站失败“播放原音”和出站失败“已静音”。`TranslationChannelRow` 的输入合同固定为：

```swift
struct TranslationChannelRow: View {
    let title: String
    let direction: String
    let level: Double
    let presentation: TranslationChannelPresentation
    let action: () -> Void

    var body: some View {
        HStack(spacing: EMKEVisualStyle.groupSpacing) {
            Image(systemName: presentation.symbol)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.system(size: 18, weight: .semibold))
                Text(direction).font(.system(size: 13))
                Label(presentation.status, systemImage: presentation.statusSymbol)
                    .font(.system(size: 12))
                    .foregroundStyle(presentation.statusColor)
            }
            Spacer(minLength: 8)
            LiveWaveformView(level: level, maximumHeight: 24, compact: true)
                .frame(width: 66)
            Button(presentation.actionTitle, action: action)
                .buttonStyle(.plain)
                .disabled(!presentation.actionEnabled)
                .accessibilityLabel(
                    presentation.actionAccessibilityLabel
                )
        }
        .padding(.vertical, 14)
    }
}
```

- [ ] **Step 6: 运行定向测试和产品构建，确认 GREEN**

Run:

```bash
swift test --filter WaveformBarLayoutTests
swift test --filter TranslationChannelPresentationTests
swift build --product EMKEMenuBarApp
```

Expected: PASS。

- [ ] **Step 7: 提交视觉基础组件**

```bash
git add Sources/EMKEMenuBarApp/EMKEVisualStyle.swift Sources/EMKEMenuBarApp/LiveWaveformView.swift Sources/EMKEMenuBarApp/TranslationChannelRow.swift Tests/EMKEAudioEngineTests/WaveformBarLayoutTests.swift Tests/EMKEAudioEngineTests/TranslationChannelPresentationTests.swift
git commit -m "feat: add deterministic waveform UI primitives"
```

## Task 5: 构建 420 × 620 pt 双通道翻译控制台

**Files:**

- Create: `Sources/EMKEMenuBarApp/TranslationDashboardView.swift`
- Create: `Tests/EMKEAudioEngineTests/TranslationDashboardPresentationTests.swift`
- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`

- [ ] **Step 1: 先写所有离散运行状态的展示快照测试**

定义不含闭包的 `TranslationDashboardPresentation: Equatable`，并把 `DashboardFixture` 声明为测试 target 内可复用的 internal 类型。测试未配置、就绪、连接中、运行中、入站失败、出站失败、双旁路与停止中：

```swift
@Test(arguments: [
    DashboardFixture.unconfigured,
    .ready,
    .connecting,
    .running,
    .inboundFailed,
    .outboundFailed,
    .inboundBypassed,
    .outboundBypassed,
    .stopping,
])
func dashboardPresentationIsDeterministic(
    fixture: DashboardFixture
) {
    let first = fixture.makePresentation()
    let second = fixture.makePresentation()
    #expect(first == second)
    #expect(!first.primaryStatus.isEmpty)
    #expect(!first.primaryActionTitle.isEmpty)
    #expect(!first.inbound.status.isEmpty)
    #expect(!first.outbound.status.isEmpty)
}

@Test
func runningDashboardUsesCombinedAudioLevel() {
    let value = DashboardFixture.running.makePresentation(
        inboundLevel: 0.35,
        outboundLevel: 0.72
    )
    #expect(value.primaryLevel == 0.72)
}
```

fixture 使用伪造的 `test-key`，不得包含真实服务凭据。

- [ ] **Step 2: 运行定向测试，确认 RED**

Run:

```bash
swift test --filter TranslationDashboardPresentationTests
```

Expected: FAIL，dashboard presentation 尚不存在。

- [ ] **Step 3: 从 MenuBarModel 生成不可变展示数据**

在 `MenuBarModel` 添加 `dashboardPresentation(at:)`，返回：

```swift
struct TranslationDashboardPresentation: Equatable {
    let primaryStatus: String
    let primaryLevel: Double
    let inboundLevel: Double
    let outboundLevel: Double
    let primaryActionTitle: String
    let primaryActionEnabled: Bool
    let inputLanguageName: String
    let outputLanguageName: String
    let inboundDirection: String
    let outboundDirection: String
    let inbound: TranslationChannelPresentation
    let outbound: TranslationChannelPresentation
    let privacyText: String
    let errorText: String?
}
```

规则：主音波取 `max(inboundLevel, outboundLevel)`；入站方向为“检测到的会议语言 → 母语”的产品表达使用 `其他语言 → {母语}`，出站方向为 `{母语} → {会议输出}`。入站失败仍可显示本地原音电平；出站失败时 `outboundLevel` 和出站对 `primaryLevel` 的贡献都强制为 0，不得用历史值或真实麦克风活动伪装成成功输出。

- [ ] **Step 4: 实现不滚动的 TranslationDashboardView**

`TranslationDashboardView` 负责从 model 取动态展示数据；实际六区布局下沉到可独立渲染的 `TranslationDashboardContent`，所有动作通过闭包回传。这样视觉验收不需要连接真实服务：

```swift
struct TranslationDashboardView: View {
    @ObservedObject var model: MenuBarModel
    @State private var now = Date()

    var body: some View {
        TranslationDashboardContent(
            value: model.dashboardPresentation(at: now),
            motherLanguage: $model.motherLanguage,
            meetingOutputLanguage: $model.meetingOutputLanguage,
            settingsAction: model.showSettings,
            inboundAction: {
                Task {
                    await model.setInboundBypass(
                        !model.inboundBypassEnabled
                    )
                }
            },
            outboundAction: {
                Task {
                    await model.setOutboundBypass(
                        !model.outboundBypassEnabled
                    )
                }
            },
            primaryAction: {
                Task {
                    if model.coordinatorState.isRunning {
                        await model.stop()
                    } else {
                        await model.start()
                    }
                }
            }
        )
        .task(id: model.coordinatorState.isRunning) {
            guard model.coordinatorState.isRunning else { return }
            while !Task.isCancelled {
                do {
                    try await Task.sleep(for: .seconds(1))
                    now = Date()
                } catch {
                    return
                }
            }
        }
    }
}

struct TranslationDashboardContent: View {
    let value: TranslationDashboardPresentation
    @Binding var motherLanguage: SupportedLanguage
    @Binding var meetingOutputLanguage: SupportedLanguage
    let settingsAction: () -> Void
    let inboundAction: () -> Void
    let outboundAction: () -> Void
    let primaryAction: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            header
            Spacer(minLength: 18)
            LiveWaveformView(
                level: value.primaryLevel,
                maximumHeight: 72
            )
            Text(value.primaryStatus)
                .font(.system(size: 14, weight: .medium))
                .padding(.top, 12)
            Spacer(minLength: 18)
            languageDirection
            Divider().opacity(EMKEVisualStyle.dividerOpacity)
            channelRows
            Spacer(minLength: 16)
            primaryActionButton
            privacyFooter
        }
        .padding(.horizontal, EMKEVisualStyle.horizontalPadding)
        .padding(.vertical, 20)
    }
}
```

实现要求：

- 顶栏左侧 `EMKE Translation`，右侧 `gearshape`，VoiceOver 标签“打开设置”。
- 语言方向就绪时可选；运行或启动时禁用但仍可读。
- 两个 `TranslationChannelRow` 之间只使用细分隔线，不创建独立卡片。
- 主按钮高 52 pt、整宽胶囊、全屏唯一深色主动作。
- 底部固定 `lock` + `音频直连你的服务商`。
- 主状态文字承担 VoiceOver 状态，不让音波更新触发公告。
- 运行时长 task 只在运行状态存在；视图消失或翻译停止时由 SwiftUI 自动取消。音波本身没有 timer，隐藏窗口后不产生逐帧 UI 更新。

- [ ] **Step 5: 运行展示测试和产品构建，确认 GREEN**

Run:

```bash
swift test --filter TranslationDashboardPresentationTests
swift test --filter MenuBarTranslationModelTests
swift build --product EMKEMenuBarApp
```

Expected: PASS。

- [ ] **Step 6: 提交控制台**

```bash
git add Sources/EMKEMenuBarApp/TranslationDashboardView.swift Sources/EMKEMenuBarApp/MenuBarModel.swift Tests/EMKEAudioEngineTests/TranslationDashboardPresentationTests.swift
git commit -m "feat: build dual-channel translation dashboard"
```

## Task 6: 拆出设置页和根页面切换

**Files:**

- Create: `Sources/EMKEMenuBarApp/TranslationSettingsView.swift`
- Create: `Sources/EMKEMenuBarApp/MenuBarRootView.swift`
- Modify: `Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift`
- Modify: `Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift`

- [ ] **Step 1: 先写运行中设置只读和返回不停止会话测试**

```swift
@Test @MainActor
func runningSettingsRemainViewableButLocked() async {
    let model = makeTranslationMenuModel(secret: "test-key")
    await configureAndStart(model)
    model.showSettings()

    #expect(model.screen == .settings)
    #expect(model.selectionsLocked)
    #expect(!model.canTestConnection)
}

@Test @MainActor
func returningFromSettingsPreservesRunningState() async {
    let model = makeTranslationMenuModel(secret: "test-key")
    await configureAndStart(model)
    model.showSettings()
    model.showDashboard()

    #expect(model.coordinatorState.isRunning)
}
```

- [ ] **Step 2: 运行定向测试，确认现有模型合同**

Run:

```bash
swift test --filter runningSettingsRemainViewableButLocked
swift test --filter returningFromSettingsPreservesRunningState
```

Expected: PASS；如果 RED，只修模型导航合同，不在视图里复制 session 状态。

- [ ] **Step 3: 实现单层分组设置页**

`TranslationSettingsView` 使用一个 `ScrollView` 和两个语义分组：

```swift
struct TranslationSettingsView: View {
    @ObservedObject var model: MenuBarModel

    var body: some View {
        VStack(spacing: 0) {
            settingsHeader
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    providerSection
                    Divider().opacity(EMKEVisualStyle.dividerOpacity)
                    audioSection
                    if let error = model.configurationError {
                        Label(error, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.red)
                    }
                    Button("退出 EMKE") {
                        NSApplication.shared.terminate(nil)
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                }
                .padding(EMKEVisualStyle.horizontalPadding)
            }
        }
    }
}
```

设置合同：

- API Key 已存时显示“已存入 Keychain”，不回填明文。
- Base URL、Model ID 和物理设备在 `selectionsLocked` 时禁用但可查看；语言选择只保留在主控制台，不在设置页重复出现。
- 测试结果显示现有结构化兼容性摘要；仅握手通过时文案保持“需要音频测试”。
- 驱动缺失与设备 inventory 错误留在音频分组，不推到主控制台。
- 左上返回按钮调用 `model.showDashboard()`，不调用 `stop()` 或重建 model。

- [ ] **Step 4: 实现根页面与简化 App 入口**

`MenuBarRootView` 只切换页面：

```swift
struct MenuBarRootView: View {
    @ObservedObject var model: MenuBarModel

    var body: some View {
        Group {
            switch model.screen {
            case .dashboard:
                TranslationDashboardView(model: model)
            case .settings:
                TranslationSettingsView(model: model)
            }
        }
        .frame(
            width: EMKEVisualStyle.panelWidth,
            height: EMKEVisualStyle.panelHeight
        )
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear {
            Task { await model.loadConfiguration() }
            model.reloadDevices()
        }
    }
}
```

将 `EMKEMenuBarApp.swift` 原 420 × 620 单文件 `ScrollView` 替换为：

```swift
MenuBarExtra(
    "EMKE Translation",
    systemImage: model.systemImage
) {
    MenuBarRootView(model: model)
}
.menuBarExtraStyle(.window)
```

- [ ] **Step 5: 运行模型测试和 Debug/Release 构建，确认 GREEN**

Run:

```bash
swift test --filter MenuBarTranslationModelTests
swift build --product EMKEMenuBarApp
swift build -c release --product EMKEMenuBarApp
```

Expected: PASS。

- [ ] **Step 6: 提交两页架构**

```bash
git add Sources/EMKEMenuBarApp/TranslationSettingsView.swift Sources/EMKEMenuBarApp/MenuBarRootView.swift Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift
git commit -m "feat: split menu bar dashboard and settings"
```

## Task 7: 可访问性、真实渲染视觉验收与全量回归

**Files:**

- Modify: `Sources/EMKEMenuBarApp/TranslationDashboardView.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationSettingsView.swift`
- Modify: `Sources/EMKEMenuBarApp/LiveWaveformView.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationChannelRow.swift`
- Create: `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-07-18-emke-menu-bar-typeless-ui-design.md`

- [ ] **Step 1: 做源码级可访问性检查**

逐项确认并补齐：

- 齿轮、返回、耳机、麦克风、锁图标都有文字标签或 `.accessibilityHidden(true)`。
- 音波本身 `.accessibilityHidden(true)`，主状态文字拥有 `.accessibilityLabel`。
- 旁路按钮读作“播放入站原音”“恢复入站翻译”“发送出站原音”“恢复出站翻译”。
- Reduce Motion 下 `LiveWaveformView` 不创建显式 animation。
- Increase Contrast 下不依赖低透明蓝色表达状态；状态始终同时有文字和 SF Symbol。
- 键盘焦点顺序保持设置 → 语言 → 入站动作 → 出站动作 → 主动作。

- [ ] **Step 2: 启动实际菜单栏产品并采集 420 × 620 截图**

Run:

```bash
swift run EMKEMenuBarApp
```

通过 macOS 菜单栏打开窗口，至少采集：

1. 当前机器的未配置或就绪控制台；
2. 设置页；
3. 使用仅含伪凭据与 fixture 状态的测试渲染夹具采集运行态，不连接生产服务。

在 `TranslationDashboardRenderTests.swift` 添加 opt-in `ImageRenderer` 夹具。它只在明确设置环境变量时把运行态写入 `/tmp`：

```swift
import AppKit
import Foundation
import SwiftUI
import Testing
@testable import EMKEMenuBarApp

@Test @MainActor
func captureRunningDashboardForVisualReview() throws {
    guard ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1" else {
        return
    }

    let value = DashboardFixture.running.makePresentation(
        inboundLevel: 0.42,
        outboundLevel: 0.68
    )
    let view = TranslationDashboardContent(
        value: value,
        motherLanguage: .constant(.chinese),
        meetingOutputLanguage: .constant(.german),
        settingsAction: {},
        inboundAction: {},
        outboundAction: {},
        primaryAction: {}
    )
    .frame(
        width: EMKEVisualStyle.panelWidth,
        height: EMKEVisualStyle.panelHeight
    )
    .background(Color(nsColor: .windowBackgroundColor))

    let renderer = ImageRenderer(content: view)
    renderer.scale = 2
    let bitmap = try #require(renderer.nsImage?.tiffRepresentation)
    try bitmap.write(
        to: URL(fileURLWithPath: "/tmp/emke-running-dashboard.tiff")
    )
}
```

Run:

```bash
EMKE_CAPTURE_UI=1 swift test --filter captureRunningDashboardForVisualReview
```

所有截图必须排除 API Key 输入内容。将运行态截图与已确认视觉稿按同尺寸并排比较：

`/Users/hale/.codex/generated_images/019f7317-d192-73e2-93f7-ab12bdc4c5e3/exec-e64a9f1b-cec0-4e0a-aa39-ad89127eddbb.png`

按 P0–P2 修正：裁切、溢出、主按钮高度、六区层级、通道状态文字、音波比例、24 pt 边距、深浅模式对比和设置返回路径。P3 只记录，不在本轮扩展范围。

- [ ] **Step 3: 验证运行态 UI 不产生额外实时链路压力**

使用 Instruments 的 SwiftUI 与 Time Profiler 检查：

- 打开窗口、真实 PCM 活动时 UI 更新不超过 30 Hz；
- 关闭菜单栏窗口后没有音波绘制或 UI 定时器持续唤醒；
- coordinator 音频事件队列不会随窗口隐藏持续增长；
- 音波绘制路径不执行网络、JSON、文件 I/O 或音频数据复制。

若发现隐藏窗口仍有 1 秒 `TimelineView` 唤醒，将 timeline 限定在运行且窗口可见时创建，并在 `MenuBarRootView.onDisappear` 关闭 UI 时间刷新；不得停止后台翻译会话。

- [ ] **Step 4: 更新用户文档和设计规格状态**

在 `README.md` 更新菜单栏使用顺序：

1. 设置 Base URL、Model ID、Keychain API Key 和物理设备；
2. 返回控制台选择母语与会议输出语言；
3. 在会议应用中选择 EMKE 虚拟扬声器／麦克风；
4. 开始翻译并用两个通道行控制原音旁路。

在设计规格中把状态改为“已实现并完成视觉验收”，并记录最终截图路径与验收日期；不得把截图文件本身或任何密钥加入仓库。

- [ ] **Step 5: 运行完整自动化验证**

Run:

```bash
swift test --parallel
swift build -c release
swift build --product EMKEMenuBarApp
EMKE_RUN_LIVE_AUDIO_TESTS=1 swift test --filter liveVirtualEndpointsStartAndStop
xcrun clang -std=c11 -arch arm64 -mmacosx-version-min=14.0 -Wall -Wextra -Werror -ISources/EMKEAudioBridge/include -ISources/EMKEAudioHAL/include -fsyntax-only Sources/EMKEAudioHAL/EMKEAudioHAL.c
git diff --check
rg -n "sk-[A-Za-z0-9_-]{16,}|Authorization: Bearer|api_key=|apiKey: \"sk-" . --glob '!\.git/**'
```

Expected:

- 全量 Swift 测试通过；
- Release 与菜单栏产品构建通过；
- 已安装虚拟驱动的本机集成测试通过；若驱动未安装，明确记录为未验证，不能写成通过；
- 严格 C 语法检查通过；
- `git diff --check` 无输出；
- secret scan 只允许命中安全说明或测试正则本身，不允许出现真实密钥。

- [ ] **Step 6: 审查实际 diff 与验收清单**

Run:

```bash
git status --short
git diff --stat
git diff -- Sources/EMKECoordinator Sources/EMKEMenuBarApp Tests README.md docs/superpowers/specs/2026-07-18-emke-menu-bar-typeless-ui-design.md
```

逐项对照设计规格第 10 节，明确区分“自动化验证通过”“实际截图已验收”“依赖真实服务商的端到端体验未验证”。

- [ ] **Step 7: 提交最终视觉与文档验收**

```bash
git add Sources/EMKEMenuBarApp Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift README.md docs/superpowers/specs/2026-07-18-emke-menu-bar-typeless-ui-design.md
git commit -m "docs: complete menu bar visual acceptance"
```

## Final Acceptance Checklist

- [ ] 420 × 620 pt 控制台无滚动、无裁切、唯一深色主动作完整可见。
- [ ] 主音波为 24 个确定性胶囊柱，真实 PCM 驱动，最高 30 Hz，起音约 80 ms、释放约 220 ms。
- [ ] 主音波取双通道较大值；小音波只取对应通道；出站失败不显示虚假活动。
- [ ] 未配置、就绪、连接中、翻译中、入站失败、出站失败、双旁路、停止中均有文字与图标表达。
- [ ] 运行期间设置可查看但不可修改；返回控制台不重建或停止会话。
- [ ] Reduce Motion、VoiceOver、Increase Contrast 和键盘焦点合同已验证。
- [ ] Keychain、UserDefaults、fail-open/fail-closed、双会话和关闭行为无回归。
- [ ] 完整 Swift 测试、Release 构建、产品构建、严格 C 检查与 secret scan 已按真实结果记录。
- [ ] 实际运行截图已与确认稿比较并修复 P0–P2 视觉偏差。
