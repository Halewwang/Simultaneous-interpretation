import EMKECore
import Foundation
import Testing
@testable import EMKERealtime

@Test
func sessionUpdateEncodesTargetLanguage() throws {
    let data = try TranslationClientEvent.sessionUpdate(
        configuration: TranslationSessionConfiguration(
            targetLanguage: .german
        )
    ).encoded()
    let object = try #require(
        JSONSerialization.jsonObject(with: data) as? [String: Any]
    )
    #expect(object["type"] as? String == "session.update")
    let session = try #require(object["session"] as? [String: Any])
    let audio = try #require(session["audio"] as? [String: Any])
    let output = try #require(audio["output"] as? [String: Any])
    #expect(output["language"] as? String == "de")
}

@Test
func inboundSessionUpdateEnablesSourceTranscription() throws {
    let data = try TranslationClientEvent.sessionUpdate(
        configuration: TranslationSessionConfiguration(
            targetLanguage: .chinese,
            inputTranscriptionModel: "gpt-realtime-whisper",
            noiseReduction: .farField
        )
    ).encoded()
    let object = try #require(
        JSONSerialization.jsonObject(with: data) as? [String: Any]
    )
    let session = try #require(object["session"] as? [String: Any])
    let audio = try #require(session["audio"] as? [String: Any])
    let input = try #require(audio["input"] as? [String: Any])
    let transcription = try #require(
        input["transcription"] as? [String: Any]
    )
    let noiseReduction = try #require(
        input["noise_reduction"] as? [String: Any]
    )

    #expect(transcription["model"] as? String == "gpt-realtime-whisper")
    #expect(noiseReduction["type"] as? String == "far_field")
}

@Test
func inputAudioEncodesBase64PCM() throws {
    let data = try TranslationClientEvent.appendAudio(Data([0, 1, 2])).encoded()
    let object = try #require(
        JSONSerialization.jsonObject(with: data) as? [String: Any]
    )
    #expect(object["type"] as? String == "session.input_audio_buffer.append")
    #expect(object["audio"] as? String == "AAEC")
}

@Test
func decodesOutputAudioAndTranscripts() throws {
    #expect(
        try TranslationServerEvent.decode(
            Data(
                #"{"type":"session.output_audio.delta","delta":"AAEC","sample_rate":24000,"channels":1,"format":"pcm16","elapsed_ms":400}"#.utf8
            )
        ) == .outputAudio(
            TranslationAudioDelta(
                data: Data([0, 1, 2]),
                sampleRate: 24_000,
                channels: 1,
                format: "pcm16",
                elapsedMilliseconds: 400
            )
        )
    )
    #expect(
        try TranslationServerEvent.decode(
            Data(
                #"{"type":"session.input_transcript.delta","delta":"Hallo","elapsed_ms":600}"#.utf8
            )
        ) == .inputTranscript(
            TranslationTranscriptDelta(
                text: "Hallo",
                elapsedMilliseconds: 600
            )
        )
    )
    #expect(
        try TranslationServerEvent.decode(
            Data(#"{"type":"session.output_transcript.delta","delta":"你好"}"#.utf8)
        ) == .outputTranscript(
            TranslationTranscriptDelta(
                text: "你好",
                elapsedMilliseconds: nil
            )
        )
    )
}

@Test
func decodesHandshakeEvents() throws {
    #expect(
        try TranslationServerEvent.decode(
            Data(
                #"{"type":"session.created","session":{"model":"gpt-realtime-translate"}}"#.utf8
            )
        ) == .sessionCreated(model: "gpt-realtime-translate")
    )
    #expect(
        try TranslationServerEvent.decode(
            Data(#"{"type":"session.updated"}"#.utf8)
        ) == .sessionUpdated
    )
}

@Test
func rejectsInvalidOrUnsupportedOutputAudio() {
    #expect(throws: TranslationServerEventDecodingError.invalidBase64Audio) {
        try TranslationServerEvent.decode(
            Data(
                #"{"type":"session.output_audio.delta","delta":"%%%"}"#.utf8
            )
        )
    }
    #expect(
        throws: TranslationServerEventDecodingError.unsupportedAudioFormat(
            sampleRate: 16_000,
            channels: 1,
            format: "pcm16"
        )
    ) {
        try TranslationServerEvent.decode(
            Data(
                #"{"type":"session.output_audio.delta","delta":"AAEC","sample_rate":16000,"channels":1,"format":"pcm16"}"#.utf8
            )
        )
    }
}

@Test
func decodesServerError() throws {
    let value = try TranslationServerEvent.decode(
        Data(
            #"{"type":"error","error":{"code":"invalid_api_key","message":"bad key"}}"#.utf8
        )
    )
    #expect(value == .serverError(code: "invalid_api_key", message: "bad key"))
}
