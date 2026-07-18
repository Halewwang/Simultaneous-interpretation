import Foundation

public enum TranslationServerEvent: Equatable, Sendable {
    case outputAudio(Data)
    case inputTranscript(String)
    case outputTranscript(String)
    case closed
    case serverError(code: String, message: String)
    case ignored(type: String)

    public static func decode(_ data: Data) throws -> Self {
        let object = try JSONSerialization.jsonObject(with: data)
            as? [String: Any]
        let type = object?["type"] as? String ?? ""

        switch type {
        case "session.output_audio.delta":
            let encodedAudio = object?["delta"] as? String ?? ""
            return .outputAudio(Data(base64Encoded: encodedAudio) ?? Data())
        case "session.input_transcript.delta":
            return .inputTranscript(object?["delta"] as? String ?? "")
        case "session.output_transcript.delta":
            return .outputTranscript(object?["delta"] as? String ?? "")
        case "session.closed":
            return .closed
        case "error":
            let error = object?["error"] as? [String: Any]
            return .serverError(
                code: error?["code"] as? String ?? "unknown",
                message: error?["message"] as? String
                    ?? "Unknown server error"
            )
        default:
            return .ignored(type: type)
        }
    }
}
