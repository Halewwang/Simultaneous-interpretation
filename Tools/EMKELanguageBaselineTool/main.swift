import EMKECore
import EMKERouting
import Foundation

private let toolVersion = "emke-macos-language-baseline/1.1.0"

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

private func stable(_ route: InboundRoute) -> String {
    switch route {
    case .undecided: "undecided"
    case .original: "original"
    case .translated: "translated"
    }
}

guard CommandLine.arguments.count == 3 else {
    FileHandle.standardError.write(
        Data("usage: EMKELanguageBaselineTool INPUT OUTPUT\n".utf8)
    )
    exit(64)
}

let inputURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[2])
let decoder = JSONDecoder()
private var corpus = try decoder.decode(
    SeedCorpus.self,
    from: Data(contentsOf: inputURL)
)
let classifier = NaturalLanguageClassifier()
var mismatches: [String] = []

for index in corpus.cases.indices {
    let hypotheses = classifier.hypotheses(for: corpus.cases[index].text)
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
            "\(corpus.cases[index].id): expected "
                + "\(corpus.cases[index].expectedFinalRoute), observed \(route)"
        )
    }
}

if !mismatches.isEmpty {
    let listed = mismatches.prefix(10).joined(separator: "\n")
    let remaining = mismatches.count - min(mismatches.count, 10)
    let suffix = remaining == 0 ? "" : "\n... and \(remaining) more"
    FileHandle.standardError.write(
        Data(
            (
                "macOS baseline disagrees with \(mismatches.count) "
                    + "independent expected route(s):\n\(listed)\(suffix)\n"
            ).utf8
        )
    )
    exit(65)
}

corpus.macOSBaselineEnvironment =
    ProcessInfo.processInfo.operatingSystemVersionString
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
FileHandle.standardOutput.write(
    Data(
        "\(corpus.cases.count) macOS baselines -> \(outputURL.path)\n".utf8
    )
)
