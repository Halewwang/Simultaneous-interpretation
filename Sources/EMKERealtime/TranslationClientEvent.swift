import EMKECore
import Foundation

public enum TranslationClientEvent: Sendable {
    case sessionUpdate(language: SupportedLanguage)
    case appendAudio(Data)
    case close

    public func encoded() throws -> Data {
        let object: [String: Any]
        switch self {
        case .sessionUpdate(let language):
            object = [
                "type": "session.update",
                "session": [
                    "audio": [
                        "output": [
                            "language": language.rawValue,
                        ],
                    ],
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
