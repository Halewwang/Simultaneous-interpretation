import Foundation

public enum PCMVoiceActivityEvent: Equatable, Sendable {
    case none
    case speechStarted
    case speechEnded
}

public enum PCMVoiceActivityDetectorError: Error, Equatable, Sendable {
    case invalidPCM16ByteCount
}

public struct PCMVoiceActivityDetector: Sendable {
    public let speechThreshold: Double
    public let silenceFrameLimit: Int

    public private(set) var isSpeaking = false
    private var consecutiveSilentFrames = 0

    public init(
        speechThreshold: Double = 0.015,
        silenceFrameLimit: Int = 30
    ) {
        precondition(speechThreshold >= 0)
        precondition(silenceFrameLimit > 0)
        self.speechThreshold = speechThreshold
        self.silenceFrameLimit = silenceFrameLimit
    }

    public mutating func observe(
        _ pcm16: Data
    ) throws -> PCMVoiceActivityEvent {
        guard pcm16.count.isMultiple(of: 2) else {
            throw PCMVoiceActivityDetectorError.invalidPCM16ByteCount
        }
        guard !pcm16.isEmpty else { return .none }

        let rms = pcm16.withUnsafeBytes { rawBuffer -> Double in
            let bytes = rawBuffer.bindMemory(to: UInt8.self)
            var squareSum = 0.0
            var index = 0
            while index < bytes.count {
                let bits = UInt16(bytes[index])
                    | UInt16(bytes[index + 1]) << 8
                let normalized = Double(Int16(bitPattern: bits)) / 32_768
                squareSum += normalized * normalized
                index += 2
            }
            return sqrt(squareSum / Double(bytes.count / 2))
        }

        if rms >= speechThreshold {
            consecutiveSilentFrames = 0
            if !isSpeaking {
                isSpeaking = true
                return .speechStarted
            }
            return .none
        }

        guard isSpeaking else { return .none }
        consecutiveSilentFrames += 1
        guard consecutiveSilentFrames >= silenceFrameLimit else {
            return .none
        }

        isSpeaking = false
        consecutiveSilentFrames = 0
        return .speechEnded
    }

    public mutating func reset() {
        isSpeaking = false
        consecutiveSilentFrames = 0
    }
}
