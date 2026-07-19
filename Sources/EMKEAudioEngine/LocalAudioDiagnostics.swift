import CoreAudio
import Foundation

public enum AudioInputDiagnosticState: Equatable, Sendable {
    case stopped
    case waitingForFrames
    case receivingSilence
    case receivingAudio
}

public struct AudioInputDiagnosticSample: Equatable, Sendable {
    public let state: AudioInputDiagnosticState
    public let level: Double
    public let frameCount: Int
    public let rms: Double
    public let transportDiagnostics: AudioInputTransportDiagnostics

    public init(
        state: AudioInputDiagnosticState,
        level: Double,
        frameCount: Int,
        rms: Double,
        transportDiagnostics: AudioInputTransportDiagnostics = .unavailable
    ) {
        self.state = state
        self.level = level
        self.frameCount = frameCount
        self.rms = rms
        self.transportDiagnostics = transportDiagnostics
    }
}

public struct AudioOutputDiagnosticResult: Equatable, Sendable {
    public let requestedFrames: Int
    public let writtenFrames: Int

    public init(requestedFrames: Int, writtenFrames: Int) {
        self.requestedFrames = requestedFrames
        self.writtenFrames = writtenFrames
    }
}

public actor LocalAudioDiagnostics {
    private static let capacityFrames: UInt32 = 48_000
    private static let sampleFrames = 480
    private static let audioThreshold = 0.005
    private static let displayNoiseFloor = 0.002
    private static let displayCeiling = 0.15

    private let factory: any AudioEndpointFactory
    private var input: (any AudioInputEndpoint)?
    private var output: (any AudioOutputEndpoint)?
    private var inputCapture = Array(
        repeating: Float(0),
        count: sampleFrames * 2
    )

    public init() {
        factory = HALAudioEndpointFactory()
    }

    init(factory: any AudioEndpointFactory) {
        self.factory = factory
    }

    public func startInput(deviceID: AudioObjectID) throws {
        input?.stop()
        let newInput = try factory.makeInput(
            deviceID: deviceID,
            capacityFrames: Self.capacityFrames
        )
        try newInput.start()
        input = newInput
    }

    public func sampleInput() -> AudioInputDiagnosticSample {
        guard let input else {
            return AudioInputDiagnosticSample(
                state: .stopped,
                level: 0,
                frameCount: 0,
                rms: 0
            )
        }
        let transportDiagnostics = input.diagnostics()
        let frameCount = inputCapture.withUnsafeMutableBufferPointer {
            input.read(into: $0)
        }
        guard frameCount > 0 else {
            return AudioInputDiagnosticSample(
                state: .waitingForFrames,
                level: 0,
                frameCount: 0,
                rms: 0,
                transportDiagnostics: transportDiagnostics
            )
        }

        let sampleCount = frameCount * 2
        let sumOfSquares = inputCapture.prefix(sampleCount).reduce(into: 0.0) {
            sum, sample in
            sum += Double(sample * sample)
        }
        let rms = sqrt(sumOfSquares / Double(sampleCount))
        let displayRange = Self.displayCeiling - Self.displayNoiseFloor
        let level = min(
            max((rms - Self.displayNoiseFloor) / displayRange, 0),
            1
        )
        return AudioInputDiagnosticSample(
            state: rms >= Self.audioThreshold
                ? .receivingAudio
                : .receivingSilence,
            level: level,
            frameCount: frameCount,
            rms: rms,
            transportDiagnostics: transportDiagnostics
        )
    }

    public func stopInput() {
        input?.stop()
        input = nil
    }

    public func startOutputTest(
        deviceID: AudioObjectID
    ) throws -> AudioOutputDiagnosticResult {
        output?.stop()
        let newOutput = try factory.makeOutput(
            deviceID: deviceID,
            capacityFrames: Self.capacityFrames
        )
        try newOutput.start()
        let samples = Self.testTone()
        let writtenFrames = samples.withUnsafeBufferPointer {
            newOutput.write($0)
        }
        output = newOutput
        return AudioOutputDiagnosticResult(
            requestedFrames: samples.count / 2,
            writtenFrames: writtenFrames
        )
    }

    public func stopOutputTest() {
        output?.stop()
        output = nil
    }

    private static func testTone() -> [Float] {
        let sampleRate = 48_000.0
        let duration = 0.35
        let frequency = 660.0
        let amplitude = 0.12
        let frameCount = Int(sampleRate * duration)
        let fadeFrames = Int(sampleRate * 0.01)
        var samples: [Float] = []
        samples.reserveCapacity(frameCount * 2)

        for frame in 0..<frameCount {
            let fadeIn = min(Double(frame) / Double(fadeFrames), 1)
            let fadeOut = min(
                Double(frameCount - frame - 1) / Double(fadeFrames),
                1
            )
            let envelope = min(fadeIn, fadeOut)
            let phase = (2 * Double.pi * frequency * Double(frame)) / sampleRate
            let sample = Float(sin(phase) * amplitude * envelope)
            samples.append(sample)
            samples.append(sample)
        }
        return samples
    }
}
