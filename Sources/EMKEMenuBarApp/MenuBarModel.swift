import Combine
import CoreAudio
import EMKEAudioEngine
import EMKECoordinator
import EMKECore
import EMKESecurity
import Foundation

private struct DeviceInventorySnapshot: Sendable {
    let driverAvailable: Bool
    let physicalInputs: [AudioDevice]
    let physicalOutputs: [AudioDevice]
    let defaultInputUID: String?
    let defaultOutputUID: String?
}

private enum DeviceInventoryLoadResult: Sendable {
    case success(DeviceInventorySnapshot)
    case failure(AppMessage)
}

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
        errorText: String?,
        copy: AppCopy
    ) -> TranslationDashboardPresentation {
        let running = coordinatorState.isRunning
        let effectiveInboundState: TranslationChannelState =
            isStarting && !running ? .connecting : coordinatorState.inbound
        let effectiveOutboundState: TranslationChannelState =
            isStarting && !running ? .connecting : coordinatorState.outbound
        let inbound = TranslationChannelPresentation.make(
            channel: .inbound,
            state: effectiveInboundState,
            bypassEnabled: inboundBypassEnabled,
            copy: copy
        )
        let usesAutomaticOutboundBypass = running
            && motherLanguage == meetingOutputLanguage
            && effectiveOutboundState == .bypassed
        let outbound = TranslationChannelPresentation.make(
            channel: .outbound,
            state: effectiveOutboundState,
            bypassEnabled: outboundBypassEnabled,
            automaticBypass: usesAutomaticOutboundBypass,
            copy: copy
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
            action = (copy.text(.stopping), false)
        } else if running {
            action = (copy.text(.stopTranslation), true)
        } else if isStarting {
            action = (copy.text(.starting), false)
        } else {
            action = (copy.text(.startTranslation), readiness == .ready)
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
                now: now,
                copy: copy
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
            inputLanguageName: copy.languageName(motherLanguage),
            outputLanguageName: copy.languageName(meetingOutputLanguage),
            inboundDirection: copy.inboundDirection(to: motherLanguage),
            outboundDirection: copy.outboundDirection(
                from: motherLanguage,
                to: meetingOutputLanguage
            ),
            inbound: inbound,
            outbound: outbound,
            privacyText: copy.text(.audioDirectToProvider),
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
        now: Date,
        copy: AppCopy
    ) -> String {
        if isStopping { return copy.text(.stopping) }
        if isStarting { return copy.text(.connecting) }
        if isRunning {
            if case .failed = outboundState {
                return copy.text(.outboundMuted)
            }
            if case .failed = inboundState {
                return copy.text(.inboundOriginal)
            }
            let elapsed = translationStartedAt.map {
                MenuBarModel.formatElapsed(
                    seconds: now.timeIntervalSince($0)
                )
            } ?? "00:00"
            return copy.translating(elapsed: elapsed)
        }
        return switch readiness {
        case .driverUnavailable: copy.text(.driverMissing)
        case .selectPhysicalInput: copy.text(.selectPhysicalInput)
        case .selectPhysicalOutput: copy.text(.selectPhysicalOutput)
        case .invalidBaseURL: copy.text(.invalidBaseURLPrompt)
        case .modelRequired: copy.text(.modelRequiredPrompt)
        case .apiKeyRequired: copy.text(.apiKeyRequiredPrompt)
        case .ready: copy.text(.ready)
        case .active: copy.translating(elapsed: "00:00")
        case .error: copy.text(.configurationUnavailable)
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
    @Published var interfaceLanguage: AppInterfaceLanguage = .system {
        didSet { persistPublicSettingsIfNeeded() }
    }
    @Published private(set) var systemPreferredLanguages =
        Locale.preferredLanguages
    @Published var apiKeyDraft = ""
    @Published private(set) var coordinatorState = TranslationCoordinatorState() {
        didSet { coordinatorLifecycleRevision &+= 1 }
    }
    @Published private(set) var compatibilityReport: TranslationCompatibilityReport?
    @Published private var connectionTestMessageValue: AppMessage?
    @Published private var inventoryErrorValue: AppMessage?
    @Published private var configurationErrorValue: AppMessage?
    @Published private(set) var isTestingConnection = false
    @Published private(set) var isReloadingDevices = false
    @Published private(set) var isStarting = false
    @Published private(set) var isStopping = false
    @Published private(set) var inboundBypassEnabled = false
    @Published private(set) var outboundBypassEnabled = false
    @Published private(set) var screen: MenuBarScreen = .dashboard
    @Published private(set) var inboundLevel = 0.0
    @Published private(set) var outboundLevel = 0.0
    @Published private(set) var translationStartedAt: Date?
    @Published private(set) var isMenuBarVisible = false
    @Published private(set) var isFloatingWindowVisible = false
    @Published private(set) var isTestingAudioInput = false
    @Published private(set) var isPlayingAudioOutputTest = false
    @Published private(set) var microphonePermissionState:
        MicrophonePermissionState = .notDetermined
    @Published private(set) var audioInputDiagnosticLevel = 0.0
    @Published private var audioInputDiagnosticValue: AppMessage = .key(.notTested)
    @Published private var audioOutputDiagnosticValue: AppMessage = .key(.notTested)
    @Published private var audioDiagnosticErrorValue: AppMessage?

    private var driverAvailable = false
    private var hasStoredAPIKey = false
    private var coordinatorLifecycleRevision: UInt = 0
    private var eventTask: Task<Void, Never>?
    private var audioInputDiagnosticTask: Task<Void, Never>?
    private var deviceReloadTask: Task<DeviceInventoryLoadResult, Never>?
    private var isApplyingSettings = false
    private var lastPersistedPublicSettings: AppSettings?
    private var localeObserver: AnyCancellable?
    private var appliedAudioLevelVisibility = false
    private var audioLevelVisibilityReconciliationTask: Task<Void, Never>?

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
        },
        deferInitialDeviceReload: Bool = false
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
        localeObserver = NotificationCenter.default.publisher(
            for: NSLocale.currentLocaleDidChangeNotification
        )
        .receive(on: DispatchQueue.main)
        .sink { [weak self] _ in
            MainActor.assumeIsolated {
                self?.systemPreferredLanguages = Locale.preferredLanguages
            }
        }
        if !deferInitialDeviceReload {
            reloadDevices()
        }
    }

    func refreshMicrophonePermissionState() async {
        microphonePermissionState =
            await microphonePermissionProvider.authorizationStatus()
    }

    func requestMicrophonePermissionForOnboarding() async {
        await refreshMicrophonePermissionState()
        guard microphonePermissionState == .notDetermined else { return }
        let granted = await microphonePermissionProvider.requestAccess()
        microphonePermissionState = granted ? .authorized : .denied
    }

    var resolvedInterfaceLanguage: ResolvedInterfaceLanguage {
        AppLanguageResolver.resolve(
            preference: interfaceLanguage,
            preferredLanguages: systemPreferredLanguages
        )
    }

    var copy: AppCopy {
        AppCopy(language: resolvedInterfaceLanguage)
    }

    var connectionTestMessage: String {
        connectionTestMessageValue?.text(using: copy) ?? ""
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
        if configurationErrorValue != nil { return .error }
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
        selectionsLocked
            || isReloadingDevices
            || isTestingAudioInput
            || isPlayingAudioOutputTest
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

    var hasVisibleAudioLevelSurface: Bool {
        isMenuBarVisible || isFloatingWindowVisible
    }

    var apiKeyStatusText: String {
        copy.text(hasStoredAPIKey ? .keySaved : .keyNotSaved)
    }

    var repairMessage: String? {
        readiness == .driverUnavailable
            ? copy.text(.driverMissing)
            : nil
    }

    var statusText: String {
        if isStarting { return copy.text(.connecting) }
        if coordinatorState.isRunning { return copy.text(.translating) }
        switch readiness {
        case .driverUnavailable:
            return repairMessage ?? copy.text(.driverMissing)
        case .selectPhysicalInput:
            return copy.text(.selectPhysicalInput)
        case .selectPhysicalOutput:
            return copy.text(.selectPhysicalOutput)
        case .invalidBaseURL:
            return copy.text(.invalidBaseURLPrompt)
        case .modelRequired:
            return copy.text(.modelRequiredPrompt)
        case .apiKeyRequired:
            return copy.text(.apiKeyRequiredPrompt)
        case .ready:
            return copy.text(.ready)
        case .active:
            return copy.text(.translating)
        case .error:
            return copy.text(.configurationUnavailable)
        }
    }

    var systemImage: String {
        if coordinatorState.isRunning { return "waveform.circle.fill" }
        if isStarting { return "arrow.triangle.2.circlepath.circle" }
        if readiness == .error { return "exclamationmark.triangle.fill" }
        return driverAvailable ? "waveform.circle" : "speaker.slash.fill"
    }

    var inboundStatusText: String {
        Self.text(
            for: coordinatorState.inbound,
            channel: .inbound,
            copy: copy
        )
    }

    var outboundStatusText: String {
        Self.text(
            for: coordinatorState.outbound,
            channel: .outbound,
            copy: copy
        )
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
        dashboardPresentation(at: now).primaryStatus
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
            errorText: configurationError ?? inventoryError,
            copy: copy
        )
    }

    func floatingPresentation(
        at now: Date
    ) -> FloatingTranslationPresentation {
        FloatingTranslationPresentation.make(
            coordinatorState: coordinatorState,
            isStarting: isStarting,
            isStopping: isStopping,
            inboundLevel: inboundLevel,
            outboundLevel: outboundLevel,
            translationStartedAt: translationStartedAt,
            now: now,
            hasFatalSessionError: hasFatalSessionError,
            copy: copy
        )
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

    func loadConfiguration() async {
        apply(settingsStore.load())
        do {
            hasStoredAPIKey = try await secretStore.loadAPIKey()
                .map { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
                ?? false
            configurationErrorValue = nil
        } catch {
            hasStoredAPIKey = false
            configurationErrorValue = .detail(
                .keychainReadFailed,
                String(describing: error)
            )
        }
    }

    private func reloadDevices() {
        applyDeviceInventory(
            loadDeviceInventory(
                selectedInputUID: selectedInputUID,
                selectedOutputUID: selectedOutputUID
            )
        )
    }

    func reloadDevicesAsync() async {
        if let deviceReloadTask {
            _ = await deviceReloadTask.value
            return
        }

        let provider = self.provider
        let selectedInputUID = self.selectedInputUID
        let selectedOutputUID = self.selectedOutputUID
        isReloadingDevices = true
        let task = Task.detached(priority: .userInitiated) {
            Self.loadDeviceInventory(
                provider: provider,
                selectedInputUID: selectedInputUID,
                selectedOutputUID: selectedOutputUID
            )
        }
        deviceReloadTask = task
        let result = await task.value
        deviceReloadTask = nil
        isReloadingDevices = false
        applyDeviceInventory(result)
    }

    private func loadDeviceInventory(
        selectedInputUID: String?,
        selectedOutputUID: String?
    ) -> DeviceInventoryLoadResult {
        Self.loadDeviceInventory(
            provider: provider,
            selectedInputUID: selectedInputUID,
            selectedOutputUID: selectedOutputUID
        )
    }

    nonisolated private static func loadDeviceInventory(
        provider: any AudioDeviceProviding,
        selectedInputUID: String?,
        selectedOutputUID: String?
    ) -> DeviceInventoryLoadResult {
        do {
            let devices = try provider.devices()
            let physicalDevices = devices
                .filter { !$0.isEMKEVirtualDevice }
                .sorted { lhs, rhs in
                    let order = lhs.name.localizedStandardCompare(rhs.name)
                    if order == .orderedSame { return lhs.uid < rhs.uid }
                    return order == .orderedAscending
                }
            let physicalInputs = physicalDevices.filter {
                $0.inputChannelCount > 0
            }
            let physicalOutputs = physicalDevices.filter {
                $0.outputChannelCount > 0
            }
            let defaultInputUID: String? = if let selectedInputUID,
                !physicalInputs.contains(where: { $0.uid == selectedInputUID })
            {
                try provider.defaultInputDeviceUID()
            } else {
                nil
            }
            let defaultOutputUID: String? = if let selectedOutputUID,
                !physicalOutputs.contains(where: { $0.uid == selectedOutputUID })
            {
                try provider.defaultOutputDeviceUID()
            } else {
                nil
            }
            return .success(
                DeviceInventorySnapshot(
                    driverAvailable: devices.contains(where: {
                        $0.uid == AudioDevice.virtualSpeakerUID
                    }) && devices.contains(where: {
                        $0.uid == AudioDevice.virtualMicrophoneUID
                    }),
                    physicalInputs: physicalInputs,
                    physicalOutputs: physicalOutputs,
                    defaultInputUID: defaultInputUID,
                    defaultOutputUID: defaultOutputUID
                )
            )
        } catch {
            return .failure(.raw(String(describing: error)))
        }
    }

    private func applyDeviceInventory(_ result: DeviceInventoryLoadResult) {
        switch result {
        case let .success(snapshot):
            driverAvailable = snapshot.driverAvailable
            physicalInputs = snapshot.physicalInputs
            physicalOutputs = snapshot.physicalOutputs
            if let selectedInputUID,
               !physicalInputs.contains(where: { $0.uid == selectedInputUID }) {
                self.selectedInputUID = physicalInputs.first(where: {
                    $0.uid == snapshot.defaultInputUID
                })?.uid
            }
            if let selectedOutputUID,
               !physicalOutputs.contains(where: { $0.uid == selectedOutputUID }) {
                self.selectedOutputUID = physicalOutputs.first(where: {
                    $0.uid == snapshot.defaultOutputUID
                })?.uid
            }
            inventoryErrorValue = nil
        case let .failure(message):
            driverAvailable = false
            physicalInputs = []
            physicalOutputs = []
            inventoryErrorValue = message
        }
    }

    func start() async {
        await stopAudioInputTest()
        await reloadDevicesAsync()
        guard canStart,
              let selectedInputUID,
              let selectedOutputUID else { return }
        isStarting = true
        defer { isStarting = false }
        configurationErrorValue = nil
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
            configurationErrorValue = Self.configurationMessage(for: error)
            coordinatorState = TranslationCoordinatorState()
            resetRuntimePresentation()
        }
    }

    func stop() async {
        isStopping = true
        defer { isStopping = false }
        await coordinator.stop()
        let revision = coordinatorLifecycleRevision
        let state = await coordinator.currentState()
        guard revision == coordinatorLifecycleRevision else {
            return
        }
        coordinatorState = state
        guard !state.hasActivePresentation(
            translationStartedAt: translationStartedAt
        ) else {
            return
        }
        finishCoordinatorSession()
    }

    func startAudioInputTest() async {
        await reloadDevicesAsync()
        guard canTestAudioInput, let device = selectedPhysicalInput else { return }
        audioDiagnosticErrorValue = nil
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
            audioInputDiagnosticValue = .key(.microphoneTestFailed)
            audioDiagnosticErrorValue = Self.audioDiagnosticMessage(for: error)
        }
    }

    func stopAudioInputTest() async {
        await stopAudioInputTest(resetStatus: true)
    }

    func playAudioOutputTest() async {
        await reloadDevicesAsync()
        guard canTestAudioOutput, let device = selectedPhysicalOutput else { return }
        isPlayingAudioOutputTest = true
        audioDiagnosticErrorValue = nil
        audioOutputDiagnosticValue = .key(.testTonePlaying)
        do {
            let result = try await audioDiagnostics.startOutputTest(
                deviceID: device.id
            )
            guard result.writtenFrames == result.requestedFrames else {
                throw AudioDiagnosticPresentationError.outputBackpressure
            }
            try await audioOutputTestDelay()
            await audioDiagnostics.stopOutputTest()
            audioOutputDiagnosticValue = .key(.testTonePlayed)
        } catch {
            await audioDiagnostics.stopOutputTest()
            audioOutputDiagnosticValue = .key(.speakerTestFailed)
            audioDiagnosticErrorValue = Self.audioDiagnosticMessage(for: error)
        }
        isPlayingAudioOutputTest = false
    }

    func testConnection() async {
        guard !isTestingConnection else { return }
        isTestingConnection = true
        compatibilityReport = nil
        connectionTestMessageValue = .key(.testingTranslationProtocol)
        configurationErrorValue = nil
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
            connectionTestMessageValue = Self.message(for: report)
        } catch {
            configurationErrorValue = Self.configurationMessage(for: error)
            connectionTestMessageValue = Self.connectionFailureMessage(for: error)
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
        await refreshMicrophonePermissionState()
        switch microphonePermissionState {
        case .authorized:
            return
        case .notDetermined:
            let granted = await microphonePermissionProvider.requestAccess()
            microphonePermissionState = granted ? .authorized : .denied
            guard granted else {
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
        audioInputDiagnosticValue = switch sample.state {
        case .stopped:
            .key(.notTested)
        case .waitingForFrames:
            Self.inputTransportStatus(sample.transportDiagnostics)
        case .receivingSilence:
            .key(.microphoneConnectedWaiting)
        case .receivingAudio:
            .key(.microphoneDetected)
        }
    }

    private static func inputTransportStatus(
        _ diagnostics: AudioInputTransportDiagnostics
    ) -> AppMessage {
        guard diagnostics.isAvailable else { return .key(.noAudioFrames) }
        if diagnostics.oversizedCallbackCount > 0 {
            return .inputOversized(
                callbackFrames: Int(diagnostics.lastCallbackFrameCount),
                capacityFrames: Int(diagnostics.scratchCapacityFrames)
            )
        }
        if diagnostics.renderErrorCount > 0 {
            return .audioReadFailed(status: diagnostics.lastRenderStatus)
        }
        if diagnostics.callbackCount == 0 {
            return .key(.inputCallbackMissing)
        }
        if diagnostics.writtenFrameCount == 0 {
            return .key(.inputCallbackDidNotWrite)
        }
        return .key(.waitingForAudioFrames)
    }

    private func stopAudioInputTest(resetStatus: Bool) async {
        audioInputDiagnosticTask?.cancel()
        audioInputDiagnosticTask = nil
        await audioDiagnostics.stopInput()
        isTestingAudioInput = false
        audioInputDiagnosticLevel = 0
        if resetStatus {
            audioInputDiagnosticValue = .key(.notTested)
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

    private var hasFatalSessionError: Bool {
        // The coordinator currently exposes no global fatal-session event.
        false
    }

    private var currentPublicSettings: AppSettings {
        AppSettings(
            baseURLString: baseURLString,
            modelID: modelID,
            preferences: currentPreferences,
            selectedInputUID: selectedInputUID,
            selectedOutputUID: selectedOutputUID,
            interfaceLanguage: interfaceLanguage
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
        interfaceLanguage = settings.interfaceLanguage
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
                    if !state.hasActivePresentation(
                        translationStartedAt: translationStartedAt
                    ) {
                        finishCoordinatorSession()
                        return
                    }
                case .audioLevels(let levels):
                    if hasVisibleAudioLevelSurface {
                        inboundLevel = levels.inbound
                        outboundLevel = levels.outbound
                    }
                case .audioBackpressure(let droppedFrames):
                    inventoryErrorValue = .droppedFrames(droppedFrames)
                case .stopped:
                    finishCoordinatorSession()
                    return
                }
            }
        }
    }

    private func finishCoordinatorSession() {
        let task = eventTask
        eventTask = nil
        task?.cancel()
        coordinatorState = TranslationCoordinatorState()
        resetRuntimePresentation()
    }

    private func synchronizeAudioLevelVisibility() async {
        if let task = audioLevelVisibilityReconciliationTask {
            await task.value
            return
        }
        guard appliedAudioLevelVisibility != hasVisibleAudioLevelSurface else {
            return
        }

        let task = Task { @MainActor [weak self] in
            guard let self else { return }
            await self.drainAudioLevelVisibility()
        }
        audioLevelVisibilityReconciliationTask = task
        await task.value
    }

    private func drainAudioLevelVisibility() async {
        defer {
            audioLevelVisibilityReconciliationTask = nil
        }
        while appliedAudioLevelVisibility != hasVisibleAudioLevelSurface {
            let desiredVisibility = hasVisibleAudioLevelSurface
            await coordinator.setAudioLevelUpdatesEnabled(desiredVisibility)
            appliedAudioLevelVisibility = desiredVisibility
        }
        if !appliedAudioLevelVisibility, !hasVisibleAudioLevelSurface {
            inboundLevel = 0
            outboundLevel = 0
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

    private static func configurationMessage(for error: Error) -> AppMessage {
        if let error = error as? MenuBarConfigurationError {
            return error.appMessage
        }
        if let error = error as? AudioDiagnosticPresentationError {
            return error.appMessage
        }
        return .raw(String(describing: error))
    }

    private static func audioDiagnosticMessage(for error: Error) -> AppMessage {
        if let error = error as? MenuBarConfigurationError {
            return error.appMessage
        }
        if let error = error as? AudioDiagnosticPresentationError {
            return error.appMessage
        }
        return .detail(.audioDiagnosticFailed, String(describing: error))
    }

    private static func connectionFailureMessage(for error: Error) -> AppMessage {
        if error is MenuBarConfigurationError {
            return .key(.connectionTestFailed)
        }
        return .detail(.connectionTestFailed, String(describing: error))
    }

    private static func message(
        for report: TranslationCompatibilityReport
    ) -> AppMessage {
        if report.isFullyCompatible {
            return .key(.protocolFullyCompatible)
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
            return .key(.protocolNeedsAudioTest)
        }
        return .key(.protocolIncompatible)
    }

    static func text(
        for state: TranslationChannelState,
        channel: MenuBarChannel,
        copy: AppCopy
    ) -> String {
        switch state {
        case .stopped: copy.text(.stopped)
        case .connecting: copy.text(.channelConnecting)
        case .active: copy.text(.translating)
        case .bypassed: copy.text(.originalBypass)
        case .reconnecting(let attempt):
            copy.reconnecting(attempt: attempt)
        case .failed:
            channel == .inbound
                ? copy.text(.playOriginal)
                : copy.text(.muted)
        }
    }
}

private enum MenuBarConfigurationError: Error {
    case invalidBaseURL
    case modelRequired
    case apiKeyRequired
    case microphoneAccessDenied
    case microphoneAccessRestricted

    var appMessage: AppMessage {
        switch self {
        case .invalidBaseURL:
            .key(.invalidBaseURLError)
        case .modelRequired:
            .key(.modelRequiredError)
        case .apiKeyRequired:
            .key(.apiKeyRequiredError)
        case .microphoneAccessDenied:
            .key(.microphonePermissionDenied)
        case .microphoneAccessRestricted:
            .key(.microphonePermissionRestricted)
        }
    }
}

private enum AudioDiagnosticPresentationError: Error {
    case outputBackpressure

    var appMessage: AppMessage {
        switch self {
        case .outputBackpressure:
            .key(.outputTestBackpressure)
        }
    }
}
