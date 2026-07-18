import Foundation

public struct TranslationAudioDelta: Equatable, Sendable {
    public let data: Data
    public let sampleRate: Int
    public let channels: Int
    public let format: String
    public let elapsedMilliseconds: Int?

    public init(
        data: Data,
        sampleRate: Int,
        channels: Int,
        format: String,
        elapsedMilliseconds: Int?
    ) {
        self.data = data
        self.sampleRate = sampleRate
        self.channels = channels
        self.format = format
        self.elapsedMilliseconds = elapsedMilliseconds
    }
}

public struct TranslationTranscriptDelta: Equatable, Sendable {
    public let text: String
    public let elapsedMilliseconds: Int?

    public init(text: String, elapsedMilliseconds: Int?) {
        self.text = text
        self.elapsedMilliseconds = elapsedMilliseconds
    }
}

public enum TranslationServerEventDecodingError: Error, Equatable, Sendable {
    case invalidBase64Audio
    case unsupportedAudioFormat(
        sampleRate: Int,
        channels: Int,
        format: String
    )
}

public enum TranslationServerEvent: Equatable, Sendable {
    case sessionCreated(model: String)
    case sessionUpdated
    case outputAudio(TranslationAudioDelta)
    case inputTranscript(TranslationTranscriptDelta)
    case outputTranscript(TranslationTranscriptDelta)
    case closed
    case serverError(code: String, message: String)
    case ignored(type: String)

    public static func decode(_ data: Data) throws -> Self {
        let object = try JSONSerialization.jsonObject(with: data)
            as? [String: Any]
        let type = object?["type"] as? String ?? ""

        switch type {
        case "session.created":
            let session = object?["session"] as? [String: Any]
            return .sessionCreated(
                model: session?["model"] as? String ?? ""
            )
        case "session.updated":
            return .sessionUpdated
        case "session.output_audio.delta":
            guard let encodedAudio = object?["delta"] as? String,
                  let audio = Data(base64Encoded: encodedAudio) else {
                throw TranslationServerEventDecodingError.invalidBase64Audio
            }
            let sampleRate = object?["sample_rate"] as? Int ?? 24_000
            let channels = object?["channels"] as? Int ?? 1
            let format = object?["format"] as? String ?? "pcm16"
            guard sampleRate == 24_000,
                  channels == 1,
                  format == "pcm16" else {
                throw TranslationServerEventDecodingError
                    .unsupportedAudioFormat(
                        sampleRate: sampleRate,
                        channels: channels,
                        format: format
                    )
            }
            return .outputAudio(
                TranslationAudioDelta(
                    data: audio,
                    sampleRate: sampleRate,
                    channels: channels,
                    format: format,
                    elapsedMilliseconds: object?["elapsed_ms"] as? Int
                )
            )
        case "session.input_transcript.delta":
            return .inputTranscript(
                TranslationTranscriptDelta(
                    text: object?["delta"] as? String ?? "",
                    elapsedMilliseconds: object?["elapsed_ms"] as? Int
                )
            )
        case "session.output_transcript.delta":
            return .outputTranscript(
                TranslationTranscriptDelta(
                    text: object?["delta"] as? String ?? "",
                    elapsedMilliseconds: object?["elapsed_ms"] as? Int
                )
            )
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
