import Foundation

public struct AdaptivePCMVoiceActivityDetector: Sendable {
    public private(set) var isSpeaking = false
    public private(set) var noiseFloor: Double

    public var currentThreshold: Double {
        min(max(noiseFloor * thresholdMultiplier, minimumThreshold), maximumThreshold)
    }

    private let initialNoiseFloor: Double
    private let noiseFloorEMA: Double
    private let thresholdMultiplier: Double
    private let minimumThreshold: Double
    private let maximumThreshold: Double
    private let attackFrameLimit: Int
    private let silenceFrameLimit: Int

    private var consecutiveVoicedFrames = 0
    private var consecutiveSilentFrames = 0

    public init(
        initialNoiseFloor: Double = 0.002,
        noiseFloorEMA: Double = 0.05,
        thresholdMultiplier: Double = 3.0,
        minimumThreshold: Double = 0.006,
        maximumThreshold: Double = 0.030,
        attackFrameLimit: Int = 2,
        silenceFrameLimit: Int = 30
    ) {
        precondition(initialNoiseFloor >= 0)
        precondition((0...1).contains(noiseFloorEMA))
        precondition(thresholdMultiplier >= 0)
        precondition(minimumThreshold >= 0)
        precondition(maximumThreshold >= minimumThreshold)
        precondition(attackFrameLimit > 0)
        precondition(silenceFrameLimit > 0)

        self.initialNoiseFloor = initialNoiseFloor
        self.noiseFloorEMA = noiseFloorEMA
        self.thresholdMultiplier = thresholdMultiplier
        self.minimumThreshold = minimumThreshold
        self.maximumThreshold = maximumThreshold
        self.attackFrameLimit = attackFrameLimit
        self.silenceFrameLimit = silenceFrameLimit
        noiseFloor = initialNoiseFloor
    }

    public mutating func observe(
        _ pcm16: Data
    ) throws -> PCMVoiceActivityEvent {
        guard pcm16.count.isMultiple(of: 2) else {
            throw PCMVoiceActivityDetectorError.invalidPCM16ByteCount
        }
        guard !pcm16.isEmpty else { return .none }

        let rms = rms(for: pcm16)
        if isSpeaking {
            guard rms < currentThreshold else {
                consecutiveSilentFrames = 0
                return .none
            }

            consecutiveSilentFrames += 1
            guard consecutiveSilentFrames >= silenceFrameLimit else {
                return .none
            }

            isSpeaking = false
            consecutiveVoicedFrames = 0
            consecutiveSilentFrames = 0
            return .speechEnded
        }

        guard rms < currentThreshold else {
            consecutiveVoicedFrames += 1
            guard consecutiveVoicedFrames >= attackFrameLimit else {
                return .none
            }

            isSpeaking = true
            consecutiveVoicedFrames = 0
            consecutiveSilentFrames = 0
            return .speechStarted
        }

        consecutiveVoicedFrames = 0
        noiseFloor += noiseFloorEMA * (rms - noiseFloor)
        return .none
    }

    public mutating func reset() {
        isSpeaking = false
        noiseFloor = initialNoiseFloor
        consecutiveVoicedFrames = 0
        consecutiveSilentFrames = 0
    }

    private func rms(for pcm16: Data) -> Double {
        pcm16.withUnsafeBytes { rawBuffer in
            let bytes = rawBuffer.bindMemory(to: UInt8.self)
            var squareSum = 0.0
            var index = 0
            while index < bytes.count {
                let bits = UInt16(bytes[index]) | UInt16(bytes[index + 1]) << 8
                let normalized = Double(Int16(bitPattern: bits)) / 32_768
                squareSum += normalized * normalized
                index += 2
            }
            return sqrt(squareSum / Double(bytes.count / 2))
        }
    }
}

enum InboundVoiceActivityDetector: Sendable {
    case fixed(PCMVoiceActivityDetector)
    case adaptive(AdaptivePCMVoiceActivityDetector)

    init(audioStability: AudioStabilityConfiguration) {
        self = audioStability.adaptiveVADEnabled
            ? .adaptive(AdaptivePCMVoiceActivityDetector())
            : .fixed(PCMVoiceActivityDetector())
    }

    var isSpeaking: Bool {
        switch self {
        case .fixed(let detector): detector.isSpeaking
        case .adaptive(let detector): detector.isSpeaking
        }
    }

    mutating func observe(_ pcm16: Data) throws -> PCMVoiceActivityEvent {
        switch self {
        case .fixed(var detector):
            let event = try detector.observe(pcm16)
            self = .fixed(detector)
            return event
        case .adaptive(var detector):
            let event = try detector.observe(pcm16)
            self = .adaptive(detector)
            return event
        }
    }

    mutating func reset() {
        switch self {
        case .fixed(var detector):
            detector.reset()
            self = .fixed(detector)
        case .adaptive(var detector):
            detector.reset()
            self = .adaptive(detector)
        }
    }
}
