import Foundation
import Testing
@testable import EMKECore

@Test
func buildsOfficialTranslationWebSocketURL() throws {
    let url = try TranslationEndpoint.webSocketURL(configuration: .default)
    #expect(url.absoluteString == "wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate")
}

@Test
func preservesGatewayPrefixAndEscapesModel() throws {
    let configuration = APIConfiguration(
        baseURL: URL(string: "https://gateway.example.com/openai/v1/")!,
        modelID: "translation model"
    )
    let url = try TranslationEndpoint.webSocketURL(configuration: configuration)
    #expect(url.absoluteString == "wss://gateway.example.com/openai/v1/realtime/translations?model=translation%20model")
}

@Test
func rejectsInsecureBaseURL() {
    let configuration = APIConfiguration(
        baseURL: URL(string: "http://example.com/v1")!,
        modelID: "model"
    )
    #expect(throws: TranslationEndpointError.insecureScheme) {
        try TranslationEndpoint.webSocketURL(configuration: configuration)
    }
}

@Test
func rejectsBlankModelID() {
    let configuration = APIConfiguration(
        baseURL: APIConfiguration.default.baseURL,
        modelID: "  "
    )
    #expect(throws: TranslationEndpointError.emptyModelID) {
        try TranslationEndpoint.webSocketURL(configuration: configuration)
    }
}
