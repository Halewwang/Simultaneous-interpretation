import Foundation

public struct APIConfiguration: Codable, Equatable, Sendable {
    public var baseURL: URL
    public var modelID: String

    public init(baseURL: URL, modelID: String) {
        self.baseURL = baseURL
        self.modelID = modelID
    }

    public static let `default` = APIConfiguration(
        baseURL: URL(string: "https://api.openai.com/v1")!,
        modelID: "gpt-realtime-translate"
    )
}
