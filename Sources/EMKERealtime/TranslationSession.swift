import EMKECore
import Foundation

public enum TranslationSessionError: Error, Equatable, Sendable {
    case alreadyConnected
    case closing
    case unexpectedHandshakeEvent(expected: String, received: String)
    case server(code: String, message: String)
}

public actor TranslationSession {
    public typealias CloseDeadline = @Sendable () async -> Void

    private static let maximumQueuedEvents = 128

    private let configuration: APIConfiguration
    private let sessionConfiguration: TranslationSessionConfiguration
    private let apiKey: String
    private let factory: any TranslationSocketFactory
    private let closeDeadline: CloseDeadline

    private var socket: (any TranslationSocket)?
    private var connectionID: UUID?
    private var readerTask: Task<Void, Never>?
    private var closeDeadlineTask: Task<Void, Never>?
    private var queuedEvents: [TranslationServerEvent] = []
    private var eventWaiters: [
        CheckedContinuation<TranslationServerEvent, any Error>
    ] = []
    private var closeWaiters: [CheckedContinuation<Void, any Error>] = []
    private var terminalError: (any Error)?
    private var isClosing = false

    public init(
        configuration: APIConfiguration,
        sessionConfiguration: TranslationSessionConfiguration,
        apiKey: String,
        factory: any TranslationSocketFactory,
        closeDeadline: @escaping CloseDeadline = {
            try? await Task.sleep(for: .seconds(1))
        }
    ) {
        self.configuration = configuration
        self.sessionConfiguration = sessionConfiguration
        self.apiKey = apiKey
        self.factory = factory
        self.closeDeadline = closeDeadline
    }

    public func connect() async throws {
        guard socket == nil else {
            throw TranslationSessionError.alreadyConnected
        }

        resetConnectionState()
        let url = try TranslationEndpoint.webSocketURL(
            configuration: configuration
        )
        let newSocket = try await factory.makeSocket(
            url: url,
            authorization: apiKey
        )

        do {
            let created = try TranslationServerEvent.decode(
                try await newSocket.receive()
            )
            try validateHandshake(created, expected: "session.created")

            try await newSocket.send(
                try TranslationClientEvent.sessionUpdate(
                    configuration: sessionConfiguration
                ).encoded()
            )

            let updated = try TranslationServerEvent.decode(
                try await newSocket.receive()
            )
            try validateHandshake(updated, expected: "session.updated")
        } catch {
            await newSocket.cancel()
            throw error
        }

        let id = UUID()
        socket = newSocket
        connectionID = id
        readerTask = Task { [weak self] in
            guard let self else { return }
            await self.readLoop(socket: newSocket, connectionID: id)
        }
    }

    public func appendAudio(_ pcm16: Data) async throws {
        guard let socket else {
            throw TranslationSocketError.disconnected
        }
        guard !isClosing else {
            throw TranslationSessionError.closing
        }
        let event = try TranslationClientEvent.appendAudio(pcm16).encoded()
        try await socket.send(event)
    }

    public func nextEvent() async throws -> TranslationServerEvent {
        if !queuedEvents.isEmpty {
            return queuedEvents.removeFirst()
        }
        if let terminalError {
            throw terminalError
        }
        guard socket != nil else {
            throw TranslationSocketError.disconnected
        }
        return try await withCheckedThrowingContinuation { continuation in
            eventWaiters.append(continuation)
        }
    }

    public func close() async throws {
        guard let socket, let id = connectionID else { return }

        if !isClosing {
            isClosing = true
            do {
                try await socket.send(TranslationClientEvent.close.encoded())
            } catch {
                finishConnection(connectionID: id, error: error)
                throw error
            }

            let deadline = closeDeadline
            closeDeadlineTask = Task { [weak self] in
                await deadline()
                guard !Task.isCancelled else { return }
                await self?.forceFinishClose(connectionID: id, socket: socket)
            }
        }

        if connectionID != id {
            if let terminalError { throw terminalError }
            return
        }

        try await withCheckedThrowingContinuation { continuation in
            closeWaiters.append(continuation)
        }
    }

    private func readLoop(
        socket: any TranslationSocket,
        connectionID id: UUID
    ) async {
        while !Task.isCancelled {
            do {
                let event = try TranslationServerEvent.decode(
                    try await socket.receive()
                )
                guard connectionID == id else { return }
                emit(event)
                if case .closed = event {
                    await socket.cancel()
                    finishConnection(connectionID: id, error: nil)
                    return
                }
                if case .serverError(let code, let message) = event {
                    let error = TranslationSessionError.server(
                        code: code,
                        message: message
                    )
                    await socket.cancel()
                    finishConnection(connectionID: id, error: error)
                    return
                }
            } catch {
                guard connectionID == id else { return }
                await socket.cancel()
                finishConnection(connectionID: id, error: error)
                return
            }
        }
    }

    private func forceFinishClose(
        connectionID id: UUID,
        socket: any TranslationSocket
    ) async {
        guard connectionID == id, isClosing else { return }
        finishConnection(connectionID: id, error: nil)
        await socket.cancel()
    }

    private func emit(_ event: TranslationServerEvent) {
        if !eventWaiters.isEmpty {
            eventWaiters.removeFirst().resume(returning: event)
        } else if queuedEvents.count < Self.maximumQueuedEvents {
            queuedEvents.append(event)
        } else {
            finishConnection(
                connectionID: connectionID,
                error: TranslationSocketError.disconnected
            )
        }
    }

    private func finishConnection(
        connectionID id: UUID?,
        error: (any Error)?
    ) {
        guard connectionID == id else { return }
        closeDeadlineTask?.cancel()
        closeDeadlineTask = nil
        terminalError = error
        socket = nil
        connectionID = nil
        readerTask = nil
        isClosing = false

        let eventWaiters = self.eventWaiters
        self.eventWaiters.removeAll(keepingCapacity: false)
        let closeWaiters = self.closeWaiters
        self.closeWaiters.removeAll(keepingCapacity: false)

        if let error {
            for waiter in eventWaiters {
                waiter.resume(throwing: error)
            }
            for waiter in closeWaiters {
                waiter.resume(throwing: error)
            }
        } else {
            for waiter in eventWaiters {
                waiter.resume(returning: .closed)
            }
            for waiter in closeWaiters {
                waiter.resume()
            }
        }
    }

    private func resetConnectionState() {
        terminalError = nil
        isClosing = false
        queuedEvents.removeAll(keepingCapacity: false)
        eventWaiters.removeAll(keepingCapacity: false)
        closeWaiters.removeAll(keepingCapacity: false)
    }

    private func validateHandshake(
        _ event: TranslationServerEvent,
        expected: String
    ) throws {
        switch (expected, event) {
        case ("session.created", .sessionCreated):
            return
        case ("session.updated", .sessionUpdated):
            return
        case (_, .serverError(let code, let message)):
            throw TranslationSessionError.server(
                code: code,
                message: message
            )
        default:
            throw TranslationSessionError.unexpectedHandshakeEvent(
                expected: expected,
                received: event.typeName
            )
        }
    }
}

private extension TranslationServerEvent {
    var typeName: String {
        switch self {
        case .sessionCreated: "session.created"
        case .sessionUpdated: "session.updated"
        case .outputAudio: "session.output_audio.delta"
        case .inputTranscript: "session.input_transcript.delta"
        case .outputTranscript: "session.output_transcript.delta"
        case .closed: "session.closed"
        case .serverError: "error"
        case .ignored(let type): type
        }
    }
}
