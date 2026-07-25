import AppKit
import SwiftUI

@MainActor
protocol OnboardingWindowPresenting: AnyObject {
    func show()
    func bringToFront()
    func hide()
}

@MainActor
final class OnboardingWindowController: ObservableObject {
    @Published private(set) var flow = OnboardingFlowState()
    @Published private(set) var isVisible = false

    private let progressStore: any OnboardingProgressStoring
    private let stopAudioInputDiagnostic: () -> Void
    private var window: (any OnboardingWindowPresenting)?

    init(
        progressStore: any OnboardingProgressStoring,
        stopAudioInputDiagnostic: @escaping () -> Void = {}
    ) {
        self.progressStore = progressStore
        self.stopAudioInputDiagnostic = stopAudioInputDiagnostic
    }

    func attachWindow(_ window: any OnboardingWindowPresenting) {
        precondition(self.window == nil)
        self.window = window
    }

    func showIfNeeded() {
        guard progressStore.shouldPresent(
            currentVersion: OnboardingVersion.current
        ) else { return }
        show()
    }

    func show() {
        if flow.step == .audioSetup {
            stopAudioInputDiagnostic()
        }
        flow.restart()
        isVisible = true
        window?.show()
    }

    func restoreAfterExternalPrompt() {
        guard isVisible else { return }
        window?.bringToFront()
    }

    func moveForward() {
        stopDiagnosticBeforeLeavingAudioSetup()
        flow.moveForward()
    }

    func moveBackward() {
        stopDiagnosticBeforeLeavingAudioSetup()
        flow.moveBackward()
    }

    func skipForNow() {
        stopAudioInputDiagnostic()
        isVisible = false
        window?.hide()
    }

    func doNotShowAgain() {
        finishAndHide()
    }

    func complete() {
        finishAndHide()
    }

    private func finishAndHide() {
        stopAudioInputDiagnostic()
        progressStore.markCompleted(version: OnboardingVersion.current)
        isVisible = false
        window?.hide()
    }

    private func stopDiagnosticBeforeLeavingAudioSetup() {
        guard flow.step == .audioSetup else { return }
        stopAudioInputDiagnostic()
    }
}

@MainActor
final class OnboardingAppWindowPresenter:
    NSObject,
    OnboardingWindowPresenting,
    NSWindowDelegate
{
    private let window: NSWindow
    private let closeAction: () -> Void

    init(rootView: AnyView, closeAction: @escaping () -> Void) {
        self.closeAction = closeAction
        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 560, height: 620),
            styleMask: [.titled, .closable, .miniaturizable],
            backing: .buffered,
            defer: false
        )
        super.init()
        window.title = "EMKE Translation"
        window.contentViewController = NSHostingController(rootView: rootView)
        window.isReleasedWhenClosed = false
        window.center()
        window.delegate = self
    }

    func show() {
        bringToFront()
    }

    func bringToFront() {
        NSApplication.shared.activate(ignoringOtherApps: true)
        window.orderFrontRegardless()
        window.makeKey()
    }

    func hide() {
        window.orderOut(nil)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        closeAction()
        return false
    }
}
