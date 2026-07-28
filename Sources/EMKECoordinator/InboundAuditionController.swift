import EMKECore
import EMKERouting
import Foundation

public enum InboundAuditionCommand: Equatable, Sendable {
    case original(Data)
    case setOriginalGain(Double, rampSamples: Int)
    case beginCrossfade([Data], rampSamples: Int)
    case translation(Data)
    case failOpen(rampSamples: Int)
    case reset
}

public struct InboundAuditionController: Sendable {
    public private(set) var route: InboundRoute = .undecided
    public private(set) var utteranceID: UInt64?

    private static let rampSampleCount = 1_920

    private var gate: InboundLanguageGate
    private let maximumBufferedTranslationBytes: Int
    private var nextUtteranceID: UInt64 = 0
    private var bufferedTranslation: [Data] = []
    private var bufferedTranslationByteCount = 0
    private var crossfadeStarted = false
    private var rendererResetPending = false

    public init(
        motherLanguage: SupportedLanguage,
        maximumBufferedTranslationBytes: Int = 240_000
    ) {
        precondition(maximumBufferedTranslationBytes > 0)
        gate = InboundLanguageGate(motherLanguage: motherLanguage)
        self.maximumBufferedTranslationBytes =
            maximumBufferedTranslationBytes
    }

    public mutating func beginUtterance() -> UInt64 {
        let replacesActiveUtterance = utteranceID != nil
        nextUtteranceID += 1
        clearUtterance()
        gate.reset()
        route = .undecided
        utteranceID = nextUtteranceID
        rendererResetPending = replacesActiveUtterance
        return nextUtteranceID
    }

    public mutating func appendOriginal(
        _ pcm16: Data,
        utteranceID: UInt64
    ) -> [InboundAuditionCommand] {
        guard self.utteranceID == utteranceID else { return [] }
        return commandsWithPendingRendererReset([.original(pcm16)])
    }

    public mutating func appendTranslation(
        _ pcm16: Data,
        utteranceID: UInt64
    ) -> [InboundAuditionCommand] {
        guard self.utteranceID == utteranceID else { return [] }
        guard !pcm16.isEmpty else { return [] }

        let commands: [InboundAuditionCommand]
        switch route {
        case .original:
            commands = []
        case .translated:
            if crossfadeStarted {
                commands = [.translation(pcm16)]
            } else {
                crossfadeStarted = true
                commands = [.beginCrossfade(
                    [pcm16],
                    rampSamples: Self.rampSampleCount
                )]
            }
        case .undecided:
            bufferedTranslation.append(pcm16)
            bufferedTranslationByteCount += pcm16.count
            if bufferedTranslationByteCount
                >= maximumBufferedTranslationBytes
            {
                _ = gate.resolveDeadline(isSpeech: true)
                route = .translated
                commands = beginCrossfadeWithBufferedTranslation()
            } else {
                commands = []
            }
        }
        return commandsWithPendingRendererReset(commands)
    }

    public mutating func observe(
        _ hypotheses: LanguageHypotheses,
        utteranceID: UInt64
    ) -> [InboundAuditionCommand] {
        guard self.utteranceID == utteranceID, route == .undecided else {
            return []
        }
        route = gate.observe(hypotheses)
        return commandsWithPendingRendererReset(
            commandsForLockedRoute()
        )
    }

    public mutating func resolveDeadline(
        isSpeech: Bool,
        utteranceID: UInt64
    ) -> [InboundAuditionCommand] {
        guard self.utteranceID == utteranceID, route == .undecided else {
            return []
        }
        let translationIsAvailable =
            isSpeech && !bufferedTranslation.isEmpty
        route = gate.resolveDeadline(isSpeech: translationIsAvailable)
        return commandsWithPendingRendererReset(
            commandsForLockedRoute()
        )
    }

    public mutating func failOpen() -> [InboundAuditionCommand] {
        guard utteranceID != nil else { return [] }
        gate.reset()
        route = gate.resolveDeadline(isSpeech: false)
        discardBufferedTranslation()
        crossfadeStarted = false
        return commandsWithPendingRendererReset([
            .failOpen(rampSamples: Self.rampSampleCount),
        ])
    }

    public mutating func finish(
        utteranceID: UInt64
    ) -> [InboundAuditionCommand] {
        guard self.utteranceID == utteranceID else { return [] }
        clearUtterance()
        gate.reset()
        route = .undecided
        self.utteranceID = nil
        rendererResetPending = false
        return [.reset]
    }

    public mutating func reset() -> [InboundAuditionCommand] {
        clearUtterance()
        gate.reset()
        route = .undecided
        utteranceID = nil
        rendererResetPending = false
        return [.reset]
    }

    private mutating func commandsForLockedRoute()
        -> [InboundAuditionCommand]
    {
        switch route {
        case .undecided:
            return []
        case .original:
            discardBufferedTranslation()
            return [.setOriginalGain(
                1.0,
                rampSamples: Self.rampSampleCount
            )]
        case .translated:
            return beginCrossfadeWithBufferedTranslation()
        }
    }

    private mutating func beginCrossfadeWithBufferedTranslation()
        -> [InboundAuditionCommand]
    {
        guard !bufferedTranslation.isEmpty else { return [] }
        let chunks = bufferedTranslation
        discardBufferedTranslation()
        crossfadeStarted = true
        return [.beginCrossfade(
            chunks,
            rampSamples: Self.rampSampleCount
        )]
    }

    private mutating func discardBufferedTranslation() {
        bufferedTranslation.removeAll(keepingCapacity: false)
        bufferedTranslationByteCount = 0
    }

    private mutating func clearUtterance() {
        discardBufferedTranslation()
        crossfadeStarted = false
    }

    private mutating func commandsWithPendingRendererReset(
        _ commands: [InboundAuditionCommand]
    ) -> [InboundAuditionCommand] {
        guard rendererResetPending else { return commands }
        rendererResetPending = false
        if commands.first == .reset {
            return commands
        }
        return [.reset] + commands
    }
}
