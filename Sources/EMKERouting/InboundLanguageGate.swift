import EMKECore

public enum InboundRoute: Equatable, Sendable {
    case undecided
    case original
    case translated
}

public struct LanguageHypotheses: Equatable, Sendable {
    public let confidenceByPrimaryTag: [String: Double]

    public init(_ values: [String: Double]) {
        confidenceByPrimaryTag = values.reduce(into: [:]) { result, item in
            let normalized = item.key.lowercased()
            let primaryTag = normalized.split(separator: "-").first
                .map(String.init) ?? normalized
            result[primaryTag] = min(
                1,
                result[primaryTag, default: 0] + item.value
            )
        }
    }
}

public struct InboundLanguageGate: Sendable {
    public let motherLanguage: SupportedLanguage
    public private(set) var route: InboundRoute = .undecided

    public init(motherLanguage: SupportedLanguage) {
        self.motherLanguage = motherLanguage
    }

    public mutating func observe(
        _ hypotheses: LanguageHypotheses
    ) -> InboundRoute {
        guard route == .undecided else { return route }

        let motherConfidence = hypotheses.confidenceByPrimaryTag[
            motherLanguage.rawValue,
            default: 0
        ]
        if motherConfidence >= 0.75 {
            route = .original
            return route
        }

        let strongestOtherLanguage = hypotheses.confidenceByPrimaryTag
            .filter { $0.key != motherLanguage.rawValue }
            .map(\.value)
            .max() ?? 0
        if strongestOtherLanguage >= 0.60 {
            route = .translated
        }
        return route
    }

    public mutating func resolveDeadline(isSpeech: Bool) -> InboundRoute {
        guard route == .undecided else { return route }
        route = isSpeech ? .translated : .original
        return route
    }

    public mutating func reset() {
        route = .undecided
    }
}
