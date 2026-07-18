import Foundation
@testable import EMKEAudioEngine
import Testing

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

@Test func decoderConvertsSignedPCM16AndDuplicatesBothChannels() throws {
    var decoder = NetworkPCMDecoder()

    let decoded = try decoder.append24kMonoPCM16(
        Data([0xff, 0x7f, 0x00, 0x80])
    )

    #expect(decoded == [
        1, 1,
        1, 1,
        -1, -1,
        -1, -1,
    ])
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
