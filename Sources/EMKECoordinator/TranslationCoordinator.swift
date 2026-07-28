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

enum TranslationReaderChannel: Equatable, Sendable {
    case inbound
    case outbound
}

enum TranslationReaderEventDisposition: Equatable, Sendable {
    case accepted
    case stale
}

typealias TranslationReaderDispositionObserver = @Sendable (
    TranslationReaderChannel,
    UInt64,
    TranslationReaderEventDisposition
) async -> Void

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
    public let audioStability: AudioStabilityConfiguration

    public init(
        apiConfiguration: APIConfiguration,
        preferences: TranslationPreferences,
        audioConfiguration: AudioEngineConfiguration,
        apiKey: String,
        inputTranscriptionModel: String = "gpt-realtime-whisper",
        audioStability: AudioStabilityConfiguration = .production
    ) {
        self.apiConfiguration = apiConfiguration
        self.preferences = preferences
        self.audioConfiguration = audioConfiguration
        self.apiKey = apiKey
        self.inputTranscriptionModel = inputTranscriptionModel
        self.audioStability = audioStability
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

    private struct PendingSession: Sendable {
        let lifecycle: UInt64
        let epoch: UInt64
        let session: any TranslationSessionControlling
    }

    private enum InboundFinishPhase: Equatable, Sendable {
        case scheduled
        case draining
    }

    private struct InboundFinishState: Sendable {
        let token: UInt64
        let epoch: UInt64
        var phase: InboundFinishPhase
    }

    private var configuration: TranslationCoordinatorConfiguration?
    private var inboundSession: (any TranslationSessionControlling)?
    private var outboundSession: (any TranslationSessionControlling)?
    private var inboundPendingSession: PendingSession?
    private var outboundPendingSession: PendingSession?
    private var audioTask: Task<Void, Never>?
    private var inboundReceiveTask: Task<Void, Never>?
    private var outboundReceiveTask: Task<Void, Never>?
    private var inboundDeadlineTask: Task<Void, Never>?
    private var inboundFinishTask: Task<Void, Never>?
    private var inboundReconnectTask: Task<Void, Never>?
    private var outboundReconnectTask: Task<Void, Never>?
    private var inboundFinishState: InboundFinishState?
    private var inboundFinishGeneration: UInt64 = 0
    private var inboundEpoch: UInt64 = 0
    private var outboundEpoch: UInt64 = 0
    private var lifecycleGeneration: UInt64 = 0
    private var readerDispositionObserver:
        TranslationReaderDispositionObserver?
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
    private var isStarting = false
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
        guard !state.isRunning, !isStarting, !isStopping else { return }

        let lifecycle = advanceLifecycleGeneration()
        let startInboundEpoch = advanceEpoch(for: .inbound)
        let startOutboundEpoch = advanceEpoch(for: .outbound)
        isStarting = true
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

        do {
            try await audioEngine.start(
                configuration: configuration.audioConfiguration
            )
        } catch {
            if isCurrentLifecycle(lifecycle) {
                isStarting = false
            }
            throw error
        }
        guard isCurrentLifecycle(lifecycle),
              isCurrent(startInboundEpoch, for: .inbound),
              isCurrent(startOutboundEpoch, for: .outbound),
              !isStopping else { return }

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
        guard isCurrentLifecycle(lifecycle),
              isCurrent(startInboundEpoch, for: .inbound),
              !isStopping else {
            try? await inbound.close()
            return
        }
        storePendingSession(
            inbound,
            channel: .inbound,
            lifecycle: lifecycle,
            epoch: startInboundEpoch
        )

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
            guard isCurrentLifecycle(lifecycle),
                  isCurrent(startOutboundEpoch, for: .outbound),
                  !isStopping else {
                try? await newOutbound.close()
                return
            }
            outbound = newOutbound
            storePendingSession(
                newOutbound,
                channel: .outbound,
                lifecycle: lifecycle,
                epoch: startOutboundEpoch
            )
        }

        if let outbound {
            async let inboundConnection: Void = connectInitialSession(
                inbound,
                channel: .inbound,
                epoch: startInboundEpoch,
                lifecycle: lifecycle
            )
            async let outboundConnection: Void = connectInitialSession(
                outbound,
                channel: .outbound,
                epoch: startOutboundEpoch,
                lifecycle: lifecycle
            )
            _ = await (inboundConnection, outboundConnection)
        } else {
            await connectInitialSession(
                inbound,
                channel: .inbound,
                epoch: startInboundEpoch,
                lifecycle: lifecycle
            )
        }

        guard isCurrentLifecycle(lifecycle),
              !isStopping,
              self.configuration != nil else { return }
        let completionInboundEpoch = inboundEpoch
        let completionOutboundEpoch = outboundEpoch
        await applyRouting()
        guard isCurrent(completionInboundEpoch, for: .inbound),
              isCurrent(completionOutboundEpoch, for: .outbound),
              isCurrentLifecycle(lifecycle),
              !isStopping else { return }
        state.isRunning = true
        isStarting = false
        publishState()
    }

    public func stop() async {
        guard !isStopping,
              isStarting
                || state.isRunning
                || state.inbound != .stopped
                || state.outbound != .stopped else { return }
        isStopping = true
        isStarting = false
        advanceLifecycleGeneration()
        advanceEpoch(for: .inbound)
        advanceEpoch(for: .outbound)
        cancelTimingAndReconnectTasks()

        let inbound = inboundSession
        let outbound = outboundSession
        let pendingInbound = inboundPendingSession?.session
        let pendingOutbound = outboundPendingSession?.session
        inboundSession = nil
        outboundSession = nil
        inboundPendingSession = nil
        outboundPendingSession = nil
        configuration = nil
        await withTaskGroup(of: Void.self) { group in
            if let inbound {
                group.addTask { try? await inbound.close() }
            }
            if let outbound {
                group.addTask { try? await outbound.close() }
            }
            if let pendingInbound {
                group.addTask { try? await pendingInbound.close() }
            }
            if let pendingOutbound {
                group.addTask { try? await pendingOutbound.close() }
            }
        }

        await audioEngine.stop()
        audioTask?.cancel()
        inboundReceiveTask?.cancel()
        outboundReceiveTask?.cancel()
        await audioTask?.value
        await inboundReceiveTask?.value
        await outboundReceiveTask?.value

        audioTask = nil
        inboundReceiveTask = nil
        outboundReceiveTask = nil
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

    func setReaderDispositionObserver(
        _ observer: TranslationReaderDispositionObserver?
    ) {
        readerDispositionObserver = observer
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

    private static func nextGeneration(
        after generation: UInt64,
        name: StaticString
    ) -> UInt64 {
        guard generation != UInt64.max else {
            fatalError("\(name) generation exhausted")
        }
        return generation + 1
    }

    @discardableResult
    private func advanceLifecycleGeneration() -> UInt64 {
        lifecycleGeneration = Self.nextGeneration(
            after: lifecycleGeneration,
            name: "lifecycle"
        )
        return lifecycleGeneration
    }

    private func isCurrentLifecycle(_ generation: UInt64) -> Bool {
        generation == lifecycleGeneration
    }

    @discardableResult
    private func advanceEpoch(for channel: Channel) -> UInt64 {
        switch channel {
        case .inbound:
            let rebindsFinish = !isStopping
                && inboundFinishState != nil
                && inboundUtteranceActive
                && !inboundVAD.isSpeaking
            cancelInboundFinish()
            inboundEpoch = Self.nextGeneration(
                after: inboundEpoch,
                name: "inbound"
            )
            if rebindsFinish {
                scheduleInboundFinish(epoch: inboundEpoch)
            }
            return inboundEpoch
        case .outbound:
            outboundEpoch = Self.nextGeneration(
                after: outboundEpoch,
                name: "outbound"
            )
            return outboundEpoch
        }
    }

    private func isCurrent(_ epoch: UInt64, for channel: Channel) -> Bool {
        switch channel {
        case .inbound:
            epoch == inboundEpoch
        case .outbound:
            epoch == outboundEpoch
        }
    }

    private func storePendingSession(
        _ session: any TranslationSessionControlling,
        channel: Channel,
        lifecycle: UInt64,
        epoch: UInt64
    ) {
        let pending = PendingSession(
            lifecycle: lifecycle,
            epoch: epoch,
            session: session
        )
        switch channel {
        case .inbound:
            inboundPendingSession = pending
        case .outbound:
            outboundPendingSession = pending
        }
    }

    private func takePendingSession(
        channel: Channel,
        lifecycle: UInt64,
        epoch: UInt64
    ) -> (any TranslationSessionControlling)? {
        switch channel {
        case .inbound:
            guard inboundPendingSession?.lifecycle == lifecycle,
                  inboundPendingSession?.epoch == epoch else { return nil }
            defer { inboundPendingSession = nil }
            return inboundPendingSession?.session
        case .outbound:
            guard outboundPendingSession?.lifecycle == lifecycle,
                  outboundPendingSession?.epoch == epoch else { return nil }
            defer { outboundPendingSession = nil }
            return outboundPendingSession?.session
        }
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

    private func connectInitialSession(
        _ session: any TranslationSessionControlling,
        channel: Channel,
        epoch: UInt64,
        lifecycle: UInt64
    ) async {
        let error = await Self.connectError(for: session)
        guard isCurrentLifecycle(lifecycle),
              isCurrent(epoch, for: channel),
              !isStopping else {
            if let stale = takePendingSession(
                channel: channel,
                lifecycle: lifecycle,
                epoch: epoch
            ) {
                try? await stale.close()
            }
            return
        }
        guard let ownedSession = takePendingSession(
            channel: channel,
            lifecycle: lifecycle,
            epoch: epoch
        ) else { return }

        let resultingEpoch: UInt64
        if let error {
            let retryEpoch = advanceEpoch(for: channel)
            resultingEpoch = retryEpoch
            switch channel {
            case .inbound:
                state.inbound = .failed(message: String(describing: error))
                routing.handle(.inboundConnectionFailed)
            case .outbound:
                state.outbound = .failed(message: String(describing: error))
                routing.handle(.outboundConnectionFailed)
            }
            scheduleReconnect(
                channel: channel,
                attempt: 0,
                expectedEpoch: retryEpoch,
                lifecycle: lifecycle
            )
        } else {
            resultingEpoch = epoch
            switch channel {
            case .inbound:
                inboundSession = ownedSession
                state.inbound = .active
                startInboundReceiveLoop(
                    session: ownedSession,
                    epoch: epoch
                )
            case .outbound:
                outboundSession = ownedSession
                state.outbound = .active
                startOutboundReceiveLoop(
                    session: ownedSession,
                    epoch: epoch
                )
            }
        }
        await applyRouting()
        if error != nil {
            try? await ownedSession.close()
        }
        guard isCurrent(resultingEpoch, for: channel),
              isCurrentLifecycle(lifecycle),
              !isStopping else { return }
        publishState()
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
        session: any TranslationSessionControlling,
        epoch: UInt64
    ) {
        guard isCurrent(epoch, for: .inbound) else { return }
        inboundReceiveTask?.cancel()
        inboundReceiveTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    let event = try await session.nextEvent()
                    guard let self else { return }
                    guard await self.observeReaderEvent(
                        epoch,
                        for: .inbound
                    ) else { return }
                    let shouldContinue = await self.handleInboundEvent(
                        event,
                        epoch: epoch
                    )
                    if !shouldContinue { return }
                } catch {
                    guard let self else { return }
                    await self.handleChannelFailure(
                        .inbound,
                        error: error,
                        epoch: epoch
                    )
                    return
                }
            }
        }
    }

    private func startOutboundReceiveLoop(
        session: any TranslationSessionControlling,
        epoch: UInt64
    ) {
        guard isCurrent(epoch, for: .outbound) else { return }
        outboundReceiveTask?.cancel()
        outboundReceiveTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    let event = try await session.nextEvent()
                    guard let self else { return }
                    guard await self.observeReaderEvent(
                        epoch,
                        for: .outbound
                    ) else { return }
                    let shouldContinue = await self.handleOutboundEvent(
                        event,
                        epoch: epoch
                    )
                    if !shouldContinue { return }
                } catch {
                    guard let self else { return }
                    await self.handleChannelFailure(
                        .outbound,
                        error: error,
                        epoch: epoch
                    )
                    return
                }
            }
        }
    }

    private func observeReaderEvent(
        _ epoch: UInt64,
        for channel: Channel
    ) async -> Bool {
        let isAccepted = isCurrent(epoch, for: channel)
        if let readerDispositionObserver {
            let observedChannel: TranslationReaderChannel = switch channel {
            case .inbound:
                .inbound
            case .outbound:
                .outbound
            }
            await readerDispositionObserver(
                observedChannel,
                epoch,
                isAccepted ? .accepted : .stale
            )
        }
        return isAccepted
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
        let epoch = inboundEpoch
        do {
            let vadEvent = try inboundVAD.observe(pcm16)
            if vadEvent == .speechStarted {
                cancelInboundFinish()
                inboundBuffer.begin()
                inboundUtteranceActive = true
                clearInboundSubtitles()
            }

            if inboundUtteranceActive {
                await playInbound(inboundBuffer.appendOriginal(pcm16))
            }

            if state.inbound == .active,
               isCurrent(epoch, for: .inbound),
               let inboundSession {
                let frames = try inboundBatcher.append(pcm16)
                for frame in frames {
                    guard isCurrent(epoch, for: .inbound) else { break }
                    do {
                        try await inboundSession.appendAudio(frame)
                        guard isCurrent(epoch, for: .inbound) else { break }
                    } catch {
                        await handleChannelFailure(
                            .inbound,
                            error: error,
                            epoch: epoch
                        )
                        break
                    }
                }
            } else {
                inboundBatcher.reset()
            }

            if vadEvent == .speechEnded {
                scheduleInboundFinish(epoch: epoch)
            }
        } catch {
            await handleChannelFailure(
                .inbound,
                error: error,
                epoch: epoch
            )
        }
    }

    private func handleOutboundAudio(_ pcm16: Data) async {
        let epoch = outboundEpoch
        do {
            if state.outbound == .active,
               isCurrent(epoch, for: .outbound),
               let outboundSession {
                let frames = try outboundBatcher.append(pcm16)
                for frame in frames {
                    guard isCurrent(epoch, for: .outbound) else { break }
                    do {
                        try await outboundSession.appendAudio(frame)
                        guard isCurrent(epoch, for: .outbound) else { break }
                    } catch {
                        await handleChannelFailure(
                            .outbound,
                            error: error,
                            epoch: epoch
                        )
                        break
                    }
                }
            } else {
                outboundBatcher.reset()
            }
        } catch {
            await handleChannelFailure(
                .outbound,
                error: error,
                epoch: epoch
            )
        }
    }

    private func handleInboundEvent(
        _ event: TranslationServerEvent,
        epoch: UInt64
    ) async -> Bool {
        guard isCurrent(epoch, for: .inbound), !isStopping else {
            return false
        }
        switch event {
        case .outputAudio(let delta):
            guard inboundUtteranceActive else { return true }
            await playInbound(
                inboundBuffer.appendTranslation(delta.data),
                epoch: epoch
            )
            guard isCurrent(epoch, for: .inbound), !isStopping else {
                return false
            }
            if inboundBuffer.currentRoute == .undecided {
                scheduleInboundDeadline(epoch: epoch)
            }
            extendInboundFinishWindowIfDraining(epoch: epoch)
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
                    ),
                    epoch: epoch
                )
                guard isCurrent(epoch, for: .inbound), !isStopping else {
                    return false
                }
                cancelDeadlineIfResolved()
                extendInboundFinishWindowIfDraining(epoch: epoch)
            }
            publishState()
        case .outputTranscript(let delta):
            appendText(
                delta.text,
                to: &state.subtitles.inboundTranslation
            )
            extendInboundFinishWindowIfDraining(epoch: epoch)
            publishState()
        case .closed:
            if !isStopping {
                await handleChannelFailure(
                    .inbound,
                    error: TranslationSocketError.disconnected,
                    epoch: epoch
                )
            }
            return false
        case .serverError(let code, let message):
            await handleChannelFailure(
                .inbound,
                error: TranslationSessionError.server(
                    code: code,
                    message: message
                ),
                epoch: epoch
            )
            return false
        case .sessionCreated, .sessionUpdated, .ignored:
            break
        }
        return true
    }

    private func handleOutboundEvent(
        _ event: TranslationServerEvent,
        epoch: UInt64
    ) async -> Bool {
        guard isCurrent(epoch, for: .outbound), !isStopping else {
            return false
        }
        switch event {
        case .outputAudio(let delta):
            try? await audioEngine.enqueueOutboundTranslation(delta.data)
            guard isCurrent(epoch, for: .outbound), !isStopping else {
                return false
            }
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
                    error: TranslationSocketError.disconnected,
                    epoch: epoch
                )
            }
            return false
        case .serverError(let code, let message):
            await handleChannelFailure(
                .outbound,
                error: TranslationSessionError.server(
                    code: code,
                    message: message
                ),
                epoch: epoch
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

    private func playInbound(_ chunks: [Data], epoch: UInt64) async {
        for chunk in chunks {
            guard isCurrent(epoch, for: .inbound), !isStopping else { return }
            try? await audioEngine.enqueueInboundOutput(chunk)
        }
    }

    private func playInboundFinish(
        _ chunks: [Data],
        epoch: UInt64,
        token: UInt64
    ) async {
        for chunk in chunks {
            guard isCurrentInboundFinish(
                epoch: epoch,
                token: token,
                phase: .draining
            ) else { return }
            try? await audioEngine.enqueueInboundOutput(chunk)
            guard isCurrentInboundFinish(
                epoch: epoch,
                token: token,
                phase: .draining
            ) else { return }
        }
    }

    private func scheduleInboundDeadline(epoch: UInt64) {
        guard isCurrent(epoch, for: .inbound),
              inboundDeadlineTask == nil else { return }
        inboundDeadlineTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(250))
            guard !Task.isCancelled, let self else { return }
            await self.resolveInboundDeadline(epoch: epoch)
        }
    }

    private func resolveInboundDeadline(epoch: UInt64) async {
        guard isCurrent(epoch, for: .inbound), !isStopping else { return }
        inboundDeadlineTask = nil
        guard inboundUtteranceActive,
              inboundBuffer.currentRoute == .undecided else { return }
        await playInbound(
            inboundBuffer.resolveDeadline(isSpeech: inboundVAD.isSpeaking),
            epoch: epoch
        )
    }

    private func cancelDeadlineIfResolved() {
        guard inboundBuffer.currentRoute != .undecided else { return }
        inboundDeadlineTask?.cancel()
        inboundDeadlineTask = nil
    }

    private func scheduleInboundFinish(epoch: UInt64) {
        guard isCurrent(epoch, for: .inbound) else { return }
        cancelInboundFinish()
        inboundFinishGeneration = Self.nextGeneration(
            after: inboundFinishGeneration,
            name: "inbound finish"
        )
        let token = inboundFinishGeneration
        inboundFinishState = InboundFinishState(
            token: token,
            epoch: epoch,
            phase: .scheduled
        )
        inboundFinishTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled, let self else { return }
            await self.finishInboundUtterance(epoch: epoch, token: token)
        }
    }

    private func cancelInboundFinish() {
        inboundFinishTask?.cancel()
        inboundFinishTask = nil
        inboundFinishState = nil
    }

    private func extendInboundFinishWindowIfDraining(epoch: UInt64) {
        guard isCurrent(epoch, for: .inbound),
              inboundUtteranceActive,
              !inboundVAD.isSpeaking else { return }
        scheduleInboundFinish(epoch: epoch)
    }

    private func finishInboundUtterance(
        epoch: UInt64,
        token: UInt64
    ) async {
        guard isCurrentInboundFinish(
            epoch: epoch,
            token: token,
            phase: .scheduled
        ),
              inboundUtteranceActive else { return }
        inboundFinishState?.phase = .draining
        await playInboundFinish(
            inboundBuffer.finish(isSpeech: true),
            epoch: epoch,
            token: token
        )
        guard isCurrentInboundFinish(
            epoch: epoch,
            token: token,
            phase: .draining
        ) else { return }
        inboundFinishTask = nil
        inboundFinishState = nil
        inboundUtteranceActive = false
        inboundDeadlineTask?.cancel()
        inboundDeadlineTask = nil
        routing.handle(.utteranceEnded)
        await applyRouting()
    }

    private func isCurrentInboundFinish(
        epoch: UInt64,
        token: UInt64,
        phase: InboundFinishPhase
    ) -> Bool {
        isCurrent(epoch, for: .inbound)
            && !isStopping
            && !Task.isCancelled
            && inboundFinishState?.epoch == epoch
            && inboundFinishState?.token == token
            && inboundFinishState?.phase == phase
    }

    private func handleChannelFailure(
        _ channel: Channel,
        error: any Error,
        epoch: UInt64
    ) async {
        guard isCurrent(epoch, for: channel), !isStopping else { return }
        let lifecycle = lifecycleGeneration
        let retryEpoch: UInt64
        let failedSession: (any TranslationSessionControlling)?
        switch channel {
        case .inbound:
            guard inboundSession != nil || state.inbound == .active else {
                return
            }
            retryEpoch = advanceEpoch(for: .inbound)
            inboundReceiveTask?.cancel()
            inboundDeadlineTask?.cancel()
            inboundDeadlineTask = nil
            inboundBatcher.reset()
            failedSession = inboundSession
            inboundSession = nil
            state.inbound = .failed(message: String(describing: error))
            routing.handle(.inboundConnectionFailed)
            scheduleReconnect(
                channel: .inbound,
                attempt: 0,
                expectedEpoch: retryEpoch,
                lifecycle: lifecycle
            )
        case .outbound:
            guard outboundSession != nil || state.outbound == .active else {
                return
            }
            retryEpoch = advanceEpoch(for: .outbound)
            outboundReceiveTask?.cancel()
            outboundBatcher.reset()
            failedSession = outboundSession
            outboundSession = nil
            state.outbound = .failed(message: String(describing: error))
            routing.handle(.outboundConnectionFailed)
            scheduleReconnect(
                channel: .outbound,
                attempt: 0,
                expectedEpoch: retryEpoch,
                lifecycle: lifecycle
            )
        }
        await applyRouting()
        if let failedSession {
            try? await failedSession.close()
        }
        guard isCurrent(retryEpoch, for: channel),
              isCurrentLifecycle(lifecycle),
              !isStopping else { return }
        publishState()
    }

    private func scheduleReconnect(
        channel: Channel,
        attempt: Int,
        expectedEpoch: UInt64,
        lifecycle: UInt64
    ) {
        guard reconnectDelays.indices.contains(attempt),
              configuration != nil,
              !isStopping,
              isCurrentLifecycle(lifecycle),
              isCurrent(expectedEpoch, for: channel) else { return }
        let delay = reconnectDelays[attempt]
        let task = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled, let self else { return }
            await self.reconnect(
                channel: channel,
                attempt: attempt,
                expectedEpoch: expectedEpoch,
                lifecycle: lifecycle
            )
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

    private func reconnect(
        channel: Channel,
        attempt: Int,
        expectedEpoch: UInt64,
        lifecycle: UInt64
    ) async {
        guard isCurrent(expectedEpoch, for: channel),
              isCurrentLifecycle(lifecycle),
              !isStopping,
              let configuration else { return }
        let sessionEpoch = expectedEpoch
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
        guard isCurrentLifecycle(lifecycle),
              isCurrent(sessionEpoch, for: channel),
              !isStopping else {
            try? await session.close()
            return
        }
        storePendingSession(
            session,
            channel: channel,
            lifecycle: lifecycle,
            epoch: sessionEpoch
        )
        let error = await Self.connectError(for: session)
        guard isCurrentLifecycle(lifecycle),
              isCurrent(sessionEpoch, for: channel),
              !isStopping else {
            if let stale = takePendingSession(
                channel: channel,
                lifecycle: lifecycle,
                epoch: sessionEpoch
            ) {
                try? await stale.close()
            }
            return
        }
        guard let ownedSession = takePendingSession(
            channel: channel,
            lifecycle: lifecycle,
            epoch: sessionEpoch
        ) else { return }

        if let error {
            let retryEpoch = advanceEpoch(for: channel)
            switch channel {
            case .inbound:
                state.inbound = .failed(message: String(describing: error))
            case .outbound:
                state.outbound = .failed(message: String(describing: error))
            }
            publishState()
            scheduleReconnect(
                channel: channel,
                attempt: attempt + 1,
                expectedEpoch: retryEpoch,
                lifecycle: lifecycle
            )
            try? await ownedSession.close()
            return
        }

        switch channel {
        case .inbound:
            inboundSession = ownedSession
            state.inbound = .active
            routing.handle(.inboundConnectionRecovered)
            if !inboundUtteranceActive {
                routing.handle(.utteranceEnded)
            }
            startInboundReceiveLoop(
                session: ownedSession,
                epoch: sessionEpoch
            )
        case .outbound:
            outboundSession = ownedSession
            state.outbound = .active
            routing.handle(.outboundConnectionRecovered)
            startOutboundReceiveLoop(
                session: ownedSession,
                epoch: sessionEpoch
            )
        }
        await applyRouting()
        guard isCurrent(sessionEpoch, for: channel),
              isCurrentLifecycle(lifecycle),
              !isStopping else { return }
        publishState()
    }

    private func applyRouting() async {
        await audioEngine.setRouting(
            inbound: routing.inbound,
            outbound: routing.outbound
        )
    }

    private func resetRuntimeBuffers(motherLanguage: SupportedLanguage) {
        cancelInboundFinish()
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
        cancelInboundFinish()
        inboundReconnectTask?.cancel()
        outboundReconnectTask?.cancel()
        inboundDeadlineTask = nil
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
