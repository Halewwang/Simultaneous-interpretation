import Testing
@testable import EMKESecurity

private actor MemorySecretStore: SecretStore {
    private var value: String?

    func saveAPIKey(_ value: String) async throws {
        self.value = value
    }

    func loadAPIKey() async throws -> String? {
        value
    }

    func deleteAPIKey() async throws {
        value = nil
    }
}

@Test
func secretStoreRoundTripsAndDeletesAPIKey() async throws {
    let store = MemorySecretStore()

    try await store.saveAPIKey("sk-private")
    let loaded = try await store.loadAPIKey()
    #expect(loaded == "sk-private")

    try await store.deleteAPIKey()
    let deleted = try await store.loadAPIKey()
    #expect(deleted == nil)
}
