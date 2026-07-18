import Foundation

public enum NetworkPCMError: Error, Equatable, Sendable {
    case misalignedStereoSamples
    case misalignedPCM16
}

public struct NetworkPCMEncoder: Sendable {
    private var pendingMonoFrame: Float?

    public init() {}

    public mutating func append48kStereo(
        _ interleavedSamples: [Float]
    ) throws -> Data {
        guard interleavedSamples.count.isMultiple(of: 2) else {
            throw NetworkPCMError.misalignedStereoSamples
        }

        let incomingFrameCount = interleavedSamples.count / 2
        let availableFrameCount = incomingFrameCount
            + (pendingMonoFrame == nil ? 0 : 1)
        var result = Data()
        result.reserveCapacity((availableFrameCount / 2) * 2)

        var index = 0
        while index < interleavedSamples.count {
            let monoFrame = (
                interleavedSamples[index]
                    + interleavedSamples[index + 1]
            ) * 0.5
            index += 2

            guard let pendingMonoFrame else {
                self.pendingMonoFrame = monoFrame
                continue
            }

            appendPCM16(
                (pendingMonoFrame + monoFrame) * 0.5,
                to: &result
            )
            self.pendingMonoFrame = nil
        }
        return result
    }

    private func appendPCM16(_ sample: Float, to data: inout Data) {
        let clamped = max(-1, min(1, sample))
        let value: Int16
        if clamped <= -1 {
            value = .min
        } else {
            value = Int16((clamped * Float(Int16.max)).rounded())
        }
        var littleEndian = value.littleEndian
        withUnsafeBytes(of: &littleEndian) { bytes in
            data.append(contentsOf: bytes)
        }
    }
}

public struct NetworkPCMDecoder: Sendable {
    public init() {}

    public mutating func append24kMonoPCM16(
        _ pcm16: Data
    ) throws -> [Float] {
        guard pcm16.count.isMultiple(of: 2) else {
            throw NetworkPCMError.misalignedPCM16
        }

        var result: [Float] = []
        result.reserveCapacity(pcm16.count * 2)
        var index = pcm16.startIndex
        while index < pcm16.endIndex {
            let nextIndex = pcm16.index(after: index)
            let raw = UInt16(pcm16[index])
                | (UInt16(pcm16[nextIndex]) << 8)
            let signed = Int16(bitPattern: raw)
            let sample = signed == .min
                ? -1
                : Float(signed) / Float(Int16.max)

            result.append(sample)
            result.append(sample)
            result.append(sample)
            result.append(sample)
            index = pcm16.index(after: nextIndex)
        }
        return result
    }
}
