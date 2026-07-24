import EMKECoordinator
import Foundation
import Testing
@testable import EMKEMenuBarApp

private let floatingNow = Date(timeIntervalSince1970: 10_000)

@MainActor
private final class FloatingSettingsStoreStub: AppSettingsStoring {
    private var settings = AppSettings.default

    func load() -> AppSettings {
        settings
    }

    func save(_ settings: AppSettings) {
        self.settings = settings
    }
}

private func makeFloatingPresentation(
    coordinatorState: TranslationCoordinatorState = TranslationCoordinatorState(),
    isStarting: Bool = false,
    isStopping: Bool = false,
    inboundLevel: Double = 0,
    outboundLevel: Double = 0,
    translationStartedAt: Date? = nil,
    hasFatalSessionError: Bool = false,
    language: ResolvedInterfaceLanguage = .english
) -> FloatingTranslationPresentation {
    FloatingTranslationPresentation.make(
        coordinatorState: coordinatorState,
        isStarting: isStarting,
        isStopping: isStopping,
        inboundLevel: inboundLevel,
        outboundLevel: outboundLevel,
        translationStartedAt: translationStartedAt,
        now: floatingNow,
        hasFatalSessionError: hasFatalSessionError,
        copy: AppCopy(language: language)
    )
}

@Test
func runningFloatingPresentationUsesCombinedRealLevelAndTimer() {
    let value = FloatingTranslationPresentation.make(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        ),
        isStarting: false,
        isStopping: false,
        inboundLevel: 0.35,
        outboundLevel: 0.72,
        translationStartedAt: Date(timeIntervalSince1970: 9_935),
        now: Date(timeIntervalSince1970: 10_000),
        hasFatalSessionError: false,
        copy: AppCopy(language: .english)
    )

    #expect(value.isVisible)
    #expect(value.tone == .healthy)
    #expect(value.status == "Translating")
    #expect(value.statusAccessibilityLabel == "Translating")
    #expect(!value.showsActivityPulse)
    #expect(value.elapsed == "01:05")
    #expect(value.level == 0.72)
    #expect(value.stopEnabled)
    #expect(value.stopAccessibilityLabel == "Stop translation")
}

@Test
func idleFloatingPresentationIsHidden() {
    let value = FloatingTranslationPresentation.make(
        coordinatorState: TranslationCoordinatorState(),
        isStarting: false,
        isStopping: false,
        inboundLevel: 0,
        outboundLevel: 0,
        translationStartedAt: nil,
        now: .now,
        hasFatalSessionError: true,
        copy: AppCopy(language: .english)
    )

    #expect(!value.isVisible)
    #expect(value.elapsed == nil)
    #expect(!value.stopEnabled)
}

@Test(arguments: [
    TranslationChannelState.connecting,
    TranslationChannelState.active,
    TranslationChannelState.bypassed,
    TranslationChannelState.reconnecting(attempt: 1),
    TranslationChannelState.failed(message: "offline"),
])
func nonRunningNonStoppedChannelKeepsFloatingPresentationActive(
    channelState: TranslationChannelState
) {
    let states = [
        TranslationCoordinatorState(
            isRunning: false,
            inbound: channelState,
            outbound: .stopped
        ),
        TranslationCoordinatorState(
            isRunning: false,
            inbound: .stopped,
            outbound: channelState
        ),
    ]

    for state in states {
        let value = makeFloatingPresentation(
            coordinatorState: state,
            translationStartedAt: floatingNow.addingTimeInterval(-65)
        )

        #expect(value.isVisible)
        #expect(value.elapsed == "01:05")
        #expect(value.stopEnabled)
    }
}

@Test
func establishedNonRunningFailedSessionUsesDegradedStatus() {
    let inboundFailure = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: false,
            inbound: .failed(message: "inbound offline"),
            outbound: .stopped
        ),
        translationStartedAt: floatingNow
    )
    let outboundFailure = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: false,
            inbound: .stopped,
            outbound: .failed(message: "outbound offline")
        ),
        translationStartedAt: floatingNow
    )

    #expect(inboundFailure.isVisible)
    #expect(inboundFailure.status == "Original")
    #expect(inboundFailure.tone == .degraded)
    #expect(inboundFailure.stopEnabled)
    #expect(outboundFailure.isVisible)
    #expect(outboundFailure.status == "Muted")
    #expect(outboundFailure.tone == .degraded)
    #expect(outboundFailure.stopEnabled)
}

@Test
func nonRunningFailedSessionWithoutStartContextIsHidden() {
    let value = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: false,
            inbound: .failed(message: "pre-run failure"),
            outbound: .stopped
        ),
        translationStartedAt: nil
    )

    #expect(!value.isVisible)
    #expect(value.elapsed == nil)
    #expect(!value.stopEnabled)
}

@Test(arguments: [
    TranslationChannelState.connecting,
    TranslationChannelState.failed(message: "pre-run failure"),
])
func preRunChannelStateRemainsConnectingAndCannotStop(
    channelState: TranslationChannelState
) {
    for startedAt in [nil, floatingNow] as [Date?] {
        let value = makeFloatingPresentation(
            coordinatorState: TranslationCoordinatorState(
                isRunning: false,
                inbound: channelState,
                outbound: .stopped
            ),
            isStarting: true,
            translationStartedAt: startedAt
        )

        #expect(value.isVisible)
        #expect(value.tone == .neutral)
        #expect(value.status == "Connecting")
        #expect(value.statusAccessibilityLabel == "Connecting")
        #expect(value.showsActivityPulse)
        #expect(value.elapsed == nil)
        #expect(!value.stopEnabled)
    }
}

@Test
func startingFloatingPresentationIsVisibleNeutralAndCannotStop() {
    let value = makeFloatingPresentation(
        isStarting: true,
        inboundLevel: 0.2,
        outboundLevel: 0.4
    )

    #expect(value.isVisible)
    #expect(value.tone == .neutral)
    #expect(value.status == "Connecting")
    #expect(value.statusAccessibilityLabel == "Connecting")
    #expect(value.showsActivityPulse)
    #expect(value.elapsed == nil)
    #expect(value.level == 0.4)
    #expect(!value.stopEnabled)
}

@Test
func inboundFailureFloatingPresentationUsesFailOpenChineseCopy() {
    let chinese = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .failed(message: "inbound offline"),
            outbound: .active
        ),
        translationStartedAt: floatingNow,
        language: .zhHans
    )
    let english = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .failed(message: "inbound offline"),
            outbound: .active
        ),
        translationStartedAt: floatingNow
    )

    #expect(chinese.isVisible)
    #expect(chinese.tone == .degraded)
    #expect(chinese.status == "播放原音")
    #expect(chinese.statusAccessibilityLabel == "入站播放原音")
    #expect(!chinese.showsActivityPulse)
    #expect(chinese.elapsed == "00:00")
    #expect(chinese.stopEnabled)
    #expect(chinese.stopAccessibilityLabel == "停止翻译")
    #expect(english.status == "Original")
    #expect(
        english.statusAccessibilityLabel
            == "Playing original incoming audio"
    )
}

@Test
func outboundFailureFloatingPresentationPrioritizesFailClosed() {
    let english = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .failed(message: "inbound offline"),
            outbound: .failed(message: "outbound offline")
        ),
        translationStartedAt: floatingNow
    )
    let chinese = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .failed(message: "outbound offline")
        ),
        translationStartedAt: floatingNow,
        language: .zhHans
    )

    #expect(english.tone == .degraded)
    #expect(english.status == "Muted")
    #expect(english.statusAccessibilityLabel == "Outbound muted")
    #expect(!english.showsActivityPulse)
    #expect(english.stopEnabled)
    #expect(chinese.status == "出站静音")
    #expect(chinese.statusAccessibilityLabel == "出站已静音")
}

@Test
func fatalRunningErrorFloatingPresentationTakesPriority() {
    let value = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .failed(message: "inbound offline"),
            outbound: .failed(message: "outbound offline")
        ),
        translationStartedAt: nil,
        hasFatalSessionError: true,
        language: .zhHans
    )

    #expect(value.isVisible)
    #expect(value.tone == .failure)
    #expect(value.status == "异常")
    #expect(value.statusAccessibilityLabel == "翻译异常")
    #expect(!value.showsActivityPulse)
    #expect(value.elapsed == "00:00")
    #expect(value.stopEnabled)
}

@Test
func stoppingFloatingPresentationTakesPriorityAndDisablesStop() {
    let value = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .failed(message: "outbound offline")
        ),
        isStarting: true,
        isStopping: true,
        translationStartedAt: floatingNow.addingTimeInterval(-65),
        hasFatalSessionError: true
    )

    #expect(value.isVisible)
    #expect(value.tone == .neutral)
    #expect(value.status == "Stopping…")
    #expect(value.statusAccessibilityLabel == "Stopping…")
    #expect(!value.showsActivityPulse)
    #expect(value.elapsed == "01:05")
    #expect(!value.stopEnabled)
}

@Test
func stoppingWithoutRunningFloatingPresentationRemainsVisible() {
    let value = makeFloatingPresentation(
        isStopping: true,
        language: .zhHans
    )

    #expect(value.isVisible)
    #expect(value.status == "正在停止…")
    #expect(value.statusAccessibilityLabel == "正在停止…")
    #expect(!value.showsActivityPulse)
    #expect(value.elapsed == nil)
    #expect(!value.stopEnabled)
}

@Test
func levelClampingFloatingPresentationHandlesOutOfRangeValues() {
    let belowZero = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(isRunning: true),
        inboundLevel: -0.8,
        outboundLevel: -0.2
    )
    let aboveOne = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(isRunning: true),
        inboundLevel: 1.4,
        outboundLevel: 0.8
    )

    #expect(belowZero.level == 0)
    #expect(aboveOne.level == 1)
}

@Test
func nonFiniteFloatingPresentationLevelsAreSanitizedIndividually() {
    let nanInbound = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(isRunning: true),
        inboundLevel: .nan,
        outboundLevel: 0.65
    )
    let infiniteOutbound = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(isRunning: true),
        inboundLevel: 0.4,
        outboundLevel: .infinity
    )
    let negativeInfiniteInbound = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(isRunning: true),
        inboundLevel: -.infinity,
        outboundLevel: 0.25
    )

    #expect(nanInbound.level == 0.65)
    #expect(infiniteOutbound.level == 0.4)
    #expect(negativeInfiniteInbound.level == 0.25)
}

@Test
func futureStartDateFloatingPresentationClampsElapsedToZero() {
    let value = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        ),
        translationStartedAt: floatingNow.addingTimeInterval(5)
    )

    #expect(value.elapsed == "00:00")
}

@Test @MainActor
func menuBarModelFloatingPresentationIsAPureCurrentStateProjection() {
    let model = MenuBarModel(
        settingsStore: FloatingSettingsStoreStub(),
        deferInitialDeviceReload: true
    )
    model.interfaceLanguage = .english
    let now = Date(timeIntervalSince1970: 12_345)
    let before = (
        state: model.coordinatorState,
        isStarting: model.isStarting,
        isStopping: model.isStopping,
        inboundLevel: model.inboundLevel,
        outboundLevel: model.outboundLevel,
        startedAt: model.translationStartedAt
    )

    let value = model.floatingPresentation(at: now)

    #expect(!value.isVisible)
    #expect(value.status == "Translating")
    #expect(value.statusAccessibilityLabel == "Translating")
    #expect(value.tone == .healthy)
    #expect(!value.showsActivityPulse)
    #expect(value.elapsed == nil)
    #expect(value.level == 0)
    #expect(!value.stopEnabled)
    #expect(model.coordinatorState == before.state)
    #expect(model.isStarting == before.isStarting)
    #expect(model.isStopping == before.isStopping)
    #expect(model.inboundLevel == before.inboundLevel)
    #expect(model.outboundLevel == before.outboundLevel)
    #expect(model.translationStartedAt == before.startedAt)
}

@Test
func englishFloatingErrorStaysDistinctFromHealthyVisibleStatus() {
    let healthy = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        )
    )
    let failure = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        ),
        hasFatalSessionError: true
    )

    #expect(healthy.status == "Translating")
    #expect(failure.status == "Error")
    #expect(failure.statusAccessibilityLabel == "Translation error")
    #expect(failure.status != healthy.status)
}

@Test
func floatingActivityPulseIsLimitedToPreRunStartup() {
    let starting = makeFloatingPresentation(isStarting: true)
    let startingButRunning = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        ),
        isStarting: true
    )
    let stopping = makeFloatingPresentation(
        isStarting: true,
        isStopping: true
    )
    let degraded = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .failed(message: "offline")
        )
    )
    let failure = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        ),
        hasFatalSessionError: true
    )

    #expect(starting.showsActivityPulse)
    #expect(!startingButRunning.showsActivityPulse)
    #expect(!stopping.showsActivityPulse)
    #expect(!degraded.showsActivityPulse)
    #expect(!failure.showsActivityPulse)
}
