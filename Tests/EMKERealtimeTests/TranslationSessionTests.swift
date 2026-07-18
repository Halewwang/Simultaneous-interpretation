import EMKECore
import Foundation
import Testing
@testable import EMKERealtime

private actor FakeSocket: TranslationSocket {
    private(set) var sent: [Data] = []
    private(set) var wasCancelled = false
    private var incoming: [Data]

    init(incoming: [Data] = []) {
        self.incoming = incoming
    }

    func send(_ data: Data) async throws {
        sent.append(data)
    }

    func receive() async throws -> Data {
        guard !incoming.isEmpty else {
            throw TranslationSocketError.disconnected
        }
        return incoming.removeFirst()
    }

    func cancel() async {
        wasCancelled = true
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
    let socket = FakeSocket()
    let session = TranslationSession(
        configuration: .default,
        language: .german,
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
}

@Test
func closeWaitsForSessionClosedAndCancelsSocket() async throws {
    let socket = FakeSocket(
        incoming: [Data(#"{"type":"session.closed"}"#.utf8)]
    )
    let session = TranslationSession(
        configuration: .default,
        language: .chinese,
        apiKey: "secret",
        factory: FakeFactory(socket: socket)
    )

    try await session.connect()
    try await session.close()

    let sent = await socket.sent
    #expect(String(decoding: sent.last!, as: UTF8.self).contains("session.close"))
    #expect(await socket.wasCancelled)
}
