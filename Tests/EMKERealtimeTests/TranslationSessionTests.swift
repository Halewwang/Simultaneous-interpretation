import EMKECore
import Foundation
import Testing
@testable import EMKERealtime

private actor FakeSocket: TranslationSocket {
    private(set) var sent: [Data] = []
    private(set) var wasCancelled = false
    private(set) var receivedCount = 0
    private var incoming: [Data]
    private var receiveWaiters: [CheckedContinuation<Data, any Error>] = []
    private var closeSendWaiter: CheckedContinuation<Void, Never>?
    private let acknowledgesClose: Bool
    private let waitsForCancellationBeforeCloseSendReturns: Bool

    init(
        incoming: [Data] = [],
        acknowledgesClose: Bool = true,
        waitsForCancellationBeforeCloseSendReturns: Bool = false
    ) {
        self.incoming = incoming
        self.acknowledgesClose = acknowledgesClose
        self.waitsForCancellationBeforeCloseSendReturns =
            waitsForCancellationBeforeCloseSendReturns
    }

    func send(_ data: Data) async throws {
        sent.append(data)
        if acknowledgesClose,
           String(decoding: data, as: UTF8.self).contains("session.close") {
            enqueue(Data(#"{"type":"session.closed"}"#.utf8))
            if waitsForCancellationBeforeCloseSendReturns {
                await withCheckedContinuation { closeSendWaiter = $0 }
            }
        }
    }

    func receive() async throws -> Data {
        if !incoming.isEmpty {
            receivedCount += 1
            return incoming.removeFirst()
        }
        return try await withCheckedThrowingContinuation { continuation in
            receiveWaiters.append(continuation)
        }
    }

    func cancel() async {
        wasCancelled = true
        closeSendWaiter?.resume()
        closeSendWaiter = nil
        let waiters = receiveWaiters
        receiveWaiters.removeAll()
        for waiter in waiters {
            waiter.resume(throwing: TranslationSocketError.disconnected)
        }
    }

    private func enqueue(_ data: Data) {
        if receiveWaiters.isEmpty {
            incoming.append(data)
        } else {
            receivedCount += 1
            receiveWaiters.removeFirst().resume(returning: data)
        }
    }
}

private struct FakeFactory: TranslationSocketFactory {
    let socket: FakeSocket

    func makeSocket(
        url: URL,
        authorization: String
    ) async throws -> any TranslationSocket {
        socket
    }
}

private actor CloseDeadlineGate {
    private var fired = false
    private var waiter: CheckedContinuation<Void, Never>?
    private(set) var waitCount = 0

    func wait() async {
        waitCount += 1
        guard !fired else { return }
        await withCheckedContinuation { waiter = $0 }
    }

    func fire() {
        guard !fired else { return }
        fired = true
        waiter?.resume()
        waiter = nil
    }
}

private let handshakeEvents = [
    Data(
        #"{"type":"session.created","session":{"model":"gpt-realtime-translate"}}"#.utf8
    ),
    Data(#"{"type":"session.updated"}"#.utf8),
]

private let tailAudioEvent = Data(
    #"{"type":"session.output_audio.delta","delta":"AAEC","sample_rate":24000,"channels":1,"format":"pcm16"}"#.utf8
)

private let expectedTailAudio = TranslationServerEvent.outputAudio(
    TranslationAudioDelta(
        data: Data([0, 1, 2]),
        sampleRate: 24_000,
        channels: 1,
        format: "pcm16",
        elapsedMilliseconds: nil
    )
)

private func makeSession(
    socket: FakeSocket,
    closeDeadline: @escaping TranslationSession.CloseDeadline
) -> TranslationSession {
    TranslationSession(
        configuration: .default,
        sessionConfiguration: TranslationSessionConfiguration(
            targetLanguage: .chinese
        ),
        apiKey: "secret",
        factory: FakeFactory(socket: socket),
        closeDeadline: closeDeadline
    )
}

private func eventually(
    _ condition: @escaping @Sendable () async -> Bool
) async -> Bool {
    for _ in 0..<100 {
        if await condition() { return true }
        await Task.yield()
    }
    return false
}

@Test
func connectSendsLanguageBeforeAudio() async throws {
    let socket = FakeSocket(incoming: [
        Data(
            #"{"type":"session.created","session":{"model":"gpt-realtime-translate"}}"#.utf8
        ),
        Data(#"{"type":"session.updated"}"#.utf8),
    ])
    let session = TranslationSession(
        configuration: .default,
        sessionConfiguration: TranslationSessionConfiguration(
            targetLanguage: .german
        ),
        apiKey: "secret",
        factory: FakeFactory(socket: socket)
    )

    try await session.connect()
    try await session.appendAudio(Data([1, 2]))

    let sent = await socket.sent
    #expect(sent.count == 2)
    #expect(String(decoding: sent[0], as: UTF8.self).contains("session.update"))
    #expect(
        String(decoding: sent[1], as: UTF8.self)
            .contains("session.input_audio_buffer.append")
    )
    #expect(await socket.receivedCount == 2)
    try await session.close()
}

@Test
func closeLetsTheSingleReaderDeliverTailAudioBeforeClosed() async throws {
    let socket = FakeSocket(
        incoming: [
            Data(
                #"{"type":"session.created","session":{"model":"gpt-realtime-translate"}}"#.utf8
            ),
            Data(#"{"type":"session.updated"}"#.utf8),
            Data(
                #"{"type":"session.output_audio.delta","delta":"AAEC","sample_rate":24000,"channels":1,"format":"pcm16"}"#.utf8
            ),
        ]
    )
    let session = TranslationSession(
        configuration: .default,
        sessionConfiguration: TranslationSessionConfiguration(
            targetLanguage: .chinese,
            inputTranscriptionModel: "gpt-realtime-whisper"
        ),
        apiKey: "secret",
        factory: FakeFactory(socket: socket)
    )

    try await session.connect()
    async let next = session.nextEvent()
    async let closed: Void = session.close()

    #expect(
        try await next == .outputAudio(
            TranslationAudioDelta(
                data: Data([0, 1, 2]),
                sampleRate: 24_000,
                channels: 1,
                format: "pcm16",
                elapsedMilliseconds: nil
            )
        )
    )
    try await closed

    let sent = await socket.sent
    #expect(String(decoding: sent.last!, as: UTF8.self).contains("session.close"))
    #expect(await socket.wasCancelled)
}

@Test
func closeCancelsSocketWhenServerDoesNotAcknowledgeDeadline() async throws {
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(
        incoming: handshakeEvents,
        acknowledgesClose: false
    )
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    let closeTask = Task { try await session.close() }
    #expect(await eventually { await socket.sent.count == 2 })
    await deadline.fire()
    try await closeTask.value

    #expect(await eventually { await socket.wasCancelled })
}

@Test
func serverCloseBeforeDeadlineKeepsTailAudioAndDoesNotWaitForDeadline()
    async throws
{
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(incoming: handshakeEvents + [tailAudioEvent])
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    async let event = session.nextEvent()
    try await session.close()

    #expect(try await event == expectedTailAudio)
    #expect(await socket.wasCancelled)
    await deadline.fire()
}

@Test
func fastServerCloseDuringSendDoesNotArmDeadline() async throws {
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(
        incoming: handshakeEvents,
        waitsForCancellationBeforeCloseSendReturns: true
    )
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    try await session.close()

    #expect(await socket.wasCancelled)
    #expect(!(await eventually { await deadline.waitCount > 0 }))
}

@Test
func connectRejectsUnexpectedHandshakeWithoutKeepingSocket() async throws {
    let socket = FakeSocket(
        incoming: [Data(#"{"type":"session.updated"}"#.utf8)]
    )
    let session = TranslationSession(
        configuration: .default,
        sessionConfiguration: TranslationSessionConfiguration(
            targetLanguage: .english
        ),
        apiKey: "secret",
        factory: FakeFactory(socket: socket)
    )

    await #expect(throws: TranslationSessionError.self) {
        try await session.connect()
    }
    #expect(await socket.wasCancelled)
}
