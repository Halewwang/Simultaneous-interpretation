import EMKECore
@testable import EMKELanguageBaseline
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

private let repositoryRoot = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
    .deletingLastPathComponent()
    .deletingLastPathComponent()

private let languageCorpusURL = repositoryRoot
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
    #expect(corpus.generatorVersion == "emke-language-corpus/1.1.0")
    #expect(corpus.sentenceLicense == "CC0-1.0")
    #expect(!corpus.macOSBaselineEnvironment.isEmpty)
    #expect(corpus.macOSBaselineToolVersion == "emke-macos-language-baseline/1.1.0")
    #expect(Set(corpus.cases.map(\.id)).count == corpus.cases.count)
    #expect(corpus.cases.filter { $0.category == "zh" }.count == 100)
    #expect(corpus.cases.filter { $0.category == "en" }.count == 100)
    #expect(corpus.cases.filter { $0.category == "de" }.count == 100)
    #expect(corpus.cases.filter { $0.category == "ambiguous" }.count == 60)
    #expect(corpus.cases.allSatisfy {
        switch $0.category {
        case "zh", "en", "de":
            $0.expectedFinalRoute == "original"
        case "ambiguous":
            $0.expectedFinalRoute == "undecided"
        default:
            false
        }
    })
}

@Test("Stored macOS route baselines are reproduced by NaturalLanguage")
func storedMacOSRouteBaselinesAreReproducedByNaturalLanguage() throws {
    let corpus = try JSONDecoder().decode(
        LanguageCorpus.self,
        from: Data(contentsOf: languageCorpusURL)
    )
    let classifier = NaturalLanguageClassifier()

    for testCase in corpus.cases {
        #expect(
            testCase.macOSBaselineDecision == testCase.expectedFinalRoute,
            "stored macOS baseline disagrees with the golden route for \(testCase.id)"
        )
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

@Test("macOS baseline tool rejects a wrong golden route without rewriting files")
func macOSBaselineToolRejectsWrongGoldenRouteWithoutRewritingFiles() throws {
    let storedCorpus = try Data(contentsOf: languageCorpusURL)
    var wrongCorpus = String(decoding: storedCorpus, as: UTF8.self)
    let correctExpectation = #""expectedFinalRoute" : "original""#
    let wrongExpectation = #""expectedFinalRoute" : "translated""#
    let expectationRange = try #require(wrongCorpus.range(of: correctExpectation))
    wrongCorpus.replaceSubrange(expectationRange, with: wrongExpectation)
    let wrongCorpusData = Data(wrongCorpus.utf8)

    let temporaryDirectory = FileManager.default.temporaryDirectory
        .appendingPathComponent(UUID().uuidString, isDirectory: true)
    try FileManager.default.createDirectory(
        at: temporaryDirectory,
        withIntermediateDirectories: true
    )
    defer {
        try? FileManager.default.removeItem(at: temporaryDirectory)
    }

    let inputURL = temporaryDirectory.appendingPathComponent("input.json")
    let outputURL = temporaryDirectory.appendingPathComponent("output.json")
    let originalOutput = Data("existing output must remain unchanged".utf8)
    try wrongCorpusData.write(to: inputURL)
    try originalOutput.write(to: outputURL)

    do {
        _ = try LanguageBaselineGenerator.generate(
            inputURL: inputURL,
            outputURL: outputURL,
            environment: "hermetic-test"
        )
        Issue.record("Expected the wrong golden route to be rejected")
    } catch let error as LanguageBaselineGenerationError {
        #expect(error.description.contains("zh-001"))
    }
    #expect(try Data(contentsOf: inputURL) == wrongCorpusData)
    #expect(try Data(contentsOf: outputURL) == originalOutput)
}
