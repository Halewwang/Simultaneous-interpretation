import EMKECoordinator
import EMKECore
import Foundation
import Testing
@testable import EMKEMenuBarApp

struct DashboardFixture: Sendable {
    let readiness: MenuBarReadiness
    let coordinatorState: TranslationCoordinatorState
    let isStarting: Bool
    let isStopping: Bool
    let inboundBypassEnabled: Bool
    let outboundBypassEnabled: Bool
    let startedAt: Date?
    let errorText: String?

    static let now = Date(timeIntervalSince1970: 10_000)

    static let unconfigured = DashboardFixture(
        readiness: .apiKeyRequired
    )
    static let ready = DashboardFixture(readiness: .ready)
    static let connecting = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            inbound: .connecting,
            outbound: .connecting
        ),
        isStarting: true
    )
    static let engineReady = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            audioEngineStarted: true,
            inbound: .connecting,
            outbound: .connecting
        ),
        isStarting: true
    )
    static let running = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            audioEngineStarted: true,
            inbound: .active,
            outbound: .active
        ),
        startedAt: now.addingTimeInterval(-65)
    )
    static let inboundFailed = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            audioEngineStarted: true,
            inbound: .failed(message: "offline"),
            outbound: .active
        ),
        startedAt: now.addingTimeInterval(-65),
        errorText: "offline"
    )
    static let outboundFailed = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            audioEngineStarted: true,
            inbound: .active,
            outbound: .failed(message: "offline")
        ),
        startedAt: now.addingTimeInterval(-65),
        errorText: "offline"
    )
    static let inboundBypassed = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            audioEngineStarted: true,
            inbound: .bypassed,
            outbound: .active
        ),
        inboundBypassEnabled: true,
        startedAt: now.addingTimeInterval(-65)
    )
    static let outboundBypassed = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            audioEngineStarted: true,
            inbound: .active,
            outbound: .bypassed
        ),
        outboundBypassEnabled: true,
        startedAt: now.addingTimeInterval(-65)
    )
    static let stopping = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            audioEngineStarted: true,
            inbound: .active,
            outbound: .active
        ),
        isStopping: true,
        startedAt: now.addingTimeInterval(-65)
    )

    init(
        readiness: MenuBarReadiness,
        coordinatorState: TranslationCoordinatorState =
            TranslationCoordinatorState(),
        isStarting: Bool = false,
        isStopping: Bool = false,
        inboundBypassEnabled: Bool = false,
        outboundBypassEnabled: Bool = false,
        startedAt: Date? = nil,
        errorText: String? = nil
    ) {
        self.readiness = readiness
        self.coordinatorState = coordinatorState
        self.isStarting = isStarting
        self.isStopping = isStopping
        self.inboundBypassEnabled = inboundBypassEnabled
        self.outboundBypassEnabled = outboundBypassEnabled
        self.startedAt = startedAt
        self.errorText = errorText
    }

    func makePresentation(
        inboundLevel: Double = 0.35,
        outboundLevel: Double = 0.72,
        copy: AppCopy = AppCopy(language: .zhHans)
    ) -> TranslationDashboardPresentation {
        TranslationDashboardPresentation.make(
            readiness: readiness,
            coordinatorState: coordinatorState,
            isStarting: isStarting,
            isStopping: isStopping,
            inboundBypassEnabled: inboundBypassEnabled,
            outboundBypassEnabled: outboundBypassEnabled,
            inboundLevel: inboundLevel,
            outboundLevel: outboundLevel,
            translationStartedAt: startedAt,
            motherLanguage: .chinese,
            meetingOutputLanguage: .german,
            now: Self.now,
            errorText: errorText,
            copy: copy
        )
    }
}

@Test(arguments: [
    DashboardFixture.unconfigured,
    .ready,
    .connecting,
    .running,
    .inboundFailed,
    .outboundFailed,
    .inboundBypassed,
    .outboundBypassed,
    .stopping,
])
func dashboardPresentationIsDeterministic(
    fixture: DashboardFixture
) {
    let first = fixture.makePresentation()
    let second = fixture.makePresentation()
    #expect(first == second)
    #expect(!first.primaryStatus.isEmpty)
    #expect(!first.primaryActionTitle.isEmpty)
    #expect(!first.inbound.status.isEmpty)
    #expect(!first.outbound.status.isEmpty)
}

@Test
func dashboardPrimaryStatusUsesSemanticSymbolForEveryGlobalState() {
    let cases: [(fixture: DashboardFixture, symbol: String)] = [
        (.unconfigured, "exclamationmark.circle"),
        (.ready, "checkmark.circle"),
        (.connecting, "arrow.triangle.2.circlepath"),
        (.running, "waveform.circle"),
        (.inboundFailed, "exclamationmark.triangle"),
        (.outboundFailed, "exclamationmark.triangle"),
        (.stopping, "stop.circle"),
    ]

    for item in cases {
        let value = item.fixture.makePresentation()
        #expect(value.primaryStatusSymbol == item.symbol)
    }
}

@Test
func startupShowsAudioEngineReadinessOnlyAfterTheEngineStarts() {
    let beforeEngineStarts = DashboardFixture.connecting.makePresentation(
        copy: AppCopy(language: .english)
    )
    let afterEngineStarts = DashboardFixture.engineReady.makePresentation(
        copy: AppCopy(language: .english)
    )

    #expect(beforeEngineStarts.primaryStatus == "Connecting")
    #expect(afterEngineStarts.primaryStatus == "Audio engine ready")
    #expect(afterEngineStarts.inbound.status == "Connecting")
    #expect(afterEngineStarts.outbound.status == "Connecting")
}

@Test
func stoppingDashboardDoesNotContinueToPresentListenOrSpeakReadiness() {
    let value = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            audioEngineStarted: true,
            inbound: .active,
            outbound: .active
        ),
        isStopping: true,
        startedAt: DashboardFixture.now
    ).makePresentation()

    #expect(value.inbound.status == "已停止")
    #expect(value.outbound.status == "已停止")
}

@Test
func runningDashboardUsesCombinedAudioLevel() {
    let value = DashboardFixture.running.makePresentation(
        inboundLevel: 0.35,
        outboundLevel: 0.72
    )
    #expect(value.primaryLevel == 0.72)
}

@Test
func inboundFailureRetainsOriginalAudioLevel() {
    let value = DashboardFixture.inboundFailed.makePresentation(
        inboundLevel: 0.61,
        outboundLevel: 0.24
    )
    #expect(value.inboundLevel == 0.61)
    #expect(value.primaryLevel == 0.61)
}

@Test
func outboundFailureNeverPresentsOutputActivity() {
    let value = DashboardFixture.outboundFailed.makePresentation(
        inboundLevel: 0.35,
        outboundLevel: 0.92
    )
    #expect(value.outboundLevel == 0)
    #expect(value.primaryLevel == 0.35)
}

@Test
func runningInboundFailurePromotesFailOpenStatusBeforeElapsedTime() {
    let value = DashboardFixture.inboundFailed.makePresentation()
    #expect(value.primaryStatus == "入站播放原音")
    #expect(value.primaryStatusSymbol == "exclamationmark.triangle")
}

@Test
func runningOutboundFailurePromotesFailClosedStatusBeforeElapsedTime() {
    let value = DashboardFixture.outboundFailed.makePresentation()
    #expect(value.primaryStatus == "出站已静音")
    #expect(value.primaryStatusSymbol == "exclamationmark.triangle")
}

@Test
func simultaneousFailuresPrioritizeOutboundFailClosedDanger() {
    let value = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .failed(message: "inbound offline"),
            outbound: .failed(message: "outbound offline")
        ),
        startedAt: DashboardFixture.now.addingTimeInterval(-65),
        errorText: "offline"
    ).makePresentation()
    #expect(value.primaryStatus == "出站已静音")
    #expect(value.primaryStatusSymbol == "exclamationmark.triangle")
}

@Test
func dashboardDirectionsUseProductLanguageContract() {
    let value = DashboardFixture.ready.makePresentation()
    #expect(value.inboundDirection == "其他语言 → 中文")
    #expect(value.outboundDirection == "中文 → 德语")
    #expect(value.inputLanguageName == "中文")
    #expect(value.outputLanguageName == "德语")
}

@Test
func dashboardPresentationRendersCompleteEnglishCopy() {
    let copy = AppCopy(language: .english)
    let value = DashboardFixture.running.makePresentation(copy: copy)
    #expect(value.primaryStatus == "Translating · 01:05")
    #expect(value.primaryActionTitle == "Stop translation")
    #expect(value.inputLanguageName == "Chinese")
    #expect(value.outputLanguageName == "German")
    #expect(value.inboundDirection == "Other → Chinese")
    #expect(value.outboundDirection == "Chinese → German")
    #expect(value.privacyText == "Powered by Eager")
}

@Test
func languageDisplayNamesAreLocalizedWithoutChangingStorageCodes() {
    #expect(SupportedLanguage.chinese.displayName == "中文")
    #expect(SupportedLanguage.english.displayName == "英语")
    #expect(SupportedLanguage.german.displayName == "德语")
    #expect(SupportedLanguage.chinese.rawValue == "zh")
    #expect(SupportedLanguage.english.rawValue == "en")
    #expect(SupportedLanguage.german.rawValue == "de")
}
