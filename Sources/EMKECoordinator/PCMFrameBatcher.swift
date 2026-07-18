import Foundation

public enum PCMFrameBatcherError: Error, Equatable, Sendable {
    case invalidPCM16ByteCount
}

public struct PCMFrameBatcher: Sendable {
    public static let frameByteCount = 9_600

    private var buffer = Data()

    public init() {}

    public var bufferedByteCount: Int {
        buffer.count
    }

    public mutating func append(_ pcm16: Data) throws -> [Data] {
        guard pcm16.count.isMultiple(of: 2) else {
            throw PCMFrameBatcherError.invalidPCM16ByteCount
        }

        buffer.append(pcm16)
        var frames: [Data] = []
        while buffer.count >= Self.frameByteCount {
            frames.append(Data(buffer.prefix(Self.frameByteCount)))
            buffer.removeFirst(Self.frameByteCount)
        }
        return frames
    }

    public mutating func reset() {
        buffer.removeAll(keepingCapacity: false)
    }
}
