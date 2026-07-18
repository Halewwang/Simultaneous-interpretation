import Foundation
import Testing
@testable import EMKECore

@Test
func supportedLanguagesUseExpectedBCP47Tags() {
    #expect(SupportedLanguage.allCases.map(\.rawValue) == ["zh", "en", "de"])
}

@Test
func preferencesAllowDifferentMotherAndMeetingLanguages() {
    let value = TranslationPreferences(motherLanguage: .chinese, meetingOutputLanguage: .german)
    #expect(value.motherLanguage == .chinese)
    #expect(value.meetingOutputLanguage == .german)
}

@Test
func apiConfigurationDefaultsMatchTranslationEndpoint() {
    #expect(APIConfiguration.default.baseURL.absoluteString == "https://api.openai.com/v1")
    #expect(APIConfiguration.default.modelID == "gpt-realtime-translate")
}
