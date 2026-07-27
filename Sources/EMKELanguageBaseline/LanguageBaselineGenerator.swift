import EMKECore
import EMKERouting
import Foundation

public struct LanguageBaselineMismatch: Equatable, Sendable {
    public let id: String
    public let expected: String
    public let observed: String
}

public enum LanguageBaselineGenerationError: Error, Equatable, Sendable {
    case routeMismatches([LanguageBaselineMismatch])
}

extension LanguageBaselineGenerationError: CustomStringConvertible {
    public var description: String {
        switch self {
        case let .routeMismatches(mismatches):
            let listed = mismatches.prefix(10).map {
                "\($0.id): expected \($0.expected), observed \($0.observed)"
            }.joined(separator: "\n")
            let remaining = mismatches.count - min(mismatches.count, 10)
            let suffix = remaining == 0 ? "" : "\n... and \(remaining) more"
            return "macOS baseline disagrees with \(mismatches.count) "
                + "independent expected route(s):\n\(listed)\(suffix)"
        }
    }
}

public enum LanguageBaselineGenerator {
    public static let toolVersion = "emke-macos-language-baseline/1.1.0"

    @discardableResult
    public static func generate(
        inputURL: URL,
        outputURL: URL,
        environment: String = ProcessInfo.processInfo
            .operatingSystemVersionString
    ) throws -> Int {
        let decoder = JSONDecoder()
        var corpus = try decoder.decode(
            SeedCorpus.self,
            from: Data(contentsOf: inputURL)
        )
        let classifier = NaturalLanguageClassifier()
        var mismatches: [LanguageBaselineMismatch] = []

        for index in corpus.cases.indices {
            let hypotheses = classifier.hypotheses(
                for: corpus.cases[index].text
            )
            let strongest = hypotheses.confidenceByPrimaryTag.sorted {
                if $0.value == $1.value {
                    return $0.key < $1.key
                }
                return $0.value > $1.value
            }.first
            var gate = InboundLanguageGate(
                motherLanguage: corpus.cases[index].nativeLanguage
            )
            let route = stable(gate.observe(hypotheses))
            corpus.cases[index].macOSPrimaryLanguage = strongest?.key
            corpus.cases[index].macOSPrimaryConfidence = strongest?.value
            corpus.cases[index].macOSBaselineDecision = route
            if route != corpus.cases[index].expectedFinalRoute {
                mismatches.append(
                    LanguageBaselineMismatch(
                        id: corpus.cases[index].id,
                        expected: corpus.cases[index].expectedFinalRoute,
                        observed: route
                    )
                )
            }
        }

        if !mismatches.isEmpty {
            throw LanguageBaselineGenerationError.routeMismatches(mismatches)
        }

        corpus.macOSBaselineEnvironment = environment
        corpus.macOSBaselineToolVersion = toolVersion
        let encoder = JSONEncoder()
        encoder.outputFormatting = [
            .prettyPrinted,
            .sortedKeys,
            .withoutEscapingSlashes,
        ]
        var output = try encoder.encode(corpus)
        output.append(0x0A)
        try FileManager.default.createDirectory(
            at: outputURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try output.write(to: outputURL, options: .atomic)
        return corpus.cases.count
    }

    private static func stable(_ route: InboundRoute) -> String {
        switch route {
        case .undecided: "undecided"
        case .original: "original"
        case .translated: "translated"
        }
    }
}

private struct SeedCorpus: Codable {
    let contractVersion: Int
    let corpusId: String
    let generatorVersion: String
    let sentenceLicense: String
    let generationNote: String
    var macOSBaselineEnvironment: String?
    var macOSBaselineToolVersion: String?
    var cases: [CorpusCase]
}

private struct CorpusCase: Codable {
    let id: String
    let category: String
    let nativeLanguage: SupportedLanguage
    let text: String
    var macOSPrimaryLanguage: String?
    var macOSPrimaryConfidence: Double?
    var macOSBaselineDecision: String?
    let expectedFinalRoute: String
}
