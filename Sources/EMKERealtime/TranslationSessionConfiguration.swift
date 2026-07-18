import EMKECore

public enum TranslationNoiseReduction: String, Codable, Sendable {
    case nearField = "near_field"
    case farField = "far_field"
}

public struct TranslationSessionConfiguration: Equatable, Sendable {
    public let targetLanguage: SupportedLanguage
    public let inputTranscriptionModel: String?
    public let noiseReduction: TranslationNoiseReduction?

    public init(
        targetLanguage: SupportedLanguage,
        inputTranscriptionModel: String? = nil,
        noiseReduction: TranslationNoiseReduction? = nil
    ) {
        self.targetLanguage = targetLanguage
        self.inputTranscriptionModel = inputTranscriptionModel
        self.noiseReduction = noiseReduction
    }
}
