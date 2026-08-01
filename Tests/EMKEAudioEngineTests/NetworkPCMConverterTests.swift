import Foundation
@testable import EMKEAudioEngine
import Testing

@Test func stereoResamplerKeeps48kFramesBitExact() {
    var resampler = StreamingStereoResampler(
        sourceSampleRate: 48_000,
        targetSampleRate: 48_000
    )
    let samples: [Float] = [0, 0.1, 0.2, 0.3, 0.4, 0.5]

    #expect(resampler.append(samples) == samples)
}

@Test func stereoResamplerUpsamples24kWithLinearContinuity() {
    var resampler = StreamingStereoResampler(
        sourceSampleRate: 24_000,
        targetSampleRate: 48_000
    )

    let converted = resampler.append([
        0, 0,
        1, 1,
        2, 2,
    ])

    #expect(converted == [
        0, 0,
        0.5, 0.5,
        1, 1,
        1.5, 1.5,
    ])
}

@Test func stereoResamplerMatchesContiguousInputAcrossChunks() {
    let samples = (0..<480).flatMap { frame -> [Float] in
        let value = Float(frame) / 480
        return [value, -value]
    }
    var contiguous = StreamingStereoResampler(
        sourceSampleRate: 44_100,
        targetSampleRate: 48_000
    )
    let expected = contiguous.append(samples)

    var chunked = StreamingStereoResampler(
        sourceSampleRate: 44_100,
        targetSampleRate: 48_000
    )
    var actual = chunked.append(Array(samples[..<320]))
    actual.append(contentsOf: chunked.append(Array(samples[320...])))

    #expect(actual == expected)
    #expect(actual.count / 2 == 522)
}

@Test func encoderKeepsSilenceSilent() throws {
    var encoder = NetworkPCMEncoder()

    let encoded = try encoder.append48kStereo(
        Array(repeating: 0, count: 8)
    )

    #expect(encoded == Data([0, 0, 0, 0]))
}

@Test func encoderDownmixesStereoAndAveragesAdjacentFrames() throws {
    var encoder = NetworkPCMEncoder()

    let encoded = try encoder.append48kStereo([
        1, -1,
        0.5, 0.5,
    ])

    #expect(encoded == Data([0x00, 0x20]))
}

@Test func encoderClampsToSignedLittleEndianPCM16() throws {
    var encoder = NetworkPCMEncoder()

    let encoded = try encoder.append48kStereo([
        2, 2,
        2, 2,
        -2, -2,
        -2, -2,
    ])

    #expect(encoded == Data([0xff, 0x7f, 0x00, 0x80]))
}

@Test func encoderProducesOneSampleForEveryTwoStereoFrames() throws {
    var encoder = NetworkPCMEncoder()

    let first = try encoder.append48kStereo([
        0.25, 0.25,
        0.25, 0.25,
        0.5, 0.5,
    ])
    let second = try encoder.append48kStereo([
        0.5, 0.5,
    ])

    #expect(first.count == 2)
    #expect(second.count == 2)
}

@Test func encoderPreservesResultsAcrossOddFrameChunkBoundaries() throws {
    let samples: [Float] = [
        0.1, 0.1,
        0.2, 0.2,
        0.3, 0.3,
        0.4, 0.4,
        0.5, 0.5,
        0.6, 0.6,
    ]
    var contiguousEncoder = NetworkPCMEncoder()
    let contiguous = try contiguousEncoder.append48kStereo(samples)

    var chunkedEncoder = NetworkPCMEncoder()
    var chunked = try chunkedEncoder.append48kStereo(
        Array(samples[0..<6])
    )
    chunked.append(
        try chunkedEncoder.append48kStereo(Array(samples[6...]))
    )

    #expect(chunked == contiguous)
}

@Test func encoderRejectsAnIncompleteStereoFrame() {
    var encoder = NetworkPCMEncoder()

    #expect(throws: NetworkPCMError.misalignedStereoSamples) {
        try encoder.append48kStereo([0, 0, 0])
    }
}

@Test func decoderKeepsSilenceSilentAndDoublesTheFrameRate() throws {
    var decoder = NetworkPCMDecoder()

    let decoded = try decoder.append24kMonoPCM16(Data([0, 0, 0, 0]))

    #expect(decoded == Array(repeating: 0, count: 8))
}

@Test func decoderProducesTwoStereoFramesPer24kSample() throws {
    var decoder = NetworkPCMDecoder()

    let decoded = try decoder.append24kMonoPCM16(
        Data([0x00, 0x00, 0xff, 0x7f])
    )

    #expect(decoded.count == 8)
    #expect(stride(from: 0, to: decoded.count, by: 2).allSatisfy {
        decoded[$0] == decoded[$0 + 1]
    })
}

@Test func decoderSuppressesThe24kUpsamplingImageBand() throws {
    let sourceFrequency = 10_560.0
    let imageFrequency = 24_000.0 - sourceFrequency
    var pcm16 = Data()
    for frame in 0..<12_000 {
        let phase = 2 * Double.pi * sourceFrequency
            * Double(frame) / 24_000
        var sample = Int16(
            (sin(phase) * 0.5 * Double(Int16.max)).rounded()
        ).littleEndian
        withUnsafeBytes(of: &sample) { pcm16.append(contentsOf: $0) }
    }
    var decoder = NetworkPCMDecoder()

    let decoded = try decoder.append24kMonoPCM16(pcm16)
    let desiredMagnitude = toneMagnitude(
        decoded,
        frequency: sourceFrequency,
        sampleRate: 48_000
    )
    let imageMagnitude = toneMagnitude(
        decoded,
        frequency: imageFrequency,
        sampleRate: 48_000
    )

    #expect(desiredMagnitude > 0)
    #expect(imageMagnitude < desiredMagnitude * 0.01)
}

@Test func decoderConvertsSignedPCM16AndDuplicatesBothChannels() throws {
    var positiveDecoder = NetworkPCMDecoder()
    var negativeDecoder = NetworkPCMDecoder()
    let positivePCM = Data(
        (0..<160).flatMap { _ in [UInt8(0xff), UInt8(0x7f)] }
    )
    let negativePCM = Data(
        (0..<160).flatMap { _ in [UInt8(0x00), UInt8(0x80)] }
    )

    let positive = try positiveDecoder.append24kMonoPCM16(positivePCM)
    let negative = try negativeDecoder.append24kMonoPCM16(negativePCM)

    #expect(positive.count == 640)
    #expect(negative.count == 640)
    #expect(positive.suffix(16).allSatisfy { abs($0 - 1) < 0.0001 })
    #expect(negative.suffix(16).allSatisfy { abs($0 + 1) < 0.0001 })
    #expect(stride(from: 0, to: positive.count, by: 2).allSatisfy {
        positive[$0] == positive[$0 + 1]
    })
}

@Test func decoderPreservesResultsAcrossChunkBoundaries() throws {
    let pcm = Data([0x00, 0x20, 0x00, 0x40, 0x00, 0x60])
    var contiguousDecoder = NetworkPCMDecoder()
    let contiguous = try contiguousDecoder.append24kMonoPCM16(pcm)

    var chunkedDecoder = NetworkPCMDecoder()
    var chunked = try chunkedDecoder.append24kMonoPCM16(pcm.prefix(2))
    chunked.append(
        contentsOf: try chunkedDecoder.append24kMonoPCM16(pcm.dropFirst(2))
    )

    #expect(chunked == contiguous)
}

@Test func decoderRejectsAnIncompletePCM16Sample() {
    var decoder = NetworkPCMDecoder()

    #expect(throws: NetworkPCMError.misalignedPCM16) {
        try decoder.append24kMonoPCM16(Data([0]))
    }
}

private func toneMagnitude(
    _ interleavedStereo: [Float],
    frequency: Double,
    sampleRate: Double
) -> Double {
    let mono = stride(from: 0, to: interleavedStereo.count, by: 2)
        .map { Double(interleavedStereo[$0]) }
    let start = min(512, mono.count / 4)
    let count = mono.count - start
    guard count > 1 else { return 0 }

    var real = 0.0
    var imaginary = 0.0
    for offset in 0..<count {
        let window = 0.5 - 0.5 * cos(
            2 * Double.pi * Double(offset) / Double(count - 1)
        )
        let phase = 2 * Double.pi * frequency
            * Double(start + offset) / sampleRate
        real += mono[start + offset] * window * cos(phase)
        imaginary -= mono[start + offset] * window * sin(phase)
    }
    return hypot(real, imaginary)
}
