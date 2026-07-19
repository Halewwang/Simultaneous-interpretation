import Foundation
import Testing
@testable import EMKERealtime

@Test
func clientEventsUseTextWebSocketFrames() throws {
    let json = Data(#"{"type":"session.update"}"#.utf8)

    let message = try URLSessionTranslationSocket.outboundMessage(for: json)

    switch message {
    case .string(let value):
        #expect(value == #"{"type":"session.update"}"#)
    case .data:
        Issue.record("OpenAI Translation only accepts text WebSocket frames")
    @unknown default:
        Issue.record("Unexpected WebSocket message type")
    }
}

@Test
func clientEventsRejectNonUTF8Payloads() {
    #expect(throws: TranslationSocketError.invalidUTF8TextFrame) {
        try URLSessionTranslationSocket.outboundMessage(
            for: Data([0xC3, 0x28])
        )
    }
}
