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
    errorText: String? = nil,
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
        errorText: errorText,
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
        errorText: nil,
        copy: AppCopy(language: .english)
    )

    #expect(value.isVisible)
    #expect(value.tone == .healthy)
    #expect(value.status == "Translating")
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
        errorText: "configuration error",
        copy: AppCopy(language: .english)
    )

    #expect(!value.isVisible)
    #expect(value.elapsed == nil)
    #expect(!value.stopEnabled)
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
    #expect(value.elapsed == nil)
    #expect(value.level == 0.4)
    #expect(!value.stopEnabled)
}

@Test
func inboundFailureFloatingPresentationUsesFailOpenChineseCopy() {
    let value = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .failed(message: "inbound offline"),
            outbound: .active
        ),
        translationStartedAt: floatingNow,
        language: .zhHans
    )

    #expect(value.isVisible)
    #expect(value.tone == .degraded)
    #expect(value.status == "入站播放原音")
    #expect(value.elapsed == "00:00")
    #expect(value.stopEnabled)
    #expect(value.stopAccessibilityLabel == "停止翻译")
}

@Test
func outboundFailureFloatingPresentationPrioritizesFailClosed() {
    let value = makeFloatingPresentation(
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .failed(message: "inbound offline"),
            outbound: .failed(message: "outbound offline")
        ),
        translationStartedAt: floatingNow
    )

    #expect(value.tone == .degraded)
    #expect(value.status == "Outbound muted")
    #expect(value.stopEnabled)
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
        errorText: "fatal",
        language: .zhHans
    )

    #expect(value.isVisible)
    #expect(value.tone == .failure)
    #expect(value.status == "翻译异常")
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
        errorText: "fatal"
    )

    #expect(value.isVisible)
    #expect(value.tone == .neutral)
    #expect(value.status == "Stopping…")
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

    #expect(
        value == FloatingTranslationPresentation.make(
            coordinatorState: before.state,
            isStarting: before.isStarting,
            isStopping: before.isStopping,
            inboundLevel: before.inboundLevel,
            outboundLevel: before.outboundLevel,
            translationStartedAt: before.startedAt,
            now: now,
            errorText: model.configurationError ?? model.inventoryError,
            copy: AppCopy(language: .english)
        )
    )
    #expect(model.coordinatorState == before.state)
    #expect(model.isStarting == before.isStarting)
    #expect(model.isStopping == before.isStopping)
    #expect(model.inboundLevel == before.inboundLevel)
    #expect(model.outboundLevel == before.outboundLevel)
    #expect(model.translationStartedAt == before.startedAt)
}
