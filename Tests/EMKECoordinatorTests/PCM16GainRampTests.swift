import Foundation
import Testing
@testable import EMKECoordinator

private func constantPCM16(_ amplitude: Int16, samples: Int) -> Data {
    let bits = UInt16(bitPattern: amplitude)
    var data = Data(capacity: samples * 2)
    for _ in 0..<samples {
        data.append(UInt8(truncatingIfNeeded: bits))
        data.append(UInt8(truncatingIfNeeded: bits >> 8))
    }
    return data
}

private func decodePCM16(_ data: Data) -> [Int16] {
    stride(from: 0, to: data.count, by: 2).map { index in
        Int16(bitPattern:
            UInt16(data[index]) | UInt16(data[index + 1]) << 8
        )
    }
}

@Test
func eightyMillisecondRampReachesTargetAcrossChunks() throws {
    var ramp = PCM16GainRamp(initialGain: 0.12)
    ramp.setTarget(1.0, overSamples: 1_920)

    let first = try ramp.process(constantPCM16(10_000, samples: 960))
    let second = try ramp.process(constantPCM16(10_000, samples: 960))

    #expect(decodePCM16(first).first == 1_200)
    #expect(abs(Int(decodePCM16(second).last ?? 0) - 10_000) <= 1)
    #expect(ramp.currentGain == 1.0)
}

@Test
func completedRampFinalSampleUsesExactTargetGain() throws {
    var ramp = PCM16GainRamp(initialGain: 0.12)
    let target = Double(-1_723) / Double(-30_001)
    ramp.setTarget(target, overSamples: 2)

    let output = try ramp.process(constantPCM16(-30_001, samples: 2))

    #expect(decodePCM16(output).last == -1_723)
}

@Test
func mixerSaturatesInsteadOfWrapping() throws {
    let mixed = try PCM16Mixer.mix(
        constantPCM16(30_000, samples: 2),
        constantPCM16(30_000, samples: 2)
    )
    #expect(decodePCM16(mixed) == [Int16.max, Int16.max])
}

@Test
func mixerSaturatesNegativeValuesInsteadOfWrapping() throws {
    let mixed = try PCM16Mixer.mix(
        constantPCM16(-30_000, samples: 1),
        constantPCM16(-30_000, samples: 1)
    )

    #expect(decodePCM16(mixed) == [Int16.min])
}

@Test
func rampRejectsOddByteCounts() {
    var ramp = PCM16GainRamp(initialGain: 0.12)

    #expect(throws: PCM16ProcessingError.invalidPCM16ByteCount) {
        try ramp.process(Data([0x01]))
    }
}

@Test
func mixerRejectsMismatchedSampleCounts() {
    #expect(throws: PCM16ProcessingError.mismatchedSampleCount) {
        try PCM16Mixer.mix(
            constantPCM16(1, samples: 1),
            constantPCM16(1, samples: 2)
        )
    }
}

@Test
func mixerRejectsOddByteCounts() {
    #expect(throws: PCM16ProcessingError.invalidPCM16ByteCount) {
        try PCM16Mixer.mix(Data([0x01]), constantPCM16(1, samples: 1))
    }
}

@Test
func changingRampMidwayStartsAtCurrentGain() throws {
    var ramp = PCM16GainRamp(initialGain: 0.2)
    ramp.setTarget(1.0, overSamples: 4)
    _ = try ramp.process(constantPCM16(10_000, samples: 1))

    ramp.setTarget(0.6, overSamples: 2)
    let output = try ramp.process(constantPCM16(10_000, samples: 2))

    #expect(decodePCM16(output) == [2_000, 6_000])
    #expect(ramp.currentGain == 0.6)
}

@Test
func silenceStaysSilentAtEveryGain() throws {
    var ramp = PCM16GainRamp(initialGain: 0.12)
    ramp.setTarget(1.0, overSamples: 1_920)

    let output = try ramp.process(constantPCM16(0, samples: 3))

    #expect(decodePCM16(output) == [0, 0, 0])
}

@Test
func negativePCMUsesSignedLittleEndianSamples() throws {
    var ramp = PCM16GainRamp(initialGain: 0.5)

    let output = try ramp.process(constantPCM16(-10_000, samples: 2))

    #expect(decodePCM16(output) == [-5_000, -5_000])
}

@Test
func zeroSampleRampChangesGainImmediately() throws {
    var ramp = PCM16GainRamp(initialGain: 0.12)
    ramp.setTarget(0.5, overSamples: 0)

    let output = try ramp.process(constantPCM16(10_000, samples: 1))

    #expect(ramp.currentGain == 0.5)
    #expect(decodePCM16(output) == [5_000])
}
