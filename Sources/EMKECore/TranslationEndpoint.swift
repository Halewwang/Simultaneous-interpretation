import Foundation

public enum TranslationEndpointError: Error, Equatable {
    case insecureScheme
    case missingHost
    case emptyModelID
    case invalidURL
}

public enum TranslationEndpoint {
    public static func webSocketURL(
        configuration: APIConfiguration
    ) throws -> URL {
        let modelID = configuration.modelID.trimmingCharacters(
            in: .whitespacesAndNewlines
        )
        guard !modelID.isEmpty else {
            throw TranslationEndpointError.emptyModelID
        }

        var components = URLComponents(
            url: configuration.baseURL,
            resolvingAgainstBaseURL: false
        )
        guard let scheme = components?.scheme?.lowercased(),
              scheme == "https" || scheme == "wss" else {
            throw TranslationEndpointError.insecureScheme
        }
        guard components?.host?.isEmpty == false else {
            throw TranslationEndpointError.missingHost
        }

        components?.scheme = "wss"
        let basePath = components?.path.trimmingCharacters(
            in: CharacterSet(charactersIn: "/")
        ) ?? ""
        components?.path = "/" + [basePath, "realtime", "translations"]
            .filter { !$0.isEmpty }
            .joined(separator: "/")
        components?.queryItems = [
            URLQueryItem(name: "model", value: modelID),
        ]

        guard let url = components?.url else {
            throw TranslationEndpointError.invalidURL
        }
        return url
    }
}
