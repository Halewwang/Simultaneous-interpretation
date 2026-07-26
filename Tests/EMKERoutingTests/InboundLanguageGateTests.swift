import EMKECore
import Testing
@testable import EMKERouting

@Test
func motherLanguageSelectsOriginalAndLocksUntilReset() {
    var gate = InboundLanguageGate(motherLanguage: .chinese)
    #expect(
        gate.observe(LanguageHypotheses(["zh": 0.82, "en": 0.18]))
            == .original
    )
    #expect(gate.observe(LanguageHypotheses(["de": 0.99])) == .original)
}

@Test
func otherLanguageSelectsTranslation() {
    var gate = InboundLanguageGate(motherLanguage: .chinese)
    #expect(
        gate.observe(LanguageHypotheses(["de": 0.72, "zh": 0.20]))
            == .translated
    )
}

@Test
func unresolvedSpeechDefaultsToTranslationAtDeadline() {
    var gate = InboundLanguageGate(motherLanguage: .english)
    #expect(
        gate.observe(LanguageHypotheses(["en": 0.51, "de": 0.49]))
            == .undecided
    )
    #expect(gate.resolveDeadline(isSpeech: true) == .translated)
}

@Test
func nonSpeechDefaultsToOriginalAtDeadline() {
    var gate = InboundLanguageGate(motherLanguage: .english)
    #expect(gate.resolveDeadline(isSpeech: false) == .original)
}

@Test
func resetAllowsNextUtteranceToChooseAgain() {
    var gate = InboundLanguageGate(motherLanguage: .german)
    #expect(gate.observe(LanguageHypotheses(["de": 0.9])) == .original)
    gate.reset()
    #expect(gate.observe(LanguageHypotheses(["en": 0.9])) == .translated)
}

@Test
func regionalLanguageTagsAggregateIntoPrimaryTagConfidence() {
    let hypotheses = LanguageHypotheses([
        "en-US": 0.52,
        "en-GB": 0.81,
        "de-DE": 0.19,
    ])
    #expect(hypotheses.confidenceByPrimaryTag["en"] == 1)
    #expect(hypotheses.confidenceByPrimaryTag["de"] == 0.19)
}

@Test
func scriptVariantsContributeToOnePrimaryLanguageDecision() {
    let hypotheses = LanguageHypotheses([
        "zh-Hans": 0.411,
        "zh-Hant": 0.565,
        "ja": 0.024,
    ])
    var gate = InboundLanguageGate(motherLanguage: .chinese)

    #expect(hypotheses.confidenceByPrimaryTag["zh"] == 0.976)
    #expect(gate.observe(hypotheses) == .original)
}
