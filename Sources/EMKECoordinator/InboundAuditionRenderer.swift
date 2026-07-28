import Foundation

public enum InboundRenderedSource: Equatable, Sendable {
    case original
    case crossfade
    case translation
}

public struct InboundRenderedChunk: Equatable, Sendable {
    public let pcm16: Data
    public let source: InboundRenderedSource

    public init(pcm16: Data, source: InboundRenderedSource) {
        self.pcm16 = pcm16
        self.source = source
    }
}

public enum InboundAuditionRendererError: Error, Equatable, Sendable {
    case bufferLimitExceeded
}

public struct InboundAuditionRenderer: Sendable {
    public static let previewGain = 0.12
    public static let rampSampleCount = 1_920

    private enum Mode: Sendable {
        case original
        case crossfade
        case translation
    }

    private let maximumQueuedBytesPerSource: Int
    private var mode: Mode = .original
    private var originalRamp = PCM16GainRamp(initialGain: previewGain)
    private var translationRamp = PCM16GainRamp(initialGain: 0)
    private var originalQueue = Data()
    private var translationQueue = Data()
    private var remainingCrossfadeSamples = 0

    public init(maximumQueuedBytesPerSource: Int = 240_000) {
        precondition(maximumQueuedBytesPerSource > 0)
        self.maximumQueuedBytesPerSource =
            maximumQueuedBytesPerSource
    }

    public mutating func consume(
        _ command: InboundAuditionCommand
    ) throws -> [InboundRenderedChunk] {
        switch command {
        case let .original(pcm16):
            try validatePCM16(pcm16)
            return try consumeOriginal(pcm16)

        case let .setOriginalGain(gain, rampSamples):
            try validateRampSampleCount(rampSamples)
            clearQueues()
            mode = .original
            remainingCrossfadeSamples = 0
            translationRamp = PCM16GainRamp(initialGain: 0)
            originalRamp.setTarget(gain, overSamples: rampSamples)
            return []

        case let .beginCrossfade(chunks, rampSamples):
            try validateRampSampleCount(rampSamples)
            for chunk in chunks {
                try validatePCM16(chunk)
            }
            var byteCount = 0
            for chunk in chunks {
                guard chunk.count
                        <= maximumQueuedBytesPerSource - byteCount
                else {
                    throw InboundAuditionRendererError
                        .bufferLimitExceeded
                }
                byteCount += chunk.count
            }

            clearQueues()
            mode = rampSamples == 0 ? .translation : .crossfade
            remainingCrossfadeSamples = rampSamples
            originalRamp.setTarget(0, overSamples: rampSamples)
            translationRamp = PCM16GainRamp(initialGain: 0)
            translationRamp.setTarget(1, overSamples: rampSamples)
            chunks.forEach { translationQueue.append($0) }

            if rampSamples == 0 {
                return try flushTranslationQueue()
            }
            return []

        case let .translation(pcm16):
            try validatePCM16(pcm16)
            return try consumeTranslation(pcm16)

        case let .failOpen(rampSamples):
            try validateRampSampleCount(rampSamples)
            clearQueues()
            mode = .original
            remainingCrossfadeSamples = 0
            translationRamp = PCM16GainRamp(initialGain: 0)
            originalRamp.setTarget(1, overSamples: rampSamples)
            return []

        case .reset:
            clearQueues()
            mode = .original
            remainingCrossfadeSamples = 0
            originalRamp = PCM16GainRamp(
                initialGain: Self.previewGain
            )
            translationRamp = PCM16GainRamp(initialGain: 0)
            return []
        }
    }

    private mutating func consumeOriginal(
        _ pcm16: Data
    ) throws -> [InboundRenderedChunk] {
        switch mode {
        case .original:
            guard !pcm16.isEmpty else { return [] }
            return [InboundRenderedChunk(
                pcm16: try originalRamp.process(
                    zeroBasedData(pcm16)
                ),
                source: .original
            )]
        case .translation:
            return []
        case .crossfade:
            try ensureQueueLimitBeforeAddingOriginal(pcm16.count)
            originalQueue.append(pcm16)
            return try drainCrossfade()
        }
    }

    private mutating func consumeTranslation(
        _ pcm16: Data
    ) throws -> [InboundRenderedChunk] {
        switch mode {
        case .original:
            return []
        case .translation:
            guard !pcm16.isEmpty else { return [] }
            return [InboundRenderedChunk(
                pcm16: try translationRamp.process(
                    zeroBasedData(pcm16)
                ),
                source: .translation
            )]
        case .crossfade:
            try ensureQueueLimitBeforeAddingTranslation(pcm16.count)
            translationQueue.append(pcm16)
            return try drainCrossfade()
        }
    }

    private mutating func drainCrossfade()
        throws -> [InboundRenderedChunk]
    {
        var output: [InboundRenderedChunk] = []

        while mode == .crossfade,
              remainingCrossfadeSamples > 0,
              !originalQueue.isEmpty,
              !translationQueue.isEmpty
        {
            let byteCount = min(
                originalQueue.count,
                translationQueue.count,
                remainingCrossfadeSamples * 2
            )
            let original = removePrefix(
                byteCount,
                from: &originalQueue
            )
            let translation = removePrefix(
                byteCount,
                from: &translationQueue
            )
            let mixed = try PCM16Mixer.mix(
                originalRamp.process(original),
                translationRamp.process(translation)
            )
            output.append(InboundRenderedChunk(
                pcm16: mixed,
                source: .crossfade
            ))
            remainingCrossfadeSamples -= byteCount / 2
        }

        if mode == .crossfade, remainingCrossfadeSamples == 0 {
            mode = .translation
            originalQueue.removeAll(keepingCapacity: false)
            output.append(contentsOf: try flushTranslationQueue())
        }
        return output
    }

    private mutating func flushTranslationQueue()
        throws -> [InboundRenderedChunk]
    {
        guard !translationQueue.isEmpty else { return [] }
        let pcm16 = Data(translationQueue)
        translationQueue.removeAll(keepingCapacity: false)
        return [InboundRenderedChunk(
            pcm16: try translationRamp.process(pcm16),
            source: .translation
        )]
    }

    private func ensureQueueLimitBeforeAddingOriginal(
        _ byteCount: Int
    ) throws {
        guard byteCount
                <= maximumQueuedBytesPerSource - originalQueue.count
        else {
            throw InboundAuditionRendererError.bufferLimitExceeded
        }
    }

    private func ensureQueueLimitBeforeAddingTranslation(
        _ byteCount: Int
    ) throws {
        guard byteCount
                <= maximumQueuedBytesPerSource - translationQueue.count
        else {
            throw InboundAuditionRendererError.bufferLimitExceeded
        }
    }

    private mutating func clearQueues() {
        originalQueue.removeAll(keepingCapacity: false)
        translationQueue.removeAll(keepingCapacity: false)
    }

    private func validatePCM16(_ pcm16: Data) throws {
        guard pcm16.count.isMultiple(of: 2) else {
            throw PCM16ProcessingError.invalidPCM16ByteCount
        }
    }

    private func validateRampSampleCount(_ sampleCount: Int) throws {
        guard sampleCount >= 0, sampleCount <= Int.max / 2 else {
            throw PCM16ProcessingError.invalidRampSampleCount
        }
    }

    private func zeroBasedData(_ data: Data) -> Data {
        data.startIndex == 0 ? data : Data(data)
    }

    private func removePrefix(
        _ count: Int,
        from data: inout Data
    ) -> Data {
        let prefix = Data(data.prefix(count))
        data.removeFirst(count)
        return prefix
    }
}
