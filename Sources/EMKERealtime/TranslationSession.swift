import EMKECore
import Foundation

public actor TranslationSession {
    private let configuration: APIConfiguration
    private let language: SupportedLanguage
    private let apiKey: String
    private let factory: any TranslationSocketFactory
    private var socket: (any TranslationSocket)?

    public init(
        configuration: APIConfiguration,
        language: SupportedLanguage,
        apiKey: String,
        factory: any TranslationSocketFactory
    ) {
        self.configuration = configuration
        self.language = language
        self.apiKey = apiKey
        self.factory = factory
    }

    public func connect() async throws {
        let url = try TranslationEndpoint.webSocketURL(
            configuration: configuration
        )
        let newSocket = try await factory.makeSocket(
            url: url,
            authorization: apiKey
        )
        let sessionUpdate = try TranslationClientEvent
            .sessionUpdate(language: language)
            .encoded()
        try await newSocket.send(sessionUpdate)
        socket = newSocket
    }

    public func appendAudio(_ pcm16: Data) async throws {
        guard let socket else {
            throw TranslationSocketError.disconnected
        }
        let event = try TranslationClientEvent.appendAudio(pcm16).encoded()
        try await socket.send(event)
    }

    public func nextEvent() async throws -> TranslationServerEvent {
        guard let socket else {
            throw TranslationSocketError.disconnected
        }
        let data = try await socket.receive()
        return try TranslationServerEvent.decode(data)
    }

    public func close() async throws {
        guard let socket else { return }

        try await socket.send(TranslationClientEvent.close.encoded())
        while true {
            let data = try await socket.receive()
            if try TranslationServerEvent.decode(data) == .closed {
                break
            }
        }
        await socket.cancel()
        self.socket = nil
    }
}
