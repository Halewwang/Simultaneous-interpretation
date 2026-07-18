import EMKECore
import Foundation

public enum TranslationClientEvent: Sendable {
    case sessionUpdate(configuration: TranslationSessionConfiguration)
    case appendAudio(Data)
    case close

    public func encoded() throws -> Data {
        let object: [String: Any]
        switch self {
        case .sessionUpdate(let configuration):
            var audio: [String: Any] = [
                "output": [
                    "language": configuration.targetLanguage.rawValue,
                ],
            ]
            var input: [String: Any] = [:]
            if let model = configuration.inputTranscriptionModel {
                input["transcription"] = ["model": model]
            }
            if let noiseReduction = configuration.noiseReduction {
                input["noise_reduction"] = [
                    "type": noiseReduction.rawValue,
                ]
            }
            if !input.isEmpty {
                audio["input"] = input
            }
            object = [
                "type": "session.update",
                "session": [
                    "audio": audio,
                ],
            ]
        case .appendAudio(let data):
            object = [
                "type": "session.input_audio_buffer.append",
                "audio": data.base64EncodedString(),
            ]
        case .close:
            object = ["type": "session.close"]
        }
        return try JSONSerialization.data(
            withJSONObject: object,
            options: [.sortedKeys]
        )
    }
}
