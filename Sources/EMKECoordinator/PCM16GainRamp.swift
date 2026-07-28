import Foundation

public enum PCM16ProcessingError: Error, Equatable, Sendable {
    case invalidPCM16ByteCount
    case mismatchedSampleCount
    case invalidRampSampleCount
}

public struct PCM16GainRamp: Sendable {
    public private(set) var currentGain: Double
    private var startGain: Double
    private var targetGain: Double
    private var totalSamples = 0
    private var processedSamples = 0

    public init(initialGain: Double) {
        currentGain = initialGain
        startGain = initialGain
        targetGain = initialGain
    }

    public mutating func setTarget(_ gain: Double, overSamples: Int) {
        precondition(overSamples >= 0)
        startGain = currentGain
        targetGain = gain
        totalSamples = overSamples
        processedSamples = 0
        if overSamples == 0 { currentGain = gain }
    }

    public mutating func process(_ pcm16: Data) throws -> Data {
        guard pcm16.count.isMultiple(of: 2) else {
            throw PCM16ProcessingError.invalidPCM16ByteCount
        }
        var output = Data(capacity: pcm16.count)
        var index = 0
        while index < pcm16.count {
            let sample = decodeSample(pcm16, at: index)
            let gain = gainForNextSample()
            let scaled = Double(sample) * gain
            let clamped = min(
                max(scaled, Double(Int16.min)),
                Double(Int16.max)
            )
            appendSample(Int16(clamped), to: &output)
            index += 2
        }
        return output
    }

    private mutating func gainForNextSample() -> Double {
        guard processedSamples < totalSamples else { return currentGain }

        let gain: Double
        if totalSamples == 1 {
            gain = targetGain
        } else {
            gain = startGain + (targetGain - startGain)
                * Double(processedSamples) / Double(totalSamples - 1)
        }
        processedSamples += 1
        currentGain = processedSamples == totalSamples ? targetGain : gain
        return gain
    }
}

public enum PCM16Mixer {
    public static func mix(_ lhs: Data, _ rhs: Data) throws -> Data {
        guard lhs.count.isMultiple(of: 2), rhs.count.isMultiple(of: 2) else {
            throw PCM16ProcessingError.invalidPCM16ByteCount
        }
        guard lhs.count == rhs.count else {
            throw PCM16ProcessingError.mismatchedSampleCount
        }

        var output = Data(capacity: lhs.count)
        var index = 0
        while index < lhs.count {
            let sum = Int32(decodeSample(lhs, at: index))
                + Int32(decodeSample(rhs, at: index))
            let clamped = min(max(sum, Int32(Int16.min)), Int32(Int16.max))
            appendSample(Int16(clamped), to: &output)
            index += 2
        }
        return output
    }
}

private func decodeSample(_ data: Data, at index: Int) -> Int16 {
    Int16(bitPattern: UInt16(data[index]) | UInt16(data[index + 1]) << 8)
}

private func appendSample(_ sample: Int16, to data: inout Data) {
    let bits = UInt16(bitPattern: sample)
    data.append(UInt8(truncatingIfNeeded: bits))
    data.append(UInt8(truncatingIfNeeded: bits >> 8))
}
