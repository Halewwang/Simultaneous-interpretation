import NaturalLanguage

public struct NaturalLanguageClassifier: Sendable {
    public init() {}

    public func hypotheses(
        for text: String,
        maximum: Int = 3
    ) -> LanguageHypotheses {
        let recognizer = NLLanguageRecognizer()
        recognizer.processString(text)
        let values = recognizer.languageHypotheses(withMaximum: maximum)
        return LanguageHypotheses(
            Dictionary(
                uniqueKeysWithValues: values.map {
                    ($0.key.rawValue, $0.value)
                }
            )
        )
    }
}
