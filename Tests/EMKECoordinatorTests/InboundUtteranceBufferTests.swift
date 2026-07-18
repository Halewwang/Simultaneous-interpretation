import EMKECore
import EMKERouting
import Foundation
import Testing
@testable import EMKECoordinator

@Test
func motherLanguageFlushesOnlyBufferedOriginalAndLocksRoute() {
    var buffer = InboundUtteranceBuffer(motherLanguage: .chinese)
    buffer.begin()

    #expect(buffer.appendOriginal(Data([1, 1])).isEmpty)
    #expect(buffer.appendTranslation(Data([2, 2])).isEmpty)
    #expect(
        buffer.observe(LanguageHypotheses(["zh": 0.9]))
            == [Data([1, 1])]
    )
    #expect(buffer.appendOriginal(Data([3, 3])) == [Data([3, 3])])
    #expect(buffer.appendTranslation(Data([4, 4])).isEmpty)
    #expect(buffer.currentRoute == .original)
}

@Test
func foreignLanguageFlushesOnlyTranslation() {
    var buffer = InboundUtteranceBuffer(motherLanguage: .chinese)
    buffer.begin()

    _ = buffer.appendOriginal(Data([1, 1]))
    _ = buffer.appendTranslation(Data([2, 2]))

    #expect(
        buffer.observe(LanguageHypotheses(["de": 0.8]))
            == [Data([2, 2])]
    )
    #expect(buffer.appendOriginal(Data([3, 3])).isEmpty)
    #expect(
        buffer.appendTranslation(Data([4, 4])) == [Data([4, 4])]
    )
    #expect(buffer.currentRoute == .translated)
}

@Test
func unresolvedSpeechDeadlinePrefersAvailableTranslation() {
    var buffer = InboundUtteranceBuffer(motherLanguage: .english)
    buffer.begin()
    _ = buffer.appendOriginal(Data([1, 1]))
    _ = buffer.appendTranslation(Data([2, 2]))

    #expect(buffer.resolveDeadline(isSpeech: true) == [Data([2, 2])])
}

@Test
func finishFailsOpenWhenNoTranslatedAudioExistsAndClearsMemory() {
    var buffer = InboundUtteranceBuffer(motherLanguage: .german)
    buffer.begin()
    _ = buffer.appendOriginal(Data([1, 1]))

    #expect(buffer.finish(isSpeech: true) == [Data([1, 1])])
    #expect(buffer.currentRoute == .undecided)
    #expect(buffer.bufferedByteCount == 0)
}

@Test
func bufferLimitFailsOpenInsteadOfGrowingWithoutBound() {
    var buffer = InboundUtteranceBuffer(
        motherLanguage: .chinese,
        maximumBufferedBytesPerCandidate: 4
    )
    buffer.begin()

    #expect(buffer.appendOriginal(Data([1, 1])).isEmpty)
    #expect(
        buffer.appendOriginal(Data([2, 2]))
            == [Data([1, 1]), Data([2, 2])]
    )
    #expect(buffer.currentRoute == .original)
}
