public protocol SecretStore: Sendable {
    func saveAPIKey(_ value: String) async throws
    func loadAPIKey() async throws -> String?
    func deleteAPIKey() async throws
}

public enum SecretStoreError: Error, Equatable {
    case invalidEncoding
    case keychainStatus(Int32)
}
