import Foundation

public enum PCMLevelMeterError: Error, Equatable, Sendable {
    case oddByteCount
    case invalidSampleRate
}

public struct PCMLevelMeter: Sendable {
    public private(set) var level = 0.0

    private let noiseFloor: Double
    private let ceiling: Double
    private let attackSeconds: Double
    private let releaseSeconds: Double

    public init(
        noiseFloor: Double = 0.0015,
        ceiling: Double = 0.1,
        attackSeconds: Double = 0.08,
        releaseSeconds: Double = 0.22
    ) {
        self.noiseFloor = noiseFloor
        self.ceiling = ceiling
        self.attackSeconds = attackSeconds
        self.releaseSeconds = releaseSeconds
    }

    @discardableResult
    public mutating func observe(
        _ pcm16: Data,
        sampleRate: Double = 24_000
    ) throws -> Double {
        guard pcm16.count.isMultiple(of: 2) else {
            throw PCMLevelMeterError.oddByteCount
        }
        guard sampleRate > 0 else {
            throw PCMLevelMeterError.invalidSampleRate
        }

        let sampleCount = pcm16.count / MemoryLayout<Int16>.size
        guard sampleCount > 0 else { return level }

        let sumOfSquares = pcm16.withUnsafeBytes { bytes in
            (0..<sampleCount).reduce(into: 0.0) { sum, index in
                let sample = Int16(littleEndian: bytes.loadUnaligned(
                    fromByteOffset: index * MemoryLayout<Int16>.size,
                    as: Int16.self
                ))
                let normalized = Double(sample) / Double(Int16.max)
                sum += normalized * normalized
            }
        }
        let rms = sqrt(sumOfSquares / Double(sampleCount))
        let safeNoiseFloor = max(noiseFloor, .leastNonzeroMagnitude)
        let safeCeiling = max(ceiling, safeNoiseFloor)
        let floorDB = 20 * log10(safeNoiseFloor)
        let ceilingDB = 20 * log10(safeCeiling)
        let rmsDB = rms > 0 ? 20 * log10(rms) : -.infinity
        let rangeDB = max(ceilingDB - floorDB, .leastNonzeroMagnitude)
        let target = min(max((rmsDB - floorDB) / rangeDB, 0), 1)
        let duration = Double(sampleCount) / sampleRate
        let timeConstant = target > level ? attackSeconds : releaseSeconds
        let alpha = 1 - exp(-duration / timeConstant)
        level += (target - level) * alpha
        level = min(max(level, 0), 1)
        return level
    }

    public mutating func reset() {
        level = 0
    }
}
