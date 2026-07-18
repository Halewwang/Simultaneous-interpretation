import Foundation

public enum TranslationSocketError: Error, Equatable {
    case disconnected
}

public protocol TranslationSocket: Sendable {
    func send(_ data: Data) async throws
    func receive() async throws -> Data
    func cancel() async
}

public protocol TranslationSocketFactory: Sendable {
    func makeSocket(
        url: URL,
        authorization: String
    ) async throws -> any TranslationSocket
}
