import CoreAudio
import Foundation
import Testing
@testable import EMKEAudioEngine

private final class DiagnosticInputFake: AudioInputEndpoint, @unchecked Sendable {
    var samples: [Float] = []
    var transportDiagnostics = AudioInputTransportDiagnostics.unavailable
    private(set) var startCount = 0
    private(set) var stopCount = 0

    func start() throws {
        startCount += 1
    }

    func stop() {
        stopCount += 1
    }

    func read(
        into interleavedSamples: UnsafeMutableBufferPointer<Float>
    ) -> Int {
        let copiedSampleCount = min(samples.count, interleavedSamples.count)
        guard copiedSampleCount > 0 else { return 0 }
        for index in 0..<copiedSampleCount {
            interleavedSamples[index] = samples[index]
        }
        samples.removeFirst(copiedSampleCount)
        return copiedSampleCount / 2
    }

    func diagnostics() -> AudioInputTransportDiagnostics {
        transportDiagnostics
    }
}

private final class DiagnosticOutputFake: AudioOutputEndpoint, @unchecked Sendable {
    private(set) var startCount = 0
    private(set) var stopCount = 0
    private(set) var writes: [[Float]] = []

    func start() throws {
        startCount += 1
    }

    func stop() {
        stopCount += 1
    }

    func write(_ interleavedSamples: UnsafeBufferPointer<Float>) -> Int {
        writes.append(Array(interleavedSamples))
        return interleavedSamples.count / 2
    }
}

private struct DiagnosticEndpointFactory: AudioEndpointFactory, @unchecked Sendable {
    let input: DiagnosticInputFake
    let output: DiagnosticOutputFake

    func makeInput(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws -> any AudioInputEndpoint {
        input
    }

    func makeOutput(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws -> any AudioOutputEndpoint {
        output
    }
}

@Test
func inputDiagnosticDistinguishesNoFramesFromCapturedAudio() async throws {
    let input = DiagnosticInputFake()
    input.transportDiagnostics = AudioInputTransportDiagnostics(
        isAvailable: true,
        isStarted: true,
        callbackCount: 0,
        lastCallbackFrameCount: 0,
        renderedFrameCount: 0,
        writtenFrameCount: 0,
        renderErrorCount: 0,
        oversizedCallbackCount: 0,
        lastRenderStatus: noErr,
        scratchCapacityFrames: 512
    )
    let output = DiagnosticOutputFake()
    let diagnostics = LocalAudioDiagnostics(
        factory: DiagnosticEndpointFactory(input: input, output: output)
    )

    try await diagnostics.startInput(deviceID: 42)
    let waiting = await diagnostics.sampleInput()
    #expect(waiting.state == .waitingForFrames)
    #expect(waiting.frameCount == 0)
    #expect(waiting.transportDiagnostics.isAvailable)
    #expect(waiting.transportDiagnostics.callbackCount == 0)

    input.samples = Array(repeating: 0.2, count: 960)
    let captured = await diagnostics.sampleInput()
    #expect(captured.state == .receivingAudio)
    #expect(captured.frameCount == 480)
    #expect(captured.level > 0.5)

    await diagnostics.stopInput()
    #expect(input.startCount == 1)
    #expect(input.stopCount == 1)
}

@Test
func inputDiagnosticConsumesOneRealtimeMicQuantumPerRefresh() async throws {
    let input = DiagnosticInputFake()
    let output = DiagnosticOutputFake()
    let diagnostics = LocalAudioDiagnostics(
        factory: DiagnosticEndpointFactory(input: input, output: output)
    )
    let quietFrames = 1_725
    let voicedFrames = 480
    input.samples = Array(repeating: 0, count: quietFrames * 2)
        + Array(repeating: 0.1, count: voicedFrames * 2)

    try await diagnostics.startInput(deviceID: 42)
    let sample = await diagnostics.sampleInput()

    #expect(sample.frameCount == quietFrames + voicedFrames)
    #expect(sample.state == .receivingAudio)
    #expect(sample.level > 0.3)
}

@Test
func outputDiagnosticWritesSafeStereoTestTone() async throws {
    let input = DiagnosticInputFake()
    let output = DiagnosticOutputFake()
    let diagnostics = LocalAudioDiagnostics(
        factory: DiagnosticEndpointFactory(input: input, output: output)
    )

    let result = try await diagnostics.startOutputTest(deviceID: 84)
    let samples = try #require(output.writes.first)

    #expect(result.requestedFrames > 0)
    #expect(result.writtenFrames == result.requestedFrames)
    #expect(samples.count == result.requestedFrames * 2)
    #expect(samples.map(abs).max() ?? 1 <= 0.15)
    #expect(stride(from: 0, to: samples.count, by: 2).allSatisfy {
        samples[$0] == samples[$0 + 1]
    })

    await diagnostics.stopOutputTest()
    #expect(output.startCount == 1)
    #expect(output.stopCount == 1)
}
