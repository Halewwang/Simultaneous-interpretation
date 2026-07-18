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

    init(incoming: [Data] = []) {
        self.incoming = incoming
    }

    func send(_ data: Data) async throws {
        sent.append(data)
        if String(decoding: data, as: UTF8.self).contains("session.close") {
            enqueue(Data(#"{"type":"session.closed"}"#.utf8))
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
