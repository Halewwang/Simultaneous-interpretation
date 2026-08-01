import Foundation

public enum PCMFrameBatcherError: Error, Equatable, Sendable {
    case invalidPCM16ByteCount
}

public struct PCMFrameBatcher: Sendable {
    public let frameByteCount: Int

    private var buffer = Data()

    public init(frameDurationMilliseconds: Int = 200) {
        precondition(frameDurationMilliseconds > 0)
        let bytesTimesMilliseconds = 24_000 * 2 * frameDurationMilliseconds
        precondition(bytesTimesMilliseconds.isMultiple(of: 1_000))
        frameByteCount = bytesTimesMilliseconds / 1_000
    }

    public var bufferedByteCount: Int {
        buffer.count
    }

    public mutating func append(_ pcm16: Data) throws -> [Data] {
        guard pcm16.count.isMultiple(of: 2) else {
            throw PCMFrameBatcherError.invalidPCM16ByteCount
        }

        buffer.append(pcm16)
        var frames: [Data] = []
        while buffer.count >= frameByteCount {
            frames.append(Data(buffer.prefix(frameByteCount)))
            buffer.removeFirst(frameByteCount)
        }
        return frames
    }

    public mutating func reset() {
        buffer.removeAll(keepingCapacity: false)
    }
}
