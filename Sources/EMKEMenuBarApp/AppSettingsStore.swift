import EMKECore
import Foundation

struct AppSettings: Equatable, Sendable {
    var apiConfiguration: APIConfiguration
    var preferences: TranslationPreferences
    var selectedInputUID: String?
    var selectedOutputUID: String?

    static let `default` = AppSettings(
        apiConfiguration: .default,
        preferences: TranslationPreferences(
            motherLanguage: .chinese,
            meetingOutputLanguage: .german
        ),
        selectedInputUID: nil,
        selectedOutputUID: nil
    )
}

@MainActor
protocol AppSettingsStoring: AnyObject {
    func load() -> AppSettings
    func save(_ settings: AppSettings)
}

@MainActor
final class UserDefaultsAppSettingsStore: AppSettingsStoring {
    private enum Key {
        static let baseURL = "emke.baseURL"
        static let modelID = "emke.modelID"
        static let motherLanguage = "emke.motherLanguage"
        static let meetingOutputLanguage = "emke.meetingOutputLanguage"
        static let selectedInputUID = "emke.physicalInputUID"
        static let selectedOutputUID = "emke.physicalOutputUID"
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load() -> AppSettings {
        let fallback = AppSettings.default
        let baseURL = defaults.string(forKey: Key.baseURL)
            .flatMap(URL.init(string:))
            ?? fallback.apiConfiguration.baseURL
        let modelID = defaults.string(forKey: Key.modelID)
            ?? fallback.apiConfiguration.modelID
        let motherLanguage = defaults.string(forKey: Key.motherLanguage)
            .flatMap(SupportedLanguage.init(rawValue:))
            ?? fallback.preferences.motherLanguage
        let meetingOutputLanguage = defaults.string(
            forKey: Key.meetingOutputLanguage
        )
        .flatMap(SupportedLanguage.init(rawValue:))
            ?? fallback.preferences.meetingOutputLanguage

        return AppSettings(
            apiConfiguration: APIConfiguration(
                baseURL: baseURL,
                modelID: modelID
            ),
            preferences: TranslationPreferences(
                motherLanguage: motherLanguage,
                meetingOutputLanguage: meetingOutputLanguage
            ),
            selectedInputUID: defaults.string(
                forKey: Key.selectedInputUID
            ),
            selectedOutputUID: defaults.string(
                forKey: Key.selectedOutputUID
            )
        )
    }

    func save(_ settings: AppSettings) {
        defaults.set(
            settings.apiConfiguration.baseURL.absoluteString,
            forKey: Key.baseURL
        )
        defaults.set(
            settings.apiConfiguration.modelID,
            forKey: Key.modelID
        )
        defaults.set(
            settings.preferences.motherLanguage.rawValue,
            forKey: Key.motherLanguage
        )
        defaults.set(
            settings.preferences.meetingOutputLanguage.rawValue,
            forKey: Key.meetingOutputLanguage
        )
        update(
            settings.selectedInputUID,
            forKey: Key.selectedInputUID
        )
        update(
            settings.selectedOutputUID,
            forKey: Key.selectedOutputUID
        )
    }

    private func update(_ value: String?, forKey key: String) {
        if let value {
            defaults.set(value, forKey: key)
        } else {
            defaults.removeObject(forKey: key)
        }
    }
}
