import Foundation

public actor URLSessionTranslationSocket: TranslationSocket {
    private let task: URLSessionWebSocketTask

    public init(
        url: URL,
        authorization: String,
        session: URLSession = .shared
    ) {
        var request = URLRequest(url: url)
        request.setValue(
            "Bearer \(authorization)",
            forHTTPHeaderField: "Authorization"
        )
        task = session.webSocketTask(with: request)
        task.resume()
    }

    public func send(_ data: Data) async throws {
        try await task.send(.data(data))
    }

    public func receive() async throws -> Data {
        switch try await task.receive() {
        case .data(let data):
            return data
        case .string(let text):
            return Data(text.utf8)
        @unknown default:
            throw TranslationSocketError.disconnected
        }
    }

    public func cancel() async {
        task.cancel(with: .goingAway, reason: nil)
    }
}

public struct URLSessionTranslationSocketFactory: TranslationSocketFactory {
    public init() {}

    public func makeSocket(
        url: URL,
        authorization: String
    ) async throws -> any TranslationSocket {
        URLSessionTranslationSocket(
            url: url,
            authorization: authorization
        )
    }
}
