public enum TranslationLatencyMilestone: CaseIterable, Sendable {
    case speechStarted
    case firstNetworkFrameSent
    case firstSourceTranscriptReceived
    case routeDecided
    case firstTranslationAudioReceived
    case firstPlaybackScheduled
}

public struct TranslationLatencySnapshot: Equatable, Sendable {
    public let utteranceID: UInt64
    public let speechToFirstNetworkFrameMilliseconds: Double?
    public let speechToFirstSourceTranscriptMilliseconds: Double?
    public let speechToRouteDecisionMilliseconds: Double?
    public let speechToFirstTranslationAudioMilliseconds: Double?
    public let translationAudioToPlaybackMilliseconds: Double?
}

public struct TranslationLatencyPercentiles: Equatable, Sendable {
    public let sampleCount: Int
    public let p50Milliseconds: Double?
    public let p95Milliseconds: Double?

    public static let empty = Self(
        sampleCount: 0,
        p50Milliseconds: nil,
        p95Milliseconds: nil
    )
}

public struct TranslationLatencySummary: Equatable, Sendable {
    public let speechToFirstNetworkFrame: TranslationLatencyPercentiles
    public let speechToFirstSourceTranscript: TranslationLatencyPercentiles
    public let speechToRouteDecision: TranslationLatencyPercentiles
    public let speechToFirstTranslationAudio: TranslationLatencyPercentiles
    public let translationAudioToPlayback: TranslationLatencyPercentiles

    public static let empty = Self(
        speechToFirstNetworkFrame: .empty,
        speechToFirstSourceTranscript: .empty,
        speechToRouteDecision: .empty,
        speechToFirstTranslationAudio: .empty,
        translationAudioToPlayback: .empty
    )
}

public struct TranslationLatencyDiagnostics: Equatable, Sendable {
    public let latest: TranslationLatencySnapshot?
    public let summary: TranslationLatencySummary

    public static let empty = Self(latest: nil, summary: .empty)
}

public struct TranslationLatencyTracker: Sendable {
    private struct Milestones: Sendable {
        var speechStarted: UInt64?
        var firstNetworkFrameSent: UInt64?
        var firstSourceTranscriptReceived: UInt64?
        var routeDecided: UInt64?
        var firstTranslationAudioReceived: UInt64?
        var firstPlaybackScheduled: UInt64?

        mutating func mark(
            _ milestone: TranslationLatencyMilestone,
            at nanoseconds: UInt64
        ) {
            switch milestone {
            case .speechStarted:
                if speechStarted == nil { speechStarted = nanoseconds }
            case .firstNetworkFrameSent:
                if firstNetworkFrameSent == nil {
                    firstNetworkFrameSent = nanoseconds
                }
            case .firstSourceTranscriptReceived:
                if firstSourceTranscriptReceived == nil {
                    firstSourceTranscriptReceived = nanoseconds
                }
            case .routeDecided:
                if routeDecided == nil { routeDecided = nanoseconds }
            case .firstTranslationAudioReceived:
                if firstTranslationAudioReceived == nil {
                    firstTranslationAudioReceived = nanoseconds
                }
            case .firstPlaybackScheduled:
                if firstPlaybackScheduled == nil {
                    firstPlaybackScheduled = nanoseconds
                }
            }
        }
    }

    private let capacity: Int
    private var milestonesByUtterance: [UInt64: Milestones] = [:]
    private var utteranceOrder: [UInt64] = []
    private var latestUtteranceID: UInt64?

    public init(capacity: Int = 128) {
        precondition(capacity > 0)
        self.capacity = capacity
    }

    public mutating func mark(
        _ milestone: TranslationLatencyMilestone,
        utteranceID: UInt64,
        at nanoseconds: UInt64
    ) {
        if milestonesByUtterance[utteranceID] == nil {
            retainCapacityForNewUtterance()
            milestonesByUtterance[utteranceID] = Milestones()
            utteranceOrder.append(utteranceID)
        }

        milestonesByUtterance[utteranceID]?.mark(
            milestone,
            at: nanoseconds
        )
        latestUtteranceID = utteranceID
    }

    public func snapshot(
        for utteranceID: UInt64
    ) -> TranslationLatencySnapshot? {
        guard let milestones = milestonesByUtterance[utteranceID] else {
            return nil
        }
        return snapshot(for: utteranceID, milestones: milestones)
    }

    public var latestSnapshot: TranslationLatencySnapshot? {
        guard let latestUtteranceID else { return nil }
        return snapshot(for: latestUtteranceID)
    }

    public var diagnostics: TranslationLatencyDiagnostics {
        let snapshots = utteranceOrder.compactMap { snapshot(for: $0) }
        return TranslationLatencyDiagnostics(
            latest: latestSnapshot,
            summary: TranslationLatencySummary(
                speechToFirstNetworkFrame: percentiles(
                    snapshots.map(\.speechToFirstNetworkFrameMilliseconds)
                ),
                speechToFirstSourceTranscript: percentiles(
                    snapshots.map(\.speechToFirstSourceTranscriptMilliseconds)
                ),
                speechToRouteDecision: percentiles(
                    snapshots.map(\.speechToRouteDecisionMilliseconds)
                ),
                speechToFirstTranslationAudio: percentiles(
                    snapshots.map(\.speechToFirstTranslationAudioMilliseconds)
                ),
                translationAudioToPlayback: percentiles(
                    snapshots.map(\.translationAudioToPlaybackMilliseconds)
                )
            )
        )
    }

    public mutating func reset() {
        milestonesByUtterance.removeAll(keepingCapacity: false)
        utteranceOrder.removeAll(keepingCapacity: false)
        latestUtteranceID = nil
    }

    private mutating func retainCapacityForNewUtterance() {
        guard utteranceOrder.count == capacity else { return }
        let oldestUtteranceID = utteranceOrder.removeFirst()
        milestonesByUtterance.removeValue(forKey: oldestUtteranceID)
        if latestUtteranceID == oldestUtteranceID {
            latestUtteranceID = utteranceOrder.last
        }
    }

    private func snapshot(
        for utteranceID: UInt64,
        milestones: Milestones
    ) -> TranslationLatencySnapshot {
        TranslationLatencySnapshot(
            utteranceID: utteranceID,
            speechToFirstNetworkFrameMilliseconds: elapsedMilliseconds(
                from: milestones.speechStarted,
                to: milestones.firstNetworkFrameSent
            ),
            speechToFirstSourceTranscriptMilliseconds: elapsedMilliseconds(
                from: milestones.speechStarted,
                to: milestones.firstSourceTranscriptReceived
            ),
            speechToRouteDecisionMilliseconds: elapsedMilliseconds(
                from: milestones.speechStarted,
                to: milestones.routeDecided
            ),
            speechToFirstTranslationAudioMilliseconds: elapsedMilliseconds(
                from: milestones.speechStarted,
                to: milestones.firstTranslationAudioReceived
            ),
            translationAudioToPlaybackMilliseconds: elapsedMilliseconds(
                from: milestones.firstTranslationAudioReceived,
                to: milestones.firstPlaybackScheduled
            )
        )
    }

    private func elapsedMilliseconds(
        from start: UInt64?,
        to end: UInt64?
    ) -> Double? {
        guard let start, let end, end >= start else { return nil }
        return Double(end - start) / 1_000_000
    }

    private func percentiles(
        _ samples: [Double?]
    ) -> TranslationLatencyPercentiles {
        let sorted = samples.compactMap { $0 }.sorted()
        guard !sorted.isEmpty else { return .empty }

        return TranslationLatencyPercentiles(
            sampleCount: sorted.count,
            p50Milliseconds: sorted[nearestRankIndex(
                numerator: 50,
                count: sorted.count
            )],
            p95Milliseconds: sorted[nearestRankIndex(
                numerator: 95,
                count: sorted.count
            )]
        )
    }

    private func nearestRankIndex(numerator: Int, count: Int) -> Int {
        ((count * numerator + 99) / 100) - 1
    }
}
