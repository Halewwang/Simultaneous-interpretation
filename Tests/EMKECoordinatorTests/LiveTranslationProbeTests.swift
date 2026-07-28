import EMKECore
import EMKECoordinator
import Foundation
import Testing

private let liveTranslationProbeEnabled =
    ProcessInfo.processInfo.environment[
        "EMKE_RUN_LIVE_TRANSLATION_TESTS"
    ] == "1"

@Test(
    .enabled(
        if: liveTranslationProbeEnabled,
        "Set EMKE_RUN_LIVE_TRANSLATION_TESTS=1 and provider inputs"
    )
)
func liveFortyMillisecondTranslationProbe() async throws {
    let environment = ProcessInfo.processInfo.environment
    let apiKey = try #require(environment["EMKE_API_KEY"])
    let baseURLString = try #require(environment["EMKE_BASE_URL"])
    let baseURL = try #require(URL(string: baseURLString))
    let modelID = try #require(environment["EMKE_MODEL_ID"])
    let sampleURL = URL(
        fileURLWithPath: try #require(environment["EMKE_SPEECH_SAMPLE"])
    )
    let speech = try Data(contentsOf: sampleURL)
    #expect(speech.count.isMultiple(of: 1_920))

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
        speechSample: speech
    )

    #expect(report.handshake == .passed)
    #expect(report.sourceTranscript == .passed)
    #expect(report.audioOutput == .passed)
}
