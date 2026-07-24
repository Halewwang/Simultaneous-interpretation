import EMKECore
import Foundation
import Testing
@testable import EMKEMenuBarApp

@Test
func interfaceLanguageRawValuesRemainStableForPersistence() {
    #expect(AppInterfaceLanguage.system.rawValue == "system")
    #expect(AppInterfaceLanguage.zhHans.rawValue == "zh-Hans")
    #expect(AppInterfaceLanguage.english.rawValue == "en")
}

@Test @MainActor
func settingsStoreDefaultsUnknownInterfaceLanguageToSystem() {
    let suite = "emke-interface-language-\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    defaults.set("unexpected", forKey: "emke.interfaceLanguage")

    let value = UserDefaultsAppSettingsStore(defaults: defaults).load()

    #expect(value.interfaceLanguage == .system)
}

@Test @MainActor
func settingsStorePersistsInterfaceLanguageWithoutChangingOtherSettings() {
    let suite = "emke-interface-language-\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    let store = UserDefaultsAppSettingsStore(defaults: defaults)
    var expected = AppSettings.default
    expected.interfaceLanguage = .english

    store.save(expected)

    #expect(store.load() == expected)
}
