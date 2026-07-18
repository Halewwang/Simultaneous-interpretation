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

@Test
func connectingAndFailureSafetyStatesOverrideManualBypassPresentation() {
    let stopped = TranslationChannelPresentation.make(
        channel: .inbound,
        state: .stopped,
        bypassEnabled: true
    )
    let connecting = TranslationChannelPresentation.make(
        channel: .inbound,
        state: .connecting,
        bypassEnabled: true
    )
    let failed = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .failed(message: "offline"),
        bypassEnabled: true
    )

    #expect(stopped.status == "已停止")
    #expect(!stopped.actionEnabled)
    #expect(connecting.status == "连接中")
    #expect(!connecting.actionEnabled)
    #expect(failed.status == "已静音")
    #expect(!failed.actionEnabled)
}
