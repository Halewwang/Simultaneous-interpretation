import Testing
@testable import EMKEMenuBarApp

@Test
func waveformProducesTwentyFourDeterministicBars() {
    let first = WaveformBarLayout.heights(level: 0.65)
    let second = WaveformBarLayout.heights(level: 0.65)
    #expect(first.count == 24)
    #expect(first == second)
}

@Test
func silenceUsesLowStaticBaseline() {
    let heights = WaveformBarLayout.heights(level: 0)
    #expect(heights.allSatisfy { $0 >= 4 && $0 <= 7 })
}

@Test
func floatingWaveformSilenceIsNearFlatAndActiveAudioRemainsVisible() {
    let silence = WaveformBarLayout.heights(
        level: 0,
        minimum: 0.5,
        maximum: 24
    )
    let active = WaveformBarLayout.heights(
        level: 0.68,
        minimum: 0.5,
        maximum: 24
    )

    #expect(silence.max() ?? .infinity <= 2.5)
    #expect(active.max() ?? 0 >= 16)
    #expect((active.max() ?? 0) - (silence.max() ?? 0) >= 13)
}

@Test
func compactWaveformMatchesFloatingColumnExactly() {
    #expect(
        WaveformBarLayout.compactRequiredWidth
            == EMKEFloatingMetrics.waveformWidth
    )
}

@Test
func waveformClampsOutOfRangeLevels() {
    #expect(
        WaveformBarLayout.heights(level: -1)
            == WaveformBarLayout.heights(level: 0)
    )
    #expect(
        WaveformBarLayout.heights(level: 2)
            == WaveformBarLayout.heights(level: 1)
    )
}
