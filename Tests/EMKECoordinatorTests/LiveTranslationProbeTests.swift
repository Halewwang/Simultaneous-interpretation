import EMKECore
import EMKECoordinator
import Foundation
import Testing

private let liveTranslationProbeEnabled =
    ProcessInfo.processInfo.environment[
        "EMKE_RUN_LIVE_TRANSLATION_TESTS"
    ] == "1"

private struct LiveTranslationSpeechSample {
    let data: Data

    init?(_ data: Data) {
        guard !data.isEmpty,
              data.count.isMultiple(of: 1_920) else { return nil }
        self.data = data
    }
}

@Test
func invalidLiveSpeechSamplesCannotReachProbeConstruction() {
    var constructionCount = 0
    let invalidSamples = [
        Data(),
        Data(repeating: 1, count: 1_921),
        Data(repeating: 1, count: 1_922),
    ]

    for speech in invalidSamples {
        if LiveTranslationSpeechSample(speech) != nil {
            constructionCount += 1
        }
    }

    #expect(constructionCount == 0)
}

@Test
func completeLiveSpeechChunksCanReachProbeConstruction() {
    let speech = Data(repeating: 1, count: 3_840)

    #expect(LiveTranslationSpeechSample(speech)?.data == speech)
}

@Test(
    .enabled(
        if: liveTranslationProbeEnabled,
        "Set EMKE_RUN_LIVE_TRANSLATION_TESTS=1 and provider inputs"
    ),
    .timeLimit(.minutes(1))
)
func liveFortyMillisecondTranslationProbe() async throws {
    let environment = ProcessInfo.processInfo.environment
    let samplePath = try #require(environment["EMKE_SPEECH_SAMPLE"])
    let sampleURL = URL(fileURLWithPath: samplePath)
    let speech = try Data(contentsOf: sampleURL)
    try #require(!speech.isEmpty)
    try #require(speech.count.isMultiple(of: 1_920))
    let validatedSpeech = try #require(
        LiveTranslationSpeechSample(speech)
    )

    let apiKey = try #require(environment["EMKE_API_KEY"])
    let baseURLString = try #require(environment["EMKE_BASE_URL"])
    let baseURL = try #require(URL(string: baseURLString))
    let modelID = try #require(environment["EMKE_MODEL_ID"])
    let report = await TranslationConnectionProbe().run(
        configuration: TranslationConnectionProbeConfiguration(
            apiConfiguration: APIConfiguration(
                baseURL: baseURL,
                modelID: modelID
            ),
            apiKey: apiKey,
            inboundTargetLanguage: .chinese,
            outboundTargetLanguage: .german,
            speechChunkByteCount: 1_920
        ),
        speechSample: validatedSpeech.data
    )

    #expect(report.handshake == .passed)
    #expect(report.sourceTranscript == .passed)
    #expect(report.audioOutput == .passed)
}
