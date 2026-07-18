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
    static let running = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .active,
            outbound: .active
        ),
        startedAt: now.addingTimeInterval(-65)
    )
    static let inboundFailed = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
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
        outboundLevel: Double = 0.72
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
            errorText: errorText
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
func dashboardDirectionsUseProductLanguageContract() {
    let value = DashboardFixture.ready.makePresentation()
    #expect(value.inboundDirection == "其他语言 → 中文")
    #expect(value.outboundDirection == "中文 → Deutsch")
    #expect(value.inputLanguageName == "中文")
    #expect(value.outputLanguageName == "Deutsch")
}
