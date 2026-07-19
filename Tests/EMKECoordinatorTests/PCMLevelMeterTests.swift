import Foundation
import Testing
@testable import EMKECoordinator

func levelMeterPCM16(
    amplitude: Int16,
    sampleCount: Int = 240
) -> Data {
    var data = Data(capacity: sampleCount * MemoryLayout<Int16>.size)
    for index in 0..<sampleCount {
        var sample = (index.isMultiple(of: 2) ? amplitude : -amplitude)
            .littleEndian
        withUnsafeBytes(of: &sample) { data.append(contentsOf: $0) }
    }
    return data
}

@Test
func silenceRemainsAtZero() throws {
    var meter = PCMLevelMeter()
    #expect(try meter.observe(levelMeterPCM16(amplitude: 0)) == 0)
}

@Test
func fixedPCMProducesNormalizedDeterministicLevel() throws {
    var first = PCMLevelMeter()
    var second = PCMLevelMeter()
    let sample = levelMeterPCM16(amplitude: 12_000)

    let firstLevel = try first.observe(sample)
    let secondLevel = try second.observe(sample)

    #expect(firstLevel > 0)
    #expect(firstLevel <= 1)
    #expect(abs(firstLevel - secondLevel) < 0.000_001)
}

@Test
func typicalBuiltInMicrophoneSpeechProducesVisibleLevel() throws {
    var meter = PCMLevelMeter()
    let typicalMicPCM = levelMeterPCM16(amplitude: 262)

    for _ in 0..<10 {
        _ = try meter.observe(typicalMicPCM)
    }

    #expect(meter.level > 0.2)
}

@Test
func attackIsFasterThanRelease() throws {
    var meter = PCMLevelMeter()
    let loud = levelMeterPCM16(amplitude: 18_000)
    let silence = levelMeterPCM16(amplitude: 0)

    let attacked = try meter.observe(loud)
    let released = try meter.observe(silence)

    #expect(attacked > 0)
    #expect(released > 0)
    #expect(released < attacked)
}

@Test
func resetClearsSmoothedLevel() throws {
    var meter = PCMLevelMeter()
    _ = try meter.observe(levelMeterPCM16(amplitude: 18_000))
    meter.reset()
    #expect(meter.level == 0)
}

@Test
func oddByteCountIsRejected() {
    var meter = PCMLevelMeter()
    #expect(throws: PCMLevelMeterError.oddByteCount) {
        try meter.observe(Data([0x01]))
    }
}

@Test(arguments: [0, -24_000])
func nonPositiveSampleRateIsRejected(sampleRate: Double) {
    var meter = PCMLevelMeter()
    #expect(throws: PCMLevelMeterError.invalidSampleRate) {
        try meter.observe(levelMeterPCM16(amplitude: 1), sampleRate: sampleRate)
    }
}

@Test
func combinedSnapshotUsesTheLouderChannel() {
    let snapshot = AudioLevelSnapshot(inbound: 0.3, outbound: 0.7)
    #expect(snapshot.combined == 0.7)
}
