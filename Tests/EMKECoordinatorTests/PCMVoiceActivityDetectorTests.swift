import Foundation
import Testing
@testable import EMKECoordinator

private func pcm16(amplitude: Int16, sampleCount: Int = 240) -> Data {
    let bits = UInt16(bitPattern: amplitude)
    var data = Data(capacity: sampleCount * 2)
    for _ in 0..<sampleCount {
        data.append(UInt8(truncatingIfNeeded: bits))
        data.append(UInt8(truncatingIfNeeded: bits >> 8))
    }
    return data
}

@Test
func speechStartsOnVoicedPCMAndEndsAfterConfiguredSilence() throws {
    var detector = PCMVoiceActivityDetector(silenceFrameLimit: 3)

    #expect(
        try detector.observe(pcm16(amplitude: 8_000)) == .speechStarted
    )
    #expect(try detector.observe(pcm16(amplitude: 0)) == .none)
    #expect(try detector.observe(pcm16(amplitude: 0)) == .none)
    #expect(
        try detector.observe(pcm16(amplitude: 0)) == .speechEnded
    )
    #expect(!detector.isSpeaking)
}

@Test
func continuousSpeechDoesNotRepeatTheStartEdge() throws {
    var detector = PCMVoiceActivityDetector(silenceFrameLimit: 2)

    #expect(
        try detector.observe(pcm16(amplitude: 10_000)) == .speechStarted
    )
    #expect(
        try detector.observe(pcm16(amplitude: 10_000)) == .none
    )
    #expect(detector.isSpeaking)
}

@Test
func vadRejectsIncompletePCM16Samples() {
    var detector = PCMVoiceActivityDetector()

    #expect(throws: PCMVoiceActivityDetectorError.invalidPCM16ByteCount) {
        try detector.observe(Data([1]))
    }
}
