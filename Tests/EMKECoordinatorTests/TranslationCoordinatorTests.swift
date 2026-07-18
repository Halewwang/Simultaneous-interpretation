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
    private(set) var startConfigurations: [AudioEngineConfiguration] = []
    private(set) var routings: [(InboundOutputMode, OutboundOutputMode)] = []
    private(set) var inboundPlayback: [Data] = []
    private(set) var outboundPlayback: [Data] = []
    private var events: [AudioEngineEvent] = []
    private var waiters: [CheckedContinuation<AudioEngineEvent, Never>] = []

    func start(configuration: AudioEngineConfiguration) async throws {
        startConfigurations.append(configuration)
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

    var lastRouting: (InboundOutputMode, OutboundOutputMode)? {
        routings.last
    }
}

private actor CoordinatorSessionFake: TranslationSessionControlling {
    let connectError: CoordinatorTestError?
    private(set) var appended: [Data] = []
    private(set) var closeCount = 0
    private var events: [Result<TranslationServerEvent, CoordinatorTestError>]
    private let closeEvents: [TranslationServerEvent]
    private var waiters: [
        CheckedContinuation<TranslationServerEvent, any Error>
    ] = []

    init(
        connectError: CoordinatorTestError? = nil,
        events: [Result<TranslationServerEvent, CoordinatorTestError>] = [],
        closeEvents: [TranslationServerEvent] = []
    ) {
        self.connectError = connectError
        self.events = events
        self.closeEvents = closeEvents
    }

    func connect() async throws {
        if let connectError { throw connectError }
    }

    func appendAudio(_ pcm16: Data) async throws {
        appended.append(pcm16)
    }

    func nextEvent() async throws -> TranslationServerEvent {
        if !events.isEmpty {
            return try events.removeFirst().get()
        }
        return try await withCheckedThrowingContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func close() async throws {
        closeCount += 1
        for event in closeEvents {
            emit(.success(event))
        }
        emit(.success(.closed))
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
}

private struct SessionBuildRequest: Equatable, Sendable {
    let configuration: TranslationSessionConfiguration
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

private struct CoordinatorHarness {
    let audio = CoordinatorAudioEngineFake()
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
        inboundError: CoordinatorTestError? = nil,
        outboundCloseEvents: [TranslationServerEvent] = [],
        additionalSessions: [CoordinatorSessionFake] = [],
        reconnectDelays: [Duration] = [],
        classifier: @escaping @Sendable (String) -> LanguageHypotheses = {
            _ in LanguageHypotheses(["de": 0.9])
        }
    ) {
        inbound = CoordinatorSessionFake(connectError: inboundError)
        outbound = CoordinatorSessionFake(
            closeEvents: outboundCloseEvents
        )
        builder = CoordinatorSessionBuilderFake(
            sessions: [inbound, outbound] + additionalSessions
        )
        coordinator = TranslationCoordinator(
            audioEngine: audio,
            sessionBuilder: builder,
            languageClassifier: classifier,
            reconnectDelays: reconnectDelays
        )
        configuration = TranslationCoordinatorConfiguration(
            apiConfiguration: .default,
            preferences: preferences,
            audioConfiguration: AudioEngineConfiguration(
                selection: coordinatorAudioSelection()
            ),
            apiKey: "test-key"
        )
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
    for _ in 0..<200 {
        if await condition() { return true }
        await Task.yield()
    }
    return false
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
func stopGracefullyClosesBothSessions() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.coordinator.stop()

    #expect(await harness.inbound.closeCount == 1)
    #expect(await harness.outbound.closeCount == 1)
}

@Test
func stopDrainsTailTranslationAudioBeforeStoppingTheEngine() async throws {
    let tail = Data([9, 9])
    let harness = CoordinatorHarness(
        outboundCloseEvents: [.outputAudio(audioDelta(tail))]
    )
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.coordinator.stop()

    #expect(await harness.audio.outboundPlayback == [tail])
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
