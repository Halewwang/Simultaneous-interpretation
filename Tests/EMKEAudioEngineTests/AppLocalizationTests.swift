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
func onboardingCopyIsCompleteInBothLanguages() {
    let keys: [AppCopyKey] = [
        .gettingStarted,
        .openGettingStarted,
        .onboardingSkipForNow,
        .onboardingDoNotShowAgain,
        .onboardingBack,
        .onboardingContinue,
        .onboardingFinish,
        .onboardingOverviewTitle,
        .onboardingOverviewBody,
        .onboardingMicrophoneTitle,
        .onboardingMicrophoneBody,
        .onboardingAllowMicrophone,
        .onboardingOpenSystemSettings,
        .onboardingAuthorized,
        .onboardingDenied,
        .onboardingRestricted,
        .onboardingAudioTitle,
        .onboardingAudioBody,
        .onboardingMeetingTitle,
        .onboardingMeetingBody,
        .meetingAppSpeaker,
        .meetingAppMicrophone,
        .onboardingProgress,
    ]

    for language in [
        ResolvedInterfaceLanguage.zhHans,
        ResolvedInterfaceLanguage.english,
    ] {
        let copy = AppCopy(language: language)
        for key in keys {
            #expect(!copy.text(key).isEmpty)
        }
    }
}

@Test
func meetingEndpointLabelsExplicitlyNameTheMeetingAppControls() {
    #expect(
        AppCopy(language: .zhHans).text(.meetingAppSpeaker)
            == "会议应用扬声器"
    )
    #expect(
        AppCopy(language: .zhHans).text(.meetingAppMicrophone)
            == "会议应用麦克风"
    )
    #expect(
        AppCopy(language: .english).text(.meetingAppSpeaker)
            == "Meeting app speaker"
    )
    #expect(
        AppCopy(language: .english).text(.meetingAppMicrophone)
            == "Meeting app microphone"
    )
}

@Test
func dashboardBrandFooterIsExactInBothLanguages() {
    #expect(
        AppCopy(language: .zhHans).text(.audioDirectToProvider)
            == "Powered by Eager"
    )
    #expect(
        AppCopy(language: .english).text(.audioDirectToProvider)
            == "Powered by Eager"
    )
}

@Test
func floatingStatusCopyUsesExactCompactChineseAndEnglishLabels() {
    let chinese = AppCopy(language: .zhHans)
    let english = AppCopy(language: .english)

    #expect(chinese.text(.floatingOutboundMuted) == "出站静音")
    #expect(english.text(.floatingOutboundMuted) == "Muted")
    #expect(chinese.text(.floatingInboundOriginal) == "播放原音")
    #expect(english.text(.floatingInboundOriginal) == "Original")
    #expect(chinese.text(.floatingTranslationError) == "异常")
    #expect(english.text(.floatingTranslationError) == "Error")
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
    #expect(englishCopy.inboundDirection(to: .german) == "Other → German")
    #expect(chineseCopy.outboundDirection(from: .chinese, to: .german) == "中文 → 德语")
    #expect(englishCopy.outboundDirection(from: .english, to: .chinese) == "English → Chinese")
}

@Test
func semanticMessageKeepsRawDetailButLocalizesItsPrefix() {
    let message = AppMessage.detail(.keychainReadFailed, "OSStatus -50")
    #expect(message.text(using: AppCopy(language: .zhHans)) == "无法读取 Keychain：OSStatus -50")
    #expect(message.text(using: AppCopy(language: .english)) == "Could not read Keychain: OSStatus -50")
}
