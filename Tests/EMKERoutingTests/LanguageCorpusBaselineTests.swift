import EMKECore
import Foundation
import Testing
@testable import EMKERouting

private struct LanguageCorpus: Decodable {
    let contractVersion: Int
    let corpusID: String
    let generatorVersion: String
    let sentenceLicense: String
    let macOSBaselineEnvironment: String
    let macOSBaselineToolVersion: String
    let cases: [LanguageCorpusCase]

    private enum CodingKeys: String, CodingKey {
        case contractVersion
        case corpusID = "corpusId"
        case generatorVersion
        case sentenceLicense
        case macOSBaselineEnvironment
        case macOSBaselineToolVersion
        case cases
    }
}

private struct LanguageCorpusCase: Decodable {
    let id: String
    let category: String
    let nativeLanguage: SupportedLanguage
    let text: String
    let macOSBaselineDecision: String
    let expectedFinalRoute: String
}

private let languageCorpusURL = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
    .deletingLastPathComponent()
    .deletingLastPathComponent()
    .appendingPathComponent(
        "Shared/TestVectors/Routing/LanguageCorpus/language-corpus-v1.json"
    )

@Test("Synthetic language corpus inventory and license are fixed")
func syntheticLanguageCorpusInventoryAndLicenseAreFixed() throws {
    let corpus = try JSONDecoder().decode(
        LanguageCorpus.self,
        from: Data(contentsOf: languageCorpusURL)
    )

    #expect(corpus.contractVersion == 1)
    #expect(corpus.corpusID == "routing.language-corpus.v1")
    #expect(corpus.generatorVersion == "emke-language-corpus/1.0.0")
    #expect(corpus.sentenceLicense == "CC0-1.0")
    #expect(!corpus.macOSBaselineEnvironment.isEmpty)
    #expect(corpus.macOSBaselineToolVersion == "emke-macos-language-baseline/1.0.0")
    #expect(Set(corpus.cases.map(\.id)).count == corpus.cases.count)
    #expect(corpus.cases.filter { $0.category == "zh" }.count == 100)
    #expect(corpus.cases.filter { $0.category == "en" }.count == 100)
    #expect(corpus.cases.filter { $0.category == "de" }.count == 100)
    #expect(corpus.cases.filter { $0.category == "ambiguous" }.count == 60)
}

@Test("Stored macOS route baselines are reproduced by NaturalLanguage")
func storedMacOSRouteBaselinesAreReproducedByNaturalLanguage() throws {
    let corpus = try JSONDecoder().decode(
        LanguageCorpus.self,
        from: Data(contentsOf: languageCorpusURL)
    )
    let classifier = NaturalLanguageClassifier()

    for testCase in corpus.cases {
        var gate = InboundLanguageGate(
            motherLanguage: testCase.nativeLanguage
        )
        let actual = gate.observe(
            classifier.hypotheses(for: testCase.text)
        )
        let stable = switch actual {
        case .undecided: "undecided"
        case .original: "original"
        case .translated: "translated"
        }

        #expect(
            stable == testCase.macOSBaselineDecision,
            "macOS baseline changed for \(testCase.id)"
        )
        #expect(
            stable == testCase.expectedFinalRoute,
            "final route expectation changed for \(testCase.id)"
        )
    }
}
