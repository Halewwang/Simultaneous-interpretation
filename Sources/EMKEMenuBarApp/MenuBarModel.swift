import Combine
import CoreAudio
import EMKEAudioEngine
import EMKECoordinator
import EMKECore
import EMKESecurity
import Foundation

protocol TranslationCoordinatorControlling: Sendable {
    func start(
        configuration: TranslationCoordinatorConfiguration
    ) async throws
    func stop() async
    func nextEvent() async -> TranslationCoordinatorEvent
    func currentState() async -> TranslationCoordinatorState
    func setInboundBypass(_ enabled: Bool) async
    func setOutboundBypass(_ enabled: Bool) async
    func setAudioLevelUpdatesEnabled(_ enabled: Bool) async
}

extension TranslationCoordinator: TranslationCoordinatorControlling {}

protocol TranslationConnectionProbing: Sendable {
    func run(
        configuration: TranslationConnectionProbeConfiguration,
        speechSample: Data?
    ) async -> TranslationCompatibilityReport
}

extension TranslationConnectionProbe: TranslationConnectionProbing {}

protocol AudioDiagnosticsControlling: Sendable {
    func startInput(deviceID: AudioObjectID) async throws
    func sampleInput() async -> AudioInputDiagnosticSample
    func stopInput() async
    func startOutputTest(
        deviceID: AudioObjectID
    ) async throws -> AudioOutputDiagnosticResult
    func stopOutputTest() async
}

extension LocalAudioDiagnostics: AudioDiagnosticsControlling {}

enum MenuBarReadiness: Equatable {
    case driverUnavailable
    case selectPhysicalInput
    case selectPhysicalOutput
    case invalidBaseURL
    case modelRequired
    case apiKeyRequired
    case ready
    case active
    case error
}

enum MenuBarScreen: Equatable {
    case dashboard
    case settings
}

enum MenuBarChannel {
    case inbound
    case outbound
}

struct TranslationDashboardPresentation: Equatable {
    let primaryStatus: String
    let primaryStatusSymbol: String
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
        errorText: String?
    ) -> TranslationDashboardPresentation {
        let running = coordinatorState.isRunning
        let effectiveInboundState: TranslationChannelState =
            isStarting && !running ? .connecting : coordinatorState.inbound
        let effectiveOutboundState: TranslationChannelState =
            isStarting && !running ? .connecting : coordinatorState.outbound
        let inbound = TranslationChannelPresentation.make(
            channel: .inbound,
            state: effectiveInboundState,
            bypassEnabled: inboundBypassEnabled
        )
        let usesAutomaticOutboundBypass = running
            && motherLanguage == meetingOutputLanguage
            && effectiveOutboundState == .bypassed
        let outbound = TranslationChannelPresentation.make(
            channel: .outbound,
            state: effectiveOutboundState,
            bypassEnabled: outboundBypassEnabled,
            automaticBypass: usesAutomaticOutboundBypass
        )
        let hasChannelFailure: Bool
        if case .failed = effectiveInboundState {
            hasChannelFailure = true
        } else if case .failed = effectiveOutboundState {
            hasChannelFailure = true
        } else {
            hasChannelFailure = false
        }
        let safeInboundLevel = min(max(inboundLevel, 0), 1)
        let safeOutboundLevel: Double
        if case .failed = effectiveOutboundState {
            safeOutboundLevel = 0
        } else {
            safeOutboundLevel = min(max(outboundLevel, 0), 1)
        }
        let action: (title: String, enabled: Bool)
        if isStopping {
            action = ("正在停止…", false)
        } else if running {
            action = ("停止翻译", true)
        } else if isStarting {
            action = ("正在连接…", false)
        } else {
            action = ("开始翻译", readiness == .ready)
        }

        return TranslationDashboardPresentation(
            primaryStatus: primaryStatus(
                readiness: readiness,
                isStarting: isStarting,
                isStopping: isStopping,
                isRunning: running,
                inboundState: effectiveInboundState,
                outboundState: effectiveOutboundState,
                translationStartedAt: translationStartedAt,
                now: now
            ),
            primaryStatusSymbol: primaryStatusSymbol(
                readiness: readiness,
                isStarting: isStarting,
                isStopping: isStopping,
                isRunning: running,
                hasFailure: hasChannelFailure || errorText != nil
            ),
            primaryLevel: max(safeInboundLevel, safeOutboundLevel),
            inboundLevel: safeInboundLevel,
            outboundLevel: safeOutboundLevel,
            primaryActionTitle: action.title,
            primaryActionEnabled: action.enabled,
            inputLanguageName: motherLanguage.displayName,
            outputLanguageName: meetingOutputLanguage.displayName,
            inboundDirection: "其他语言 → \(motherLanguage.displayName)",
            outboundDirection:
                "\(motherLanguage.displayName) → "
                + meetingOutputLanguage.displayName,
            inbound: inbound,
            outbound: outbound,
            privacyText: "音频直连你的服务商",
            errorText: errorText
        )
    }

    private static func primaryStatus(
        readiness: MenuBarReadiness,
        isStarting: Bool,
        isStopping: Bool,
        isRunning: Bool,
        inboundState: TranslationChannelState,
        outboundState: TranslationChannelState,
        translationStartedAt: Date?,
        now: Date
    ) -> String {
        if isStopping { return "正在停止" }
        if isStarting { return "正在连接" }
        if isRunning {
            if case .failed = outboundState { return "出站已静音" }
            if case .failed = inboundState { return "入站播放原音" }
            let elapsed = translationStartedAt.map {
                MenuBarModel.formatElapsed(
                    seconds: now.timeIntervalSince($0)
                )
            } ?? "00:00"
            return "翻译中 · \(elapsed)"
        }
        return switch readiness {
        case .driverUnavailable: "未检测到 EMKE 虚拟音频驱动"
        case .selectPhysicalInput: "请选择真实麦克风"
        case .selectPhysicalOutput: "请选择真实耳机或扬声器"
        case .invalidBaseURL: "请输入安全有效的 Base URL"
        case .modelRequired: "请输入模型名称"
        case .apiKeyRequired: "请输入 API Key"
        case .ready: "准备开始"
        case .active: "翻译中 · 00:00"
        case .error: "配置或连接不可用"
        }
    }

    private static func primaryStatusSymbol(
        readiness: MenuBarReadiness,
        isStarting: Bool,
        isStopping: Bool,
        isRunning: Bool,
        hasFailure: Bool
    ) -> String {
        if isStopping { return "stop.circle" }
        if isStarting { return "arrow.triangle.2.circlepath" }
        if hasFailure || readiness == .error {
            return "exclamationmark.triangle"
        }
        if isRunning || readiness == .active { return "waveform.circle" }
        if readiness == .ready { return "checkmark.circle" }
        return "exclamationmark.circle"
    }
}

@MainActor
final class MenuBarModel: ObservableObject {
    private let provider: any AudioDeviceProviding
    private let coordinator: any TranslationCoordinatorControlling
    private let connectionProbe: any TranslationConnectionProbing
    private let secretStore: any SecretStore
    private let settingsStore: any AppSettingsStoring
    private let microphonePermissionProvider:
        any MicrophonePermissionProviding
    private let audioDiagnostics: any AudioDiagnosticsControlling
    private let audioOutputTestDelay: @Sendable () async throws -> Void

    @Published var physicalInputs: [AudioDevice] = []
    @Published var physicalOutputs: [AudioDevice] = []
    @Published var selectedInputUID: String? {
        didSet { persistPublicSettingsIfNeeded() }
    }
    @Published var selectedOutputUID: String? {
        didSet { persistPublicSettingsIfNeeded() }
    }
    @Published var baseURLString = APIConfiguration.default.baseURL.absoluteString {
        didSet { persistPublicSettingsIfNeeded() }
    }
    @Published var modelID = APIConfiguration.default.modelID {
        didSet { persistPublicSettingsIfNeeded() }
    }
    @Published var motherLanguage: SupportedLanguage = .chinese {
        didSet { persistPublicSettingsIfNeeded() }
    }
    @Published var meetingOutputLanguage: SupportedLanguage = .german {
        didSet { persistPublicSettingsIfNeeded() }
    }
    @Published var apiKeyDraft = ""
    @Published private(set) var coordinatorState = TranslationCoordinatorState()
    @Published private(set) var compatibilityReport: TranslationCompatibilityReport?
    @Published private(set) var connectionTestMessage = ""
    @Published private(set) var inventoryError: String?
    @Published private(set) var configurationError: String?
    @Published private(set) var isTestingConnection = false
    @Published private(set) var isStarting = false
    @Published private(set) var isStopping = false
    @Published private(set) var inboundBypassEnabled = false
    @Published private(set) var outboundBypassEnabled = false
    @Published private(set) var screen: MenuBarScreen = .dashboard
    @Published private(set) var inboundLevel = 0.0
    @Published private(set) var outboundLevel = 0.0
    @Published private(set) var translationStartedAt: Date?
    @Published private(set) var isWindowVisible = false
    @Published private(set) var isTestingAudioInput = false
    @Published private(set) var isPlayingAudioOutputTest = false
    @Published private(set) var audioInputDiagnosticLevel = 0.0
    @Published private(set) var audioInputDiagnosticText = "未测试"
    @Published private(set) var audioOutputDiagnosticText = "未测试"
    @Published private(set) var audioDiagnosticError: String?

    private var driverAvailable = false
    private var hasStoredAPIKey = false
    private var eventTask: Task<Void, Never>?
    private var audioInputDiagnosticTask: Task<Void, Never>?
    private var isApplyingSettings = false
    private var lastPersistedPublicSettings: AppSettings?

    init(
        provider: any AudioDeviceProviding = CoreAudioDeviceProvider(),
        coordinator: any TranslationCoordinatorControlling =
            TranslationCoordinator(),
        connectionProbe: any TranslationConnectionProbing =
            TranslationConnectionProbe(),
        secretStore: any SecretStore = KeychainSecretStore(),
        settingsStore: any AppSettingsStoring =
            UserDefaultsAppSettingsStore(),
        microphonePermissionProvider: any MicrophonePermissionProviding =
            SystemMicrophonePermissionProvider(),
        audioDiagnostics: any AudioDiagnosticsControlling =
            LocalAudioDiagnostics(),
        audioOutputTestDelay: @escaping @Sendable () async throws -> Void = {
            try await Task.sleep(for: .milliseconds(450))
        }
    ) {
        self.provider = provider
        self.coordinator = coordinator
        self.connectionProbe = connectionProbe
        self.secretStore = secretStore
        self.settingsStore = settingsStore
        self.microphonePermissionProvider = microphonePermissionProvider
        self.audioDiagnostics = audioDiagnostics
        self.audioOutputTestDelay = audioOutputTestDelay
        apply(settingsStore.load())
        reloadDevices()
    }

    var readiness: MenuBarReadiness {
        guard driverAvailable else { return .driverUnavailable }
        if coordinatorState.isRunning || isStarting { return .active }
        guard let selectedInputUID,
              physicalInputs.contains(where: { $0.uid == selectedInputUID })
        else {
            return .selectPhysicalInput
        }
        guard let selectedOutputUID,
              physicalOutputs.contains(where: { $0.uid == selectedOutputUID })
        else {
            return .selectPhysicalOutput
        }
        guard validatedBaseURL != nil else { return .invalidBaseURL }
        guard !trimmedModelID.isEmpty else { return .modelRequired }
        guard hasStoredAPIKey || !trimmedDraftKey.isEmpty else {
            return .apiKeyRequired
        }
        if configurationError != nil { return .error }
        return .ready
    }

    var canStart: Bool {
        readiness == .ready
    }

    var canTestConnection: Bool {
        !coordinatorState.isRunning
            && !isStarting
            && !isTestingConnection
            && validatedBaseURL != nil
            && !trimmedModelID.isEmpty
            && (hasStoredAPIKey || !trimmedDraftKey.isEmpty)
    }

    var selectionsLocked: Bool {
        coordinatorState.isRunning || isStarting
    }

    var audioDeviceControlsLocked: Bool {
        selectionsLocked || isTestingAudioInput || isPlayingAudioOutputTest
    }

    var canTestAudioInput: Bool {
        !audioDeviceControlsLocked && selectedPhysicalInput != nil
    }

    var canTestAudioOutput: Bool {
        !audioDeviceControlsLocked && selectedPhysicalOutput != nil
    }

    var combinedLevel: Double {
        max(inboundLevel, outboundLevel)
    }

    var apiKeyStatusText: String {
        hasStoredAPIKey ? "已存入 Keychain" : "尚未保存"
    }

    var repairMessage: String? {
        readiness == .driverUnavailable
            ? "未检测到 EMKE 虚拟音频驱动"
            : nil
    }

    var statusText: String {
        if isStarting { return "正在启动同声传译" }
        if coordinatorState.isRunning { return "同声传译运行中" }
        switch readiness {
        case .driverUnavailable:
            return repairMessage ?? "虚拟音频驱动不可用"
        case .selectPhysicalInput:
            return "请选择真实麦克风"
        case .selectPhysicalOutput:
            return "请选择真实耳机或扬声器"
        case .invalidBaseURL:
            return "请输入安全有效的 Base URL"
        case .modelRequired:
            return "请输入模型名称"
        case .apiKeyRequired:
            return "请输入 API Key"
        case .ready:
            return "同声传译准备就绪"
        case .active:
            return "同声传译运行中"
        case .error:
            return "配置或连接不可用"
        }
    }

    var systemImage: String {
        if coordinatorState.isRunning { return "waveform.circle.fill" }
        if isStarting { return "arrow.triangle.2.circlepath.circle" }
        if readiness == .error { return "exclamationmark.triangle.fill" }
        return driverAvailable ? "waveform.circle" : "speaker.slash.fill"
    }

    var inboundStatusText: String {
        Self.text(for: coordinatorState.inbound, channel: .inbound)
    }

    var outboundStatusText: String {
        Self.text(for: coordinatorState.outbound, channel: .outbound)
    }

    func showSettings() {
        screen = .settings
    }

    func showDashboard() {
        screen = .dashboard
    }

    nonisolated static func formatElapsed(seconds: TimeInterval) -> String {
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

    func dashboardStatusText(at now: Date) -> String {
        if isStarting { return "正在连接" }
        if coordinatorState.isRunning {
            return "翻译中 · \(elapsedText(at: now))"
        }
        return readiness == .ready ? "准备开始" : statusText
    }

    func dashboardPresentation(
        at now: Date
    ) -> TranslationDashboardPresentation {
        TranslationDashboardPresentation.make(
            readiness: readiness,
            coordinatorState: coordinatorState,
            isStarting: isStarting,
            isStopping: isStopping,
            inboundBypassEnabled: inboundBypassEnabled,
            outboundBypassEnabled: outboundBypassEnabled,
            inboundLevel: inboundLevel,
            outboundLevel: outboundLevel,
            translationStartedAt: translationStartedAt,
            motherLanguage: motherLanguage,
            meetingOutputLanguage: meetingOutputLanguage,
            now: now,
            errorText: configurationError ?? inventoryError
        )
    }

    func setWindowVisible(_ visible: Bool) async {
        isWindowVisible = visible
        await coordinator.setAudioLevelUpdatesEnabled(visible)
        if !visible {
            await stopAudioInputTest()
            inboundLevel = 0
            outboundLevel = 0
        }
    }

    func loadConfiguration() async {
        apply(settingsStore.load())
        do {
            hasStoredAPIKey = try await secretStore.loadAPIKey()
                .map { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
                ?? false
            configurationError = nil
        } catch {
            hasStoredAPIKey = false
            configurationError = "无法读取 Keychain：\(error)"
        }
    }

    func reloadDevices() {
        do {
            let devices = try provider.devices()
            let uids = Set(devices.map(\.uid))
            driverAvailable = uids.contains(AudioDevice.virtualSpeakerUID)
                && uids.contains(AudioDevice.virtualMicrophoneUID)
            let catalog = AudioDeviceCatalog(provider: provider)
            physicalInputs = try catalog.physicalInputs()
            physicalOutputs = try catalog.physicalOutputs()
            if let selectedInputUID,
               !physicalInputs.contains(where: { $0.uid == selectedInputUID }) {
                let defaultUID = try provider.defaultInputDeviceUID()
                self.selectedInputUID = physicalInputs.first(where: {
                    $0.uid == defaultUID
                })?.uid
            }
            if let selectedOutputUID,
               !physicalOutputs.contains(where: { $0.uid == selectedOutputUID }) {
                let defaultUID = try provider.defaultOutputDeviceUID()
                self.selectedOutputUID = physicalOutputs.first(where: {
                    $0.uid == defaultUID
                })?.uid
            }
            inventoryError = nil
        } catch {
            driverAvailable = false
            physicalInputs = []
            physicalOutputs = []
            inventoryError = String(describing: error)
        }
    }

    func start() async {
        await stopAudioInputTest()
        reloadDevices()
        guard canStart,
              let selectedInputUID,
              let selectedOutputUID else { return }
        isStarting = true
        defer { isStarting = false }
        configurationError = nil
        do {
            try await requireMicrophonePermission()
            try await persistDraftKeyIfNeeded()
            guard let apiKey = try await nonemptyStoredAPIKey() else {
                hasStoredAPIKey = false
                throw MenuBarConfigurationError.apiKeyRequired
            }
            let apiConfiguration = try makeAPIConfiguration()
            let selection = try AudioDeviceCatalog(provider: provider).resolve(
                physicalInputUID: selectedInputUID,
                physicalOutputUID: selectedOutputUID
            )
            try await coordinator.start(
                configuration: TranslationCoordinatorConfiguration(
                    apiConfiguration: apiConfiguration,
                    preferences: currentPreferences,
                    audioConfiguration: AudioEngineConfiguration(
                        selection: selection
                    ),
                    apiKey: apiKey
                )
            )
            coordinatorState = await coordinator.currentState()
            if coordinatorState.isRunning {
                translationStartedAt = Date()
            } else {
                resetRuntimePresentation()
            }
            startObservingCoordinator()
        } catch {
            configurationError = String(describing: error)
            coordinatorState = TranslationCoordinatorState()
            resetRuntimePresentation()
        }
    }

    func stop() async {
        isStopping = true
        defer { isStopping = false }
        await coordinator.stop()
        eventTask?.cancel()
        eventTask = nil
        coordinatorState = TranslationCoordinatorState()
        resetRuntimePresentation()
    }

    func startAudioInputTest() async {
        guard canTestAudioInput, let device = selectedPhysicalInput else { return }
        audioDiagnosticError = nil
        do {
            try await requireMicrophonePermission()
            try await audioDiagnostics.startInput(deviceID: device.id)
            isTestingAudioInput = true
            await refreshAudioInputDiagnostic()
            audioInputDiagnosticTask = Task { @MainActor [weak self] in
                while !Task.isCancelled {
                    do {
                        try await Task.sleep(for: .milliseconds(50))
                    } catch {
                        return
                    }
                    guard let self else { return }
                    await self.refreshAudioInputDiagnostic()
                }
            }
        } catch {
            await audioDiagnostics.stopInput()
            isTestingAudioInput = false
            audioInputDiagnosticLevel = 0
            audioInputDiagnosticText = "麦克风测试失败"
            audioDiagnosticError = String(describing: error)
        }
    }

    func stopAudioInputTest() async {
        await stopAudioInputTest(resetStatus: true)
    }

    func playAudioOutputTest() async {
        guard canTestAudioOutput, let device = selectedPhysicalOutput else { return }
        isPlayingAudioOutputTest = true
        audioDiagnosticError = nil
        audioOutputDiagnosticText = "正在播放测试音…"
        do {
            let result = try await audioDiagnostics.startOutputTest(
                deviceID: device.id
            )
            guard result.writtenFrames == result.requestedFrames else {
                throw AudioDiagnosticPresentationError.outputBackpressure
            }
            try await audioOutputTestDelay()
            await audioDiagnostics.stopOutputTest()
            audioOutputDiagnosticText = "测试音已播放"
        } catch {
            await audioDiagnostics.stopOutputTest()
            audioOutputDiagnosticText = "扬声器测试失败"
            audioDiagnosticError = String(describing: error)
        }
        isPlayingAudioOutputTest = false
    }

    func testConnection() async {
        guard !isTestingConnection else { return }
        isTestingConnection = true
        compatibilityReport = nil
        connectionTestMessage = "正在测试 Translation 协议"
        configurationError = nil
        defer { isTestingConnection = false }

        do {
            try await persistDraftKeyIfNeeded()
            guard let apiKey = try await nonemptyStoredAPIKey() else {
                hasStoredAPIKey = false
                throw MenuBarConfigurationError.apiKeyRequired
            }
            let apiConfiguration = try makeAPIConfiguration()
            let report = await connectionProbe.run(
                configuration: TranslationConnectionProbeConfiguration(
                    apiConfiguration: apiConfiguration,
                    apiKey: apiKey,
                    inboundTargetLanguage: motherLanguage,
                    outboundTargetLanguage: meetingOutputLanguage
                ),
                speechSample: nil
            )
            compatibilityReport = report
            connectionTestMessage = Self.message(for: report)
        } catch {
            configurationError = String(describing: error)
            connectionTestMessage = "连接测试失败：\(error)"
        }
    }

    func setInboundBypass(_ enabled: Bool) async {
        await coordinator.setInboundBypass(enabled)
        inboundBypassEnabled = enabled
        coordinatorState = await coordinator.currentState()
    }

    func setOutboundBypass(_ enabled: Bool) async {
        guard !usesAutomaticOutboundBypass else { return }
        await coordinator.setOutboundBypass(enabled)
        outboundBypassEnabled = enabled
        coordinatorState = await coordinator.currentState()
    }

    private var currentPreferences: TranslationPreferences {
        TranslationPreferences(
            motherLanguage: motherLanguage,
            meetingOutputLanguage: meetingOutputLanguage
        )
    }

    private var trimmedModelID: String {
        modelID.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var trimmedDraftKey: String {
        apiKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var validatedBaseURL: URL? {
        let value = baseURLString.trimmingCharacters(
            in: .whitespacesAndNewlines
        )
        guard let url = URL(string: value),
              let scheme = url.scheme?.lowercased(),
              scheme == "https" || scheme == "wss",
              url.host?.isEmpty == false else { return nil }
        return url
    }

    private func makeAPIConfiguration() throws -> APIConfiguration {
        guard let baseURL = validatedBaseURL else {
            throw MenuBarConfigurationError.invalidBaseURL
        }
        guard !trimmedModelID.isEmpty else {
            throw MenuBarConfigurationError.modelRequired
        }
        return APIConfiguration(baseURL: baseURL, modelID: trimmedModelID)
    }

    private func requireMicrophonePermission() async throws {
        switch await microphonePermissionProvider.authorizationStatus() {
        case .authorized:
            return
        case .notDetermined:
            guard await microphonePermissionProvider.requestAccess() else {
                throw MenuBarConfigurationError.microphoneAccessDenied
            }
        case .denied:
            throw MenuBarConfigurationError.microphoneAccessDenied
        case .restricted:
            throw MenuBarConfigurationError.microphoneAccessRestricted
        }
    }

    private var selectedPhysicalInput: AudioDevice? {
        guard let selectedInputUID else { return nil }
        return physicalInputs.first { $0.uid == selectedInputUID }
    }

    private var selectedPhysicalOutput: AudioDevice? {
        guard let selectedOutputUID else { return nil }
        return physicalOutputs.first { $0.uid == selectedOutputUID }
    }

    private func refreshAudioInputDiagnostic() async {
        let sample = await audioDiagnostics.sampleInput()
        audioInputDiagnosticLevel = sample.level
        audioInputDiagnosticText = switch sample.state {
        case .stopped:
            "未测试"
        case .waitingForFrames:
            "未收到音频帧"
        case .receivingSilence:
            "设备已连接，等待声音"
        case .receivingAudio:
            "已检测到麦克风输入"
        }
    }

    private func stopAudioInputTest(resetStatus: Bool) async {
        audioInputDiagnosticTask?.cancel()
        audioInputDiagnosticTask = nil
        await audioDiagnostics.stopInput()
        isTestingAudioInput = false
        audioInputDiagnosticLevel = 0
        if resetStatus {
            audioInputDiagnosticText = "未测试"
        }
    }

    private func persistDraftKeyIfNeeded() async throws {
        guard !trimmedDraftKey.isEmpty else { return }
        try await secretStore.saveAPIKey(trimmedDraftKey)
        hasStoredAPIKey = true
        apiKeyDraft = ""
    }

    private func nonemptyStoredAPIKey() async throws -> String? {
        guard let value = try await secretStore.loadAPIKey() else {
            return nil
        }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    private var usesAutomaticOutboundBypass: Bool {
        coordinatorState.isRunning
            && motherLanguage == meetingOutputLanguage
            && coordinatorState.outbound == .bypassed
    }

    private var currentPublicSettings: AppSettings {
        AppSettings(
            baseURLString: baseURLString,
            modelID: modelID,
            preferences: currentPreferences,
            selectedInputUID: selectedInputUID,
            selectedOutputUID: selectedOutputUID
        )
    }

    private func persistPublicSettingsIfNeeded() {
        guard !isApplyingSettings else { return }
        let settings = currentPublicSettings
        guard settings != lastPersistedPublicSettings else { return }
        settingsStore.save(settings)
        lastPersistedPublicSettings = settings
    }

    private func apply(_ settings: AppSettings) {
        isApplyingSettings = true
        defer {
            lastPersistedPublicSettings = settings
            isApplyingSettings = false
        }
        baseURLString = settings.baseURLString
        modelID = settings.modelID
        motherLanguage = settings.preferences.motherLanguage
        meetingOutputLanguage = settings.preferences.meetingOutputLanguage
        selectedInputUID = settings.selectedInputUID
        selectedOutputUID = settings.selectedOutputUID
    }

    private func startObservingCoordinator() {
        eventTask?.cancel()
        eventTask = Task { [weak self] in
            guard let self else { return }
            while !Task.isCancelled {
                let event = await coordinator.nextEvent()
                if Task.isCancelled { return }
                switch event {
                case .stateChanged(let state):
                    coordinatorState = state
                case .audioLevels(let levels):
                    if isWindowVisible {
                        inboundLevel = levels.inbound
                        outboundLevel = levels.outbound
                    }
                case .audioBackpressure(let droppedFrames):
                    inventoryError = "音频输出繁忙，已丢弃 \(droppedFrames) 帧"
                case .stopped:
                    coordinatorState = TranslationCoordinatorState()
                    resetRuntimePresentation()
                    return
                }
            }
        }
    }

    private func resetRuntimePresentation() {
        inboundLevel = 0
        outboundLevel = 0
        translationStartedAt = nil
        inboundBypassEnabled = false
        outboundBypassEnabled = false
        isStopping = false
    }

    private static func message(
        for report: TranslationCompatibilityReport
    ) -> String {
        if report.isFullyCompatible {
            return "Translation 协议与音频能力均兼容"
        }
        let statuses = [
            report.authentication,
            report.handshake,
            report.targetLanguage,
            report.dualSession,
            report.sourceTranscript,
            report.audioOutput,
            report.gracefulClose,
        ]
        let hasFailure = statuses.contains { status in
            if case .failed = status { return true }
            return false
        }
        let needsAudio = statuses.contains(.requiresInteractiveAudio)
        if !hasFailure && needsAudio {
            return "Translation 协议连接通过，需要音频测试"
        }
        return "Translation 协议不兼容"
    }

    static func text(
        for state: TranslationChannelState,
        channel: MenuBarChannel
    ) -> String {
        switch state {
        case .stopped: "已停止"
        case .connecting: "连接中"
        case .active: "翻译中"
        case .bypassed: "原音旁路"
        case .reconnecting(let attempt): "重连中（第 \(attempt) 次）"
        case .failed:
            channel == .inbound ? "播放原音" : "已静音"
        }
    }
}

private enum MenuBarConfigurationError: Error, CustomStringConvertible {
    case invalidBaseURL
    case modelRequired
    case apiKeyRequired
    case microphoneAccessDenied
    case microphoneAccessRestricted

    var description: String {
        switch self {
        case .invalidBaseURL: "Base URL 必须是有效的 HTTPS 或 WSS 地址"
        case .modelRequired: "模型名称不能为空"
        case .apiKeyRequired: "API Key 未写入 Keychain"
        case .microphoneAccessDenied:
            "麦克风权限未开启，请在系统设置的隐私与安全性中允许 EMKE Translation"
        case .microphoneAccessRestricted:
            "当前系统策略限制了麦克风访问"
        }
    }
}

private enum AudioDiagnosticPresentationError: Error, CustomStringConvertible {
    case outputBackpressure

    var description: String {
        switch self {
        case .outputBackpressure:
            "测试音未完整写入所选输出设备"
        }
    }
}
