import EMKECore
import EMKERouting
import Foundation
import Testing
@testable import EMKECoordinator

@Test
func motherLanguageLocksOriginalWithoutReplayingBufferedPCM() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let id = controller.beginUtterance()

    #expect(controller.appendOriginal(Data([1, 1]), utteranceID: id) == [
        .original(Data([1, 1])),
    ])
    #expect(controller.observe(
        LanguageHypotheses(["zh": 0.9]),
        utteranceID: id
    ) == [.setOriginalGain(1.0, rampSamples: 1_920)])
    #expect(controller.route == .original)
}

@Test
func translationArrivingBeforeLanguageDecisionIsHeldThenCrossfaded() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let id = controller.beginUtterance()

    #expect(
        controller.appendTranslation(
            Data([2, 2]),
            utteranceID: id
        ).isEmpty
    )
    #expect(controller.observe(
        LanguageHypotheses(["de": 0.9]),
        utteranceID: id
    ) == [.beginCrossfade([Data([2, 2])], rampSamples: 1_920)])
    #expect(controller.route == .translated)
}

@Test
func staleUtteranceIdentifierIsIgnored() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let staleID = controller.beginUtterance()
    let currentID = controller.beginUtterance()

    #expect(
        controller.appendOriginal(
            Data([1, 1]),
            utteranceID: staleID
        ).isEmpty
    )
    #expect(
        controller.appendTranslation(
            Data([2, 2]),
            utteranceID: staleID
        ).isEmpty
    )
    #expect(controller.observe(
        LanguageHypotheses(["de": 0.9]),
        utteranceID: staleID
    ).isEmpty)
    #expect(
        controller.resolveDeadline(
            isSpeech: true,
            utteranceID: staleID
        ).isEmpty
    )
    #expect(controller.finish(utteranceID: staleID).isEmpty)
    #expect(controller.utteranceID == currentID)
    #expect(controller.route == .undecided)
}

@Test
func lockedRouteCannotSwitchAfterLaterHypotheses() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let id = controller.beginUtterance()

    _ = controller.observe(
        LanguageHypotheses(["zh": 0.9]),
        utteranceID: id
    )

    #expect(controller.observe(
        LanguageHypotheses(["de": 0.99]),
        utteranceID: id
    ).isEmpty)
    #expect(controller.route == .original)
    #expect(
        controller.appendTranslation(
            Data([2, 2]),
            utteranceID: id
        ).isEmpty
    )
}

@Test
func speechDeadlinePrefersAvailableTranslation() {
    var controller = InboundAuditionController(motherLanguage: .english)
    let id = controller.beginUtterance()

    _ = controller.appendTranslation(Data([2, 2]), utteranceID: id)

    #expect(
        controller.resolveDeadline(isSpeech: true, utteranceID: id)
            == [.beginCrossfade(
                [Data([2, 2])],
                rampSamples: 1_920
            )]
    )
    #expect(controller.route == .translated)
}

@Test
func emptyTranslationDoesNotCountAsAvailableAtSpeechDeadline() {
    var controller = InboundAuditionController(motherLanguage: .english)
    let id = controller.beginUtterance()

    #expect(
        controller.appendTranslation(Data(), utteranceID: id).isEmpty
    )
    #expect(
        controller.resolveDeadline(isSpeech: true, utteranceID: id)
            == [.setOriginalGain(1.0, rampSamples: 1_920)]
    )
    #expect(controller.route == .original)
}

@Test
func nonSpeechDeadlinePrefersOriginalWithoutReplayingPCM() {
    var controller = InboundAuditionController(motherLanguage: .english)
    let id = controller.beginUtterance()

    #expect(controller.appendOriginal(Data([1, 1]), utteranceID: id) == [
        .original(Data([1, 1])),
    ])
    #expect(
        controller.resolveDeadline(isSpeech: false, utteranceID: id)
            == [.setOriginalGain(1.0, rampSamples: 1_920)]
    )
    #expect(controller.route == .original)
}

@Test
func translatedRouteWaitsForFirstAudioBeforeBeginningCrossfade() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let id = controller.beginUtterance()

    #expect(controller.observe(
        LanguageHypotheses(["de": 0.9]),
        utteranceID: id
    ).isEmpty)
    #expect(controller.appendOriginal(Data([1, 1]), utteranceID: id) == [
        .original(Data([1, 1])),
    ])
    #expect(
        controller.appendTranslation(
            Data([2, 2]),
            utteranceID: id
        ) == [.beginCrossfade([Data([2, 2])], rampSamples: 1_920)]
    )
    #expect(
        controller.appendTranslation(
            Data([3, 3]),
            utteranceID: id
        ) == [.translation(Data([3, 3]))]
    )
}

@Test
func failOpenOverridesTranslatedRouteWithoutReplayingOriginalPCM() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let id = controller.beginUtterance()

    _ = controller.appendOriginal(Data([1, 1]), utteranceID: id)
    _ = controller.observe(
        LanguageHypotheses(["de": 0.9]),
        utteranceID: id
    )

    #expect(controller.failOpen() == [.failOpen(rampSamples: 1_920)])
    #expect(controller.route == .original)
    #expect(controller.appendOriginal(Data([3, 3]), utteranceID: id) == [
        .original(Data([3, 3])),
    ])
}

@Test
func finishAndResetEndTheCurrentUtterance() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    let firstID = controller.beginUtterance()

    #expect(controller.finish(utteranceID: firstID) == [.reset])
    #expect(controller.utteranceID == nil)
    #expect(controller.route == .undecided)

    let secondID = controller.beginUtterance()
    #expect(secondID > firstID)
    #expect(controller.reset() == [.reset])
    #expect(controller.utteranceID == nil)
    #expect(controller.route == .undecided)
}
