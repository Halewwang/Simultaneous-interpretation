import EMKECore
import EMKERouting
import Foundation

public struct InboundUtteranceBuffer: Sendable {
    public static let defaultMaximumBufferedBytesPerCandidate = 240_000

    private var gate: InboundLanguageGate
    private let maximumBufferedBytesPerCandidate: Int
    private var originalChunks: [Data] = []
    private var translatedChunks: [Data] = []
    private var originalByteCount = 0
    private var translatedByteCount = 0
    private var isActive = false

    public init(
        motherLanguage: SupportedLanguage,
        maximumBufferedBytesPerCandidate: Int = Self
            .defaultMaximumBufferedBytesPerCandidate
    ) {
        precondition(maximumBufferedBytesPerCandidate > 0)
        gate = InboundLanguageGate(motherLanguage: motherLanguage)
        self.maximumBufferedBytesPerCandidate =
            maximumBufferedBytesPerCandidate
    }

    public var currentRoute: InboundRoute {
        gate.route
    }

    public var bufferedByteCount: Int {
        originalByteCount + translatedByteCount
    }

    public mutating func begin() {
        clearCandidates()
        gate.reset()
        isActive = true
    }

    public mutating func appendOriginal(_ pcm16: Data) -> [Data] {
        ensureActive()
        switch gate.route {
        case .original:
            return [pcm16]
        case .translated:
            return []
        case .undecided:
            originalChunks.append(pcm16)
            originalByteCount += pcm16.count
            guard originalByteCount >= maximumBufferedBytesPerCandidate else {
                return []
            }
            _ = gate.resolveDeadline(isSpeech: false)
            return flushSelectedCandidate()
        }
    }

    public mutating func appendTranslation(_ pcm16: Data) -> [Data] {
        ensureActive()
        switch gate.route {
        case .translated:
            return [pcm16]
        case .original:
            return []
        case .undecided:
            translatedChunks.append(pcm16)
            translatedByteCount += pcm16.count
            guard translatedByteCount >= maximumBufferedBytesPerCandidate
            else {
                return []
            }
            _ = gate.resolveDeadline(isSpeech: true)
            return flushSelectedCandidate()
        }
    }

    public mutating func observe(
        _ hypotheses: LanguageHypotheses
    ) -> [Data] {
        ensureActive()
        _ = gate.observe(hypotheses)
        return flushSelectedCandidate()
    }

    public mutating func resolveDeadline(isSpeech: Bool) -> [Data] {
        ensureActive()
        _ = gate.resolveDeadline(isSpeech: isSpeech)
        return flushSelectedCandidate()
    }

    public mutating func finish(isSpeech: Bool) -> [Data] {
        guard isActive else { return [] }
        if gate.route == .undecided {
            let canUseTranslation = isSpeech && !translatedChunks.isEmpty
            _ = gate.resolveDeadline(isSpeech: canUseTranslation)
        }
        let output = flushSelectedCandidate()
        clearCandidates()
        gate.reset()
        isActive = false
        return output
    }

    public mutating func reset() {
        clearCandidates()
        gate.reset()
        isActive = false
    }

    private mutating func ensureActive() {
        if !isActive {
            begin()
        }
    }

    private mutating func flushSelectedCandidate() -> [Data] {
        switch gate.route {
        case .undecided:
            return []
        case .original:
            let output = originalChunks
            clearCandidates()
            return output
        case .translated:
            let output = translatedChunks
            clearCandidates()
            return output
        }
    }

    private mutating func clearCandidates() {
        originalChunks.removeAll(keepingCapacity: false)
        translatedChunks.removeAll(keepingCapacity: false)
        originalByteCount = 0
        translatedByteCount = 0
    }
}
