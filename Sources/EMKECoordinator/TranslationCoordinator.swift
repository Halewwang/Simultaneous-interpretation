import EMKEAudioEngine
import EMKECore
import EMKERealtime
import EMKERouting
import Foundation

public protocol TranslationAudioEngine: Sendable {
    func start(configuration: AudioEngineConfiguration) async throws
    func stop() async
    func setRouting(
        inbound: InboundOutputMode,
        outbound: OutboundOutputMode
    ) async
    func nextEvent() async -> AudioEngineEvent
    func enqueueInboundOutput(_ pcm16: Data) async throws
    func enqueueOutboundTranslation(_ pcm16: Data) async throws
}

extension LocalAudioEngine: TranslationAudioEngine {}

public protocol TranslationSessionControlling: Sendable {
    func connect() async throws
    func appendAudio(_ pcm16: Data) async throws
    func nextEvent() async throws -> TranslationServerEvent
    func close() async throws
}

extension TranslationSession: TranslationSessionControlling {}

public protocol TranslationSessionBuilding: Sendable {
    func makeSession(
        configuration: APIConfiguration,
        sessionConfiguration: TranslationSessionConfiguration,
        apiKey: String
    ) async -> any TranslationSessionControlling
}

public struct URLSessionTranslationSessionBuilder: TranslationSessionBuilding {
    private let socketFactory: any TranslationSocketFactory

    public init(
        socketFactory: any TranslationSocketFactory =
            URLSessionTranslationSocketFactory()
    ) {
        self.socketFactory = socketFactory
    }

    public func makeSession(
        configuration: APIConfiguration,
        sessionConfiguration: TranslationSessionConfiguration,
        apiKey: String
    ) async -> any TranslationSessionControlling {
        TranslationSession(
            configuration: configuration,
            sessionConfiguration: sessionConfiguration,
            apiKey: apiKey,
            factory: socketFactory
        )
    }
}

public struct TranslationCoordinatorConfiguration: Sendable {
    public let apiConfiguration: APIConfiguration
    public let preferences: TranslationPreferences
    public let audioConfiguration: AudioEngineConfiguration
    public let apiKey: String
    public let inputTranscriptionModel: String

    public init(
        apiConfiguration: APIConfiguration,
        preferences: TranslationPreferences,
        audioConfiguration: AudioEngineConfiguration,
        apiKey: String,
        inputTranscriptionModel: String = "gpt-realtime-whisper"
    ) {
        self.apiConfiguration = apiConfiguration
        self.preferences = preferences
        self.audioConfiguration = audioConfiguration
        self.apiKey = apiKey
        self.inputTranscriptionModel = inputTranscriptionModel
    }
}

public actor TranslationCoordinator {
    private static let maximumQueuedEvents = 128
    private static let maximumSubtitleCharacters = 4_096
    private static let minimumLevelPublishInterval: UInt64 = 33_333_334

    private let audioEngine: any TranslationAudioEngine
    private let sessionBuilder: any TranslationSessionBuilding
    private let languageClassifier: @Sendable (String) -> LanguageHypotheses
    private let reconnectDelays: [Duration]
    private let levelTimeNanoseconds: @Sendable () -> UInt64

    private var configuration: TranslationCoordinatorConfiguration?
    private var inboundSession: (any TranslationSessionControlling)?
    private var outboundSession: (any TranslationSessionControlling)?
    private var audioTask: Task<Void, Never>?
    private var inboundReceiveTask: Task<Void, Never>?
    private var outboundReceiveTask: Task<Void, Never>?
    private var inboundDeadlineTask: Task<Void, Never>?
    private var inboundFinishTask: Task<Void, Never>?
    private var inboundReconnectTask: Task<Void, Never>?
    private var outboundReconnectTask: Task<Void, Never>?
    private var inboundBatcher = PCMFrameBatcher()
    private var outboundBatcher = PCMFrameBatcher()
    private var inboundVAD = PCMVoiceActivityDetector()
    private var inboundLevelMeter = PCMLevelMeter()
    private var outboundLevelMeter = PCMLevelMeter()
    private var audioLevels = AudioLevelSnapshot()
    private var lastLevelPublishTime: UInt64?
    private var audioLevelUpdatesEnabled = true
    private var inboundBuffer = InboundUtteranceBuffer(
        motherLanguage: .chinese
    )
    private var inboundUtteranceActive = false
    private var routing = RoutingStateMachine()
    private var isStopping = false
    private var events: [TranslationCoordinatorEvent] = []
    private var eventWaiters: [
        CheckedContinuation<TranslationCoordinatorEvent, Never>
    ] = []

    public private(set) var state = TranslationCoordinatorState()

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

    public func start(
        configuration: TranslationCoordinatorConfiguration
    ) async throws {
        guard !state.isRunning else { return }

        self.configuration = configuration
        isStopping = false
        state = TranslationCoordinatorState(
            isRunning: false,
            inbound: .connecting,
            outbound: configuration.preferences.motherLanguage
                == configuration.preferences.meetingOutputLanguage
                ? .bypassed
                : .connecting
        )
        publishState()

        try await audioEngine.start(
            configuration: configuration.audioConfiguration
        )

        resetRuntimeBuffers(motherLanguage: configuration.preferences.motherLanguage)
        routing = RoutingStateMachine()
        routing.handle(.translationStarted)
        startAudioLoop()

        let inbound = await sessionBuilder.makeSession(
            configuration: configuration.apiConfiguration,
            sessionConfiguration: TranslationSessionConfiguration(
                targetLanguage: configuration.preferences.motherLanguage,
                inputTranscriptionModel: configuration
                    .inputTranscriptionModel,
                noiseReduction: .farField
            ),
            apiKey: configuration.apiKey
        )
        inboundSession = inbound

        let usesOutboundBypass = configuration.preferences.motherLanguage
            == configuration.preferences.meetingOutputLanguage
        let outbound: (any TranslationSessionControlling)?
        if usesOutboundBypass {
            outbound = nil
            routing.handle(.outboundAutomaticBypassEnabled)
        } else {
            let newOutbound = await sessionBuilder.makeSession(
                configuration: configuration.apiConfiguration,
                sessionConfiguration: TranslationSessionConfiguration(
                    targetLanguage: configuration.preferences
                        .meetingOutputLanguage
                ),
                apiKey: configuration.apiKey
            )
            outbound = newOutbound
            outboundSession = newOutbound
        }

        async let inboundError = Self.connectError(for: inbound)
        async let outboundError: (any Error)? = {
            guard let outbound else { return nil }
            return await Self.connectError(for: outbound)
        }()

        let resolvedInboundError = await inboundError
        let resolvedOutboundError = await outboundError

        if let resolvedInboundError {
            inboundSession = nil
            state.inbound = .failed(
                message: String(describing: resolvedInboundError)
            )
            routing.handle(.inboundConnectionFailed)
            scheduleReconnect(channel: .inbound, attempt: 0)
        } else {
            state.inbound = .active
            startInboundReceiveLoop(session: inbound)
        }

        if usesOutboundBypass {
            state.outbound = .bypassed
        } else if let resolvedOutboundError {
            outboundSession = nil
            state.outbound = .failed(
                message: String(describing: resolvedOutboundError)
            )
            routing.handle(.outboundConnectionFailed)
            scheduleReconnect(channel: .outbound, attempt: 0)
        } else if let outbound {
            state.outbound = .active
            startOutboundReceiveLoop(session: outbound)
        }

        await applyRouting()
        state.isRunning = true
        publishState()
    }

    public func stop() async {
        guard state.isRunning || state.inbound == .connecting else { return }
        isStopping = true
        cancelTimingAndReconnectTasks()

        let inbound = inboundSession
        let outbound = outboundSession
        await withTaskGroup(of: Void.self) { group in
            if let inbound {
                group.addTask { try? await inbound.close() }
            }
            if let outbound {
                group.addTask { try? await outbound.close() }
            }
        }

        await audioEngine.stop()
        audioTask?.cancel()
        inboundReceiveTask?.cancel()
        outboundReceiveTask?.cancel()
        await audioTask?.value
        await inboundReceiveTask?.value
        await outboundReceiveTask?.value

        inboundSession = nil
        outboundSession = nil
        audioTask = nil
        inboundReceiveTask = nil
        outboundReceiveTask = nil
        configuration = nil
        resetRuntimeBuffers(motherLanguage: .chinese)
        state = TranslationCoordinatorState()
        routing.handle(.translationStopped)
        isStopping = false
        publish(.stopped)
    }

    public func nextEvent() async -> TranslationCoordinatorEvent {
        if !events.isEmpty {
            return events.removeFirst()
        }
        return await withCheckedContinuation { continuation in
            eventWaiters.append(continuation)
        }
    }

    public func currentState() -> TranslationCoordinatorState {
        state
    }

    public func setAudioLevelUpdatesEnabled(_ enabled: Bool) {
        audioLevelUpdatesEnabled = enabled
        inboundLevelMeter.reset()
        outboundLevelMeter.reset()
        audioLevels = AudioLevelSnapshot()
        lastLevelPublishTime = nil
        events.removeAll { event in
            if case .audioLevels = event { return true }
            return false
        }
    }

    public func setInboundBypass(_ enabled: Bool) async {
        if enabled {
            routing.handle(.inboundBypassEnabled)
        } else {
            routing.handle(.inboundBypassDisabled)
        }
        await applyRouting()
        publishState()
    }

    public func setOutboundBypass(_ enabled: Bool) async {
        if usesAutomaticOutboundBypass {
            routing.handle(.outboundAutomaticBypassEnabled)
            state.outbound = .bypassed
            await applyRouting()
            publishState()
            return
        }
        if enabled {
            routing.handle(.outboundBypassEnabled)
        } else {
            routing.handle(.outboundBypassDisabled)
        }
        await applyRouting()
        publishState()
    }

    private var usesAutomaticOutboundBypass: Bool {
        guard let configuration else { return false }
        return configuration.preferences.motherLanguage
            == configuration.preferences.meetingOutputLanguage
            && outboundSession == nil
    }

    private enum Channel {
        case inbound
        case outbound
    }

    private static func connectError(
        for session: any TranslationSessionControlling
    ) async -> (any Error)? {
        do {
            try await session.connect()
            return nil
        } catch {
            return error
        }
    }

    private func startAudioLoop() {
        let audioEngine = self.audioEngine
        audioTask = Task { [weak self] in
            while !Task.isCancelled {
                let event = await audioEngine.nextEvent()
                guard let self else { return }
                let shouldContinue = await self.handleAudioEvent(event)
                if !shouldContinue { return }
            }
        }
    }

    private func startInboundReceiveLoop(
        session: any TranslationSessionControlling
    ) {
        inboundReceiveTask?.cancel()
        inboundReceiveTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    let event = try await session.nextEvent()
                    guard let self else { return }
                    let shouldContinue = await self.handleInboundEvent(event)
                    if !shouldContinue { return }
                } catch {
                    guard let self else { return }
                    await self.handleChannelFailure(
                        .inbound,
                        error: error
                    )
                    return
                }
            }
        }
    }

    private func startOutboundReceiveLoop(
        session: any TranslationSessionControlling
    ) {
        outboundReceiveTask?.cancel()
        outboundReceiveTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    let event = try await session.nextEvent()
                    guard let self else { return }
                    let shouldContinue = await self.handleOutboundEvent(event)
                    if !shouldContinue { return }
                } catch {
                    guard let self else { return }
                    await self.handleChannelFailure(
                        .outbound,
                        error: error
                    )
                    return
                }
            }
        }
    }

    private func handleAudioEvent(_ event: AudioEngineEvent) async -> Bool {
        guard !isStopping else { return false }
        switch event {
        case .inboundNetworkAudio(let pcm16):
            observeAudioLevel(pcm16, channel: .inbound)
            await handleInboundAudio(pcm16)
        case .outboundNetworkAudio(let pcm16):
            observeAudioLevel(pcm16, channel: .outbound)
            await handleOutboundAudio(pcm16)
        case .outputBackpressure(_, let droppedFrames):
            publish(.audioBackpressure(droppedFrames: droppedFrames))
        case .stopped:
            return false
        }
        return true
    }

    private func observeAudioLevel(_ pcm16: Data, channel: Channel) {
        guard audioLevelUpdatesEnabled else { return }
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

    private func handleInboundAudio(_ pcm16: Data) async {
        do {
            let vadEvent = try inboundVAD.observe(pcm16)
            if vadEvent == .speechStarted {
                inboundFinishTask?.cancel()
                inboundBuffer.begin()
                inboundUtteranceActive = true
                clearInboundSubtitles()
            }

            if inboundUtteranceActive {
                await playInbound(inboundBuffer.appendOriginal(pcm16))
            }

            let frames = try inboundBatcher.append(pcm16)
            if let inboundSession {
                for frame in frames {
                    do {
                        try await inboundSession.appendAudio(frame)
                    } catch {
                        await handleChannelFailure(.inbound, error: error)
                        break
                    }
                }
            }

            if vadEvent == .speechEnded {
                scheduleInboundFinish()
            }
        } catch {
            await handleChannelFailure(.inbound, error: error)
        }
    }

    private func handleOutboundAudio(_ pcm16: Data) async {
        do {
            let frames = try outboundBatcher.append(pcm16)
            if let outboundSession {
                for frame in frames {
                    do {
                        try await outboundSession.appendAudio(frame)
                    } catch {
                        await handleChannelFailure(.outbound, error: error)
                        break
                    }
                }
            }
        } catch {
            await handleChannelFailure(.outbound, error: error)
        }
    }

    private func handleInboundEvent(
        _ event: TranslationServerEvent
    ) async -> Bool {
        switch event {
        case .outputAudio(let delta):
            guard inboundUtteranceActive else { return true }
            await playInbound(
                inboundBuffer.appendTranslation(delta.data)
            )
            if inboundBuffer.currentRoute == .undecided {
                scheduleInboundDeadline()
            }
        case .inputTranscript(let delta):
            appendText(
                delta.text,
                to: &state.subtitles.inboundSource
            )
            if inboundUtteranceActive {
                await playInbound(
                    inboundBuffer.observe(
                        languageClassifier(
                            state.subtitles.inboundSource
                        )
                    )
                )
                cancelDeadlineIfResolved()
            }
            publishState()
        case .outputTranscript(let delta):
            appendText(
                delta.text,
                to: &state.subtitles.inboundTranslation
            )
            publishState()
        case .closed:
            if !isStopping {
                await handleChannelFailure(
                    .inbound,
                    error: TranslationSocketError.disconnected
                )
            }
            return false
        case .serverError(let code, let message):
            await handleChannelFailure(
                .inbound,
                error: TranslationSessionError.server(
                    code: code,
                    message: message
                )
            )
            return false
        case .sessionCreated, .sessionUpdated, .ignored:
            break
        }
        return true
    }

    private func handleOutboundEvent(
        _ event: TranslationServerEvent
    ) async -> Bool {
        switch event {
        case .outputAudio(let delta):
            try? await audioEngine.enqueueOutboundTranslation(delta.data)
        case .inputTranscript(let delta):
            appendText(
                delta.text,
                to: &state.subtitles.outboundSource
            )
            publishState()
        case .outputTranscript(let delta):
            appendText(
                delta.text,
                to: &state.subtitles.outboundTranslation
            )
            publishState()
        case .closed:
            if !isStopping {
                await handleChannelFailure(
                    .outbound,
                    error: TranslationSocketError.disconnected
                )
            }
            return false
        case .serverError(let code, let message):
            await handleChannelFailure(
                .outbound,
                error: TranslationSessionError.server(
                    code: code,
                    message: message
                )
            )
            return false
        case .sessionCreated, .sessionUpdated, .ignored:
            break
        }
        return true
    }

    private func playInbound(_ chunks: [Data]) async {
        for chunk in chunks {
            try? await audioEngine.enqueueInboundOutput(chunk)
        }
    }

    private func scheduleInboundDeadline() {
        guard inboundDeadlineTask == nil else { return }
        inboundDeadlineTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(250))
            guard !Task.isCancelled, let self else { return }
            await self.resolveInboundDeadline()
        }
    }

    private func resolveInboundDeadline() async {
        inboundDeadlineTask = nil
        guard inboundUtteranceActive,
              inboundBuffer.currentRoute == .undecided else { return }
        await playInbound(
            inboundBuffer.resolveDeadline(isSpeech: inboundVAD.isSpeaking)
        )
    }

    private func cancelDeadlineIfResolved() {
        guard inboundBuffer.currentRoute != .undecided else { return }
        inboundDeadlineTask?.cancel()
        inboundDeadlineTask = nil
    }

    private func scheduleInboundFinish() {
        inboundFinishTask?.cancel()
        inboundFinishTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled, let self else { return }
            await self.finishInboundUtterance()
        }
    }

    private func finishInboundUtterance() async {
        inboundFinishTask = nil
        guard inboundUtteranceActive else { return }
        await playInbound(inboundBuffer.finish(isSpeech: true))
        inboundUtteranceActive = false
        inboundDeadlineTask?.cancel()
        inboundDeadlineTask = nil
        routing.handle(.utteranceEnded)
        await applyRouting()
    }

    private func handleChannelFailure(
        _ channel: Channel,
        error: any Error
    ) async {
        guard !isStopping else { return }
        switch channel {
        case .inbound:
            guard inboundSession != nil || state.inbound == .active else {
                return
            }
            inboundSession = nil
            state.inbound = .failed(message: String(describing: error))
            routing.handle(.inboundConnectionFailed)
            scheduleReconnect(channel: .inbound, attempt: 0)
        case .outbound:
            guard outboundSession != nil || state.outbound == .active else {
                return
            }
            outboundSession = nil
            state.outbound = .failed(message: String(describing: error))
            routing.handle(.outboundConnectionFailed)
            scheduleReconnect(channel: .outbound, attempt: 0)
        }
        await applyRouting()
        publishState()
    }

    private func scheduleReconnect(channel: Channel, attempt: Int) {
        guard reconnectDelays.indices.contains(attempt),
              configuration != nil,
              !isStopping else { return }
        let delay = reconnectDelays[attempt]
        let task = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled, let self else { return }
            await self.reconnect(channel: channel, attempt: attempt)
        }
        switch channel {
        case .inbound:
            inboundReconnectTask?.cancel()
            inboundReconnectTask = task
        case .outbound:
            outboundReconnectTask?.cancel()
            outboundReconnectTask = task
        }
    }

    private func reconnect(channel: Channel, attempt: Int) async {
        guard !isStopping, let configuration else { return }
        switch channel {
        case .inbound:
            state.inbound = .reconnecting(attempt: attempt + 1)
        case .outbound:
            state.outbound = .reconnecting(attempt: attempt + 1)
        }
        publishState()

        let sessionConfiguration: TranslationSessionConfiguration
        switch channel {
        case .inbound:
            sessionConfiguration = TranslationSessionConfiguration(
                targetLanguage: configuration.preferences.motherLanguage,
                inputTranscriptionModel: configuration
                    .inputTranscriptionModel,
                noiseReduction: .farField
            )
        case .outbound:
            sessionConfiguration = TranslationSessionConfiguration(
                targetLanguage: configuration.preferences
                    .meetingOutputLanguage
            )
        }

        let session = await sessionBuilder.makeSession(
            configuration: configuration.apiConfiguration,
            sessionConfiguration: sessionConfiguration,
            apiKey: configuration.apiKey
        )
        if let error = await Self.connectError(for: session) {
            switch channel {
            case .inbound:
                state.inbound = .failed(message: String(describing: error))
            case .outbound:
                state.outbound = .failed(message: String(describing: error))
            }
            publishState()
            scheduleReconnect(channel: channel, attempt: attempt + 1)
            return
        }

        switch channel {
        case .inbound:
            inboundSession = session
            state.inbound = .active
            routing.handle(.inboundConnectionRecovered)
            if !inboundUtteranceActive {
                routing.handle(.utteranceEnded)
            }
            startInboundReceiveLoop(session: session)
        case .outbound:
            outboundSession = session
            state.outbound = .active
            routing.handle(.outboundConnectionRecovered)
            startOutboundReceiveLoop(session: session)
        }
        await applyRouting()
        publishState()
    }

    private func applyRouting() async {
        await audioEngine.setRouting(
            inbound: routing.inbound,
            outbound: routing.outbound
        )
    }

    private func resetRuntimeBuffers(motherLanguage: SupportedLanguage) {
        inboundBatcher.reset()
        outboundBatcher.reset()
        inboundVAD.reset()
        inboundLevelMeter.reset()
        outboundLevelMeter.reset()
        audioLevels = AudioLevelSnapshot()
        lastLevelPublishTime = nil
        inboundBuffer = InboundUtteranceBuffer(
            motherLanguage: motherLanguage
        )
        inboundUtteranceActive = false
        state.subtitles = SubtitleSnapshot()
    }

    private func clearInboundSubtitles() {
        state.subtitles.inboundSource = ""
        state.subtitles.inboundTranslation = ""
        publishState()
    }

    private func appendText(_ delta: String, to value: inout String) {
        value.append(delta)
        if value.count > Self.maximumSubtitleCharacters {
            value.removeFirst(
                value.count - Self.maximumSubtitleCharacters
            )
        }
    }

    private func cancelTimingAndReconnectTasks() {
        inboundDeadlineTask?.cancel()
        inboundFinishTask?.cancel()
        inboundReconnectTask?.cancel()
        outboundReconnectTask?.cancel()
        inboundDeadlineTask = nil
        inboundFinishTask = nil
        inboundReconnectTask = nil
        outboundReconnectTask = nil
    }

    private func publishState() {
        publish(.stateChanged(state))
    }

    private func publish(_ event: TranslationCoordinatorEvent) {
        if !eventWaiters.isEmpty {
            eventWaiters.removeFirst().resume(returning: event)
            return
        }
        if case .audioLevels = event,
           let index = events.lastIndex(where: { queued in
               if case .audioLevels = queued { return true }
               return false
           }) {
            events.remove(at: index)
            events.append(event)
            return
        }
        if case .audioLevels = event,
           events.count >= Self.maximumQueuedEvents {
            return
        }
        if events.count < Self.maximumQueuedEvents {
            events.append(event)
        } else {
            events.removeFirst()
            events.append(event)
        }
    }
}
