import EMKECore
import EMKERealtime
import Foundation
import Testing
@testable import EMKECoordinator

private enum ProbeTestError: Error {
    case endpointUnavailable
}

private actor ProbeSessionFake: TranslationSessionControlling {
    private let connectError: (any Error)?
    private let closingEvents: [TranslationServerEvent]
    private var queuedEvents: [TranslationServerEvent] = []
    private var waiters: [
        CheckedContinuation<TranslationServerEvent, any Error>
    ] = []
    private(set) var appended: [Data] = []
    private(set) var closeCount = 0

    init(
        connectError: (any Error)? = nil,
        closingEvents: [TranslationServerEvent] = []
    ) {
        self.connectError = connectError
        self.closingEvents = closingEvents
    }

    func connect() async throws {
        if let connectError { throw connectError }
    }

    func appendAudio(_ pcm16: Data) async throws {
        appended.append(pcm16)
    }

    func nextEvent() async throws -> TranslationServerEvent {
        if !queuedEvents.isEmpty {
            return queuedEvents.removeFirst()
        }
        return try await withCheckedThrowingContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func close() async throws {
        closeCount += 1
        for event in closingEvents + [.closed] {
            emit(event)
        }
    }

    private func emit(_ event: TranslationServerEvent) {
        if waiters.isEmpty {
            queuedEvents.append(event)
        } else {
            waiters.removeFirst().resume(returning: event)
        }
    }
}

private actor ProbeBuilderFake: TranslationSessionBuilding {
    private var sessions: [ProbeSessionFake]

    init(sessions: [ProbeSessionFake]) {
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

private let probeConfiguration = TranslationConnectionProbeConfiguration(
    apiConfiguration: .default,
    apiKey: "test-key",
    inboundTargetLanguage: .chinese,
    outboundTargetLanguage: .german
)

private func probeAudioDelta(_ data: Data) -> TranslationAudioDelta {
    TranslationAudioDelta(
        data: data,
        sampleRate: 24_000,
        channels: 1,
        format: "pcm16",
        elapsedMilliseconds: nil
    )
}

@Test
func chatOnlyGatewayIsReportedAsTranslationHandshakeFailure() async {
    let first = ProbeSessionFake(
        connectError: ProbeTestError.endpointUnavailable
    )
    let second = ProbeSessionFake()
    let probe = TranslationConnectionProbe(
        sessionBuilder: ProbeBuilderFake(sessions: [first, second])
    )

    let report = await probe.run(configuration: probeConfiguration)

    #expect(
        report.handshake
            == .failed(.translationEndpointUnavailable)
    )
    #expect(report.authentication == .notRun)
    #expect(report.targetLanguage == .notRun)
    #expect(!report.isFullyCompatible)
}

@Test
func missingSourceTranscriptIsNotMisreportedAsInvalidKey() async {
    let first = ProbeSessionFake(
        closingEvents: [
            .outputAudio(probeAudioDelta(Data([1, 2]))),
        ]
    )
    let second = ProbeSessionFake()
    let probe = TranslationConnectionProbe(
        sessionBuilder: ProbeBuilderFake(sessions: [first, second])
    )

    let report = await probe.run(
        configuration: probeConfiguration,
        speechSample: Data(repeating: 1, count: 9_600)
    )

    #expect(report.authentication == .passed)
    #expect(report.handshake == .passed)
    #expect(report.audioOutput == .passed)
    #expect(
        report.sourceTranscript
            == .failed(.sourceTranscriptionUnavailable)
    )
    #expect(report.gracefulClose == .passed)
}

@Test
func missingSpeechSampleRequiresInteractiveAudioTest() async {
    let first = ProbeSessionFake()
    let second = ProbeSessionFake()
    let probe = TranslationConnectionProbe(
        sessionBuilder: ProbeBuilderFake(sessions: [first, second])
    )

    let report = await probe.run(configuration: probeConfiguration)

    #expect(report.handshake == .passed)
    #expect(report.dualSession == .passed)
    #expect(report.sourceTranscript == .requiresInteractiveAudio)
    #expect(report.audioOutput == .requiresInteractiveAudio)
    #expect(!report.isFullyCompatible)
}

@Test
func completeTranslationCapabilitiesAreReportedSeparately() async {
    let first = ProbeSessionFake(
        closingEvents: [
            .inputTranscript(
                TranslationTranscriptDelta(
                    text: "hello",
                    elapsedMilliseconds: nil
                )
            ),
            .outputAudio(probeAudioDelta(Data([1, 2]))),
        ]
    )
    let second = ProbeSessionFake()
    let probe = TranslationConnectionProbe(
        sessionBuilder: ProbeBuilderFake(sessions: [first, second])
    )

    let report = await probe.run(
        configuration: probeConfiguration,
        speechSample: Data(repeating: 1, count: 9_600)
    )

    #expect(report.authentication == .passed)
    #expect(report.handshake == .passed)
    #expect(report.targetLanguage == .passed)
    #expect(report.dualSession == .passed)
    #expect(report.sourceTranscript == .passed)
    #expect(report.audioOutput == .passed)
    #expect(report.gracefulClose == .passed)
    #expect(report.isFullyCompatible)
}

@Test
func invalidAPIKeyIsReportedAsAuthenticationFailure() async {
    let first = ProbeSessionFake(
        connectError: TranslationSessionError.server(
            code: "invalid_api_key",
            message: "Authentication failed"
        )
    )
    let probe = TranslationConnectionProbe(
        sessionBuilder: ProbeBuilderFake(
            sessions: [first, ProbeSessionFake()]
        )
    )

    let report = await probe.run(configuration: probeConfiguration)

    #expect(
        report.authentication == .failed(.authenticationRejected)
    )
    #expect(report.handshake == .notRun)
}

@Test
func rejectedModelIsDistinctFromMissingTranslationEndpoint() async {
    let first = ProbeSessionFake(
        connectError: TranslationSessionError.server(
            code: "model_not_found",
            message: "Requested model is unavailable"
        )
    )
    let probe = TranslationConnectionProbe(
        sessionBuilder: ProbeBuilderFake(
            sessions: [first, ProbeSessionFake()]
        )
    )

    let report = await probe.run(configuration: probeConfiguration)

    #expect(report.authentication == .passed)
    #expect(report.handshake == .failed(.modelRejected))
}

@Test
func rejectedSessionUpdateIsReportedAsTargetLanguageFailure() async {
    let first = ProbeSessionFake(
        connectError: TranslationSessionError
            .unexpectedHandshakeEvent(
                expected: "session.updated",
                received: "error"
            )
    )
    let probe = TranslationConnectionProbe(
        sessionBuilder: ProbeBuilderFake(
            sessions: [first, ProbeSessionFake()]
        )
    )

    let report = await probe.run(configuration: probeConfiguration)

    #expect(report.authentication == .passed)
    #expect(report.handshake == .passed)
    #expect(
        report.targetLanguage == .failed(.targetLanguageRejected)
    )
}
