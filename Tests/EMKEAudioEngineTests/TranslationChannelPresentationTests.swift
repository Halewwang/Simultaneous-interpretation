import EMKECoordinator
import Testing
@testable import EMKEMenuBarApp

@Test
func inboundActiveUsesOriginalAudioAction() {
    let value = TranslationChannelPresentation.make(
        channel: .inbound,
        state: .active,
        bypassEnabled: false
    )
    #expect(value.status == "稳定")
    #expect(value.actionTitle == "播放原音")
    #expect(value.actionAccessibilityLabel == "播放入站原音")
}

@Test
func outboundFailureUsesMutedStatusAndNoFalseStableState() {
    let value = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .failed(message: "offline"),
        bypassEnabled: false
    )
    #expect(value.status == "已静音")
    #expect(value.symbol == "mic.slash")
    #expect(value.isBlockingFailure)
}
