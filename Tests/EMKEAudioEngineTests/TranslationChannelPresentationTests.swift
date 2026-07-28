import EMKECoordinator
import Testing
@testable import EMKEMenuBarApp

@Test
func runningCapabilitiesSeparateEngineListenAndSpeakReadiness() {
    let connecting = TranslationCoordinatorState(
        audioEngineStarted: true,
        inbound: .connecting,
        outbound: .connecting
    )
    #expect(connecting.audioEngineStarted)
    #expect(!connecting.canListen)
    #expect(!connecting.canSpeak)

    let active = TranslationCoordinatorState(
        isRunning: true,
        audioEngineStarted: true,
        inbound: .active,
        outbound: .active
    )
    #expect(active.canListen)
    #expect(active.canSpeak)

    let inboundFailed = TranslationCoordinatorState(
        isRunning: true,
        audioEngineStarted: true,
        inbound: .failed(message: "offline"),
        outbound: .active
    )
    #expect(inboundFailed.canListen)
    #expect(inboundFailed.canSpeak)

    let outboundFailed = TranslationCoordinatorState(
        isRunning: true,
        audioEngineStarted: true,
        inbound: .active,
        outbound: .failed(message: "offline")
    )
    #expect(outboundFailed.canListen)
    #expect(!outboundFailed.canSpeak)

    let outboundReconnecting = TranslationCoordinatorState(
        isRunning: true,
        audioEngineStarted: true,
        inbound: .reconnecting(attempt: 1),
        outbound: .reconnecting(attempt: 1)
    )
    #expect(outboundReconnecting.canListen)
    #expect(!outboundReconnecting.canSpeak)

    let bypassed = TranslationCoordinatorState(
        isRunning: true,
        audioEngineStarted: true,
        inbound: .bypassed,
        outbound: .bypassed
    )
    #expect(bypassed.canListen)
    #expect(bypassed.canSpeak)

    #expect(!TranslationCoordinatorState().canListen)
    #expect(!TranslationCoordinatorState().canSpeak)

    let engineUnavailable = TranslationCoordinatorState(
        isRunning: true,
        inbound: .active,
        outbound: .active
    )
    #expect(!engineUnavailable.canListen)
    #expect(!engineUnavailable.canSpeak)

    let stoppedChannels = TranslationCoordinatorState(
        audioEngineStarted: true,
        inbound: .stopped,
        outbound: .stopped
    )
    #expect(!stoppedChannels.canListen)
    #expect(!stoppedChannels.canSpeak)
}

@Test
func inboundActiveShowsListenReadinessAndOriginalAudioAction() {
    let value = TranslationChannelPresentation.make(
        channel: .inbound,
        state: .active,
        capabilityAvailable: true,
        bypassEnabled: false,
        copy: AppCopy(language: .zhHans)
    )
    #expect(value.status == "可以收听")
    #expect(value.actionTitle == "播放原音")
    #expect(value.actionAccessibilityLabel == "播放入站原音")
}

@Test
func outboundActiveShowsSpeakReadinessInEnglish() {
    let value = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .active,
        capabilityAvailable: true,
        bypassEnabled: false,
        copy: AppCopy(language: .english)
    )

    #expect(value.status == "Can speak")
}

@Test
func outboundFailureUsesMutedStatusAndNoFalseStableState() {
    let value = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .failed(message: "offline"),
        bypassEnabled: false,
        copy: AppCopy(language: .zhHans)
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
        bypassEnabled: true,
        copy: AppCopy(language: .zhHans)
    )
    let connecting = TranslationChannelPresentation.make(
        channel: .inbound,
        state: .connecting,
        bypassEnabled: true,
        copy: AppCopy(language: .zhHans)
    )
    let failed = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .failed(message: "offline"),
        bypassEnabled: true,
        copy: AppCopy(language: .zhHans)
    )

    #expect(stopped.status == "已停止")
    #expect(!stopped.actionEnabled)
    #expect(connecting.status == "连接中")
    #expect(!connecting.actionEnabled)
    #expect(failed.status == "已静音")
    #expect(!failed.actionEnabled)
}

@Test
func channelPresentationRendersEnglishFailureAndActions() {
    let copy = AppCopy(language: .english)
    let value = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .failed(message: "offline"),
        bypassEnabled: false,
        copy: copy
    )
    #expect(value.status == "Muted")
    #expect(value.actionTitle == "Send original")
    #expect(value.actionAccessibilityLabel == "Send original outbound audio")
}
