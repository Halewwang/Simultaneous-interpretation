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
func adaptiveVADRequiresTwoVoicedFramesToStart() throws {
    var detector = AdaptivePCMVoiceActivityDetector()

    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .none)
    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .speechStarted)
    #expect(detector.isSpeaking)
}

@Test
func noiseFloorAdaptsOnlyOutsideSpeech() throws {
    var detector = AdaptivePCMVoiceActivityDetector()

    for _ in 0..<20 {
        _ = try detector.observe(pcm16(amplitude: 120))
    }
    let learned = detector.noiseFloor

    _ = try detector.observe(pcm16(amplitude: 8_000))
    _ = try detector.observe(pcm16(amplitude: 8_000))

    #expect(detector.noiseFloor == learned)
}

@Test
func adaptiveVADEndsAfterThirtySilentFrames() throws {
    var detector = AdaptivePCMVoiceActivityDetector()

    _ = try detector.observe(pcm16(amplitude: 2_000))
    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .speechStarted)
    for _ in 0..<29 {
        #expect(try detector.observe(pcm16(amplitude: 0)) == .none)
    }

    #expect(try detector.observe(pcm16(amplitude: 0)) == .speechEnded)
    #expect(!detector.isSpeaking)
}

@Test
func adaptiveVADClampsThresholdToConfiguredBounds() {
    let belowMinimum = AdaptivePCMVoiceActivityDetector(initialNoiseFloor: 0.0001)
    let aboveMaximum = AdaptivePCMVoiceActivityDetector(initialNoiseFloor: 0.02)

    #expect(belowMinimum.currentThreshold == 0.006)
    #expect(aboveMaximum.currentThreshold == 0.030)
}

@Test
func adaptiveVADResetRestoresInitialState() throws {
    var detector = AdaptivePCMVoiceActivityDetector(initialNoiseFloor: 0.004)

    _ = try detector.observe(pcm16(amplitude: 120))
    _ = try detector.observe(pcm16(amplitude: 2_000))
    _ = try detector.observe(pcm16(amplitude: 2_000))
    detector.reset()

    #expect(!detector.isSpeaking)
    #expect(detector.noiseFloor == 0.004)
    #expect(detector.currentThreshold == 0.012)
    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .none)
}

@Test
func adaptiveVADIgnoresEmptyInput() throws {
    var detector = AdaptivePCMVoiceActivityDetector()

    #expect(try detector.observe(Data()) == .none)
    #expect(!detector.isSpeaking)
    #expect(detector.noiseFloor == 0.002)
}

@Test
func adaptiveVADRejectsIncompletePCM16Samples() {
    var detector = AdaptivePCMVoiceActivityDetector()

    #expect(throws: PCMVoiceActivityDetectorError.invalidPCM16ByteCount) {
        try detector.observe(Data([1]))
    }
}

@Test
func adaptiveVADIgnoresTransientNoiseBeforeAttackCompletes() throws {
    var detector = AdaptivePCMVoiceActivityDetector()

    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .none)
    #expect(try detector.observe(pcm16(amplitude: 0)) == .none)
    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .none)
    #expect(!detector.isSpeaking)
}

@Test
func inboundVADSelectsAdaptiveDetectorWhenEnabled() throws {
    var detector = InboundVoiceActivityDetector(audioStability: .production)

    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .none)
    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .speechStarted)
    #expect(detector.isSpeaking)
}

@Test
func inboundVADSelectsLegacyDetectorWhenAdaptiveVADIsDisabled() throws {
    var detector = InboundVoiceActivityDetector(audioStability: .legacy)

    #expect(try detector.observe(pcm16(amplitude: 2_000)) == .speechStarted)
    detector.reset()
    #expect(!detector.isSpeaking)
}
