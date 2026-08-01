import Testing
@testable import EMKECoordinator

@Test
func trackerComputesOnlyFirstOccurrenceOfEachMilestone() {
    var tracker = TranslationLatencyTracker(capacity: 2)
    tracker.mark(.speechStarted, utteranceID: 7, at: 1_000_000)
    tracker.mark(.firstNetworkFrameSent, utteranceID: 7, at: 41_000_000)
    tracker.mark(.firstNetworkFrameSent, utteranceID: 7, at: 99_000_000)

    let value = tracker.snapshot(for: 7)
    #expect(value?.speechToFirstNetworkFrameMilliseconds == 40)
}

@Test
func trackerEvictsOldUtterancesAndResetClearsMemory() {
    var tracker = TranslationLatencyTracker(capacity: 1)
    tracker.mark(.speechStarted, utteranceID: 1, at: 1)
    tracker.mark(.speechStarted, utteranceID: 2, at: 2)
    #expect(tracker.snapshot(for: 1) == nil)
    tracker.reset()
    #expect(tracker.latestSnapshot == nil)
}

@Test
func trackerPublishesNearestRankP95() {
    var tracker = TranslationLatencyTracker(capacity: 20)
    for id in UInt64(1)...20 {
        tracker.mark(.speechStarted, utteranceID: id, at: 0)
        tracker.mark(
            .firstNetworkFrameSent,
            utteranceID: id,
            at: id * 1_000_000
        )
    }
    let value = tracker.diagnostics.summary.speechToFirstNetworkFrame
    #expect(value.sampleCount == 20)
    #expect(value.p50Milliseconds == 10)
    #expect(value.p95Milliseconds == 19)
}

@Test
func trackerReportsOnlyCompletedIntervalsAndKeepsEmptyPercentilesNil() {
    var tracker = TranslationLatencyTracker()
    tracker.mark(.speechStarted, utteranceID: 1, at: 1_000_000)

    let diagnostics = tracker.diagnostics
    #expect(diagnostics.latest?.speechToFirstNetworkFrameMilliseconds == nil)
    #expect(diagnostics.summary.speechToFirstNetworkFrame == .empty)
    #expect(diagnostics.summary.translationAudioToPlayback == .empty)
}

@Test
func trackerNeverProducesNegativeDurationsForOutOfOrderMilestones() {
    var tracker = TranslationLatencyTracker()
    tracker.mark(.speechStarted, utteranceID: 1, at: 10_000_000)
    tracker.mark(.firstNetworkFrameSent, utteranceID: 1, at: 5_000_000)
    tracker.mark(.firstTranslationAudioReceived, utteranceID: 1, at: 20_000_000)
    tracker.mark(.firstPlaybackScheduled, utteranceID: 1, at: 15_000_000)

    let value = tracker.snapshot(for: 1)
    #expect(value?.speechToFirstNetworkFrameMilliseconds == nil)
    #expect(value?.translationAudioToPlaybackMilliseconds == nil)
}

@Test
func trackerUsesNearestRankAtSmallSampleBoundaries() {
    var tracker = TranslationLatencyTracker(capacity: 2)
    for id in UInt64(1)...2 {
        tracker.mark(.speechStarted, utteranceID: id, at: 0)
        tracker.mark(.firstNetworkFrameSent, utteranceID: id, at: id * 1_000_000)
    }

    let value = tracker.diagnostics.summary.speechToFirstNetworkFrame
    #expect(value.sampleCount == 2)
    #expect(value.p50Milliseconds == 1)
    #expect(value.p95Milliseconds == 2)
}

@Test
func trackerStoresOnlyTypedAnonymousIdentifiersAndTimes() {
    var tracker = TranslationLatencyTracker(capacity: 1)
    tracker.mark(.speechStarted, utteranceID: 42, at: 0)
    tracker.mark(.firstSourceTranscriptReceived, utteranceID: 42, at: 2_000_000)

    let value: TranslationLatencySnapshot? = tracker.latestSnapshot
    #expect(value?.utteranceID == 42)
    #expect(value?.speechToFirstSourceTranscriptMilliseconds == 2)
}
