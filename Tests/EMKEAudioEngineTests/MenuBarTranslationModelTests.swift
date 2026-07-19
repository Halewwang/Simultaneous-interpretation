import Combine
import EMKEAudioEngine
import EMKECoordinator
import EMKECore
import EMKESecurity
import Foundation
import Testing
@testable import EMKEMenuBarApp

private struct TranslationMenuDeviceProvider: AudioDeviceProviding {
    let includeDriver: Bool

    init(includeDriver: Bool = true) {
        self.includeDriver = includeDriver
    }

    func devices() throws -> [AudioDevice] {
        let inventory = [
            AudioDevice(
                id: 10,
                uid: AudioDevice.virtualSpeakerUID,
                name: "EMKE Virtual Speaker",
                inputChannelCount: 2,
                outputChannelCount: 2,
                nominalSampleRate: 48_000
            ),
            AudioDevice(
                id: 11,
                uid: AudioDevice.virtualMicrophoneUID,
                name: "EMKE Virtual Microphone",
                inputChannelCount: 2,
                outputChannelCount: 2,
                nominalSampleRate: 48_000
            ),
            AudioDevice(
                id: 20,
                uid: "physical.input",
                name: "Physical Input",
                inputChannelCount: 1,
                outputChannelCount: 0,
                nominalSampleRate: 48_000
            ),
            AudioDevice(
                id: 21,
                uid: "physical.output",
                name: "Physical Output",
                inputChannelCount: 0,
                outputChannelCount: 2,
                nominalSampleRate: 48_000
            ),
        ]
        if includeDriver { return inventory }
        return inventory.filter {
            $0.uid != AudioDevice.virtualSpeakerUID
                && $0.uid != AudioDevice.virtualMicrophoneUID
        }
    }
}

private final class MutableTranslationMenuDeviceProvider:
    AudioDeviceProviding,
    @unchecked Sendable
{
    private let lock = NSLock()
    private var inventory: [AudioDevice]

    init(inventory: [AudioDevice]) {
        self.inventory = inventory
    }

    func devices() throws -> [AudioDevice] {
        lock.withLock { inventory }
    }

    func replaceInventory(_ inventory: [AudioDevice]) {
        lock.withLock { self.inventory = inventory }
    }
}

private actor TranslationCoordinatorStub: TranslationCoordinatorControlling {
    private(set) var configurations: [
        TranslationCoordinatorConfiguration
    ] = []
    private var current = TranslationCoordinatorState()
    private var eventWaiters: [
        CheckedContinuation<TranslationCoordinatorEvent, Never>
    ] = []
    private var queuedEvents: [TranslationCoordinatorEvent] = []
    private(set) var audioLevelUpdateFlags: [Bool] = []
    private(set) var inboundBypassValues: [Bool] = []
    private(set) var outboundBypassValues: [Bool] = []

    func start(
        configuration: TranslationCoordinatorConfiguration
    ) async throws {
        configurations.append(configuration)
        current = TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: configuration.preferences.motherLanguage
                == configuration.preferences.meetingOutputLanguage
                ? .bypassed
                : .active
        )
    }

    func stop() async {
        current = TranslationCoordinatorState()
        let waiters = eventWaiters
        eventWaiters.removeAll()
        for waiter in waiters {
            waiter.resume(returning: .stopped)
        }
    }

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

    func currentState() async -> TranslationCoordinatorState {
        current
    }

    func setInboundBypass(_ enabled: Bool) async {
        inboundBypassValues.append(enabled)
    }

    func setOutboundBypass(_ enabled: Bool) async {
        outboundBypassValues.append(enabled)
    }
    func setAudioLevelUpdatesEnabled(_ enabled: Bool) async {
        audioLevelUpdateFlags.append(enabled)
    }
}

private struct TranslationProbeStub: TranslationConnectionProbing {
    let report: TranslationCompatibilityReport

    func run(
        configuration: TranslationConnectionProbeConfiguration,
        speechSample: Data?
    ) async -> TranslationCompatibilityReport {
        report
    }
}

private actor TranslationSecretStoreStub: SecretStore {
    private var value: String?

    init(value: String? = nil) {
        self.value = value
    }

    func saveAPIKey(_ value: String) async throws {
        self.value = value
    }

    func loadAPIKey() async throws -> String? {
        value
    }

    func deleteAPIKey() async throws {
        value = nil
    }
}

@MainActor
private final class TranslationSettingsStoreStub: AppSettingsStoring {
    var value: AppSettings
    private(set) var saved: [AppSettings] = []

    init(value: AppSettings = .default) {
        self.value = value
    }

    func load() -> AppSettings {
        value
    }

    func save(_ settings: AppSettings) {
        value = settings
        saved.append(settings)
    }
}

private let protocolOnlyReport = TranslationCompatibilityReport(
    authentication: .passed,
    handshake: .passed,
    targetLanguage: .passed,
    dualSession: .passed,
    sourceTranscript: .requiresInteractiveAudio,
    audioOutput: .requiresInteractiveAudio,
    gracefulClose: .passed
)

@MainActor
private func makeTranslationMenuModel(
    secret: String? = nil,
    coordinator: TranslationCoordinatorStub = TranslationCoordinatorStub(),
    settings: TranslationSettingsStoreStub = TranslationSettingsStoreStub(),
    provider: TranslationMenuDeviceProvider = TranslationMenuDeviceProvider()
) -> MenuBarModel {
    MenuBarModel(
        provider: provider,
        coordinator: coordinator,
        connectionProbe: TranslationProbeStub(
            report: protocolOnlyReport
        ),
        secretStore: TranslationSecretStoreStub(value: secret),
        settingsStore: settings
    )
}

@MainActor
private func configureAndStart(_ model: MenuBarModel) async {
    await model.loadConfiguration()
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"
    model.baseURLString = "https://gateway.example/v1"
    model.modelID = "translation-model"
    await model.start()
}

@Test @MainActor
func missingDriverAndPhysicalSelectionsBlockStartInOrder() async {
    let withoutDriver = makeTranslationMenuModel(
        secret: "stored-key",
        provider: TranslationMenuDeviceProvider(includeDriver: false)
    )
    await withoutDriver.loadConfiguration()
    #expect(withoutDriver.readiness == .driverUnavailable)
    #expect(withoutDriver.repairMessage == "未检测到 EMKE 虚拟音频驱动")

    let model = makeTranslationMenuModel(secret: "stored-key")
    await model.loadConfiguration()
    #expect(model.readiness == .selectPhysicalInput)
    model.selectedInputUID = "physical.input"
    #expect(model.readiness == .selectPhysicalOutput)
}

@Test @MainActor
func startRevalidatesAudioDevicesBeforeStartingCoordinator() async throws {
    let initialInventory = try TranslationMenuDeviceProvider().devices()
    let provider = MutableTranslationMenuDeviceProvider(
        inventory: initialInventory
    )
    let coordinator = TranslationCoordinatorStub()
    let model = MenuBarModel(
        provider: provider,
        coordinator: coordinator,
        connectionProbe: TranslationProbeStub(report: protocolOnlyReport),
        secretStore: TranslationSecretStoreStub(value: "stored-key"),
        settingsStore: TranslationSettingsStoreStub()
    )
    await model.loadConfiguration()
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"
    model.baseURLString = "https://gateway.example/v1"
    model.modelID = "translation-model"
    #expect(model.canStart)

    provider.replaceInventory(
        initialInventory.filter { $0.uid != "physical.input" }
    )

    await model.start()

    #expect(model.selectedInputUID == nil)
    #expect(model.readiness == .selectPhysicalInput)
    #expect(await coordinator.configurations.isEmpty)
}

@Test @MainActor
func apiReadinessRequiresKeyBaseURLModelAndDevices() async {
    let model = makeTranslationMenuModel()
    await model.loadConfiguration()
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"

    #expect(model.readiness == .apiKeyRequired)

    model.apiKeyDraft = "replacement-key"
    #expect(model.readiness == .ready)

    model.baseURLString = "http://insecure.example/v1"
    #expect(model.readiness == .invalidBaseURL)

    model.baseURLString = "https://gateway.example/v1"
    model.modelID = "  "
    #expect(model.readiness == .modelRequired)
}

@Test @MainActor
func publicSettingsNeverPersistAPIKey() {
    let suite = "EMKETranslationTests.\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    let store = UserDefaultsAppSettingsStore(defaults: defaults)
    var value = AppSettings.default
    value.baseURLString = "https://gateway.example/v1"
    value.modelID = "translate-model"

    store.save(value)

    #expect(store.load() == value)
    #expect(defaults.object(forKey: "apiKey") == nil)
    #expect(
        defaults.dictionaryRepresentation().keys
            .allSatisfy { !$0.lowercased().contains("apikey") }
    )
}

@Test @MainActor
func publicSettingsAutosaveEveryEditableFieldAndSurviveReopen() async {
    let settings = TranslationSettingsStoreStub()
    var model: MenuBarModel? = makeTranslationMenuModel(
        secret: "keychain-only",
        settings: settings
    )
    await model?.loadConfiguration()
    #expect(settings.saved.isEmpty)

    model?.baseURLString = "https://gateway.example/v2"
    model?.modelID = "translation-v2"
    model?.motherLanguage = .english
    model?.meetingOutputLanguage = .chinese
    model?.selectedInputUID = "physical.input"
    model?.selectedOutputUID = "physical.output"
    model?.apiKeyDraft = "must-not-enter-public-settings"

    #expect(settings.saved.count == 6)
    model = nil

    let reopened = makeTranslationMenuModel(settings: settings)
    await reopened.loadConfiguration()

    #expect(reopened.baseURLString == "https://gateway.example/v2")
    #expect(reopened.modelID == "translation-v2")
    #expect(reopened.motherLanguage == .english)
    #expect(reopened.meetingOutputLanguage == .chinese)
    #expect(reopened.selectedInputUID == "physical.input")
    #expect(reopened.selectedOutputUID == "physical.output")
    #expect(reopened.apiKeyDraft.isEmpty)
    #expect(settings.saved.count == 6)
}

@Test @MainActor
func invalidBaseURLDraftIsPersistedFaithfullyWithoutLossyFallback() async {
    let settings = TranslationSettingsStoreStub()
    var model: MenuBarModel? = makeTranslationMenuModel(settings: settings)
    await model?.loadConfiguration()

    model?.baseURLString = "https://"
    #expect(settings.saved.count == 1)
    model = nil

    let reopened = makeTranslationMenuModel(settings: settings)
    await reopened.loadConfiguration()

    #expect(reopened.baseURLString == "https://")
    #expect(reopened.readiness == .selectPhysicalInput)
    #expect(settings.saved.count == 1)
}

@Test @MainActor
func userDefaultsRoundTripsPublicDraftButNeverAPIKey() async {
    let suite = "EMKETranslationTests.\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    let settings = UserDefaultsAppSettingsStore(defaults: defaults)
    var model: MenuBarModel? = MenuBarModel(
        provider: TranslationMenuDeviceProvider(),
        coordinator: TranslationCoordinatorStub(),
        connectionProbe: TranslationProbeStub(report: protocolOnlyReport),
        secretStore: TranslationSecretStoreStub(),
        settingsStore: settings
    )
    await model?.loadConfiguration()

    model?.baseURLString = "https://"
    model?.modelID = "draft-model"
    model?.motherLanguage = .german
    model?.meetingOutputLanguage = .english
    model?.selectedInputUID = "physical.input"
    model?.selectedOutputUID = "physical.output"
    model?.apiKeyDraft = "keychain-only"
    model = nil

    let reopened = MenuBarModel(
        provider: TranslationMenuDeviceProvider(),
        coordinator: TranslationCoordinatorStub(),
        connectionProbe: TranslationProbeStub(report: protocolOnlyReport),
        secretStore: TranslationSecretStoreStub(),
        settingsStore: settings
    )
    await reopened.loadConfiguration()

    #expect(reopened.baseURLString == "https://")
    #expect(reopened.modelID == "draft-model")
    #expect(reopened.motherLanguage == .german)
    #expect(reopened.meetingOutputLanguage == .english)
    #expect(reopened.selectedInputUID == "physical.input")
    #expect(reopened.selectedOutputUID == "physical.output")
    #expect(reopened.apiKeyDraft.isEmpty)
    #expect(
        defaults.dictionaryRepresentation().keys
            .allSatisfy { !$0.lowercased().contains("apikey") }
    )
}

@Test @MainActor
func startMovesDraftKeyToKeychainAndBuildsCoordinatorConfiguration() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(coordinator: coordinator)
    await model.loadConfiguration()
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"
    model.baseURLString = "https://api.derouter.ai/openai/v1"
    model.modelID = "GPT-5.5"
    model.motherLanguage = .chinese
    model.meetingOutputLanguage = .german
    model.apiKeyDraft = "replacement-key"

    await model.start()

    let configuration = await coordinator.configurations.first
    #expect(
        configuration?.apiConfiguration.baseURL.absoluteString
            == "https://api.derouter.ai/openai/v1"
    )
    #expect(configuration?.apiConfiguration.modelID == "GPT-5.5")
    #expect(configuration?.preferences.motherLanguage == .chinese)
    #expect(configuration?.preferences.meetingOutputLanguage == .german)
    #expect(configuration?.audioConfiguration.selection.physicalInput.id == 20)
    #expect(configuration?.audioConfiguration.selection.physicalOutput.id == 21)
    #expect(model.apiKeyDraft.isEmpty)
    #expect(model.coordinatorState.isRunning)
    await model.stop()
}

@Test @MainActor
func connectionTestPreservesPartialCompatibilityResult() async {
    let model = makeTranslationMenuModel(secret: "stored-key")
    await model.loadConfiguration()

    await model.testConnection()

    #expect(model.compatibilityReport == protocolOnlyReport)
    #expect(model.connectionTestMessage.contains("需要音频测试"))
}

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
    await model.setWindowVisible(true)

    let levelsUpdated = Task { @MainActor in
        for await level in model.$outboundLevel.values {
            if level == 0.75 {
                return
            }
        }
    }
    await coordinator.emit(.audioLevels(
        AudioLevelSnapshot(inbound: 0.25, outbound: 0.75)
    ))
    await levelsUpdated.value

    #expect(model.inboundLevel == 0.25)
    #expect(model.outboundLevel == 0.75)
    #expect(model.combinedLevel == 0.75)
    #expect(await coordinator.audioLevelUpdateFlags.last == true)
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

@Test @MainActor
func hidingWindowClearsLevelsWithoutStoppingTranslation() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(
        secret: "test-key",
        coordinator: coordinator
    )
    await configureAndStart(model)
    await model.setWindowVisible(false)

    #expect(model.inboundLevel == 0)
    #expect(model.outboundLevel == 0)
    #expect(model.coordinatorState.isRunning)
    #expect(await coordinator.audioLevelUpdateFlags.last == false)
}

@Test @MainActor
func lateAudioLevelAfterHidingWindowDoesNotRefillLevels() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(
        secret: "test-key",
        coordinator: coordinator
    )
    await configureAndStart(model)
    await model.setWindowVisible(true)
    await model.setWindowVisible(false)

    let stateUpdated = Task { @MainActor in
        for await state in model.$coordinatorState.values {
            if state.inbound == .connecting {
                return
            }
        }
    }
    await coordinator.emit(.audioLevels(
        AudioLevelSnapshot(inbound: 0.6, outbound: 0.8)
    ))
    await coordinator.emit(.stateChanged(
        TranslationCoordinatorState(
            isRunning: true,
            inbound: .connecting,
            outbound: .active
        )
    ))
    await stateUpdated.value

    #expect(model.inboundLevel == 0)
    #expect(model.outboundLevel == 0)
}

@Test @MainActor
func outboundFailureIsPresentedAsMuted() {
    #expect(
        MenuBarModel.text(
            for: .failed(message: "offline"),
            channel: .outbound
        ) == "已静音"
    )
}

@Test @MainActor
func inboundFailureIsPresentedAsOriginalAudio() {
    #expect(
        MenuBarModel.text(
            for: .failed(message: "offline"),
            channel: .inbound
        ) == "播放原音"
    )
}

@Test @MainActor
func dashboardStatusUsesReadinessAndElapsedRuntime() async throws {
    let model = makeTranslationMenuModel(secret: "test-key")
    await model.loadConfiguration()
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"
    model.baseURLString = "https://gateway.example/v1"
    model.modelID = "translation-model"

    #expect(model.dashboardStatusText(at: Date()) == "准备开始")

    await model.start()
    let startedAt = try #require(model.translationStartedAt)
    #expect(
        model.dashboardStatusText(
            at: startedAt.addingTimeInterval(65)
        ) == "翻译中 · 01:05"
    )
    await model.stop()
}

@Test @MainActor
func apiKeyStatusReflectsKeychainAvailability() async {
    let missingKey = makeTranslationMenuModel()
    await missingKey.loadConfiguration()
    #expect(missingKey.apiKeyStatusText == "尚未保存")

    let storedKey = makeTranslationMenuModel(secret: "test-key")
    await storedKey.loadConfiguration()
    #expect(storedKey.apiKeyStatusText == "已存入 Keychain")
}

@Test @MainActor
func activeManualBypassPresentationTracksModelActionAndRestore() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(
        secret: "test-key",
        coordinator: coordinator
    )
    await configureAndStart(model)

    await model.setInboundBypass(true)
    var value = model.dashboardPresentation(at: Date())
    #expect(await coordinator.inboundBypassValues == [true])
    #expect(model.coordinatorState.inbound == .active)
    #expect(value.inbound.status == "原音旁路")
    #expect(value.inbound.statusSymbol == "speaker.wave.2")
    #expect(value.inbound.actionTitle == "恢复翻译")

    await model.setInboundBypass(false)
    value = model.dashboardPresentation(at: Date())
    #expect(await coordinator.inboundBypassValues == [true, false])
    #expect(value.inbound.status == "稳定")
    #expect(value.inbound.actionTitle == "播放原音")

    await model.setOutboundBypass(true)
    value = model.dashboardPresentation(at: Date())
    #expect(await coordinator.outboundBypassValues == [true])
    #expect(model.coordinatorState.outbound == .active)
    #expect(value.outbound.status == "原音旁路")
    #expect(value.outbound.actionTitle == "恢复翻译")

    await model.setOutboundBypass(false)
    value = model.dashboardPresentation(at: Date())
    #expect(await coordinator.outboundBypassValues == [true, false])
    #expect(value.outbound.status == "稳定")
    #expect(value.outbound.actionTitle == "发送原音")
}

@Test @MainActor
func sameLanguageOutboundDirectPathCannotOfferOrInvokeRestore() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(
        secret: "test-key",
        coordinator: coordinator
    )
    await model.loadConfiguration()
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"
    model.baseURLString = "https://gateway.example/v1"
    model.modelID = "translation-model"
    model.motherLanguage = .english
    model.meetingOutputLanguage = .english
    await model.start()

    let value = model.dashboardPresentation(at: Date())
    #expect(model.coordinatorState.outbound == .bypassed)
    #expect(value.outbound.status == "同语言直通")
    #expect(value.outbound.actionTitle == "无需翻译")
    #expect(!value.outbound.actionEnabled)

    await model.setOutboundBypass(false)
    #expect(await coordinator.outboundBypassValues.isEmpty)
    #expect(model.coordinatorState.outbound == .bypassed)
}

@Test @MainActor
func manualBypassPresentationStaysAlignedAcrossFailureAndRecovery() async {
    let coordinator = TranslationCoordinatorStub()
    let model = makeTranslationMenuModel(
        secret: "test-key",
        coordinator: coordinator
    )
    await configureAndStart(model)
    await model.setOutboundBypass(true)

    let failed = Task { @MainActor in
        for await state in model.$coordinatorState.values {
            if case .failed = state.outbound { return }
        }
    }
    await coordinator.emit(.stateChanged(
        TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .failed(message: "offline")
        )
    ))
    await failed.value
    var value = model.dashboardPresentation(at: Date())
    #expect(value.outbound.status == "已静音")
    #expect(value.primaryStatus == "出站已静音")

    let reconnecting = Task { @MainActor in
        for await state in model.$coordinatorState.values {
            if state.outbound == .reconnecting(attempt: 1) { return }
        }
    }
    await coordinator.emit(.stateChanged(
        TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .reconnecting(attempt: 1)
        )
    ))
    await reconnecting.value

    let recovered = Task { @MainActor in
        for await state in model.$coordinatorState.values {
            if state.outbound == .active { return }
        }
    }
    await coordinator.emit(.stateChanged(
        TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        )
    ))
    await recovered.value

    value = model.dashboardPresentation(at: Date())
    #expect(value.outbound.status == "原音旁路")
    #expect(value.outbound.actionTitle == "恢复翻译")
    #expect(model.outboundBypassEnabled)
}
