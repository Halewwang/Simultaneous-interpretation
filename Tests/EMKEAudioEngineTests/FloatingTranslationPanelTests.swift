import AppKit
import Combine
import EMKECoordinator
import Testing
@testable import EMKEMenuBarApp

@Test @MainActor
func floatingPanelUsesNonActivatingCrossSpaceContract() {
    let model = MenuBarModel(deferInitialDeviceReload: true)
    let controller = FloatingTranslationPanelController(model: model)
    let panel = controller.panelForTesting

    #expect(panel.styleMask.contains(.borderless))
    #expect(panel.styleMask.contains(.nonactivatingPanel))
    #expect(panel.level == .floating)
    #expect(panel.collectionBehavior.contains(.canJoinAllSpaces))
    #expect(panel.collectionBehavior.contains(.fullScreenAuxiliary))
    #expect(panel.isMovableByWindowBackground)
    #expect(!panel.canBecomeKey)
    #expect(!panel.canBecomeMain)
    #expect(!panel.isOpaque)
    #expect(panel.backgroundColor == .clear)
    #expect(panel.hasShadow)
    #expect(!panel.hidesOnDeactivate)
}

@Test @MainActor
func floatingPanelStartsHiddenAtTheApprovedSize() {
    let model = MenuBarModel(deferInitialDeviceReload: true)
    let controller = FloatingTranslationPanelController(model: model)
    let panel = controller.panelForTesting

    #expect(
        panel.frame.size
            == NSSize(
                width: EMKEFloatingMetrics.width,
                height: EMKEFloatingMetrics.height
            )
    )
    #expect(!panel.isVisible)
}

@Test
func floatingPanelPlacementCentersWithinANonzeroVisibleFrame() {
    let visibleFrame = NSRect(x: -1_440, y: 24, width: 1_440, height: 876)

    let origin = FloatingTranslationPanelPlacement.origin(
        in: visibleFrame,
        panelSize: NSSize(
            width: EMKEFloatingMetrics.width,
            height: EMKEFloatingMetrics.height
        )
    )

    #expect(origin.x == -852)
    #expect(origin.y == 60)
}

@Test
func floatingPanelContentModeKeepsOnlyActiveLifecycleStatesLive() {
    let idle = panelPresentation()
    let failedBeforeRun = panelPresentation(hasFatalSessionError: true)
    let starting = panelPresentation(isStarting: true)
    let running = panelPresentation(
        coordinatorState: TranslationCoordinatorState(isRunning: true)
    )
    let stopping = panelPresentation(isStopping: true)

    #expect(FloatingTranslationPanelContentMode.resolve(idle) == .static)
    #expect(
        FloatingTranslationPanelContentMode.resolve(failedBeforeRun) == .static
    )
    #expect(FloatingTranslationPanelContentMode.resolve(starting) == .live)
    #expect(FloatingTranslationPanelContentMode.resolve(running) == .live)
    #expect(FloatingTranslationPanelContentMode.resolve(stopping) == .live)
}

@Test
func floatingVisibilityPublisherEmitsOnlyLifecycleBoundaries() {
    let isStarting = CurrentValueSubject<Bool, Never>(false)
    let isStopping = CurrentValueSubject<Bool, Never>(false)
    let isRunning = CurrentValueSubject<Bool, Never>(false)
    var values: [Bool] = []
    let observation = FloatingTranslationPanelVisibilityPublisher.make(
        isStarting: isStarting.eraseToAnyPublisher(),
        isStopping: isStopping.eraseToAnyPublisher(),
        isRunning: isRunning.eraseToAnyPublisher()
    )
    .sink { values.append($0) }

    isStarting.send(true)
    isRunning.send(true)
    isStarting.send(false)
    isStopping.send(true)
    isRunning.send(false)
    isStopping.send(false)

    #expect(values == [false, true, false])
    withExtendedLifetime(observation) {}
}

@Test @MainActor
func unrelatedModelChangesDoNotSchedulePanelVisibilityRefresh() async {
    let model = MenuBarModel(deferInitialDeviceReload: true)
    let controller = FloatingTranslationPanelController(model: model)
    let initialCount = controller.visibilityRefreshScheduleCountForTesting

    model.interfaceLanguage = .english
    model.objectWillChange.send()
    await Task.yield()

    #expect(
        controller.visibilityRefreshScheduleCountForTesting == initialCount
    )
    #expect(!controller.panelForTesting.isVisible)
}

private func panelPresentation(
    coordinatorState: TranslationCoordinatorState =
        TranslationCoordinatorState(),
    isStarting: Bool = false,
    isStopping: Bool = false,
    hasFatalSessionError: Bool = false
) -> FloatingTranslationPresentation {
    FloatingTranslationPresentation.make(
        coordinatorState: coordinatorState,
        isStarting: isStarting,
        isStopping: isStopping,
        inboundLevel: 0,
        outboundLevel: 0,
        translationStartedAt: nil,
        now: Date(timeIntervalSince1970: 10_000),
        hasFatalSessionError: hasFatalSessionError,
        copy: AppCopy(language: .english)
    )
}
