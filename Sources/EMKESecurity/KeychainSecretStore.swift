import Foundation
import Security

public actor KeychainSecretStore: SecretStore {
    private let service = "com.emke.translation"
    private let account = "openai-api-key"

    public init() {}

    public func saveAPIKey(_ value: String) async throws {
        let data = Data(value.utf8)
        let query = baseQuery()
        let updateStatus = SecItemUpdate(
            query as CFDictionary,
            [kSecValueData as String: data] as CFDictionary
        )

        if updateStatus == errSecItemNotFound {
            var insert = query
            insert[kSecValueData as String] = data
            insert[kSecAttrAccessible as String] =
                kSecAttrAccessibleWhenUnlockedThisDeviceOnly
            let insertStatus = SecItemAdd(insert as CFDictionary, nil)
            guard insertStatus == errSecSuccess else {
                throw SecretStoreError.keychainStatus(insertStatus)
            }
        } else if updateStatus != errSecSuccess {
            throw SecretStoreError.keychainStatus(updateStatus)
        }
    }

    public func loadAPIKey() async throws -> String? {
        var query = baseQuery()
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        if status == errSecItemNotFound {
            return nil
        }
        guard status == errSecSuccess else {
            throw SecretStoreError.keychainStatus(status)
        }
        guard let data = item as? Data,
              let value = String(data: data, encoding: .utf8) else {
            throw SecretStoreError.invalidEncoding
        }
        return value
    }

    public func deleteAPIKey() async throws {
        let status = SecItemDelete(baseQuery() as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw SecretStoreError.keychainStatus(status)
        }
    }

    private func baseQuery() -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }
}
