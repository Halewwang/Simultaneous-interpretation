import Combine
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
}

extension TranslationCoordinator: TranslationCoordinatorControlling {}

protocol TranslationConnectionProbing: Sendable {
    func run(
        configuration: TranslationConnectionProbeConfiguration,
        speechSample: Data?
    ) async -> TranslationCompatibilityReport
}

extension TranslationConnectionProbe: TranslationConnectionProbing {}

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

@MainActor
final class MenuBarModel: ObservableObject {
    private let provider: any AudioDeviceProviding
    private let coordinator: any TranslationCoordinatorControlling
    private let connectionProbe: any TranslationConnectionProbing
    private let secretStore: any SecretStore
    private let settingsStore: any AppSettingsStoring

    @Published var physicalInputs: [AudioDevice] = []
    @Published var physicalOutputs: [AudioDevice] = []
    @Published var selectedInputUID: String?
    @Published var selectedOutputUID: String?
    @Published var baseURLString = APIConfiguration.default.baseURL.absoluteString
    @Published var modelID = APIConfiguration.default.modelID
    @Published var motherLanguage: SupportedLanguage = .chinese
    @Published var meetingOutputLanguage: SupportedLanguage = .german
    @Published var apiKeyDraft = ""
    @Published private(set) var coordinatorState = TranslationCoordinatorState()
    @Published private(set) var compatibilityReport: TranslationCompatibilityReport?
    @Published private(set) var connectionTestMessage = ""
    @Published private(set) var inventoryError: String?
    @Published private(set) var configurationError: String?
    @Published private(set) var isTestingConnection = false
    @Published private(set) var isStarting = false
    @Published private(set) var inboundBypassEnabled = false
    @Published private(set) var outboundBypassEnabled = false

    private var driverAvailable = false
    private var hasStoredAPIKey = false
    private var eventTask: Task<Void, Never>?

    init(
        provider: any AudioDeviceProviding = CoreAudioDeviceProvider(),
        coordinator: any TranslationCoordinatorControlling =
            TranslationCoordinator(),
        connectionProbe: any TranslationConnectionProbing =
            TranslationConnectionProbe(),
        secretStore: any SecretStore = KeychainSecretStore(),
        settingsStore: any AppSettingsStoring =
            UserDefaultsAppSettingsStore()
    ) {
        self.provider = provider
        self.coordinator = coordinator
        self.connectionProbe = connectionProbe
        self.secretStore = secretStore
        self.settingsStore = settingsStore
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
        Self.text(for: coordinatorState.inbound)
    }

    var outboundStatusText: String {
        Self.text(for: coordinatorState.outbound)
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
                self.selectedInputUID = nil
            }
            if let selectedOutputUID,
               !physicalOutputs.contains(where: { $0.uid == selectedOutputUID }) {
                self.selectedOutputUID = nil
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
        guard canStart,
              let selectedInputUID,
              let selectedOutputUID else { return }
        isStarting = true
        configurationError = nil
        do {
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
            savePublicSettings(apiConfiguration: apiConfiguration)
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
            startObservingCoordinator()
        } catch {
            configurationError = String(describing: error)
            coordinatorState = TranslationCoordinatorState()
        }
        isStarting = false
    }

    func stop() async {
        await coordinator.stop()
        eventTask?.cancel()
        eventTask = nil
        coordinatorState = TranslationCoordinatorState()
        inboundBypassEnabled = false
        outboundBypassEnabled = false
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
            savePublicSettings(apiConfiguration: apiConfiguration)
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

    private func savePublicSettings(apiConfiguration: APIConfiguration) {
        settingsStore.save(
            AppSettings(
                apiConfiguration: apiConfiguration,
                preferences: currentPreferences,
                selectedInputUID: selectedInputUID,
                selectedOutputUID: selectedOutputUID
            )
        )
    }

    private func apply(_ settings: AppSettings) {
        baseURLString = settings.apiConfiguration.baseURL.absoluteString
        modelID = settings.apiConfiguration.modelID
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
                case .audioLevels:
                    break
                case .audioBackpressure(let droppedFrames):
                    inventoryError = "音频输出繁忙，已丢弃 \(droppedFrames) 帧"
                case .stopped:
                    coordinatorState = TranslationCoordinatorState()
                    inboundBypassEnabled = false
                    outboundBypassEnabled = false
                    return
                }
            }
        }
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

    private static func text(for state: TranslationChannelState) -> String {
        switch state {
        case .stopped: "已停止"
        case .connecting: "连接中"
        case .active: "翻译中"
        case .bypassed: "原音旁路"
        case .reconnecting(let attempt): "重连中（第 \(attempt) 次）"
        case .failed: "连接失败"
        }
    }
}

private enum MenuBarConfigurationError: Error, CustomStringConvertible {
    case invalidBaseURL
    case modelRequired
    case apiKeyRequired

    var description: String {
        switch self {
        case .invalidBaseURL: "Base URL 必须是有效的 HTTPS 或 WSS 地址"
        case .modelRequired: "模型名称不能为空"
        case .apiKeyRequired: "API Key 未写入 Keychain"
        }
    }
}
