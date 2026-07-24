import EMKECore
import Foundation
import Testing
@testable import EMKERealtime

private actor FakeSocket: TranslationSocket {
    private(set) var sent: [Data] = []
    private(set) var wasCancelled = false
    private(set) var cancellationCount = 0
    private(set) var receivedCount = 0
    private var incoming: [Data]
    private var receiveErrors: [TranslationSocketError] = []
    private var receiveWaiters: [CheckedContinuation<Data, any Error>] = []
    private var closeSendWaiter: CheckedContinuation<Void, Never>?
    private var closeSendStarted = false
    private var closeSendStartWaiter: CheckedContinuation<Void, Never>?
    private var cancellationStarted = false
    private var cancellationStartWaiter: CheckedContinuation<Void, Never>?
    private var cancellationFinishWaiters: [CheckedContinuation<Void, Never>] = []
    private let acknowledgesClose: Bool
    private let closeSendError: TranslationSocketError?
    private let blocksCloseSend: Bool
    private let waitsForCancellationToFinish: Bool

    init(
        incoming: [Data] = [],
        acknowledgesClose: Bool = true,
        closeSendError: TranslationSocketError? = nil,
        blocksCloseSend: Bool = false,
        waitsForCancellationToFinish: Bool = false
    ) {
        self.incoming = incoming
        self.acknowledgesClose = acknowledgesClose
        self.closeSendError = closeSendError
        self.blocksCloseSend = blocksCloseSend
        self.waitsForCancellationToFinish = waitsForCancellationToFinish
    }

    func send(_ data: Data) async throws {
        sent.append(data)
        let isClose = String(decoding: data, as: UTF8.self)
            .contains("session.close")
        if isClose, let closeSendError {
            throw closeSendError
        }
        if acknowledgesClose, isClose {
            enqueue(Data(#"{"type":"session.closed"}"#.utf8))
        }
        if blocksCloseSend, isClose {
            closeSendStarted = true
            closeSendStartWaiter?.resume()
            closeSendStartWaiter = nil
            await withCheckedContinuation { closeSendWaiter = $0 }
        }
    }

    func receive() async throws -> Data {
        if !incoming.isEmpty {
            receivedCount += 1
            return incoming.removeFirst()
        }
        if !receiveErrors.isEmpty {
            throw receiveErrors.removeFirst()
        }
        return try await withCheckedThrowingContinuation { continuation in
            receiveWaiters.append(continuation)
        }
    }

    func cancel() async {
        wasCancelled = true
        cancellationCount += 1
        cancellationStarted = true
        cancellationStartWaiter?.resume()
        cancellationStartWaiter = nil
        if waitsForCancellationToFinish {
            await withCheckedContinuation { continuation in
                cancellationFinishWaiters.append(continuation)
            }
        }
        closeSendWaiter?.resume()
        closeSendWaiter = nil
        let waiters = receiveWaiters
        receiveWaiters.removeAll()
        for waiter in waiters {
            waiter.resume(throwing: TranslationSocketError.disconnected)
        }
    }

    func waitForCancellationStart() async {
        guard !cancellationStarted else { return }
        await withCheckedContinuation { cancellationStartWaiter = $0 }
    }

    func waitForCloseSendStart() async {
        guard !closeSendStarted else { return }
        await withCheckedContinuation { closeSendStartWaiter = $0 }
    }

    func finishCancellation() {
        let waiters = cancellationFinishWaiters
        cancellationFinishWaiters.removeAll()
        for waiter in waiters {
            waiter.resume()
        }
    }

    func enqueueIncoming(_ data: Data) {
        enqueue(data)
    }

    func failReceive(_ error: TranslationSocketError) {
        if receiveWaiters.isEmpty {
            receiveErrors.append(error)
        } else {
            receiveWaiters.removeFirst().resume(throwing: error)
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

private let serverErrorEvent = Data(
    #"{"type":"error","error":{"code":"test_error","message":"test failure"}}"#.utf8
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
func fastServerCloseDuringSendFinishesBeforeArmedDeadlineFires() async throws {
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(
        incoming: handshakeEvents,
        blocksCloseSend: true,
        waitsForCancellationToFinish: true
    )
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    let closeTask = Task { try await session.close() }
    await socket.waitForCancellationStart()

    await #expect(throws: TranslationSocketError.self) {
        try await session.appendAudio(Data([0, 1]))
    }
    await socket.finishCancellation()
    try await closeTask.value
    await deadline.fire()

    #expect(await deadline.waitCount == 1)
    #expect(await socket.cancellationCount == 1)
}

@Test
func closeDeadlineCancelsAStalledCloseSend() async throws {
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(
        incoming: handshakeEvents,
        acknowledgesClose: false,
        blocksCloseSend: true
    )
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    let closeTask = Task { try await session.close() }
    await socket.waitForCloseSendStart()

    let deadlineWasArmed = await eventually {
        await deadline.waitCount == 1
    }
    #expect(deadlineWasArmed)
    guard deadlineWasArmed else {
        await socket.cancel()
        return
    }

    await deadline.fire()
    try await closeTask.value

    #expect(await socket.wasCancelled)
    #expect(await socket.cancellationCount == 1)
}

@Test
func concurrentCloseCallersShareOneForcedSocketCancellation() async throws {
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(
        incoming: handshakeEvents,
        acknowledgesClose: false,
        blocksCloseSend: true
    )
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    let firstClose = Task { try await session.close() }
    await socket.waitForCloseSendStart()
    let secondClose = Task { try await session.close() }

    await deadline.fire()
    try await firstClose.value
    try await secondClose.value

    #expect(await socket.sent.count == 2)
    #expect(await socket.cancellationCount == 1)
}

@Test
func closeSendFailureCancelsSocketAndPropagatesError() async throws {
    let socket = FakeSocket(
        incoming: handshakeEvents,
        acknowledgesClose: false,
        closeSendError: .invalidUTF8TextFrame
    )
    let session = TranslationSession(
        configuration: .default,
        sessionConfiguration: TranslationSessionConfiguration(
            targetLanguage: .chinese
        ),
        apiKey: "secret",
        factory: FakeFactory(socket: socket)
    )
    try await session.connect()

    do {
        try await session.close()
        #expect(Bool(false))
    } catch let error as TranslationSocketError {
        #expect(error == .invalidUTF8TextFrame)
    } catch {
        #expect(Bool(false))
    }

    #expect(await socket.cancellationCount == 1)
}

@Test
func serverErrorWinsDeadlineRaceAndCancelsSocketOnce() async throws {
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(
        incoming: handshakeEvents,
        acknowledgesClose: false,
        blocksCloseSend: true,
        waitsForCancellationToFinish: true
    )
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    let closeTask = Task { try await session.close() }
    await socket.waitForCloseSendStart()
    await socket.enqueueIncoming(serverErrorEvent)
    await socket.waitForCancellationStart()
    await deadline.fire()
    await socket.finishCancellation()

    do {
        try await closeTask.value
        #expect(Bool(false))
    } catch let error as TranslationSessionError {
        #expect(error == .server(code: "test_error", message: "test failure"))
    } catch {
        #expect(Bool(false))
    }
    #expect(await socket.cancellationCount == 1)
}

@Test
func receiveErrorWinsDeadlineRaceAndCancelsSocketOnce() async throws {
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(
        incoming: handshakeEvents,
        acknowledgesClose: false,
        blocksCloseSend: true,
        waitsForCancellationToFinish: true
    )
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    let closeTask = Task { try await session.close() }
    await socket.waitForCloseSendStart()
    await socket.failReceive(.invalidUTF8TextFrame)
    await socket.waitForCancellationStart()
    await deadline.fire()
    await socket.finishCancellation()

    do {
        try await closeTask.value
        #expect(Bool(false))
    } catch let error as TranslationSocketError {
        #expect(error == .invalidUTF8TextFrame)
    } catch {
        #expect(Bool(false))
    }
    #expect(await socket.cancellationCount == 1)
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
