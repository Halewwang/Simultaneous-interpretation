import EMKECore
import Testing
@testable import EMKEMenuBarApp

@Test
func interfaceLanguageResolutionUsesFirstPreferredLanguage() {
    #expect(
        AppLanguageResolver.resolve(
            preference: .system,
            preferredLanguages: ["zh-Hans-CN", "en"]
        ) == .zhHans
    )
    #expect(
        AppLanguageResolver.resolve(
            preference: .system,
            preferredLanguages: ["en-US", "zh-Hans"]
        ) == .english
    )
    #expect(
        AppLanguageResolver.resolve(
            preference: .zhHans,
            preferredLanguages: ["en-US"]
        ) == .zhHans
    )
    #expect(
        AppLanguageResolver.resolve(
            preference: .english,
            preferredLanguages: ["zh-Hans"]
        ) == .english
    )
    #expect(
        AppLanguageResolver.resolve(
            preference: .system,
            preferredLanguages: []
        ) == .english
    )
}

@Test
func everyStaticCopyKeyHasChineseAndEnglishText() {
    for key in AppCopyKey.allCases {
        #expect(!AppCopy(language: .zhHans).text(key).isEmpty)
        #expect(!AppCopy(language: .english).text(key).isEmpty)
    }
}

@Test
func supportedLanguageNamesFollowTheInterfaceLanguage() {
    #expect(AppCopy(language: .zhHans).languageName(.german) == "德语")
    #expect(AppCopy(language: .english).languageName(.german) == "German")
}

@Test
func formattedCopyUsesLocalizedWordOrder() {
    #expect(AppCopy(language: .zhHans).reconnecting(attempt: 2) == "重连中（第 2 次）")
    #expect(AppCopy(language: .english).reconnecting(attempt: 2) == "Reconnecting (attempt 2)")
}

@Test
func dynamicCopyUsesLocalizedLanguageNamesAndDirections() {
    let chineseCopy = AppCopy(language: .zhHans)
    let englishCopy = AppCopy(language: .english)

    #expect(chineseCopy.translating(elapsed: "00:42") == "翻译中 · 00:42")
    #expect(englishCopy.translating(elapsed: "00:42") == "Translating · 00:42")
    #expect(chineseCopy.inboundDirection(to: .chinese) == "其他语言 → 中文")
    #expect(englishCopy.inboundDirection(to: .german) == "Other languages → German")
    #expect(chineseCopy.outboundDirection(from: .chinese, to: .german) == "中文 → 德语")
    #expect(englishCopy.outboundDirection(from: .english, to: .chinese) == "English → Chinese")
}
