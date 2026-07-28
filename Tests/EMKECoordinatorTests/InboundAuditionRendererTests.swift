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
func rendererPreviewsOriginalAtTwelvePercent() throws {
    var renderer = InboundAuditionRenderer()

    let output = try renderer.consume(.original(
        constantPCM16(10_000, samples: 240)
    ))

    #expect(output.count == 1)
    #expect(output[0].source == .original)
    #expect(decodePCM16(output[0].pcm16).allSatisfy { $0 == 1_200 })
}

@Test
func originalRecoveryRampContinuesAcrossChunks() throws {
    var renderer = InboundAuditionRenderer()
    _ = try renderer.consume(
        .setOriginalGain(1.0, rampSamples: 1_920)
    )

    let first = try renderer.consume(.original(
        constantPCM16(10_000, samples: 960)
    ))
    let second = try renderer.consume(.original(
        constantPCM16(10_000, samples: 960)
    ))

    #expect(first.allSatisfy { $0.source == .original })
    #expect(second.allSatisfy { $0.source == .original })
    #expect(decodePCM16(first[0].pcm16).first == 1_200)
    #expect(abs(Int(decodePCM16(second[0].pcm16).last ?? 0) - 10_000) <= 1)
}

@Test
func rendererMixesBothStreamsForExactlyEightyMilliseconds() throws {
    var renderer = InboundAuditionRenderer()
    _ = try renderer.consume(.beginCrossfade(
        [constantPCM16(2_000, samples: 1_920)],
        rampSamples: 1_920
    ))

    let output = try renderer.consume(.original(
        constantPCM16(10_000, samples: 1_920)
    ))
    let samples = output.flatMap { decodePCM16($0.pcm16) }

    #expect(output.allSatisfy { $0.source == .crossfade })
    #expect(samples.count == 1_920)
    #expect(abs(Int(samples.first ?? 0) - 1_200) <= 1)
    #expect(abs(Int(samples.last ?? 0) - 2_000) <= 1)
}

@Test
func crossfadeRampsContinueAcrossOriginalChunks() throws {
    var renderer = InboundAuditionRenderer()
    _ = try renderer.consume(.beginCrossfade(
        [constantPCM16(2_000, samples: 1_920)],
        rampSamples: 1_920
    ))

    let first = try renderer.consume(.original(
        constantPCM16(10_000, samples: 960)
    ))
    let second = try renderer.consume(.original(
        constantPCM16(10_000, samples: 960)
    ))

    #expect(first.count == 1)
    #expect(second.count == 1)
    #expect(first[0].source == .crossfade)
    #expect(second[0].source == .crossfade)
    #expect(decodePCM16(first[0].pcm16).first == 1_200)
    #expect(abs(Int(decodePCM16(second[0].pcm16).last ?? 0) - 2_000) <= 1)
}

@Test
func rendererFlushesTranslationBeyondCrossfadeAtFullGain() throws {
    var renderer = InboundAuditionRenderer()
    _ = try renderer.consume(.beginCrossfade(
        [constantPCM16(2_000, samples: 2_000)],
        rampSamples: 1_920
    ))

    let output = try renderer.consume(.original(
        constantPCM16(10_000, samples: 1_920)
    ))

    #expect(output.map(\.source) == [.crossfade, .translation])
    #expect(output[0].pcm16.count == 1_920 * 2)
    #expect(output[1].pcm16 == constantPCM16(2_000, samples: 80))
}

@Test
func completedCrossfadeDropsOriginalAndMarksTranslation() throws {
    var renderer = InboundAuditionRenderer()
    _ = try renderer.consume(.beginCrossfade(
        [constantPCM16(2_000, samples: 1_920)],
        rampSamples: 1_920
    ))
    _ = try renderer.consume(.original(
        constantPCM16(10_000, samples: 1_920)
    ))

    #expect(try renderer.consume(.original(
        constantPCM16(10_000, samples: 1)
    )).isEmpty)

    let translated = constantPCM16(3_000, samples: 2)
    let output = try renderer.consume(.translation(translated))
    #expect(output == [
        InboundRenderedChunk(pcm16: translated, source: .translation),
    ])
}

@Test
func failOpenClearsTranslationAndRecoversFromCurrentOriginalGain() throws {
    var renderer = InboundAuditionRenderer()
    _ = try renderer.consume(.beginCrossfade(
        [constantPCM16(2_000, samples: 1_920)],
        rampSamples: 1_920
    ))
    _ = try renderer.consume(.original(
        constantPCM16(10_000, samples: 960)
    ))

    #expect(
        try renderer.consume(.failOpen(rampSamples: 1_920)).isEmpty
    )
    let output = try renderer.consume(.original(
        constantPCM16(10_000, samples: 1)
    ))
    let sample = decodePCM16(output[0].pcm16)[0]

    #expect(output.count == 1)
    #expect(output[0].source == .original)
    #expect(abs(Int(sample) - 600) <= 1)
}

@Test
func resetRestoresInitialPreviewGain() throws {
    var renderer = InboundAuditionRenderer()
    _ = try renderer.consume(
        .setOriginalGain(1.0, rampSamples: 0)
    )
    _ = try renderer.consume(.reset)

    let output = try renderer.consume(.original(
        constantPCM16(10_000, samples: 1)
    ))

    #expect(decodePCM16(output[0].pcm16) == [1_200])
    #expect(output[0].source == .original)
}

@Test
func translationQueueRejectsBytesBeyondConfiguredLimit() throws {
    var renderer = InboundAuditionRenderer(
        maximumQueuedBytesPerSource: 4
    )

    #expect(throws: InboundAuditionRendererError.bufferLimitExceeded) {
        try renderer.consume(.beginCrossfade(
            [constantPCM16(2_000, samples: 3)],
            rampSamples: 1_920
        ))
    }
}

@Test
func originalQueueRejectsBytesBeyondConfiguredLimit() throws {
    var renderer = InboundAuditionRenderer(
        maximumQueuedBytesPerSource: 4
    )
    _ = try renderer.consume(
        .beginCrossfade([], rampSamples: 1_920)
    )

    #expect(throws: InboundAuditionRendererError.bufferLimitExceeded) {
        try renderer.consume(.original(
            constantPCM16(10_000, samples: 3)
        ))
    }
}

@Test
func rendererUsesExistingOddByteProcessingError() {
    var renderer = InboundAuditionRenderer()

    #expect(throws: PCM16ProcessingError.invalidPCM16ByteCount) {
        try renderer.consume(.original(Data([0x01])))
    }
    #expect(throws: PCM16ProcessingError.invalidPCM16ByteCount) {
        try renderer.consume(.beginCrossfade(
            [Data([0x01])],
            rampSamples: 1_920
        ))
    }
    #expect(throws: PCM16ProcessingError.invalidPCM16ByteCount) {
        try renderer.consume(.translation(Data([0x01])))
    }
}

@Test
func rendererRejectsNegativeRampSampleCounts() {
    var renderer = InboundAuditionRenderer()

    #expect(throws: PCM16ProcessingError.invalidRampSampleCount) {
        try renderer.consume(
            .setOriginalGain(1.0, rampSamples: -1)
        )
    }
    #expect(throws: PCM16ProcessingError.invalidRampSampleCount) {
        try renderer.consume(
            .beginCrossfade([], rampSamples: -1)
        )
    }
    #expect(throws: PCM16ProcessingError.invalidRampSampleCount) {
        try renderer.consume(.failOpen(rampSamples: -1))
    }
}

@Test
func rendererRejectsRampSampleCountThatCannotConvertToByteCount() {
    var renderer = InboundAuditionRenderer()

    #expect(throws: PCM16ProcessingError.invalidRampSampleCount) {
        try renderer.consume(
            .beginCrossfade([], rampSamples: Int.max)
        )
    }
}
