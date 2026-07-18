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
