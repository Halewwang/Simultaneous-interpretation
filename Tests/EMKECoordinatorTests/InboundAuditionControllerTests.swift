import EMKECore
import EMKERouting
import Foundation
import Testing
@testable import EMKECoordinator

private func controllerPCM16(_ samples: [Int16]) -> Data {
    var data = Data(capacity: samples.count * 2)
    for sample in samples {
        let bits = UInt16(bitPattern: sample)
        data.append(UInt8(truncatingIfNeeded: bits))
        data.append(UInt8(truncatingIfNeeded: bits >> 8))
    }
    return data
}

private func decodeControllerPCM16(_ data: Data) -> [Int16] {
    stride(from: 0, to: data.count, by: 2).map { index in
        Int16(bitPattern:
            UInt16(data[index]) | UInt16(data[index + 1]) << 8
        )
    }
}

private func render(
    _ commands: [InboundAuditionCommand],
    using renderer: inout InboundAuditionRenderer
) throws -> [InboundRenderedChunk] {
    var output: [InboundRenderedChunk] = []
    for command in commands {
        output.append(contentsOf: try renderer.consume(command))
    }
    return output
}

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

@Test
func rolloverPrefixesResetWithoutLettingStaleEventsConsumeIt() throws {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    var renderer = InboundAuditionRenderer()
    let staleID = controller.beginUtterance()
    _ = controller.observe(
        LanguageHypotheses(["de": 0.9]),
        utteranceID: staleID
    )
    _ = try render(
        controller.appendTranslation(
            controllerPCM16([2_000]),
            utteranceID: staleID
        ),
        using: &renderer
    )
    _ = try render(
        controller.appendOriginal(
            controllerPCM16([10_000]),
            utteranceID: staleID
        ),
        using: &renderer
    )

    let currentID = controller.beginUtterance()
    #expect(
        controller.appendOriginal(
            controllerPCM16([9_000]),
            utteranceID: staleID
        ).isEmpty
    )
    let commands = controller.appendOriginal(
        controllerPCM16([10_000]),
        utteranceID: currentID
    )

    #expect(commands == [
        .reset,
        .original(controllerPCM16([10_000])),
    ])
    let output = try render(commands, using: &renderer)
    #expect(output.map(\.source) == [.original])
    #expect(output.flatMap { decodeControllerPCM16($0.pcm16) }
        == [1_200])
}

@Test
func rolloverTranslationBufferingConsumesPendingResetOnce() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    _ = controller.beginUtterance()
    let id = controller.beginUtterance()
    let translation = controllerPCM16([2_000])

    #expect(
        controller.appendTranslation(
            translation,
            utteranceID: id
        ) == [.reset]
    )
    #expect(controller.observe(
        LanguageHypotheses(["de": 0.9]),
        utteranceID: id
    ) == [.beginCrossfade([translation], rampSamples: 1_920)])
}

@Test
func rolloverObservationPrefixesResetBeforeRouteCommand() {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    _ = controller.beginUtterance()
    let id = controller.beginUtterance()

    #expect(controller.observe(
        LanguageHypotheses(["zh": 0.9]),
        utteranceID: id
    ) == [
        .reset,
        .setOriginalGain(1.0, rampSamples: 1_920),
    ])
}

@Test
func nativeRouteRendersOnlyLiveOriginalSamplesWithoutReplay() throws {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    var renderer = InboundAuditionRenderer()
    let id = controller.beginUtterance()
    var output = try render(
        controller.appendOriginal(
            controllerPCM16([1_000, 2_000]),
            utteranceID: id
        ),
        using: &renderer
    )
    _ = try render(
        controller.observe(
            LanguageHypotheses(["zh": 0.9]),
            utteranceID: id
        ),
        using: &renderer
    )
    output += try render(
        controller.appendOriginal(
            controllerPCM16([3_000]),
            utteranceID: id
        ),
        using: &renderer
    )

    #expect(output.map(\.source) == [.original, .original])
    #expect(output.flatMap { decodeControllerPCM16($0.pcm16) } == [
        120,
        240,
        360,
    ])
}

@Test
func failOpenRendersOnlyLiveOriginalSamplesWithoutReplay() throws {
    var controller = InboundAuditionController(motherLanguage: .chinese)
    var renderer = InboundAuditionRenderer()
    let id = controller.beginUtterance()
    var output = try render(
        controller.appendOriginal(
            controllerPCM16([1_000, 2_000]),
            utteranceID: id
        ),
        using: &renderer
    )
    _ = controller.observe(
        LanguageHypotheses(["de": 0.9]),
        utteranceID: id
    )
    _ = try render(
        controller.appendTranslation(
            controllerPCM16([500]),
            utteranceID: id
        ),
        using: &renderer
    )
    output += try render(
        controller.appendOriginal(
            controllerPCM16([3_000]),
            utteranceID: id
        ),
        using: &renderer
    )
    _ = try render(controller.failOpen(), using: &renderer)
    output += try render(
        controller.appendOriginal(
            controllerPCM16([4_000]),
            utteranceID: id
        ),
        using: &renderer
    )

    #expect(output.map(\.source) == [
        .original,
        .crossfade,
        .original,
    ])
    #expect(output.flatMap { decodeControllerPCM16($0.pcm16) } == [
        120,
        240,
        360,
        480,
    ])
}
