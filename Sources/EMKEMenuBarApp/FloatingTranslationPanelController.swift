import AppKit
import Combine
import SwiftUI

enum FloatingTranslationPanelPlacement {
    static func origin(
        in visibleFrame: NSRect,
        panelSize: NSSize
    ) -> NSPoint {
        NSPoint(
            x: visibleFrame.midX - (panelSize.width / 2),
            y: visibleFrame.minY + 36
        )
    }
}

enum FloatingTranslationPanelContentMode: Equatable {
    case `static`
    case live

    static func resolve(
        _ presentation: FloatingTranslationPresentation
    ) -> Self {
        presentation.isVisible ? .live : .static
    }
}

enum FloatingTranslationPanelVisibilityPublisher {
    static func make(
        isStarting: AnyPublisher<Bool, Never>,
        isStopping: AnyPublisher<Bool, Never>,
        isRunning: AnyPublisher<Bool, Never>
    ) -> AnyPublisher<Bool, Never> {
        Publishers.CombineLatest3(isStarting, isStopping, isRunning)
            .map { starting, stopping, running in
                starting || stopping || running
            }
            .removeDuplicates()
            .eraseToAnyPublisher()
    }
}

private final class FloatingTranslationPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

private struct FloatingTranslationPanelRoot: View {
    @ObservedObject var model: MenuBarModel

    var body: some View {
        let presentation = model.floatingPresentation(at: .now)
        switch FloatingTranslationPanelContentMode.resolve(presentation) {
        case .static:
            Color.clear.frame(
                width: EMKEFloatingMetrics.width,
                height: EMKEFloatingMetrics.height
            )
        case .live:
            TimelineView(.periodic(from: .now, by: 1)) { context in
                FloatingTranslationStatusView(
                    presentation: model.floatingPresentation(at: context.date),
                    stopAction: {
                        Task { await model.stop() }
                    }
                )
            }
        }
    }
}

@MainActor
final class FloatingTranslationPanelController: ObservableObject {
    private let model: MenuBarModel
    private let panel: FloatingTranslationPanel
    private var visibilityObservation: AnyCancellable?
    private var refreshTask: Task<Void, Never>?
    private var visibilitySyncTask: Task<Void, Never>?
    private var refreshGeneration = 0
    private var panelIsVisible = false
    private var hasPlacedPanel = false
    private var desiredModelVisibility: Bool?
    private var appliedModelVisibility = false
    private(set) var visibilityRefreshScheduleCountForTesting = 0

    init(model: MenuBarModel) {
        self.model = model

        let panel = FloatingTranslationPanel(
            contentRect: NSRect(
                x: 0,
                y: 0,
                width: EMKEFloatingMetrics.width,
                height: EMKEFloatingMetrics.height
            ),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.level = .floating
        panel.collectionBehavior = [
            .canJoinAllSpaces,
            .fullScreenAuxiliary,
        ]
        panel.isMovableByWindowBackground = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.contentView = NSHostingView(
            rootView: FloatingTranslationPanelRoot(model: model)
        )
        self.panel = panel

        visibilityObservation = FloatingTranslationPanelVisibilityPublisher
            .make(
                isStarting: model.$isStarting.eraseToAnyPublisher(),
                isStopping: model.$isStopping.eraseToAnyPublisher(),
                isRunning: model.$coordinatorState.map(\.isRunning)
                    .eraseToAnyPublisher()
            )
            .sink { [weak self] desiredVisibility in
                MainActor.assumeIsolated {
                    self?.scheduleRefresh(to: desiredVisibility)
                }
            }
    }

    deinit {
        refreshTask?.cancel()
        visibilitySyncTask?.cancel()
    }

    var panelForTesting: NSPanel {
        panel
    }

    private func scheduleRefresh(to desiredVisibility: Bool) {
        visibilityRefreshScheduleCountForTesting += 1
        refreshGeneration += 1
        let generation = refreshGeneration
        refreshTask?.cancel()
        refreshTask = Task { @MainActor [weak self] in
            await Task.yield()
            guard
                !Task.isCancelled,
                let self,
                generation == self.refreshGeneration
            else {
                return
            }
            self.refreshTask = nil
            self.refreshPanelVisibility(to: desiredVisibility)
        }
    }

    private func refreshPanelVisibility(to desiredVisibility: Bool) {
        guard desiredVisibility != panelIsVisible else { return }

        panelIsVisible = desiredVisibility
        if desiredVisibility {
            placePanelIfNeeded()
            panel.orderFrontRegardless()
        } else {
            panel.orderOut(nil)
        }
        synchronizeModelVisibility(to: desiredVisibility)
    }

    private func placePanelIfNeeded() {
        guard !hasPlacedPanel else { return }
        guard let screen = screenContainingMouse() ?? NSScreen.main else {
            return
        }
        hasPlacedPanel = true
        panel.setFrameOrigin(
            FloatingTranslationPanelPlacement.origin(
                in: screen.visibleFrame,
                panelSize: panel.frame.size
            )
        )
    }

    private func screenContainingMouse() -> NSScreen? {
        let mouseLocation = NSEvent.mouseLocation
        return NSScreen.screens.first {
            $0.frame.contains(mouseLocation)
        }
    }

    private func synchronizeModelVisibility(to desiredVisibility: Bool) {
        desiredModelVisibility = desiredVisibility
        guard visibilitySyncTask == nil else { return }

        visibilitySyncTask = Task { @MainActor [weak self] in
            await self?.drainModelVisibility()
        }
    }

    private func drainModelVisibility() async {
        while let desiredVisibility = desiredModelVisibility {
            self.desiredModelVisibility = nil
            guard desiredVisibility != appliedModelVisibility else {
                continue
            }
            await model.setFloatingWindowVisible(desiredVisibility)
            appliedModelVisibility = desiredVisibility
        }
        visibilitySyncTask = nil
    }
}
