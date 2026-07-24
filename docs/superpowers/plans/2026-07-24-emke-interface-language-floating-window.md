# EMKE Interface Language and Floating Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a complete runtime Chinese/English interface switch, make the Settings quit action visibly button-like, and show an approved single-row translation status capsule while a session is active.

**Architecture:** Keep `MenuBarModel` as the only translation-state owner. Add a typed, code-based localization layer and semantic UI-message values so changing the interface language re-renders existing states without restarting. Project the model into a pure floating presentation, render it in SwiftUI, and host it in a non-activating AppKit `NSPanel` whose visibility participates in the existing bounded audio-level publication contract.

**Tech Stack:** Swift 6.2, SwiftUI, AppKit `NSPanel`, Combine, Swift Testing, SwiftPM, macOS 14+

---

## File Structure

### New production files

- `Sources/EMKEMenuBarApp/AppLocalization.swift`
  - Owns interface-language preference values, system-language resolution,
    typed copy keys, bilingual copy, supported-language names, and formatted
    copy.
- `Sources/EMKEMenuBarApp/AppMessage.swift`
  - Stores semantic user-facing message values that can be re-rendered after a
    runtime interface-language change.
- `Sources/EMKEMenuBarApp/FloatingTranslationPresentation.swift`
  - Pure projection from coordinator/UI state to floating-capsule state.
- `Sources/EMKEMenuBarApp/FloatingTranslationStatusView.swift`
  - SwiftUI implementation of the approved 264 × 52 pt single-row capsule.
- `Sources/EMKEMenuBarApp/FloatingTranslationPanelController.swift`
  - Creates, positions, shows, and hides the non-activating AppKit panel.

### Modified production files

- `Sources/EMKEMenuBarApp/AppSettingsStore.swift`
  - Persists `AppInterfaceLanguage` and migrates missing/invalid values to
    `.system`.
- `Sources/EMKEMenuBarApp/MenuBarModel.swift`
  - Publishes the interface-language preference, resolves current copy, stores
    semantic messages, projects floating state, and tracks menu/floating audio
    visibility separately.
- `Sources/EMKEMenuBarApp/TranslationDashboardView.swift`
  - Uses localized copy for labels, accessibility, and supported-language
    names.
- `Sources/EMKEMenuBarApp/TranslationChannelRow.swift`
  - Uses localized copy for channel state and bypass actions.
- `Sources/EMKEMenuBarApp/TranslationSettingsView.swift`
  - Adds the interface-language control, localizes all visible copy, and adds
    the styled quit button.
- `Sources/EMKEMenuBarApp/MenuBarRootView.swift`
  - Reports menu-popover visibility with the new surface-specific API.
- `Sources/EMKEMenuBarApp/EMKEVisualStyle.swift`
  - Adds the approved floating-capsule and quit-button metrics.
- `Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift`
  - Constructs one model and one floating panel controller.

### New test files

- `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
- `Tests/EMKEAudioEngineTests/AppSettingsStoreTests.swift`
- `Tests/EMKEAudioEngineTests/FloatingTranslationPresentationTests.swift`
- `Tests/EMKEAudioEngineTests/FloatingTranslationRenderTests.swift`
- `Tests/EMKEAudioEngineTests/FloatingTranslationPanelTests.swift`

### Modified test files

- `Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift`
- `Tests/EMKEAudioEngineTests/TranslationDashboardPresentationTests.swift`
- `Tests/EMKEAudioEngineTests/TranslationChannelPresentationTests.swift`
- `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`
- `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`

## Task 1: Add the typed interface-language and copy foundation

**Files:**

- Create: `Sources/EMKEMenuBarApp/AppLocalization.swift`
- Create: `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`

- [ ] **Step 1: Write failing resolution and copy-completeness tests**

```swift
import EMKECore
import Testing
@testable import EMKEMenuBarApp

@Test
func interfaceLanguageResolutionUsesFirstPreferredLanguage() {
    #expect(
        AppLanguageResolver.resolve(
            preference: .system,
            preferredLanguages: ["zh-Hans-CN", "en"]
        ) == .zhHans
    )
    #expect(
        AppLanguageResolver.resolve(
            preference: .system,
            preferredLanguages: ["en-US", "zh-Hans"]
        ) == .english
    )
    #expect(
        AppLanguageResolver.resolve(
            preference: .zhHans,
            preferredLanguages: ["en-US"]
        ) == .zhHans
    )
    #expect(
        AppLanguageResolver.resolve(
            preference: .english,
            preferredLanguages: ["zh-Hans"]
        ) == .english
    )
}

@Test
func everyStaticCopyKeyHasChineseAndEnglishText() {
    for key in AppCopyKey.allCases {
        #expect(!AppCopy(language: .zhHans).text(key).isEmpty)
        #expect(!AppCopy(language: .english).text(key).isEmpty)
    }
}

@Test
func supportedLanguageNamesFollowTheInterfaceLanguage() {
    #expect(
        AppCopy(language: .zhHans).languageName(.german) == "德语"
    )
    #expect(
        AppCopy(language: .english).languageName(.german) == "German"
    )
}

@Test
func formattedCopyUsesLocalizedWordOrder() {
    #expect(
        AppCopy(language: .zhHans).reconnecting(attempt: 2)
            == "重连中（第 2 次）"
    )
    #expect(
        AppCopy(language: .english).reconnecting(attempt: 2)
            == "Reconnecting (attempt 2)"
    )
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```bash
swift test --filter interfaceLanguageResolutionUsesFirstPreferredLanguage
```

Expected: compilation fails because `AppInterfaceLanguage`,
`AppLanguageResolver`, `AppCopyKey`, and `AppCopy` do not exist.

- [ ] **Step 3: Add the language types and exhaustive dashboard/channel copy**

Implement this public shape inside the executable target:

```swift
import EMKECore
import Foundation

enum AppInterfaceLanguage: String, CaseIterable, Sendable {
    case system
    case zhHans = "zh-Hans"
    case english = "en"
}

enum ResolvedInterfaceLanguage: Equatable, Sendable {
    case zhHans
    case english
}

enum AppLanguageResolver {
    static func resolve(
        preference: AppInterfaceLanguage,
        preferredLanguages: [String]
    ) -> ResolvedInterfaceLanguage {
        switch preference {
        case .zhHans:
            return .zhHans
        case .english:
            return .english
        case .system:
            let first = preferredLanguages.first?.lowercased() ?? "en"
            return first.hasPrefix("zh") ? .zhHans : .english
        }
    }
}

enum AppCopyKey: CaseIterable, Sendable {
    case settings
    case backToDashboard
    case openSettings
    case translationSettingsLocked
    case interface
    case interfaceLanguage
    case followSystem
    case quitEMKE
    case selected
    case chooseDevice
    case chooseTranslationLanguage
    case myLanguage
    case meetingOutput
    case heardByMe
    case heardByOther
    case languageLockedHint
    case audioDirectToProvider
    case starting
    case startTranslation
    case stopping
    case stopTranslation
    case connecting
    case translating
    case outboundMuted
    case inboundOriginal
    case translationError
    case driverMissing
    case selectPhysicalInput
    case selectPhysicalOutput
    case invalidBaseURLPrompt
    case modelRequiredPrompt
    case apiKeyRequiredPrompt
    case ready
    case configurationUnavailable
    case restoreTranslation
    case restoreInbound
    case restoreOutbound
    case playOriginal
    case sendOriginal
    case playInboundOriginal
    case sendOutboundOriginal
    case stopped
    case channelConnecting
    case originalBypass
    case stable
    case sameLanguagePassThrough
    case noTranslationNeeded
    case outboundSameLanguageNoTranslation
    case muted
}

struct AppCopy: Equatable, Sendable {
    let language: ResolvedInterfaceLanguage

    func text(_ key: AppCopyKey) -> String {
        let pair: (zh: String, en: String) = switch key {
        case .settings: ("设置", "Settings")
        case .backToDashboard: ("返回翻译控制台", "Back to translation controls")
        case .openSettings: ("打开设置", "Open Settings")
        case .translationSettingsLocked:
            ("翻译运行期间设置已锁定", "Translation settings are locked while running")
        case .interface: ("界面", "Interface")
        case .interfaceLanguage: ("界面语言", "Interface language")
        case .followSystem: ("跟随系统", "Follow System")
        case .quitEMKE: ("退出 EMKE", "Quit EMKE")
        case .selected: ("已选择", "Selected")
        case .chooseDevice: ("请选择", "Choose")
        case .chooseTranslationLanguage: ("选择翻译语言", "Choose translation language")
        case .myLanguage: ("我的母语", "My language")
        case .meetingOutput: ("会议输出", "Meeting output")
        case .heardByMe: ("我听到", "I hear")
        case .heardByOther: ("对方听到", "They hear")
        case .languageLockedHint:
            ("翻译运行期间不可修改", "Cannot be changed while translation is running")
        case .audioDirectToProvider:
            ("音频直连你的服务商", "Audio connects directly to your provider")
        case .starting: ("正在连接…", "Connecting…")
        case .startTranslation: ("开始翻译", "Start translation")
        case .stopping: ("正在停止…", "Stopping…")
        case .stopTranslation: ("停止翻译", "Stop translation")
        case .connecting: ("正在连接", "Connecting")
        case .translating: ("翻译中", "Translating")
        case .outboundMuted: ("出站已静音", "Outbound muted")
        case .inboundOriginal: ("入站播放原音", "Playing original incoming audio")
        case .translationError: ("翻译异常", "Translation error")
        case .driverMissing:
            ("未检测到 EMKE 虚拟音频驱动", "EMKE virtual audio driver not detected")
        case .selectPhysicalInput:
            ("请选择真实麦克风", "Choose a physical microphone")
        case .selectPhysicalOutput:
            ("请选择真实耳机或扬声器", "Choose physical headphones or speakers")
        case .invalidBaseURLPrompt:
            ("请输入安全有效的 Base URL", "Enter a secure, valid Base URL")
        case .modelRequiredPrompt:
            ("请输入模型名称", "Enter a model name")
        case .apiKeyRequiredPrompt:
            ("请输入 API Key", "Enter an API key")
        case .ready: ("准备开始", "Ready")
        case .configurationUnavailable:
            ("配置或连接不可用", "Configuration or connection unavailable")
        case .restoreTranslation: ("恢复翻译", "Resume translation")
        case .restoreInbound: ("恢复入站翻译", "Resume inbound translation")
        case .restoreOutbound: ("恢复出站翻译", "Resume outbound translation")
        case .playOriginal: ("播放原音", "Play original")
        case .sendOriginal: ("发送原音", "Send original")
        case .playInboundOriginal: ("播放入站原音", "Play original inbound audio")
        case .sendOutboundOriginal: ("发送出站原音", "Send original outbound audio")
        case .stopped: ("已停止", "Stopped")
        case .channelConnecting: ("连接中", "Connecting")
        case .originalBypass: ("原音旁路", "Original audio bypass")
        case .stable: ("稳定", "Stable")
        case .sameLanguagePassThrough: ("同语言直通", "Same-language pass-through")
        case .noTranslationNeeded: ("无需翻译", "No translation needed")
        case .outboundSameLanguageNoTranslation:
            ("出站同语言无需翻译", "Outbound language matches; no translation needed")
        case .muted: ("已静音", "Muted")
        }
        return language == .zhHans ? pair.zh : pair.en
    }

    func languageName(_ language: SupportedLanguage) -> String {
        switch (self.language, language) {
        case (.zhHans, .chinese): "中文"
        case (.zhHans, .english): "英语"
        case (.zhHans, .german): "德语"
        case (.english, .chinese): "Chinese"
        case (.english, .english): "English"
        case (.english, .german): "German"
        }
    }

    func reconnecting(attempt: Int) -> String {
        language == .zhHans
            ? "重连中（第 \(attempt) 次）"
            : "Reconnecting (attempt \(attempt))"
    }

    func translating(elapsed: String) -> String {
        "\(text(.translating)) · \(elapsed)"
    }

    func inboundDirection(to language: SupportedLanguage) -> String {
        language == .chinese && self.language == .zhHans
            ? "其他语言 → 中文"
            : "\(self.language == .zhHans ? "其他语言" : "Other languages") → \(languageName(language))"
    }

    func outboundDirection(
        from source: SupportedLanguage,
        to target: SupportedLanguage
    ) -> String {
        "\(languageName(source)) → \(languageName(target))"
    }
}
```

- [ ] **Step 4: Run localization tests and verify they pass**

Run:

```bash
swift test --filter interfaceLanguageResolutionUsesFirstPreferredLanguage
swift test --filter everyStaticCopyKeyHasChineseAndEnglishText
swift test --filter supportedLanguageNamesFollowTheInterfaceLanguage
swift test --filter formattedCopyUsesLocalizedWordOrder
```

Expected: all localization tests pass.

- [ ] **Step 5: Commit the localization foundation**

```bash
git add Sources/EMKEMenuBarApp/AppLocalization.swift \
  Tests/EMKEAudioEngineTests/AppLocalizationTests.swift
git commit -m "feat: add runtime interface localization"
```

## Task 2: Persist the interface-language preference and expose resolved copy

**Files:**

- Modify: `Sources/EMKEMenuBarApp/AppSettingsStore.swift`
- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- Create: `Tests/EMKEAudioEngineTests/AppSettingsStoreTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift`

- [ ] **Step 1: Write failing persistence, migration, and non-mutation tests**

```swift
import EMKECore
import Foundation
import Testing
@testable import EMKEMenuBarApp

@Test @MainActor
func settingsStoreDefaultsUnknownInterfaceLanguageToSystem() {
    let suite = "emke-interface-language-\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    defaults.set("unexpected", forKey: "emke.interfaceLanguage")

    let value = UserDefaultsAppSettingsStore(defaults: defaults).load()

    #expect(value.interfaceLanguage == .system)
}

@Test @MainActor
func settingsStorePersistsInterfaceLanguageWithoutChangingOtherSettings() {
    let suite = "emke-interface-language-\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    let store = UserDefaultsAppSettingsStore(defaults: defaults)
    var expected = AppSettings.default
    expected.interfaceLanguage = .english

    store.save(expected)

    #expect(store.load() == expected)
}
```

Add a model test that switches only `interfaceLanguage`, then asserts the latest
saved settings retain the existing base URL, model, translation preferences,
and selected device UIDs.

Add a running-session regression:

```swift
@Test @MainActor
func interfaceLanguageChangeDoesNotRestartOrStopTranslation() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(
        secret: "stored-key",
        coordinator: coordinator
    )
    await configureAndStart(model)
    #expect(await coordinator.configurations.count == 1)

    model.interfaceLanguage = .english

    #expect(await coordinator.configurations.count == 1)
    #expect(model.coordinatorState.isRunning)
    #expect(model.motherLanguage == .chinese)
    #expect(model.meetingOutputLanguage == .german)
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```bash
swift test --filter settingsStoreDefaultsUnknownInterfaceLanguageToSystem
swift test --filter settingsStorePersistsInterfaceLanguageWithoutChangingOtherSettings
swift test --filter interfaceLanguageChangePersistsWithoutChangingTranslationConfiguration
swift test --filter interfaceLanguageChangeDoesNotRestartOrStopTranslation
```

Expected: compilation fails because `AppSettings.interfaceLanguage` and the
model preference do not exist.

- [ ] **Step 3: Extend settings storage and model state**

Add this field and default:

```swift
struct AppSettings: Equatable, Sendable {
    var baseURLString: String
    var modelID: String
    var preferences: TranslationPreferences
    var selectedInputUID: String?
    var selectedOutputUID: String?
    var interfaceLanguage: AppInterfaceLanguage
}

static let `default` = AppSettings(
    baseURLString: APIConfiguration.default.baseURL.absoluteString,
    modelID: APIConfiguration.default.modelID,
    preferences: TranslationPreferences(
        motherLanguage: .chinese,
        meetingOutputLanguage: .german
    ),
    selectedInputUID: nil,
    selectedOutputUID: nil,
    interfaceLanguage: .system
)
```

Use the exact key `emke.interfaceLanguage`. On load, resolve an absent or
unknown raw value to `.system`; on save, write `settings.interfaceLanguage.rawValue`.

Add these model members:

```swift
@Published var interfaceLanguage: AppInterfaceLanguage = .system {
    didSet { persistPublicSettingsIfNeeded() }
}
@Published private(set) var systemPreferredLanguages = Locale.preferredLanguages

private var localeObserver: AnyCancellable?

var resolvedInterfaceLanguage: ResolvedInterfaceLanguage {
    AppLanguageResolver.resolve(
        preference: interfaceLanguage,
        preferredLanguages: systemPreferredLanguages
    )
}

var copy: AppCopy {
    AppCopy(language: resolvedInterfaceLanguage)
}
```

In `init`, subscribe to `NSLocale.currentLocaleDidChangeNotification` and assign
`Locale.preferredLanguages` to `systemPreferredLanguages` on the main actor.
Add `interfaceLanguage` to `currentPublicSettings` and `apply(_:)`.

- [ ] **Step 4: Run persistence and model tests**

Run:

```bash
swift test --filter settingsStore
swift test
```

Expected: persistence/migration tests pass and existing model tests remain
green after their `AppSettings` initializers include `.system`.

- [ ] **Step 5: Commit preference persistence**

```bash
git add Sources/EMKEMenuBarApp/AppSettingsStore.swift \
  Sources/EMKEMenuBarApp/MenuBarModel.swift \
  Tests/EMKEAudioEngineTests/AppSettingsStoreTests.swift \
  Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift
git commit -m "feat: persist interface language preference"
```

## Task 3: Localize dashboard and channel presentation values

**Files:**

- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationChannelRow.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardPresentationTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationChannelPresentationTests.swift`

- [ ] **Step 1: Write failing English presentation tests**

Add `copy: AppCopy = AppCopy(language: .zhHans)` to
`DashboardFixture.makePresentation`, then add:

```swift
@Test
func dashboardPresentationRendersCompleteEnglishCopy() {
    let copy = AppCopy(language: .english)
    let value = DashboardFixture.running.makePresentation(copy: copy)

    #expect(value.primaryStatus == "Translating · 01:05")
    #expect(value.primaryActionTitle == "Stop translation")
    #expect(value.inputLanguageName == "Chinese")
    #expect(value.outputLanguageName == "German")
    #expect(value.inboundDirection == "Other languages → Chinese")
    #expect(value.outboundDirection == "Chinese → German")
    #expect(value.privacyText == "Audio connects directly to your provider")
}

@Test
func channelPresentationRendersEnglishFailureAndActions() {
    let copy = AppCopy(language: .english)
    let value = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .failed(message: "offline"),
        bypassEnabled: false,
        copy: copy
    )

    #expect(value.status == "Muted")
    #expect(value.actionTitle == "Send original")
    #expect(value.actionAccessibilityLabel == "Send original outbound audio")
}
```

- [ ] **Step 2: Run the presentation tests and verify they fail**

Run:

```bash
swift test --filter dashboardPresentationRendersCompleteEnglishCopy
swift test --filter channelPresentationRendersEnglishFailureAndActions
```

Expected: compilation fails because the presentation factories do not accept
`copy`.

- [ ] **Step 3: Thread `AppCopy` through both pure presentation factories**

Change signatures:

```swift
static func make(
    readiness: MenuBarReadiness,
    coordinatorState: TranslationCoordinatorState,
    isStarting: Bool,
    isStopping: Bool,
    inboundBypassEnabled: Bool,
    outboundBypassEnabled: Bool,
    inboundLevel: Double,
    outboundLevel: Double,
    translationStartedAt: Date?,
    motherLanguage: SupportedLanguage,
    meetingOutputLanguage: SupportedLanguage,
    now: Date,
    errorText: String?,
    copy: AppCopy
) -> TranslationDashboardPresentation
```

Use `copy.text(...)`, `copy.languageName(...)`, `copy.inboundDirection(...)`,
`copy.outboundDirection(...)`, and `copy.translating(elapsed:)` for every
user-facing string. Pass `copy` to both `TranslationChannelPresentation.make`
calls.

Change the channel factory signature:

```swift
static func make(
    channel: MenuBarChannel,
    state: TranslationChannelState,
    bypassEnabled: Bool,
    automaticBypass: Bool = false,
    copy: AppCopy
) -> TranslationChannelPresentation
```

Map each existing action/status to the corresponding typed key and keep symbols,
colors, enablement, and failure behavior unchanged.

In `MenuBarModel.dashboardPresentation(at:)`, pass `copy: copy`.
Update `repairMessage`, `statusText`, `inboundStatusText`,
`outboundStatusText`, and `dashboardStatusText(at:)` to
derive from `copy` as well; do not leave parallel Chinese-only presentation
helpers in the model.

- [ ] **Step 4: Run all presentation tests**

Run:

```bash
swift test --filter dashboardPresentationRendersCompleteEnglishCopy
swift test --filter channelPresentationRendersEnglishFailureAndActions
swift test
```

Expected: both Chinese regression assertions and new English assertions pass.

- [ ] **Step 5: Commit localized presentation**

```bash
git add Sources/EMKEMenuBarApp/MenuBarModel.swift \
  Sources/EMKEMenuBarApp/TranslationChannelRow.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardPresentationTests.swift \
  Tests/EMKEAudioEngineTests/TranslationChannelPresentationTests.swift
git commit -m "feat: localize translation presentation"
```

## Task 4: Make runtime and diagnostic messages language-reactive

**Files:**

- Create: `Sources/EMKEMenuBarApp/AppMessage.swift`
- Modify: `Sources/EMKEMenuBarApp/AppLocalization.swift`
- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- Modify: `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift`

- [ ] **Step 1: Write failing tests for already-visible state changing language**

```swift
@Test @MainActor
func diagnosticAndConnectionMessagesReRenderAfterLanguageChange() async {
    let diagnostics = AudioDiagnosticsStub()
    let model = MenuBarModel(
        provider: TranslationMenuDeviceProvider(),
        coordinator: TranslationCoordinatorStub(),
        connectionProbe: TranslationProbeStub(report: protocolOnlyReport),
        secretStore: TranslationSecretStoreStub(value: "stored-key"),
        settingsStore: TranslationSettingsStoreStub(),
        microphonePermissionProvider: MicrophonePermissionStub(
            state: .authorized
        ),
        audioDiagnostics: diagnostics,
        audioOutputTestDelay: {}
    )
    model.selectedOutputUID = "physical.output"
    model.interfaceLanguage = .zhHans
    await model.playAudioOutputTest()
    #expect(model.audioOutputDiagnosticText == "测试音已播放")

    model.interfaceLanguage = .english
    #expect(model.audioOutputDiagnosticText == "Test tone played")
}

@Test
func semanticMessageKeepsRawDetailButLocalizesItsPrefix() {
    let message = AppMessage.detail(.keychainReadFailed, "OSStatus -50")
    #expect(
        message.text(using: AppCopy(language: .zhHans))
            == "无法读取 Keychain：OSStatus -50"
    )
    #expect(
        message.text(using: AppCopy(language: .english))
            == "Could not read Keychain: OSStatus -50"
    )
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```bash
swift test --filter diagnosticAndConnectionMessagesReRenderAfterLanguageChange
swift test --filter semanticMessageKeepsRawDetailButLocalizesItsPrefix
```

Expected: `AppMessage` is missing and model messages remain stored Chinese
strings.

- [ ] **Step 3: Add exact semantic message cases and diagnostic copy**

Extend `AppCopyKey` and its bilingual switch with:

```swift
case keySaved
case keyNotSaved
case keychainReadFailed
case microphoneTestFailed
case speakerTestFailed
case testTonePlaying
case testTonePlayed
case notTested
case microphoneConnectedWaiting
case microphoneDetected
case noAudioFrames
case inputCallbackMissing
case inputCallbackDidNotWrite
case waitingForAudioFrames
case testingTranslationProtocol
case connectionTestFailed
case protocolFullyCompatible
case protocolNeedsAudioTest
case protocolIncompatible
case audioOutputBusy
case invalidBaseURLError
case modelRequiredError
case apiKeyRequiredError
case microphonePermissionDenied
case microphonePermissionRestricted
case outputTestBackpressure
case audioDiagnosticFailed
```

Use these exact bilingual values:

| Key | Chinese | English |
| --- | --- | --- |
| `keySaved` | 已存入 Keychain | Saved in Keychain |
| `keyNotSaved` | 尚未保存 | Not saved |
| `keychainReadFailed` | 无法读取 Keychain | Could not read Keychain |
| `microphoneTestFailed` | 麦克风测试失败 | Microphone test failed |
| `speakerTestFailed` | 扬声器测试失败 | Speaker test failed |
| `testTonePlaying` | 正在播放测试音… | Playing test tone… |
| `testTonePlayed` | 测试音已播放 | Test tone played |
| `notTested` | 未测试 | Not tested |
| `microphoneConnectedWaiting` | 设备已连接，等待声音 | Device connected; waiting for sound |
| `microphoneDetected` | 已检测到麦克风输入 | Microphone input detected |
| `noAudioFrames` | 未收到音频帧 | No audio frames received |
| `inputCallbackMissing` | 设备未触发输入回调 | Device did not trigger an input callback |
| `inputCallbackDidNotWrite` | 输入回调未写入音频 | Input callback did not write audio |
| `waitingForAudioFrames` | 等待下一批音频帧 | Waiting for the next audio frames |
| `testingTranslationProtocol` | 正在测试 Translation 协议 | Testing Translation protocol |
| `connectionTestFailed` | 连接测试失败 | Connection test failed |
| `protocolFullyCompatible` | Translation 协议与音频能力均兼容 | Translation protocol and audio capabilities are compatible |
| `protocolNeedsAudioTest` | Translation 协议连接通过，需要音频测试 | Translation protocol connected; audio test required |
| `protocolIncompatible` | Translation 协议不兼容 | Translation protocol is incompatible |
| `audioOutputBusy` | 音频输出繁忙 | Audio output busy |
| `invalidBaseURLError` | Base URL 必须是有效的 HTTPS 或 WSS 地址 | Base URL must be a valid HTTPS or WSS address |
| `modelRequiredError` | 模型名称不能为空 | Model name cannot be empty |
| `apiKeyRequiredError` | API Key 未写入 Keychain | API key is not stored in Keychain |
| `microphonePermissionDenied` | 麦克风权限未开启，请在系统设置的隐私与安全性中允许 EMKE Translation | Allow EMKE Translation to use the microphone in Privacy & Security settings |
| `microphonePermissionRestricted` | 当前系统策略限制了麦克风访问 | The current system policy restricts microphone access |
| `outputTestBackpressure` | 测试音未完整写入所选输出设备 | The test tone was not fully written to the selected output device |
| `audioDiagnosticFailed` | 本地音频诊断失败 | Local audio diagnostic failed |

Implement:

```swift
enum AppMessage: Equatable, Sendable {
    case key(AppCopyKey)
    case detail(AppCopyKey, String)
    case inputOversized(callbackFrames: Int, capacityFrames: Int)
    case audioReadFailed(status: Int32)
    case droppedFrames(Int)
    case raw(String)

    func text(using copy: AppCopy) -> String {
        switch self {
        case .key(let key):
            return copy.text(key)
        case .detail(let key, let detail):
            let separator = copy.language == .zhHans ? "：" : ": "
            return copy.text(key) + separator + detail
        case .inputOversized(let callback, let capacity):
            return copy.language == .zhHans
                ? "输入帧超过缓冲区（\(callback) > \(capacity)）"
                : "Input frames exceeded buffer (\(callback) > \(capacity))"
        case .audioReadFailed(let status):
            return copy.language == .zhHans
                ? "读取音频失败（OSStatus \(status)）"
                : "Could not read audio (OSStatus \(status))"
        case .droppedFrames(let frames):
            return copy.language == .zhHans
                ? "音频输出繁忙，已丢弃 \(frames) 帧"
                : "Audio output busy; dropped \(frames) frames"
        case .raw(let value):
            return value
        }
    }
}
```

- [ ] **Step 4: Replace stored strings with semantic state**

Store private semantic values and expose computed strings:

```swift
@Published private var connectionTestMessageValue: AppMessage?
@Published private var inventoryErrorValue: AppMessage?
@Published private var configurationErrorValue: AppMessage?
@Published private var audioInputDiagnosticValue: AppMessage = .key(.notTested)
@Published private var audioOutputDiagnosticValue: AppMessage = .key(.notTested)
@Published private var audioDiagnosticErrorValue: AppMessage?

var connectionTestMessage: String {
    connectionTestMessageValue?.text(using: copy) ?? ""
}
var apiKeyStatusText: String {
    copy.text(hasStoredAPIKey ? .keySaved : .keyNotSaved)
}
var inventoryError: String? {
    inventoryErrorValue?.text(using: copy)
}
var configurationError: String? {
    configurationErrorValue?.text(using: copy)
}
var audioInputDiagnosticText: String {
    audioInputDiagnosticValue.text(using: copy)
}
var audioOutputDiagnosticText: String {
    audioOutputDiagnosticValue.text(using: copy)
}
var audioDiagnosticError: String? {
    audioDiagnosticErrorValue?.text(using: copy)
}
var audioInputDiagnosticSucceeded: Bool {
    audioInputDiagnosticValue == .key(.microphoneDetected)
}
var audioOutputDiagnosticSucceeded: Bool {
    audioOutputDiagnosticValue == .key(.testTonePlayed)
}
```

Replace each assignment in device loading, Keychain loading, start/test catches,
local diagnostics, backpressure events, and compatibility summaries with the
matching semantic case. Convert `MenuBarConfigurationError` and
`AudioDiagnosticPresentationError` to stable enums without Chinese
`CustomStringConvertible` output, and map them to the exact keys above.

- [ ] **Step 5: Run model and localization tests**

Run:

```bash
swift test --filter diagnosticAndConnectionMessagesReRenderAfterLanguageChange
swift test --filter semanticMessageKeepsRawDetailButLocalizesItsPrefix
swift test
```

Expected: stored state changes language immediately, raw diagnostic details are
preserved, and the existing behavior tests pass with localized computed values.

- [ ] **Step 6: Commit semantic messages**

```bash
git add Sources/EMKEMenuBarApp/AppMessage.swift \
  Sources/EMKEMenuBarApp/AppLocalization.swift \
  Sources/EMKEMenuBarApp/MenuBarModel.swift \
  Tests/EMKEAudioEngineTests/AppLocalizationTests.swift \
  Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift
git commit -m "refactor: make app messages language reactive"
```

## Task 5: Localize the views and add the interface selector and quit button

**Files:**

- Modify: `Sources/EMKEMenuBarApp/AppLocalization.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationDashboardView.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationChannelRow.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationSettingsView.swift`
- Modify: `Sources/EMKEMenuBarApp/EMKEVisualStyle.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`

- [ ] **Step 1: Write failing view source-contract and English render tests**

Add `settingsUsesLocalizedInterfaceMenuAndStyledQuitButton` and
`dashboardUsesLocalizedCopyInsteadOfChineseViewLiterals` tests with these
assertions:

```swift
#expect(settings.contains("InterfaceLanguageMenuButton("))
#expect(settings.contains("Label(copy.text(.quitEMKE), systemImage: \"power\")"))
#expect(settings.contains(".frame(maxWidth: .infinity, minHeight: 40)"))
#expect(!settings.contains("Button(\"退出 EMKE\")"))
#expect(dashboard.contains("copy.text(.openSettings)"))
#expect(dashboard.contains("copy.languageName(language)"))
```

Extend the dashboard render helper with a `copy` parameter and render an English
ready dashboard at exactly 840 × 1240 pixels in a test named
`englishReadyDashboardKeepsApprovedRenderDimensions`.

- [ ] **Step 2: Run the focused view tests and verify they fail**

Run:

```bash
swift test --filter settingsUsesLocalizedInterfaceMenuAndStyledQuitButton
swift test --filter dashboardUsesLocalizedCopyInsteadOfChineseViewLiterals
swift test --filter englishReadyDashboardKeepsApprovedRenderDimensions
```

Expected: source-contract assertions fail because views still embed Chinese
literals and the styled quit button does not exist.

- [ ] **Step 3: Add settings copy keys and the unlocked language control**

Add these exact values to `AppCopyKey` and `AppCopy.text(_:)`:

| Key | Chinese | English |
| --- | --- | --- |
| `provider` | 服务商 | Provider |
| `enterNewAPIKey` | 输入新的 API Key | Enter a new API key |
| `modelID` | Model ID | Model ID |
| `translationModel` | 翻译模型 | Translation model |
| `testing` | 测试中… | Testing… |
| `testConnection` | 测试连接 | Test connection |
| `audioDevices` | 音频设备 | Audio devices |
| `physicalMicrophone` | 真实麦克风 | Physical microphone |
| `physicalOutput` | 真实耳机 / 扬声器 | Physical headphones / speakers |
| `detectingDevices` | 正在检测设备… | Detecting devices… |
| `refreshDevices` | 刷新设备 | Refresh devices |
| `localAudioDiagnostics` | 本地音频诊断 | Local audio diagnostics |
| `localAudioOnly` | 仅检查本机音频，不连接翻译服务 | Checks local audio only; does not connect to the translation service |
| `stopTest` | 停止测试 | Stop test |
| `testMicrophone` | 测试麦克风 | Test microphone |
| `playing` | 正在播放… | Playing… |
| `playTestTone` | 播放测试音 | Play test tone |
| `authentication` | 认证 | Authentication |
| `protocolHandshake` | 协议握手 | Protocol handshake |
| `targetLanguage` | 目标语言 | Target language |
| `dualChannel` | 双通道 | Dual channel |
| `sourceTranscript` | 源语转写 | Source transcript |
| `audioOutput` | 音频输出 | Audio output |
| `secureClose` | 安全关闭 | Secure close |
| `passed` | 通过 | Passed |
| `needsAudioTest` | 需要音频测试 | Audio test required |
| `incompatible` | 不兼容 | Incompatible |
| `chooseAudioDevice` | 选择音频设备 | Choose audio device |
| `chooseInterfaceLanguage` | 选择界面语言 | Choose interface language |

At the top of the settings scroll content add:

```swift
private var interfaceSection: some View {
    VStack(alignment: .leading, spacing: 16) {
        sectionTitle(copy.text(.interface), systemImage: "character.bubble")
        settingField(copy.text(.interfaceLanguage)) {
            InterfaceLanguageMenuButton(
                copy: copy,
                selection: $model.interfaceLanguage
            )
        }
    }
}
```

`InterfaceLanguageMenuButton` must show exactly `.system`, `.zhHans`, and
`.english`, use `copy.text(.followSystem)`, `中文`, and `English`, remain enabled
while `model.selectionsLocked` is true, and expose localized selected/accessibility
values.

Implement it as the same popover-based interaction family used by the repaired
translation-language and audio-device menus:

```swift
private struct InterfaceLanguageMenuButton: View {
    let copy: AppCopy
    @Binding var selection: AppInterfaceLanguage
    @State private var isPresented = false

    var body: some View {
        Button {
            isPresented.toggle()
        } label: {
            HStack(spacing: 8) {
                Text(optionName(selection))
                Spacer(minLength: 12)
                Image(systemName: "chevron.down")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
                    .accessibilityHidden(true)
            }
            .font(.system(size: 14, weight: .medium))
            .padding(.vertical, 7)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .frame(maxWidth: .infinity, alignment: .leading)
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 2) {
                ForEach(AppInterfaceLanguage.allCases, id: \.self) { language in
                    Button {
                        selection = language
                        isPresented = false
                    } label: {
                        HStack(spacing: 10) {
                            Text(optionName(language))
                            Spacer(minLength: 16)
                            if selection == language {
                                Image(systemName: "checkmark")
                                    .accessibilityHidden(true)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 7)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityValue(
                        selection == language ? copy.text(.selected) : ""
                    )
                }
            }
            .padding(4)
            .frame(width: 190)
        }
        .accessibilityLabel(copy.text(.interfaceLanguage))
        .accessibilityValue(optionName(selection))
        .accessibilityHint(copy.text(.chooseInterfaceLanguage))
    }

    private func optionName(_ language: AppInterfaceLanguage) -> String {
        switch language {
        case .system: copy.text(.followSystem)
        case .zhHans: "中文"
        case .english: "English"
        }
    }
}
```

- [ ] **Step 4: Replace view literals and implement the quit button**

At each view body, use:

```swift
private var copy: AppCopy { model.copy }
```

Pass `copy` into `TranslationDashboardContent`, `TranslationChannelRow`,
`LanguageMenuButton`, `AudioDeviceMenuButton`, and compatibility helpers.
Change `diagnosticStatus` to accept an explicit `succeeded` boolean from
`model.audioInputDiagnosticSucceeded` or
`model.audioOutputDiagnosticSucceeded`; remove the current
Chinese-substring checks so English success states keep the checkmark icon.

Implement the quit action:

```swift
private struct ExitApplicationButton: View {
    let copy: AppCopy
    let action: () -> Void
    @State private var isHovered = false

    var body: some View {
        Button(action: action) {
            Label(copy.text(.quitEMKE), systemImage: "power")
                .font(.system(size: 13, weight: .medium))
                .frame(maxWidth: .infinity, minHeight: 40)
                .contentShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(
                    isHovered
                        ? EMKEVisualStyle.surfaceBackground.opacity(0.82)
                        : EMKEVisualStyle.surfaceBackground
                )
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .stroke(EMKEVisualStyle.separator, lineWidth: 1)
        )
        .onHover { isHovered = $0 }
        .accessibilityLabel(copy.text(.quitEMKE))
    }
}
```

Invoke it with `NSApplication.shared.terminate(nil)`. Do not change Provider,
Keychain, model, device, or audio actions.

- [ ] **Step 5: Run dashboard/settings tests**

Run:

```bash
swift test --filter settingsUsesLocalizedInterfaceMenuAndStyledQuitButton
swift test --filter englishReadyDashboardKeepsApprovedRenderDimensions
swift test
```

Expected: Chinese and English views render at existing dimensions, the language
selector remains unlocked, and the quit action satisfies the button contract.

- [ ] **Step 6: Commit localized views**

```bash
git add Sources/EMKEMenuBarApp/AppLocalization.swift \
  Sources/EMKEMenuBarApp/TranslationDashboardView.swift \
  Sources/EMKEMenuBarApp/TranslationChannelRow.swift \
  Sources/EMKEMenuBarApp/TranslationSettingsView.swift \
  Sources/EMKEMenuBarApp/EMKEVisualStyle.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift
git commit -m "feat: add English interface and styled quit action"
```

## Task 6: Add the pure floating-capsule presentation

**Files:**

- Create: `Sources/EMKEMenuBarApp/FloatingTranslationPresentation.swift`
- Create: `Tests/EMKEAudioEngineTests/FloatingTranslationPresentationTests.swift`
- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`

- [ ] **Step 1: Write failing state-mapping tests**

Cover idle, starting, healthy running, inbound fail-open, outbound fail-closed,
fatal running error, stopping, and level clamping:

```swift
@Test
func runningFloatingPresentationUsesCombinedRealLevelAndTimer() {
    let value = FloatingTranslationPresentation.make(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        ),
        isStarting: false,
        isStopping: false,
        inboundLevel: 0.35,
        outboundLevel: 0.72,
        translationStartedAt: Date(timeIntervalSince1970: 9_935),
        now: Date(timeIntervalSince1970: 10_000),
        errorText: nil,
        copy: AppCopy(language: .english)
    )

    #expect(value.isVisible)
    #expect(value.tone == .healthy)
    #expect(value.status == "Translating")
    #expect(value.elapsed == "01:05")
    #expect(value.level == 0.72)
    #expect(value.stopEnabled)
}

@Test
func idleFloatingPresentationIsHidden() {
    let value = FloatingTranslationPresentation.make(
        coordinatorState: TranslationCoordinatorState(),
        isStarting: false,
        isStopping: false,
        inboundLevel: 0,
        outboundLevel: 0,
        translationStartedAt: nil,
        now: .now,
        errorText: "configuration error",
        copy: AppCopy(language: .english)
    )
    #expect(!value.isVisible)
}
```

- [ ] **Step 2: Run tests and verify they fail**

Run:

```bash
swift test --filter runningFloatingPresentationUsesCombinedRealLevelAndTimer
```

Expected: compilation fails because the presentation does not exist.

- [ ] **Step 3: Implement the exact pure projection**

```swift
enum FloatingTranslationTone: Equatable, Sendable {
    case neutral
    case healthy
    case degraded
    case failure
}

struct FloatingTranslationPresentation: Equatable, Sendable {
    let isVisible: Bool
    let tone: FloatingTranslationTone
    let status: String
    let elapsed: String?
    let level: Double
    let stopEnabled: Bool
    let stopAccessibilityLabel: String

    static func make(
        coordinatorState: TranslationCoordinatorState,
        isStarting: Bool,
        isStopping: Bool,
        inboundLevel: Double,
        outboundLevel: Double,
        translationStartedAt: Date?,
        now: Date,
        errorText: String?,
        copy: AppCopy
    ) -> Self {
        let running = coordinatorState.isRunning
        let visible = isStarting || running || isStopping
        let elapsed = running
            ? MenuBarModel.formatElapsed(
                seconds: now.timeIntervalSince(translationStartedAt ?? now)
            )
            : nil
        let statusAndTone: (String, FloatingTranslationTone)
        if isStopping {
            statusAndTone = (copy.text(.stopping), .neutral)
        } else if isStarting && !running {
            statusAndTone = (copy.text(.connecting), .neutral)
        } else if running, errorText != nil {
            statusAndTone = (copy.text(.translationError), .failure)
        } else if running, case .failed = coordinatorState.outbound {
            statusAndTone = (copy.text(.outboundMuted), .degraded)
        } else if running, case .failed = coordinatorState.inbound {
            statusAndTone = (copy.text(.inboundOriginal), .degraded)
        } else {
            statusAndTone = (copy.text(.translating), .healthy)
        }
        return Self(
            isVisible: visible,
            tone: statusAndTone.1,
            status: statusAndTone.0,
            elapsed: elapsed,
            level: min(max(max(inboundLevel, outboundLevel), 0), 1),
            stopEnabled: running && !isStopping,
            stopAccessibilityLabel: copy.text(.stopTranslation)
        )
    }
}
```

Add `MenuBarModel.floatingPresentation(at:)` that passes the current model
state and copy to this factory.

- [ ] **Step 4: Run floating presentation tests**

Run:

```bash
swift test --filter runningFloatingPresentationUsesCombinedRealLevelAndTimer
swift test --filter idleFloatingPresentationIsHidden
swift test
```

Expected: all state-mapping tests pass.

- [ ] **Step 5: Commit floating presentation**

```bash
git add Sources/EMKEMenuBarApp/FloatingTranslationPresentation.swift \
  Sources/EMKEMenuBarApp/MenuBarModel.swift \
  Tests/EMKEAudioEngineTests/FloatingTranslationPresentationTests.swift
git commit -m "feat: add floating translation presentation"
```

## Task 7: Expand audio-level visibility to both UI surfaces

**Files:**

- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- Modify: `Sources/EMKEMenuBarApp/MenuBarRootView.swift`
- Modify: `Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift`

- [ ] **Step 1: Write the failing two-surface visibility test**

```swift
@Test @MainActor
func floatingSurfaceKeepsRealLevelsEnabledAfterMenuCloses() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(coordinator: coordinator)

    await model.setMenuBarVisible(true)
    await model.setFloatingWindowVisible(true)
    await model.setMenuBarVisible(false)

    #expect(await coordinator.audioLevelUpdateFlags.last == true)
    #expect(model.hasVisibleAudioLevelSurface)

    await model.setFloatingWindowVisible(false)

    #expect(await coordinator.audioLevelUpdateFlags.last == false)
    #expect(!model.hasVisibleAudioLevelSurface)
    #expect(model.inboundLevel == 0)
    #expect(model.outboundLevel == 0)
}
```

Also update the existing level-event test so an emitted level remains visible
when only the floating surface is true.

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```bash
swift test --filter floatingSurfaceKeepsRealLevelsEnabledAfterMenuCloses
swift test --filter modelConsumesLatestAudioLevelSnapshot
```

Expected: the new surface methods and `hasVisibleAudioLevelSurface` do not
exist.

- [ ] **Step 3: Implement combined visibility without duplicate coordinator flags**

Replace `isWindowVisible` with:

```swift
@Published private(set) var isMenuBarVisible = false
@Published private(set) var isFloatingWindowVisible = false
private var publishedAudioLevelVisibility = false

var hasVisibleAudioLevelSurface: Bool {
    isMenuBarVisible || isFloatingWindowVisible
}

func setMenuBarVisible(_ visible: Bool) async {
    isMenuBarVisible = visible
    if !visible {
        await stopAudioInputTest()
    }
    await synchronizeAudioLevelVisibility()
}

func setFloatingWindowVisible(_ visible: Bool) async {
    isFloatingWindowVisible = visible
    await synchronizeAudioLevelVisibility()
}

private func synchronizeAudioLevelVisibility() async {
    let enabled = hasVisibleAudioLevelSurface
    if enabled != publishedAudioLevelVisibility {
        publishedAudioLevelVisibility = enabled
        await coordinator.setAudioLevelUpdatesEnabled(enabled)
    }
    if !enabled {
        inboundLevel = 0
        outboundLevel = 0
    }
}
```

In the event loop, accept `.audioLevels` only when
`hasVisibleAudioLevelSurface`. In `MenuBarRootView`, rename calls to
`setMenuBarVisible`.

- [ ] **Step 4: Run visibility and model tests**

Run:

```bash
swift test --filter floatingSurfaceKeepsRealLevelsEnabledAfterMenuCloses
swift test
```

Expected: menu-only legacy behavior and the floating-only behavior both pass.

- [ ] **Step 5: Commit multi-surface visibility**

```bash
git add Sources/EMKEMenuBarApp/MenuBarModel.swift \
  Sources/EMKEMenuBarApp/MenuBarRootView.swift \
  Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift
git commit -m "fix: keep floating waveform levels live"
```

## Task 8: Build and render the approved single-row SwiftUI capsule

**Files:**

- Create: `Sources/EMKEMenuBarApp/FloatingTranslationStatusView.swift`
- Modify: `Sources/EMKEMenuBarApp/EMKEVisualStyle.swift`
- Create: `Tests/EMKEAudioEngineTests/FloatingTranslationRenderTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`

- [ ] **Step 1: Write failing geometry, render, and accessibility tests**

```swift
@Test
func floatingCapsuleMetricsMatchApprovedDirectionA() {
    #expect(EMKEFloatingMetrics.width == 264)
    #expect(EMKEFloatingMetrics.height == 52)
    #expect(EMKEFloatingMetrics.stopTarget == 32)
}

@Test @MainActor
func floatingCapsuleRendersAtRetinaDimensions() throws {
    let presentation = makeHealthyFloatingPresentation()
    let view = FloatingTranslationStatusView(
        presentation: presentation,
        stopAction: {}
    )
    let renderer = ImageRenderer(content: view)
    renderer.scale = 2
    let data = try #require(renderer.nsImage?.tiffRepresentation)
    let bitmap = try #require(NSBitmapImageRep(data: data))
    #expect(bitmap.pixelsWide == 528)
    #expect(bitmap.pixelsHigh == 104)
}
```

Add source assertions that the waveform is accessibility-hidden, the status is
one combined accessibility element, and the stop label comes from
`presentation.stopAccessibilityLabel`.

- [ ] **Step 2: Run tests and verify they fail**

Run:

```bash
swift test --filter floatingCapsuleMetricsMatchApprovedDirectionA
swift test --filter floatingCapsuleRendersAtRetinaDimensions
```

Expected: the floating metrics and view do not exist.

- [ ] **Step 3: Add exact metrics and view**

```swift
enum EMKEFloatingMetrics {
    static let width: CGFloat = 264
    static let height: CGFloat = 52
    static let cornerRadius: CGFloat = 26
    static let statusWidth: CGFloat = 72
    static let waveformWidth: CGFloat = 99
    static let stopTarget: CGFloat = 32
}
```

Implement the view:

```swift
struct FloatingTranslationStatusView: View {
    let presentation: FloatingTranslationPresentation
    let stopAction: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var isPulsing = false

    var body: some View {
        HStack(spacing: 10) {
            Circle()
                .fill(toneColor)
                .frame(width: 7, height: 7)
                .overlay {
                    if presentation.tone == .neutral && !reduceMotion {
                        Circle()
                            .stroke(toneColor.opacity(0.55), lineWidth: 1)
                            .scaleEffect(isPulsing ? 1.9 : 1)
                            .opacity(isPulsing ? 0 : 1)
                            .onAppear {
                                withAnimation(
                                    .easeOut(duration: 1)
                                        .repeatForever(autoreverses: false)
                                ) {
                                    isPulsing = true
                                }
                            }
                    }
                }
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text(presentation.status)
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(1)
                Text(presentation.elapsed ?? " ")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
            .frame(width: EMKEFloatingMetrics.statusWidth, alignment: .leading)
            .accessibilityElement(children: .combine)
            LiveWaveformView(
                level: presentation.level,
                maximumHeight: 24,
                compact: true
            )
            .frame(width: EMKEFloatingMetrics.waveformWidth)
            Button(action: stopAction) {
                RoundedRectangle(cornerRadius: 2)
                    .fill(Color(nsColor: .systemRed))
                    .frame(width: 8, height: 8)
                    .frame(
                        width: EMKEFloatingMetrics.stopTarget,
                        height: EMKEFloatingMetrics.stopTarget
                    )
                    .background(Circle().fill(.white.opacity(0.12)))
            }
            .buttonStyle(.plain)
            .disabled(!presentation.stopEnabled)
            .accessibilityLabel(presentation.stopAccessibilityLabel)
        }
        .padding(.horizontal, 10)
        .frame(
            width: EMKEFloatingMetrics.width,
            height: EMKEFloatingMetrics.height
        )
        .foregroundStyle(.white)
        .background(Color.black.opacity(0.94), in: Capsule())
        .overlay(Capsule().stroke(.white.opacity(0.12), lineWidth: 1))
        .preferredColorScheme(.dark)
    }

    private var toneColor: Color {
        switch presentation.tone {
        case .neutral: Color(nsColor: .systemGray)
        case .healthy: Color(red: 0.51, green: 0.90, blue: 0.74)
        case .degraded: Color(nsColor: .systemOrange)
        case .failure: Color(nsColor: .systemRed)
        }
    }
}
```

- [ ] **Step 4: Run floating render/accessibility tests**

Run:

```bash
swift test --filter floatingCapsuleMetricsMatchApprovedDirectionA
swift test --filter floatingCapsuleRendersAtRetinaDimensions
swift test
```

Expected: exact dimensions, stop target, and accessibility assertions pass.

- [ ] **Step 5: Commit the capsule view**

```bash
git add Sources/EMKEMenuBarApp/FloatingTranslationStatusView.swift \
  Sources/EMKEMenuBarApp/EMKEVisualStyle.swift \
  Tests/EMKEAudioEngineTests/FloatingTranslationRenderTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift
git commit -m "feat: build floating translation capsule"
```

## Task 9: Host the capsule in a non-activating `NSPanel`

**Files:**

- Create: `Sources/EMKEMenuBarApp/FloatingTranslationPanelController.swift`
- Modify: `Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift`
- Create: `Tests/EMKEAudioEngineTests/FloatingTranslationPanelTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`

- [ ] **Step 1: Write failing panel-contract tests**

```swift
@Test @MainActor
func floatingPanelUsesNonActivatingCrossSpaceContract() {
    let model = MenuBarModel(deferInitialDeviceReload: true)
    let controller = FloatingTranslationPanelController(model: model)
    let panel = controller.panelForTesting

    #expect(panel.styleMask.contains(.borderless))
    #expect(panel.styleMask.contains(.nonactivatingPanel))
    #expect(panel.level == .floating)
    #expect(panel.collectionBehavior.contains(.canJoinAllSpaces))
    #expect(panel.collectionBehavior.contains(.fullScreenAuxiliary))
    #expect(panel.isMovableByWindowBackground)
    #expect(!panel.canBecomeKey)
}
```

Add a source assertion that `EMKEMenuBarApp` constructs the model once and
passes the same instance to `FloatingTranslationPanelController`.

- [ ] **Step 2: Run tests and verify they fail**

Run:

```bash
swift test --filter floatingPanelUsesNonActivatingCrossSpaceContract
swift test --filter menuBarAppSharesOneModelWithFloatingPanel
```

Expected: the controller and shared ownership do not exist.

- [ ] **Step 3: Implement the panel and model-observing root view**

Use a non-key subclass:

```swift
private final class FloatingTranslationPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}
```

Create the panel with:

```swift
let panel = FloatingTranslationPanel(
    contentRect: NSRect(
        x: 0,
        y: 0,
        width: EMKEFloatingMetrics.width,
        height: EMKEFloatingMetrics.height
    ),
    styleMask: [.borderless, .nonactivatingPanel],
    backing: .buffered,
    defer: false
)
panel.level = .floating
panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
panel.isOpaque = false
panel.backgroundColor = .clear
panel.hasShadow = true
panel.hidesOnDeactivate = false
panel.isMovableByWindowBackground = true
```

Host a `TimelineView(.periodic(from: .now, by: 1))` containing
`FloatingTranslationStatusView`. Its stop action runs:

```swift
Task { await model.stop() }
```

Subscribe to `model.objectWillChange`, defer one main-actor turn with
`Task.yield()`, then refresh from `model.floatingPresentation(at: .now)`.
When `isVisible` changes from false to true:

1. place once on the screen containing `NSEvent.mouseLocation`, falling back to
   `NSScreen.main`;
2. center horizontally in `screen.visibleFrame`;
3. place 36 pt above the visible frame bottom;
4. call `orderFrontRegardless()`;
5. call `await model.setFloatingWindowVisible(true)`.

When it changes from true to false, call `orderOut(nil)` and
`await model.setFloatingWindowVisible(false)`.

Expose `panelForTesting` as an internal read-only property.

- [ ] **Step 4: Construct one shared model/controller pair in the app**

```swift
@main
@MainActor
struct EMKEMenuBarApp: App {
    @StateObject private var model: MenuBarModel
    @StateObject private var floatingPanelController:
        FloatingTranslationPanelController

    init() {
        let model = MenuBarModel(deferInitialDeviceReload: true)
        _model = StateObject(wrappedValue: model)
        _floatingPanelController = StateObject(
            wrappedValue: FloatingTranslationPanelController(model: model)
        )
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarRootView(model: model)
        } label: {
            Image(nsImage: MenuBarLogo.image)
                .accessibilityLabel("EMKE Translation")
        }
        .menuBarExtraStyle(.window)
    }
}
```

Make the controller conform to `ObservableObject` so SwiftUI retains the
`@StateObject`.

- [ ] **Step 5: Run panel and app ownership tests**

Run:

```bash
swift test --filter floatingPanelUsesNonActivatingCrossSpaceContract
swift test --filter menuBarAppSharesOneModelWithFloatingPanel
swift test
```

Expected: panel flags and one-model ownership pass without ordering a real
window front during tests.

- [ ] **Step 6: Commit panel integration**

```bash
git add Sources/EMKEMenuBarApp/FloatingTranslationPanelController.swift \
  Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift \
  Tests/EMKEAudioEngineTests/FloatingTranslationPanelTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift
git commit -m "feat: show translation status in floating panel"
```

## Task 10: Complete visual evidence, regression verification, and handoff

**Files:**

- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/FloatingTranslationRenderTests.swift`
- Create: `docs/visual-qa/interface-language-floating-window/README.md`

- [ ] **Step 1: Add deterministic capture output for all approved states**

Under `EMKE_CAPTURE_UI=1`, write original-size TIFF files to:

```text
/tmp/emke-interface-floating-qa/dashboard-ready-zh.tiff
/tmp/emke-interface-floating-qa/dashboard-ready-en.tiff
/tmp/emke-interface-floating-qa/settings-zh.tiff
/tmp/emke-interface-floating-qa/settings-en.tiff
/tmp/emke-interface-floating-qa/floating-connecting.tiff
/tmp/emke-interface-floating-qa/floating-running.tiff
/tmp/emke-interface-floating-qa/floating-degraded.tiff
/tmp/emke-interface-floating-qa/floating-stopping.tiff
```

Assert dashboard/settings images are 840 × 1240 pixels and capsule images are
528 × 104 pixels before writing.

Create the output directory and render Settings without reading real
UserDefaults or Keychain:

```swift
@MainActor
private final class RenderSettingsStore: AppSettingsStoring {
    var value = AppSettings.default
    func load() -> AppSettings { value }
    func save(_ settings: AppSettings) { value = settings }
}

@MainActor
private func settingsBitmap(
    language: AppInterfaceLanguage
) throws -> NSBitmapImageRep {
    let store = RenderSettingsStore()
    store.value.interfaceLanguage = language
    let model = MenuBarModel(
        settingsStore: store,
        deferInitialDeviceReload: true
    )
    let view = TranslationSettingsView(model: model)
        .frame(
            width: EMKEVisualStyle.panelWidth,
            height: EMKEVisualStyle.panelHeight
        )
        .background(EMKEVisualStyle.panelBackground)
    let renderer = ImageRenderer(content: view)
    renderer.scale = EMKEVisualStyle.captureScale
    let data = try #require(renderer.nsImage?.tiffRepresentation)
    return try #require(NSBitmapImageRep(data: data))
}

private func writeCapture(
    _ bitmap: NSBitmapImageRep,
    named name: String
) throws {
    let directory = URL(
        fileURLWithPath: "/tmp/emke-interface-floating-qa",
        isDirectory: true
    )
    try FileManager.default.createDirectory(
        at: directory,
        withIntermediateDirectories: true
    )
    let data = try #require(bitmap.representation(using: .tiff, properties: [:]))
    try data.write(to: directory.appendingPathComponent(name))
}
```

Use the pure fixtures from Tasks 3 and 6 for the dashboard and four capsule
states. Tests must not start a coordinator, enumerate Core Audio devices, or
load a Keychain value while generating images.

- [ ] **Step 2: Run the complete automated verification**

Run:

```bash
swift test
swift build -c release --product EMKEMenuBarApp
make -C Driver/EMKEAudioDriver clean all verify-strict
git diff --check
```

Expected:

- all Swift tests pass;
- Release build completes;
- strict driver verification passes;
- no whitespace errors are reported.

- [ ] **Step 3: Generate visual artifacts**

Run:

```bash
EMKE_CAPTURE_UI=1 swift test
```

Inspect every artifact at 1:1 scale. Confirm:

- English copy is not truncated;
- Chinese layout retains the approved Pass 6 geometry;
- the quit action has a visible boundary and power icon;
- the capsule matches approved direction A;
- the four floating states differ by text and tone, not color alone.

- [ ] **Step 4: Record manual-runtime boundaries**

Write `docs/visual-qa/interface-language-floating-window/README.md` with this
checklist and mark each item only `Passed`, `Failed`, or `Not verified`:

```markdown
- Panel opens at bottom center after Start:
- Panel can be dragged without stealing keyboard focus:
- Panel remains visible across Spaces:
- Panel remains visible over a full-screen meeting app:
- Real waveform continues after menu popover closes:
- Stop button stops the session and hides the panel:
- Follow System / 中文 / English persist after relaunch:
- Installed internal package includes and runs the feature:
```

Do not infer manual results from static renders or source inspection.

- [ ] **Step 5: Review the final diff for scope**

Run:

```bash
git status --short
git diff --stat 514ac2d...HEAD
git diff 514ac2d...HEAD -- \
  Sources/EMKEMenuBarApp \
  Tests/EMKEAudioEngineTests \
  docs/visual-qa/interface-language-floating-window
```

Confirm there are no edits to Provider endpoints, Keychain storage,
translation routing, audio conversion, audio drivers, or the unrelated dirty
`codex/internal-pkg-installer` worktree.

- [ ] **Step 6: Commit verification evidence**

```bash
git add Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift \
  Tests/EMKEAudioEngineTests/FloatingTranslationRenderTests.swift \
  docs/visual-qa/interface-language-floating-window/README.md
git commit -m "test: verify interface language and floating window"
```

- [ ] **Step 7: Run the final clean-tree gate**

Run:

```bash
swift test
swift build -c release --product EMKEMenuBarApp
git diff --check 514ac2d...HEAD
git status --short --branch
```

Expected: tests and Release build pass, `git diff --check` is silent, and the
feature worktree is clean on `codex/interface-language-floating-window`.
