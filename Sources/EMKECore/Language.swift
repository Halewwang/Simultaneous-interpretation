public enum SupportedLanguage: String, CaseIterable, Codable, Sendable {
    case chinese = "zh"
    case english = "en"
    case german = "de"

    public var displayName: String {
        switch self {
        case .chinese: "中文"
        case .english: "English"
        case .german: "Deutsch"
        }
    }
}

public struct TranslationPreferences: Codable, Equatable, Sendable {
    public var motherLanguage: SupportedLanguage
    public var meetingOutputLanguage: SupportedLanguage

    public init(
        motherLanguage: SupportedLanguage,
        meetingOutputLanguage: SupportedLanguage
    ) {
        self.motherLanguage = motherLanguage
        self.meetingOutputLanguage = meetingOutputLanguage
    }
}
