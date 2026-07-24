import EMKECore
import Foundation

struct AppSettings: Equatable, Sendable {
    var baseURLString: String
    var modelID: String
    var preferences: TranslationPreferences
    var selectedInputUID: String?
    var selectedOutputUID: String?
    var interfaceLanguage: AppInterfaceLanguage

    static let `default` = AppSettings(
        baseURLString: APIConfiguration.default.baseURL.absoluteString,
        modelID: APIConfiguration.default.modelID,
        preferences: TranslationPreferences(
            motherLanguage: .chinese,
            meetingOutputLanguage: .german
        ),
        selectedInputUID: nil,
        selectedOutputUID: nil,
        interfaceLanguage: .system
    )

    init(
        baseURLString: String,
        modelID: String,
        preferences: TranslationPreferences,
        selectedInputUID: String?,
        selectedOutputUID: String?,
        interfaceLanguage: AppInterfaceLanguage = .system
    ) {
        self.baseURLString = baseURLString
        self.modelID = modelID
        self.preferences = preferences
        self.selectedInputUID = selectedInputUID
        self.selectedOutputUID = selectedOutputUID
        self.interfaceLanguage = interfaceLanguage
    }

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
        static let interfaceLanguage = "emke.interfaceLanguage"
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load() -> AppSettings {
        let fallback = AppSettings.default
        let baseURLString = defaults.string(forKey: Key.baseURL)
            ?? fallback.baseURLString
        let modelID = defaults.string(forKey: Key.modelID)
            ?? fallback.modelID
        let motherLanguage = defaults.string(forKey: Key.motherLanguage)
            .flatMap(SupportedLanguage.init(rawValue:))
            ?? fallback.preferences.motherLanguage
        let meetingOutputLanguage = defaults.string(
            forKey: Key.meetingOutputLanguage
        )
        .flatMap(SupportedLanguage.init(rawValue:))
            ?? fallback.preferences.meetingOutputLanguage

        return AppSettings(
            baseURLString: baseURLString,
            modelID: modelID,
            preferences: TranslationPreferences(
                motherLanguage: motherLanguage,
                meetingOutputLanguage: meetingOutputLanguage
            ),
            selectedInputUID: defaults.string(
                forKey: Key.selectedInputUID
            ),
            selectedOutputUID: defaults.string(
                forKey: Key.selectedOutputUID
            ),
            interfaceLanguage: defaults.string(forKey: Key.interfaceLanguage)
                .flatMap(AppInterfaceLanguage.init(rawValue:))
                ?? fallback.interfaceLanguage
        )
    }

    func save(_ settings: AppSettings) {
        defaults.set(
            settings.baseURLString,
            forKey: Key.baseURL
        )
        defaults.set(
            settings.modelID,
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
        defaults.set(
            settings.interfaceLanguage.rawValue,
            forKey: Key.interfaceLanguage
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
