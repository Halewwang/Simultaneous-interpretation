import EMKECore
import Foundation
import Testing
@testable import EMKERealtime

@Test
func sessionUpdateEncodesTargetLanguage() throws {
    let data = try TranslationClientEvent.sessionUpdate(language: .german).encoded()
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
            Data(#"{"type":"session.output_audio.delta","delta":"AAEC"}"#.utf8)
        ) == .outputAudio(Data([0, 1, 2]))
    )
    #expect(
        try TranslationServerEvent.decode(
            Data(#"{"type":"session.input_transcript.delta","delta":"Hallo"}"#.utf8)
        ) == .inputTranscript("Hallo")
    )
    #expect(
        try TranslationServerEvent.decode(
            Data(#"{"type":"session.output_transcript.delta","delta":"你好"}"#.utf8)
        ) == .outputTranscript("你好")
    )
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
