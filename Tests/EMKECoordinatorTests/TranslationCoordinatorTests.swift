import EMKEAudioEngine
import EMKECore
import EMKERealtime
import EMKERouting
import Foundation
import Testing
@testable import EMKECoordinator

private enum CoordinatorTestError: Error, Equatable {
    case disconnected
}

private actor CoordinatorAudioEngineFake: TranslationAudioEngine {
    private let blocksStart: Bool
    private(set) var startConfigurations: [AudioEngineConfiguration] = []
    private(set) var routings: [(InboundOutputMode, OutboundOutputMode)] = []
    private(set) var inboundPlayback: [Data] = []
    private(set) var outboundPlayback: [Data] = []
    private var events: [AudioEngineEvent] = []
    private var waiters: [CheckedContinuation<AudioEngineEvent, Never>] = []
    private var startWaiters: [CheckedContinuation<Void, Never>] = []
    private var inboundOutputBlocksRemaining = 0
    private(set) var blockedInboundOutputCount = 0
    private var inboundOutputWaiters: [CheckedContinuation<Void, Never>] = []

    init(blocksStart: Bool = false) {
        self.blocksStart = blocksStart
    }

    func start(configuration: AudioEngineConfiguration) async throws {
        startConfigurations.append(configuration)
        if blocksStart {
            await withCheckedContinuation { continuation in
                startWaiters.append(continuation)
            }
        }
    }

    func stop() async {
        let waiters = self.waiters
        self.waiters.removeAll()
        for waiter in waiters {
            waiter.resume(returning: .stopped)
        }
    }

    func setRouting(
        inbound: InboundOutputMode,
        outbound: OutboundOutputMode
    ) async {
        routings.append((inbound, outbound))
    }

    func nextEvent() async -> AudioEngineEvent {
        if !events.isEmpty {
            return events.removeFirst()
        }
        return await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func enqueueInboundOutput(_ pcm16: Data) async throws {
        if inboundOutputBlocksRemaining > 0 {
            inboundOutputBlocksRemaining -= 1
            blockedInboundOutputCount += 1
            await withCheckedContinuation { continuation in
                inboundOutputWaiters.append(continuation)
            }
        }
        inboundPlayback.append(pcm16)
    }

    func enqueueOutboundTranslation(_ pcm16: Data) async throws {
        outboundPlayback.append(pcm16)
    }

    func emit(_ event: AudioEngineEvent) {
        if waiters.isEmpty {
            events.append(event)
        } else {
            waiters.removeFirst().resume(returning: event)
        }
    }

    func releaseStarts() {
        let waiters = startWaiters
        startWaiters.removeAll()
        for waiter in waiters {
            waiter.resume()
        }
    }

    func blockNextInboundOutput() {
        blockNextInboundOutputs(1)
    }

    func blockNextInboundOutputs(_ count: Int) {
        precondition(count > 0)
        inboundOutputBlocksRemaining = count
    }

    func releaseInboundOutputs() {
        let waiters = inboundOutputWaiters
        inboundOutputWaiters.removeAll()
        for waiter in waiters {
            waiter.resume()
        }
    }

    var lastRouting: (InboundOutputMode, OutboundOutputMode)? {
        routings.last
    }
}

private actor CoordinatorSessionFake: TranslationSessionControlling {
    let connectError: CoordinatorTestError?
    private(set) var appended: [Data] = []
    private(set) var closeCount = 0
    private(set) var deliveredEventCount = 0
    private var appendErrors: [CoordinatorTestError]
    private var events: [Result<TranslationServerEvent, CoordinatorTestError>]
    private let closeEvents: [TranslationServerEvent]
    private let emitsClosedOnClose: Bool
    private var deliveryWaiters: [
        (count: Int, continuation: CheckedContinuation<Void, Never>)
    ] = []
    private var waiters: [
        CheckedContinuation<TranslationServerEvent, any Error>
    ] = []

    init(
        connectError: CoordinatorTestError? = nil,
        appendErrors: [CoordinatorTestError] = [],
        events: [Result<TranslationServerEvent, CoordinatorTestError>] = [],
        closeEvents: [TranslationServerEvent] = [],
        emitsClosedOnClose: Bool = true
    ) {
        self.connectError = connectError
        self.appendErrors = appendErrors
        self.events = events
        self.closeEvents = closeEvents
        self.emitsClosedOnClose = emitsClosedOnClose
    }

    func connect() async throws {
        if let connectError { throw connectError }
    }

    func appendAudio(_ pcm16: Data) async throws {
        if !appendErrors.isEmpty {
            throw appendErrors.removeFirst()
        }
        appended.append(pcm16)
    }

    func failNextAppend(with error: CoordinatorTestError) {
        appendErrors.append(error)
    }

    func nextEvent() async throws -> TranslationServerEvent {
        let event: TranslationServerEvent
        if !events.isEmpty {
            event = try events.removeFirst().get()
        } else {
            event = try await withCheckedThrowingContinuation { continuation in
                waiters.append(continuation)
            }
        }
        deliveredEventCount += 1
        let ready = deliveryWaiters.filter {
            deliveredEventCount >= $0.count
        }
        deliveryWaiters.removeAll {
            deliveredEventCount >= $0.count
        }
        for waiter in ready {
            waiter.continuation.resume()
        }
        return event
    }

    func close() async throws {
        closeCount += 1
        for event in closeEvents {
            emit(.success(event))
        }
        if emitsClosedOnClose {
            emit(.success(.closed))
        }
    }

    func emit(
        _ result: Result<TranslationServerEvent, CoordinatorTestError>
    ) {
        if waiters.isEmpty {
            events.append(result)
        } else {
            let waiter = waiters.removeFirst()
            switch result {
            case .success(let event):
                waiter.resume(returning: event)
            case .failure(let error):
                waiter.resume(throwing: error)
            }
        }
    }

    func waitUntilDeliveredEventCount(_ count: Int) async {
        if deliveredEventCount >= count { return }
        await withCheckedContinuation { continuation in
            deliveryWaiters.append((count, continuation))
        }
    }
}

private struct SessionBuildRequest: Equatable, Sendable {
    let configuration: TranslationSessionConfiguration
}

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

private actor CoordinatorSessionBuilderFake: TranslationSessionBuilding {
    private var sessions: [CoordinatorSessionFake]
    private(set) var requests: [SessionBuildRequest] = []

    init(sessions: [CoordinatorSessionFake]) {
        self.sessions = sessions
    }

    func makeSession(
        configuration: APIConfiguration,
        sessionConfiguration: TranslationSessionConfiguration,
        apiKey: String
    ) async -> any TranslationSessionControlling {
        requests.append(
            SessionBuildRequest(configuration: sessionConfiguration)
        )
        return sessions.removeFirst()
    }
}

private actor BlockingCoordinatorSession: TranslationSessionControlling {
    private(set) var connectCount = 0
    private(set) var closeCount = 0
    private(set) var appendCount = 0
    private(set) var appended: [Data] = []
    private var connectWaiters: [CheckedContinuation<Void, Never>] = []
    private var eventWaiters: [
        CheckedContinuation<TranslationServerEvent, any Error>
    ] = []

    func connect() async throws {
        connectCount += 1
        await withCheckedContinuation { continuation in
            connectWaiters.append(continuation)
        }
    }

    func appendAudio(_ pcm16: Data) async throws {
        appendCount += 1
        appended.append(pcm16)
    }

    func nextEvent() async throws -> TranslationServerEvent {
        try await withCheckedThrowingContinuation { continuation in
            eventWaiters.append(continuation)
        }
    }

    func close() async throws {
        closeCount += 1
        let waiters = eventWaiters
        eventWaiters.removeAll()
        for waiter in waiters {
            waiter.resume(returning: .closed)
        }
    }

    func releaseConnections() {
        let waiters = connectWaiters
        connectWaiters.removeAll()
        for waiter in waiters {
            waiter.resume()
        }
    }
}

private actor CoordinatorCompletionRecorder {
    private(set) var isComplete = false

    func complete() {
        isComplete = true
    }
}

private actor RepeatingCoordinatorSessionBuilder: TranslationSessionBuilding {
    let session: BlockingCoordinatorSession

    init(session: BlockingCoordinatorSession) {
        self.session = session
    }

    func makeSession(
        configuration: APIConfiguration,
        sessionConfiguration: TranslationSessionConfiguration,
        apiKey: String
    ) async -> any TranslationSessionControlling {
        session
    }
}

private actor BlockingCoordinatorSessionBuilder: TranslationSessionBuilding {
    private var sessions: [BlockingCoordinatorSession]

    init(sessions: [BlockingCoordinatorSession]) {
        self.sessions = sessions
    }

    func makeSession(
        configuration: APIConfiguration,
        sessionConfiguration: TranslationSessionConfiguration,
        apiKey: String
    ) async -> any TranslationSessionControlling {
        sessions.removeFirst()
    }
}

private actor AudioLevelEventRecorder {
    private(set) var receivedAudioLevel = false

    func record(_ event: TranslationCoordinatorEvent) {
        if case .audioLevels = event {
            receivedAudioLevel = true
        }
    }
}

private actor CoordinatorReaderDispositionProbe {
    struct Record: Equatable, Sendable {
        let channel: TranslationReaderChannel
        let epoch: UInt64
        let disposition: TranslationReaderEventDisposition
    }

    private var records: [Record] = []

    func record(
        channel: TranslationReaderChannel,
        epoch: UInt64,
        disposition: TranslationReaderEventDisposition
    ) {
        let record = Record(
            channel: channel,
            epoch: epoch,
            disposition: disposition
        )
        records.append(record)
    }

    func nextRecord(maximumYields: Int = 10_000) async -> Record? {
        for _ in 0..<maximumYields {
            if !records.isEmpty {
                return records.removeFirst()
            }
            await Task.yield()
        }
        return nil
    }
}

@Test
func readerDispositionProbeWaitIsBoundedWhenNoRecordArrives() async {
    let probe = CoordinatorReaderDispositionProbe()
    let record = await probe.nextRecord(maximumYields: 1)
    #expect(record == nil)
}

private struct CoordinatorHarness {
    let audio: CoordinatorAudioEngineFake
    let levelClock: CoordinatorLevelClock
    let inbound: CoordinatorSessionFake
    let outbound: CoordinatorSessionFake
    let builder: CoordinatorSessionBuilderFake
    let coordinator: TranslationCoordinator
    let configuration: TranslationCoordinatorConfiguration

    init(
        preferences: TranslationPreferences = TranslationPreferences(
            motherLanguage: .chinese,
            meetingOutputLanguage: .german
        ),
        audio: CoordinatorAudioEngineFake? = nil,
        inbound: CoordinatorSessionFake? = nil,
        outbound: CoordinatorSessionFake? = nil,
        inboundError: CoordinatorTestError? = nil,
        outboundCloseEvents: [TranslationServerEvent] = [],
        additionalSessions: [CoordinatorSessionFake] = [],
        reconnectDelays: [Duration] = [],
        audioStability: AudioStabilityConfiguration = .legacy,
        classifier: @escaping @Sendable (String) -> LanguageHypotheses = {
            _ in LanguageHypotheses(["de": 0.9])
        }
    ) {
        self.audio = audio ?? CoordinatorAudioEngineFake()
        levelClock = CoordinatorLevelClock()
        self.inbound = inbound ?? CoordinatorSessionFake(
            connectError: inboundError
        )
        self.outbound = outbound ?? CoordinatorSessionFake(
            closeEvents: outboundCloseEvents
        )
        builder = CoordinatorSessionBuilderFake(
            sessions: [self.inbound, self.outbound] + additionalSessions
        )
        coordinator = TranslationCoordinator(
            audioEngine: self.audio,
            sessionBuilder: builder,
            languageClassifier: classifier,
            reconnectDelays: reconnectDelays,
            levelTimeNanoseconds: levelClock.now
        )
        configuration = TranslationCoordinatorConfiguration(
            apiConfiguration: .default,
            preferences: preferences,
            audioConfiguration: AudioEngineConfiguration(
                selection: coordinatorAudioSelection()
            ),
            apiKey: "test-key",
            audioStability: audioStability
        )
    }

    func start() async throws {
        try await coordinator.start(configuration: configuration)
    }

    func drainStartupEvents() async {
        _ = await coordinator.nextEvent()
        _ = await coordinator.nextEvent()
        _ = await coordinator.nextEvent()
        _ = await coordinator.nextEvent()
    }

    func nextAudioLevelEvent() async -> AudioLevelSnapshot {
        while true {
            switch await coordinator.nextEvent() {
            case .audioLevels(let snapshot):
                return snapshot
            case .stateChanged, .audioBackpressure, .stopped:
                continue
            }
        }
    }

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

private func coordinatorAudioSelection() -> AudioDeviceSelection {
    AudioDeviceSelection(
        virtualSpeaker: AudioDevice(
            id: 10,
            uid: AudioDevice.virtualSpeakerUID,
            name: "EMKE Virtual Speaker",
            inputChannelCount: 2,
            outputChannelCount: 2,
            nominalSampleRate: 48_000
        ),
        virtualMicrophone: AudioDevice(
            id: 11,
            uid: AudioDevice.virtualMicrophoneUID,
            name: "EMKE Virtual Microphone",
            inputChannelCount: 2,
            outputChannelCount: 2,
            nominalSampleRate: 48_000
        ),
        physicalInput: AudioDevice(
            id: 20,
            uid: "physical.input",
            name: "Physical Input",
            inputChannelCount: 1,
            outputChannelCount: 0,
            nominalSampleRate: 48_000
        ),
        physicalOutput: AudioDevice(
            id: 21,
            uid: "physical.output",
            name: "Physical Output",
            inputChannelCount: 0,
            outputChannelCount: 2,
            nominalSampleRate: 48_000
        )
    )
}

private func coordinatorConfiguration(
    preferences: TranslationPreferences = TranslationPreferences(
        motherLanguage: .chinese,
        meetingOutputLanguage: .german
    ),
    audioStability: AudioStabilityConfiguration = .legacy
) -> TranslationCoordinatorConfiguration {
    TranslationCoordinatorConfiguration(
        apiConfiguration: .default,
        preferences: preferences,
        audioConfiguration: AudioEngineConfiguration(
            selection: coordinatorAudioSelection()
        ),
        apiKey: "test-key",
        audioStability: audioStability
    )
}

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

private func voicedPCM16(byteCount: Int = 9_600) -> Data {
    var data = Data(capacity: byteCount)
    let amplitude = UInt16(bitPattern: 8_000)
    for _ in 0..<(byteCount / 2) {
        data.append(UInt8(truncatingIfNeeded: amplitude))
        data.append(UInt8(truncatingIfNeeded: amplitude >> 8))
    }
    return data
}

private func audioDelta(_ data: Data) -> TranslationAudioDelta {
    TranslationAudioDelta(
        data: data,
        sampleRate: 24_000,
        channels: 1,
        format: "pcm16",
        elapsedMilliseconds: nil
    )
}

private func transcriptDelta(_ text: String) -> TranslationTranscriptDelta {
    TranslationTranscriptDelta(text: text, elapsedMilliseconds: nil)
}

private func eventually(
    _ condition: @escaping @Sendable () async -> Bool
) async -> Bool {
    for _ in 0..<2_000 {
        if await condition() { return true }
        try? await Task.sleep(for: .milliseconds(1))
    }
    return false
}

@Test
func undecidedInboundSpeechPlaysTwelvePercentPreview() async throws {
    let harness = CoordinatorHarness(audioStability: .production)
    try await harness.start()

    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)

    try #require(await eventually {
        !(await harness.audio.inboundPlayback).isEmpty
    })
    let firstChunk = try #require(
        (await harness.audio.inboundPlayback).first
    )
    let first = decodePCM16(firstChunk)
    #expect(first.allSatisfy { $0 == 1_200 })
    await harness.coordinator.stop()
}

@Test
func motherLanguageRecoveryRampsFromLivePointWithoutReplay() async throws {
    let harness = CoordinatorHarness(
        audioStability: .production,
        classifier: { _ in LanguageHypotheses(["zh-Hans": 0.9]) }
    )
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 1
    })

    await harness.inbound.emit(
        .success(.inputTranscript(transcriptDelta("你好")))
    )
    await harness.inbound.waitUntilDeliveredEventCount(1)
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 8)

    try #require(await eventually {
        (await harness.audio.inboundPlayback).count == 9
    })
    let played = (await harness.audio.inboundPlayback).flatMap(decodePCM16)
    #expect(played.count == 9 * 240)
    #expect(played.prefix(240).allSatisfy { $0 == 1_200 })
    #expect(abs(Int(played.last ?? 0) - 10_000) <= 1)
    await harness.coordinator.stop()
}

@Test
func foreignSpeechCrossfadesThenDropsFollowingOriginal() async throws {
    let harness = CoordinatorHarness(
        audioStability: .production,
        classifier: { _ in LanguageHypotheses(["de": 0.9]) }
    )
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    await harness.inbound.emit(
        .success(.inputTranscript(transcriptDelta("Deutsch")))
    )
    await harness.inbound.waitUntilDeliveredEventCount(1)
    await harness.inbound.emit(.success(.outputAudio(
        audioDelta(constantPCM16(2_000, samples: 1_920))
    )))
    await harness.inbound.waitUntilDeliveredEventCount(2)
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 8)

    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 9
    })
    let playbackCount = await harness.audio.inboundPlayback.count
    let mixed = (await harness.audio.inboundPlayback).flatMap(decodePCM16)
    #expect(mixed.contains { abs(Int($0) - 2_000) <= 1 })

    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 1)
    _ = await harness.coordinator.currentState()
    #expect(await harness.audio.inboundPlayback.count == playbackCount)
    await harness.coordinator.stop()
}

@Test
func translationBeforeClassificationIsHeldForForeignCrossfade() async throws {
    let harness = CoordinatorHarness(
        audioStability: .production,
        classifier: { _ in LanguageHypotheses(["de": 0.9]) }
    )
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    await harness.inbound.emit(.success(.outputAudio(
        audioDelta(constantPCM16(2_000, samples: 1_920))
    )))
    await harness.inbound.waitUntilDeliveredEventCount(1)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 1
    })

    await harness.inbound.emit(
        .success(.inputTranscript(transcriptDelta("Deutsch")))
    )
    await harness.inbound.waitUntilDeliveredEventCount(2)
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 8)

    #expect(await eventually {
        let samples = (await harness.audio.inboundPlayback)
            .flatMap(decodePCM16)
        return samples.contains { abs(Int($0) - 2_000) <= 1 }
    })
    await harness.coordinator.stop()
}

@Test
func inboundFailureRampsOriginalFromTheLivePointBeforeReconnect() async throws {
    let recoveredInbound = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        additionalSessions: [recoveredInbound],
        reconnectDelays: [.zero],
        audioStability: .production
    )
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 1
    })
    let playbackCountBeforeFailure =
        await harness.audio.inboundPlayback.count

    await harness.inbound.emit(.failure(.disconnected))
    #expect(await eventually {
        let requestCount = await harness.builder.requests.count
        let inboundState = await harness.coordinator.state.inbound
        return requestCount == 3 && inboundState == .active
    })
    #expect(await harness.audio.lastRouting?.0 == .translated)

    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 8)
    #expect(await eventually {
        await harness.audio.inboundPlayback.count
            == playbackCountBeforeFailure + 8
    })
    let recovery = Array(
        (await harness.audio.inboundPlayback)
            .dropFirst(playbackCountBeforeFailure)
    ).flatMap(decodePCM16)
    #expect(recovery.count == 1_920)
    #expect(abs(Int(recovery.last ?? 0) - 10_000) <= 1)
    await harness.coordinator.stop()
}

@Test
func providerProbeUsesFortyMillisecondNetworkFrames() async throws {
    let harness = CoordinatorHarness(
        audioStability: .providerProbe40ms
    )
    try await harness.start()

    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 4)

    #expect(await eventually {
        await harness.inbound.appended.count == 1
    })
    #expect(await harness.inbound.appended[0].count == 1_920)
    await harness.coordinator.stop()
}

@Test
func latencyMilestonesUseFirstMonotonicTimestampPerUtterance() async throws {
    let harness = CoordinatorHarness(
        audioStability: .providerProbe40ms,
        classifier: { _ in LanguageHypotheses(["de": 0.9]) }
    )
    try await harness.start()

    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 1
    })
    let previewState = await harness.coordinator.currentState()
    #expect(
        previewState.latency.latest?
            .translationAudioToPlaybackMilliseconds == nil
    )

    harness.levelClock.advance(milliseconds: 5)
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually { await harness.inbound.appended.count == 1 })

    harness.levelClock.advance(milliseconds: 7)
    await harness.inbound.emit(
        .success(.inputTranscript(transcriptDelta("Deutsch")))
    )
    #expect(await eventually {
        await harness.coordinator.state.subtitles.inboundSource == "Deutsch"
    })

    harness.levelClock.advance(milliseconds: 11)
    await harness.inbound.emit(.success(.outputAudio(
        audioDelta(constantPCM16(2_000, samples: 1_920))
    )))
    await harness.inbound.waitUntilDeliveredEventCount(2)
    harness.levelClock.advance(milliseconds: 13)
    let playbackCountBeforeCrossfade =
        await harness.audio.inboundPlayback.count
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 1)
    #expect(await eventually {
        await harness.audio.inboundPlayback.count
            == playbackCountBeforeCrossfade + 1
    })

    harness.levelClock.advance(milliseconds: 100)
    await harness.inbound.emit(
        .success(.inputTranscript(transcriptDelta(" weiter")))
    )
    await harness.inbound.emit(.success(.outputAudio(
        audioDelta(constantPCM16(3_000, samples: 240))
    )))
    #expect(await eventually {
        await harness.coordinator.state.subtitles.inboundSource
            == "Deutsch weiter"
    })

    let state = await harness.coordinator.currentState()
    let latest = try #require(state.latency.latest)
    #expect(latest.speechToFirstNetworkFrameMilliseconds == 5)
    #expect(latest.speechToFirstSourceTranscriptMilliseconds == 12)
    #expect(latest.speechToRouteDecisionMilliseconds == 12)
    #expect(latest.speechToFirstTranslationAudioMilliseconds == 23)
    #expect(latest.translationAudioToPlaybackMilliseconds == 13)
    await harness.coordinator.stop()
}

@Test
func manualInboundBypassResetsAuditionWithoutDuplicatePlayback() async throws {
    let harness = CoordinatorHarness(
        audioStability: .providerProbe40ms
    )
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 1
    })
    let playbackCount = await harness.audio.inboundPlayback.count

    await harness.coordinator.setInboundBypass(true)
    #expect(await harness.audio.lastRouting?.0 == .originalBypass)
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 4)
    #expect(await eventually {
        await harness.inbound.appended.count >= 1
    })
    #expect(await harness.audio.inboundPlayback.count == playbackCount)
    await harness.coordinator.stop()
}

@Test
func manualInboundBypassKeepsLatencyUtteranceIdentifiersUnique()
    async throws
{
    let harness = CoordinatorHarness(
        audioStability: .providerProbe40ms
    )
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 1
    })
    let firstState = await harness.coordinator.currentState()
    let firstID = try #require(
        firstState.latency.latest?.utteranceID
    )

    await harness.coordinator.setInboundBypass(true)
    await harness.coordinator.setInboundBypass(false)
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 2
    })

    let secondState = await harness.coordinator.currentState()
    let secondID = try #require(
        secondState.latency.latest?.utteranceID
    )
    #expect(secondID != firstID)
    await harness.coordinator.stop()
}

@Test
func stopClearsAuditionBatcherVADAndLatencyState() async throws {
    let secondInbound = CoordinatorSessionFake()
    let secondOutbound = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        additionalSessions: [secondInbound, secondOutbound],
        audioStability: .providerProbe40ms
    )
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 1
    })
    #expect(
        await harness.coordinator.currentState().latency.latest != nil
    )

    await harness.coordinator.stop()
    try await harness.start()
    let playbackCount = await harness.audio.inboundPlayback.count
    #expect(
        await harness.coordinator.currentState().latency == .empty
    )

    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        await harness.audio.inboundPlayback.count == playbackCount + 1
    })
    #expect(await secondInbound.appended.isEmpty)
    let restartedPreview = decodePCM16(
        (await harness.audio.inboundPlayback).last ?? Data()
    )
    #expect(restartedPreview.allSatisfy { $0 == 1_200 })
    await harness.coordinator.stop()
}

@Test
func rendererFailureFallsBackDirectlyAndRejectsLaterTranslation() async throws {
    let harness = CoordinatorHarness(audioStability: .production)
    try await harness.start()
    await harness.emitInboundSpeechFrames(amplitude: 10_000, count: 2)
    #expect(await eventually {
        (await harness.audio.inboundPlayback).count == 1
    })

    await harness.inbound.emit(.success(.outputAudio(
        audioDelta(Data(repeating: 1, count: 240_002))
    )))
    #expect(await eventually {
        await harness.audio.lastRouting?.0 == .originalFailOpen
    })
    let playbackCount = await harness.audio.inboundPlayback.count

    await harness.inbound.emit(.success(.outputAudio(
        audioDelta(constantPCM16(2_000, samples: 240))
    )))
    _ = await harness.coordinator.currentState()
    #expect(await harness.audio.inboundPlayback.count == playbackCount)
    await harness.coordinator.stop()
}

@Test
func stopWhileAudioStartIsPendingInvalidatesTheOldStart() async throws {
    let audio = CoordinatorAudioEngineFake(blocksStart: true)
    let sessions = [
        CoordinatorSessionFake(),
        CoordinatorSessionFake(),
    ]
    let builder = CoordinatorSessionBuilderFake(sessions: sessions)
    let coordinator = TranslationCoordinator(
        audioEngine: audio,
        sessionBuilder: builder
    )
    let startTask = Task {
        try await coordinator.start(configuration: coordinatorConfiguration())
    }

    #expect(await eventually {
        await audio.startConfigurations.count == 1
    })
    await coordinator.stop()
    await audio.releaseStarts()
    try await startTask.value

    #expect(await builder.requests.isEmpty)
    #expect(await coordinator.state == TranslationCoordinatorState())
    await coordinator.stop()
}

@Test
func stopWhileOutboundHandshakeIsPendingClosesAllStartupSessions() async throws {
    let inbound = BlockingCoordinatorSession()
    let outbound = BlockingCoordinatorSession()
    let coordinator = TranslationCoordinator(
        audioEngine: CoordinatorAudioEngineFake(),
        sessionBuilder: BlockingCoordinatorSessionBuilder(
            sessions: [inbound, outbound]
        )
    )
    let startTask = Task {
        try await coordinator.start(configuration: coordinatorConfiguration())
    }

    #expect(await eventually {
        let inboundCount = await inbound.connectCount
        let outboundCount = await outbound.connectCount
        return inboundCount == 1 && outboundCount == 1
    })
    await inbound.releaseConnections()
    #expect(await eventually {
        await coordinator.state.inbound == .active
    })

    await coordinator.stop()
    #expect(await inbound.closeCount == 1)
    #expect(await outbound.closeCount == 1)

    await outbound.releaseConnections()
    try await startTask.value
    #expect(await coordinator.state == TranslationCoordinatorState())
    #expect(await inbound.closeCount == 1)
    #expect(await outbound.closeCount == 1)
    await coordinator.stop()
}

@Test
func secondStartCannotRunInParallelWithPendingStart() async throws {
    let audio = CoordinatorAudioEngineFake(blocksStart: true)
    let builder = CoordinatorSessionBuilderFake(
        sessions: [
            CoordinatorSessionFake(),
            CoordinatorSessionFake(),
            CoordinatorSessionFake(),
            CoordinatorSessionFake(),
        ]
    )
    let coordinator = TranslationCoordinator(
        audioEngine: audio,
        sessionBuilder: builder
    )
    let firstStart = Task {
        try await coordinator.start(configuration: coordinatorConfiguration())
    }
    #expect(await eventually {
        await audio.startConfigurations.count == 1
    })

    let secondCompletion = CoordinatorCompletionRecorder()
    let secondStart = Task {
        try await coordinator.start(configuration: coordinatorConfiguration())
        await secondCompletion.complete()
    }

    #expect(await eventually {
        await secondCompletion.isComplete
    })
    #expect(await audio.startConfigurations.count == 1)

    await audio.releaseStarts()
    try await firstStart.value
    try await secondStart.value
    await coordinator.stop()
}

@Test
func coordinatorPublishesSeparateInboundAndOutboundLevels() async throws {
    let harness = CoordinatorHarness()
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(
        levelMeterPCM16(amplitude: 14_000)
    ))
    let inbound = await harness.nextAudioLevelEvent()
    #expect(inbound.inbound > 0)
    #expect(inbound.outbound == 0)

    harness.levelClock.advance(milliseconds: 34)
    await harness.audio.emit(.outboundNetworkAudio(
        levelMeterPCM16(amplitude: 18_000)
    ))
    let outbound = await harness.nextAudioLevelEvent()
    #expect(outbound.inbound > 0)
    #expect(outbound.outbound > 0)
}

@Test
func localAudioLevelIsPublishedWhileRealtimeConnectionsArePending() async throws {
    let audio = CoordinatorAudioEngineFake()
    let session = BlockingCoordinatorSession()
    let coordinator = TranslationCoordinator(
        audioEngine: audio,
        sessionBuilder: RepeatingCoordinatorSessionBuilder(session: session)
    )
    let configuration = TranslationCoordinatorConfiguration(
        apiConfiguration: .default,
        preferences: TranslationPreferences(
            motherLanguage: .chinese,
            meetingOutputLanguage: .german
        ),
        audioConfiguration: AudioEngineConfiguration(
            selection: coordinatorAudioSelection()
        ),
        apiKey: "test-key",
        audioStability: .legacy
    )
    let recorder = AudioLevelEventRecorder()
    let eventTask = Task {
        while !Task.isCancelled {
            let event = await coordinator.nextEvent()
            await recorder.record(event)
            if event == .stopped { return }
        }
    }
    let startTask = Task {
        try await coordinator.start(configuration: configuration)
    }

    #expect(await eventually {
        await audio.startConfigurations.count == 1
    })
    #expect(await eventually { await session.connectCount == 2 })
    await audio.emit(.outboundNetworkAudio(
        levelMeterPCM16(amplitude: 18_000)
    ))
    let receivedBeforeConnections = await eventually {
        await recorder.receivedAudioLevel
    }

    await session.releaseConnections()
    try await startTask.value
    await coordinator.stop()
    await eventTask.value

    #expect(receivedBeforeConnections)
}

@Test
func startupAudioWaitsForHandshakeBeforeReachingSessions() async throws {
    let audio = CoordinatorAudioEngineFake()
    let session = BlockingCoordinatorSession()
    let coordinator = TranslationCoordinator(
        audioEngine: audio,
        sessionBuilder: RepeatingCoordinatorSessionBuilder(session: session)
    )
    let configuration = TranslationCoordinatorConfiguration(
        apiConfiguration: .default,
        preferences: TranslationPreferences(
            motherLanguage: .chinese,
            meetingOutputLanguage: .german
        ),
        audioConfiguration: AudioEngineConfiguration(
            selection: coordinatorAudioSelection()
        ),
        apiKey: "test-key",
        audioStability: .legacy
    )
    let startTask = Task {
        try await coordinator.start(configuration: configuration)
    }

    #expect(await eventually {
        await session.connectCount == 2
    })
    await audio.emit(.inboundNetworkAudio(voicedPCM16()))
    await audio.emit(.outboundNetworkAudio(voicedPCM16()))
    _ = await coordinator.currentState()

    #expect(await session.appendCount == 0)
    #expect(await coordinator.state.inbound == .connecting)
    #expect(await coordinator.state.outbound == .connecting)

    await session.releaseConnections()
    try await startTask.value
    await coordinator.stop()
}

@Test
func connectedInboundStartsBeforeOutboundHandshakeCompletes() async throws {
    let audio = CoordinatorAudioEngineFake()
    let inbound = BlockingCoordinatorSession()
    let outbound = BlockingCoordinatorSession()
    let coordinator = TranslationCoordinator(
        audioEngine: audio,
        sessionBuilder: BlockingCoordinatorSessionBuilder(
            sessions: [inbound, outbound]
        )
    )
    let configuration = TranslationCoordinatorConfiguration(
        apiConfiguration: .default,
        preferences: TranslationPreferences(
            motherLanguage: .chinese,
            meetingOutputLanguage: .german
        ),
        audioConfiguration: AudioEngineConfiguration(
            selection: coordinatorAudioSelection()
        ),
        apiKey: "test-key",
        audioStability: .legacy
    )
    let startTask = Task {
        try await coordinator.start(configuration: configuration)
    }

    #expect(await eventually {
        let inboundCount = await inbound.connectCount
        let outboundCount = await outbound.connectCount
        return inboundCount == 1 && outboundCount == 1
    })
    await inbound.releaseConnections()
    let inboundBecameActive = await eventually {
        await coordinator.state.inbound == .active
    }
    await audio.emit(.inboundNetworkAudio(voicedPCM16()))
    let inboundReceivedAudio = await eventually {
        await inbound.appendCount == 1
    }

    await outbound.releaseConnections()
    try await startTask.value
    await coordinator.stop()

    #expect(inboundBecameActive)
    #expect(inboundReceivedAudio)
}

@Test
func preActivePartialAudioDoesNotLeakIntoFirstLiveFrame() async throws {
    let audio = CoordinatorAudioEngineFake()
    let inbound = BlockingCoordinatorSession()
    let coordinator = TranslationCoordinator(
        audioEngine: audio,
        sessionBuilder: RepeatingCoordinatorSessionBuilder(session: inbound)
    )
    let configuration = TranslationCoordinatorConfiguration(
        apiConfiguration: .default,
        preferences: TranslationPreferences(
            motherLanguage: .chinese,
            meetingOutputLanguage: .chinese
        ),
        audioConfiguration: AudioEngineConfiguration(
            selection: coordinatorAudioSelection()
        ),
        apiKey: "test-key",
        audioStability: .legacy
    )
    let startTask = Task {
        try await coordinator.start(configuration: configuration)
    }

    #expect(await eventually { await inbound.connectCount == 1 })
    await audio.emit(.inboundNetworkAudio(
        Data(repeating: 0x11, count: 2_000)
    ))
    _ = await coordinator.currentState()

    await inbound.releaseConnections()
    try await startTask.value
    let liveFrame = Data(repeating: 0x22, count: PCMFrameBatcher().frameByteCount)
    await audio.emit(.inboundNetworkAudio(liveFrame))
    #expect(await eventually { await inbound.appendCount == 1 })
    let appended = await inbound.appended
    await coordinator.stop()

    #expect(appended == [liveFrame])
}

@Test
func reconnectDropsPartialAudioFromThePreviousSession() async throws {
    let reconnectedInbound = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        additionalSessions: [reconnectedInbound],
        reconnectDelays: [.zero]
    )
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(
        Data(repeating: 0x11, count: 2_000)
    ))
    _ = await harness.coordinator.currentState()
    #expect(await harness.inbound.appended.isEmpty)

    await harness.inbound.emit(.failure(.disconnected))
    #expect(await eventually {
        let requestCount = await harness.builder.requests.count
        let inboundState = await harness.coordinator.state.inbound
        return requestCount == 3 && inboundState == .active
    })

    let liveFrame = Data(repeating: 0x22, count: PCMFrameBatcher().frameByteCount)
    await harness.audio.emit(.inboundNetworkAudio(liveFrame))
    #expect(await eventually {
        await reconnectedInbound.appended.count == 1
    })
    let appended = await reconnectedInbound.appended
    await harness.coordinator.stop()

    #expect(appended == [liveFrame])
}

@Test
func audioLevelEventsAreThrottledAndQueuedSnapshotsAreCoalesced() async throws {
    let harness = CoordinatorHarness()
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(
        levelMeterPCM16(amplitude: 8_000, sampleCount: 4_800)
    ))
    harness.levelClock.advance(milliseconds: 34)
    await harness.audio.emit(.inboundNetworkAudio(
        levelMeterPCM16(amplitude: 12_000, sampleCount: 4_800)
    ))
    harness.levelClock.advance(milliseconds: 34)
    await harness.audio.emit(.inboundNetworkAudio(
        levelMeterPCM16(amplitude: 18_000, sampleCount: 4_800)
    ))
    #expect(
        await eventually {
            await harness.inbound.appended.count == 3
        }
    )

    let latest = await harness.nextAudioLevelEvent()
    #expect(latest.inbound > 0.2)
}

@Test
func audioLevelEventsRespectTheMinimumPublishInterval() async throws {
    let harness = CoordinatorHarness()
    try await harness.start()
    await harness.drainStartupEvents()

    await harness.audio.emit(.outboundNetworkAudio(
        levelMeterPCM16(amplitude: 8_000, sampleCount: 4_800)
    ))
    #expect(
        await eventually {
            await harness.outbound.appended.count == 1
        }
    )
    let first = await harness.nextAudioLevelEvent()

    await harness.audio.emit(.outboundNetworkAudio(
        levelMeterPCM16(amplitude: 12_000, sampleCount: 4_800)
    ))
    #expect(
        await eventually {
            await harness.outbound.appended.count == 2
        }
    )
    await harness.audio.emit(.outputBackpressure(
        role: .physicalOutput,
        droppedFrames: 7
    ))
    #expect(await harness.coordinator.nextEvent() ==
        .audioBackpressure(droppedFrames: 7))

    harness.levelClock.advance(milliseconds: 34)
    await harness.audio.emit(.outboundNetworkAudio(
        levelMeterPCM16(amplitude: 18_000, sampleCount: 4_800)
    ))
    #expect(
        await eventually {
            await harness.outbound.appended.count == 3
        }
    )
    let third = await harness.nextAudioLevelEvent()
    #expect(third.outbound > first.outbound)
}

@Test
func audioLevelsNeverEvictControlsFromAFullQueue() async throws {
    let harness = CoordinatorHarness()
    try await harness.start()
    await harness.drainStartupEvents()

    for droppedFrames in 0..<128 {
        await harness.audio.emit(.outputBackpressure(
            role: .physicalOutput,
            droppedFrames: droppedFrames
        ))
    }
    await harness.audio.emit(.outboundNetworkAudio(
        levelMeterPCM16(amplitude: 18_000, sampleCount: 4_800)
    ))
    #expect(
        await eventually {
            await harness.outbound.appended.count == 1
        }
    )

    #expect(await harness.coordinator.nextEvent() ==
        .audioBackpressure(droppedFrames: 0))
}

@Test
func disablingAudioLevelUpdatesDropsQueuedSnapshots() async throws {
    let harness = CoordinatorHarness()
    try await harness.start()
    await harness.drainStartupEvents()

    await harness.audio.emit(.outboundNetworkAudio(
        levelMeterPCM16(amplitude: 18_000, sampleCount: 4_800)
    ))
    #expect(
        await eventually {
            await harness.outbound.appended.count == 1
        }
    )
    await harness.coordinator.setAudioLevelUpdatesEnabled(false)
    await harness.audio.emit(.outputBackpressure(
        role: .physicalOutput,
        droppedFrames: 3
    ))

    #expect(await harness.coordinator.nextEvent() ==
        .audioBackpressure(droppedFrames: 3))
}

@Test
func startCreatesIndependentInboundAndOutboundSessions() async throws {
    let harness = CoordinatorHarness()

    try await harness.coordinator.start(configuration: harness.configuration)

    let requests = await harness.builder.requests
    #expect(requests.map(\.configuration.targetLanguage) == [.chinese, .german])
    #expect(
        requests.first?.configuration.inputTranscriptionModel
            == "gpt-realtime-whisper"
    )
    #expect(requests.last?.configuration.inputTranscriptionModel == nil)
    await harness.coordinator.stop()
}

@Test
func matchingLanguagesUseLocalOutboundBypassWithoutSecondSession() async throws {
    let harness = CoordinatorHarness(
        preferences: TranslationPreferences(
            motherLanguage: .english,
            meetingOutputLanguage: .english
        )
    )

    try await harness.coordinator.start(configuration: harness.configuration)

    #expect(await harness.builder.requests.count == 1)
    #expect(await harness.audio.lastRouting?.1 == .originalBypass)

    await harness.coordinator.setOutboundBypass(false)

    #expect(await harness.coordinator.state.outbound == .bypassed)
    #expect(await harness.audio.lastRouting?.1 == .originalBypass)
    #expect(await harness.builder.requests.count == 1)
    await harness.coordinator.stop()
}

@Test
func inboundFailureFailsOpenWithoutStoppingOutbound() async throws {
    let harness = CoordinatorHarness(inboundError: .disconnected)

    try await harness.coordinator.start(configuration: harness.configuration)
    await harness.audio.emit(
        .outboundNetworkAudio(Data(repeating: 2, count: 9_600))
    )

    #expect(await harness.audio.lastRouting?.0 == .originalFailOpen)
    #expect(await harness.audio.lastRouting?.1 == .translated)
    #expect(
        await eventually {
            await harness.outbound.appended.count == 1
        }
    )
    await harness.coordinator.stop()
}

@Test
func audioEventsAreBatchedAndSentToTheirOwnSessions() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.audio.emit(
        .inboundNetworkAudio(Data(repeating: 1, count: 9_600))
    )
    await harness.audio.emit(
        .outboundNetworkAudio(Data(repeating: 2, count: 9_600))
    )

    #expect(
        await eventually {
            let inboundCount = await harness.inbound.appended.count
            let outboundCount = await harness.outbound.appended.count
            return inboundCount == 1 && outboundCount == 1
        }
    )
    #expect(await harness.inbound.appended == [Data(repeating: 1, count: 9_600)])
    #expect(await harness.outbound.appended == [Data(repeating: 2, count: 9_600)])
    await harness.coordinator.stop()
}

@Test
func transcriptSelectsExactlyOneInboundCandidate() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.audio.emit(.inboundNetworkAudio(voicedPCM16()))
    #expect(
        await eventually {
            await harness.inbound.appended.count == 1
        }
    )
    await harness.inbound.emit(
        .success(.outputAudio(audioDelta(Data([2, 2]))))
    )
    await harness.inbound.emit(
        .success(.inputTranscript(transcriptDelta("Deutsch")))
    )

    #expect(
        await eventually {
            await harness.audio.inboundPlayback == [Data([2, 2])]
        }
    )
    #expect(
        await harness.audio.inboundPlayback == [Data([2, 2])]
    )
    await harness.coordinator.stop()
}

@Test
func lateContinuousTranslationDeltasExtendTheInboundTailWindow() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.audio.emit(.inboundNetworkAudio(voicedPCM16()))
    #expect(
        await eventually {
            await harness.inbound.appended.count == 1
        }
    )
    await harness.inbound.emit(
        .success(.outputAudio(audioDelta(Data([1, 1]))))
    )
    await harness.inbound.emit(
        .success(.inputTranscript(transcriptDelta("Deutsch")))
    )
    #expect(
        await eventually {
            await harness.audio.inboundPlayback == [Data([1, 1])]
        }
    )

    for _ in 0..<30 {
        await harness.audio.emit(
            .inboundNetworkAudio(Data(repeating: 0, count: 9_600))
        )
    }
    #expect(
        await eventually {
            await harness.inbound.appended.count == 31
        }
    )

    try await Task.sleep(for: .milliseconds(350))
    await harness.inbound.emit(
        .success(.outputAudio(audioDelta(Data([2, 2]))))
    )
    #expect(
        await eventually {
            await harness.audio.inboundPlayback.last == Data([2, 2])
        }
    )

    try await Task.sleep(for: .milliseconds(250))
    await harness.inbound.emit(
        .success(.outputAudio(audioDelta(Data([3, 3]))))
    )

    #expect(
        await eventually {
            await harness.audio.inboundPlayback.last == Data([3, 3])
        }
    )
    await harness.coordinator.stop()
}

@Test
func failedInboundSessionReconnectsWithoutRestartingOutbound() async throws {
    let recoveredInbound = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        inboundError: .disconnected,
        additionalSessions: [recoveredInbound],
        reconnectDelays: [.zero]
    )

    try await harness.coordinator.start(configuration: harness.configuration)

    #expect(
        await eventually {
            let requestCount = await harness.builder.requests.count
            let inboundState = await harness.coordinator.state.inbound
            return requestCount == 3 && inboundState == .active
        }
    )
    #expect(await harness.outbound.closeCount == 0)
    await harness.coordinator.stop()
}

@Test
func failedInitialConnectClosesTheRejectedSession() async throws {
    let rejected = CoordinatorSessionFake(connectError: .disconnected)
    let recovered = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        inbound: rejected,
        additionalSessions: [recovered],
        reconnectDelays: [.zero]
    )

    try await harness.start()
    #expect(await eventually {
        let requestCount = await harness.builder.requests.count
        let inboundState = await harness.coordinator.state.inbound
        return requestCount == 3 && inboundState == .active
    })

    #expect(await rejected.closeCount == 1)
    await harness.coordinator.stop()
}

@Test
func appendFailureClosesTheReplacedSession() async throws {
    let replaced = CoordinatorSessionFake(
        appendErrors: [.disconnected],
        emitsClosedOnClose: false
    )
    let recovered = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        inbound: replaced,
        additionalSessions: [recovered],
        reconnectDelays: [.zero]
    )
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(voicedPCM16()))
    #expect(await eventually {
        let requestCount = await harness.builder.requests.count
        let inboundState = await harness.coordinator.state.inbound
        return requestCount == 3 && inboundState == .active
    })

    #expect(await replaced.closeCount == 1)
    await harness.coordinator.stop()
}

@Test
func failedReconnectConnectClosesEveryRejectedSession() async throws {
    let replaced = CoordinatorSessionFake(
        appendErrors: [.disconnected],
        emitsClosedOnClose: false
    )
    let rejectedReconnect = CoordinatorSessionFake(
        connectError: .disconnected
    )
    let recovered = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        inbound: replaced,
        additionalSessions: [rejectedReconnect, recovered],
        reconnectDelays: [.zero, .zero]
    )
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(voicedPCM16()))
    #expect(await eventually {
        let requestCount = await harness.builder.requests.count
        let inboundState = await harness.coordinator.state.inbound
        return requestCount == 4 && inboundState == .active
    })

    #expect(await replaced.closeCount == 1)
    #expect(await rejectedReconnect.closeCount == 1)
    await harness.coordinator.stop()
}

@Test
func inboundFailureAfterSpeechEndedStillCompletesRecovery() async throws {
    let recovered = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        additionalSessions: [recovered],
        reconnectDelays: [.zero]
    )
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(voicedPCM16()))
    for _ in 0..<30 {
        await harness.audio.emit(
            .inboundNetworkAudio(Data(repeating: 0, count: 9_600))
        )
    }
    #expect(await eventually {
        await harness.inbound.appended.count == 31
    })

    await harness.inbound.emit(.failure(.disconnected))
    #expect(await eventually {
        let requestCount = await harness.builder.requests.count
        let inboundState = await harness.coordinator.state.inbound
        return requestCount == 3 && inboundState == .active
    })
    #expect(await harness.audio.lastRouting?.0 == .originalFailOpen)
    #expect(await eventually {
        await harness.audio.lastRouting?.0 == .translated
    })
    await harness.coordinator.stop()
}

@Test
func inboundFailureWhileFinishPlaybackIsInFlightStillCompletesRecovery()
    async throws
{
    let audio = CoordinatorAudioEngineFake()
    let recovered = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        audio: audio,
        additionalSessions: [recovered],
        reconnectDelays: [.zero]
    )
    try await harness.start()

    await harness.audio.emit(
        .inboundNetworkAudio(voicedPCM16(byteCount: 7_500))
    )
    for _ in 0..<30 {
        await harness.audio.emit(
            .inboundNetworkAudio(Data(repeating: 0, count: 7_500))
        )
    }
    #expect(await eventually {
        await harness.inbound.appended.count == 24
    })
    await audio.blockNextInboundOutput()
    #expect(await eventually {
        await audio.blockedInboundOutputCount == 1
    })
    let playbackCountBeforeRelease = await audio.inboundPlayback.count

    await harness.inbound.emit(.failure(.disconnected))
    #expect(await eventually {
        let requestCount = await harness.builder.requests.count
        let inboundState = await harness.coordinator.state.inbound
        return requestCount == 3 && inboundState == .active
    })
    #expect(await harness.audio.lastRouting?.0 == .originalFailOpen)

    await audio.releaseInboundOutputs()
    #expect(await eventually {
        await harness.audio.lastRouting?.0 == .translated
    })
    #expect(
        await audio.inboundPlayback.count <= playbackCountBeforeRelease + 1
    )
    await harness.coordinator.stop()
}

@Test
func sameEpochFinishReplacementStopsOldDrainingChunks() async throws {
    let audio = CoordinatorAudioEngineFake()
    let harness = CoordinatorHarness(audio: audio)
    try await harness.start()

    let firstChunk = voicedPCM16(byteCount: 7_500)
    let remainingChunk = Data(repeating: 0, count: 7_500)
    await audio.emit(.inboundNetworkAudio(firstChunk))
    for _ in 0..<30 {
        await audio.emit(.inboundNetworkAudio(remainingChunk))
    }
    #expect(await eventually {
        await harness.inbound.appended.count == 24
    })
    await audio.blockNextInboundOutputs(2)
    #expect(await eventually {
        await audio.blockedInboundOutputCount == 1
    })
    let playbackCountBeforeRelease = await audio.inboundPlayback.count

    let probe = CoordinatorReaderDispositionProbe()
    await harness.coordinator.setReaderDispositionObserver {
        channel,
        epoch,
        disposition in
        await probe.record(
            channel: channel,
            epoch: epoch,
            disposition: disposition
        )
    }
    await harness.inbound.emit(
        .success(.outputTranscript(transcriptDelta("late translation")))
    )
    let observedRecord = await probe.nextRecord()
    let record = try #require(
        observedRecord,
        "Coordinator did not acknowledge the same-epoch server event"
    )
    #expect(record.channel == .inbound)
    #expect(record.disposition == .accepted)
    #expect(await eventually {
        await harness.coordinator.state.subtitles.inboundTranslation
            == "late translation"
    })
    let routingCountBeforeRelease = await audio.routings.count

    await audio.releaseInboundOutputs()
    #expect(await eventually {
        await audio.routings.count > routingCountBeforeRelease
    })
    #expect(await audio.blockedInboundOutputCount == 1)
    let finishPlayback = Array(
        (await audio.inboundPlayback).dropFirst(playbackCountBeforeRelease)
    )
    #expect(finishPlayback == [firstChunk])
    #expect(!finishPlayback.contains(remainingChunk))
    await audio.releaseInboundOutputs()
    await harness.coordinator.stop()
}

@Test
func staleInboundAudioFromPreviousEpochCannotReachPlayback() async throws {
    let stale = CoordinatorSessionFake(emitsClosedOnClose: false)
    let recovered = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        inbound: stale,
        additionalSessions: [recovered],
        reconnectDelays: [.zero]
    )
    try await harness.start()

    await harness.audio.emit(.inboundNetworkAudio(voicedPCM16()))
    await stale.emit(
        .success(.inputTranscript(transcriptDelta("Deutsch")))
    )
    #expect(await eventually {
        await harness.coordinator.state.subtitles.inboundSource == "Deutsch"
    })
    await stale.failNextAppend(with: .disconnected)
    await harness.audio.emit(.inboundNetworkAudio(voicedPCM16()))
    #expect(
        await eventually {
            let requestCount = await harness.builder.requests.count
            let inboundState = await harness.coordinator.state.inbound
            return requestCount == 3 && inboundState == .active
        }
    )

    let probe = CoordinatorReaderDispositionProbe()
    await harness.coordinator.setReaderDispositionObserver {
        channel,
        epoch,
        disposition in
        await probe.record(
            channel: channel,
            epoch: epoch,
            disposition: disposition
        )
    }
    await stale.emit(.success(.outputAudio(audioDelta(Data([9, 9])))))
    let observedRecord = await probe.nextRecord()
    let record = try #require(
        observedRecord,
        "Coordinator did not acknowledge the stale inbound event"
    )
    #expect(record.channel == .inbound)
    #expect(record.disposition == .stale)
    #expect(!(await harness.audio.inboundPlayback).contains(Data([9, 9])))
    await harness.coordinator.stop()
}

@Test
func staleOutboundAudioFromPreviousEpochCannotReachPlayback() async throws {
    let stale = CoordinatorSessionFake(
        appendErrors: [.disconnected],
        emitsClosedOnClose: false
    )
    let recovered = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        outbound: stale,
        additionalSessions: [recovered],
        reconnectDelays: [.zero]
    )
    try await harness.start()

    await harness.audio.emit(
        .outboundNetworkAudio(Data(repeating: 1, count: 9_600))
    )
    #expect(
        await eventually {
            let requestCount = await harness.builder.requests.count
            let outboundState = await harness.coordinator.state.outbound
            return requestCount == 3 && outboundState == .active
        }
    )

    let probe = CoordinatorReaderDispositionProbe()
    await harness.coordinator.setReaderDispositionObserver {
        channel,
        epoch,
        disposition in
        await probe.record(
            channel: channel,
            epoch: epoch,
            disposition: disposition
        )
    }
    await stale.emit(.success(.outputAudio(audioDelta(Data([8, 8])))))
    let observedRecord = await probe.nextRecord()
    let record = try #require(
        observedRecord,
        "Coordinator did not acknowledge the stale outbound event"
    )
    #expect(record.channel == .outbound)
    #expect(record.disposition == .stale)
    #expect(!(await harness.audio.outboundPlayback).contains(Data([8, 8])))
    await harness.coordinator.stop()
}

@Test
func inboundReconnectDoesNotInvalidateCurrentOutboundEpoch() async throws {
    let staleInbound = CoordinatorSessionFake(
        appendErrors: [.disconnected]
    )
    let recoveredInbound = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        inbound: staleInbound,
        additionalSessions: [recoveredInbound],
        reconnectDelays: [.zero]
    )
    try await harness.start()

    await harness.audio.emit(
        .inboundNetworkAudio(voicedPCM16())
    )
    #expect(
        await eventually {
            let requestCount = await harness.builder.requests.count
            let inboundState = await harness.coordinator.state.inbound
            return requestCount == 3 && inboundState == .active
        }
    )

    let currentOutboundAudio = Data([7, 7])
    await harness.outbound.emit(
        .success(.outputAudio(audioDelta(currentOutboundAudio)))
    )
    #expect(
        await eventually {
            await harness.audio.outboundPlayback.contains(currentOutboundAudio)
        }
    )
    await harness.coordinator.stop()
}

@Test
func stopGracefullyClosesBothSessions() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.coordinator.stop()

    #expect(await harness.inbound.closeCount == 1)
    #expect(await harness.outbound.closeCount == 1)
}

@Test
func stopInvalidatesTailEventsFromClosingSessions() async throws {
    let tail = Data([9, 9])
    let harness = CoordinatorHarness(
        outboundCloseEvents: [.outputAudio(audioDelta(tail))]
    )
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.coordinator.stop()

    #expect(!(await harness.audio.outboundPlayback).contains(tail))
}

@Test
func manualBypassControlsAreExplicitAndReversible() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.coordinator.setInboundBypass(true)
    await harness.coordinator.setOutboundBypass(true)
    #expect(await harness.audio.lastRouting?.0 == .originalBypass)
    #expect(await harness.audio.lastRouting?.1 == .originalBypass)

    await harness.coordinator.setInboundBypass(false)
    await harness.coordinator.setOutboundBypass(false)
    #expect(await harness.audio.lastRouting?.0 == .translated)
    #expect(await harness.audio.lastRouting?.1 == .translated)
    await harness.coordinator.stop()
}

@Test
func inboundManualBypassSurvivesRealFailureAndReconnect() async throws {
    let recoveredInbound = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        additionalSessions: [recoveredInbound],
        reconnectDelays: [.zero]
    )
    try await harness.coordinator.start(configuration: harness.configuration)
    await harness.coordinator.setInboundBypass(true)
    #expect(await harness.audio.lastRouting?.0 == .originalBypass)

    await harness.inbound.emit(.failure(.disconnected))

    #expect(
        await eventually {
            let requestCount = await harness.builder.requests.count
            let inboundState = await harness.coordinator.state.inbound
            return requestCount == 3 && inboundState == .active
        }
    )
    #expect(await harness.audio.lastRouting?.0 == .originalBypass)
    #expect(await harness.coordinator.state.inbound == .active)
    await harness.coordinator.stop()
}

@Test
func outboundManualBypassSurvivesRealFailureAndReconnect() async throws {
    let recoveredOutbound = CoordinatorSessionFake()
    let harness = CoordinatorHarness(
        additionalSessions: [recoveredOutbound],
        reconnectDelays: [.zero]
    )
    try await harness.coordinator.start(configuration: harness.configuration)
    await harness.coordinator.setOutboundBypass(true)
    #expect(await harness.audio.lastRouting?.1 == .originalBypass)

    await harness.outbound.emit(.failure(.disconnected))

    #expect(
        await eventually {
            let requestCount = await harness.builder.requests.count
            let outboundState = await harness.coordinator.state.outbound
            return requestCount == 3 && outboundState == .active
        }
    )
    #expect(await harness.audio.lastRouting?.1 == .originalBypass)
    #expect(await harness.coordinator.state.outbound == .active)
    await harness.coordinator.stop()
}
