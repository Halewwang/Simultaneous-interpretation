import SwiftUI
import AppKit

@main
@MainActor
struct EMKEMenuBarApp: App {
    @StateObject private var model: MenuBarModel
    @StateObject private var floatingPanelController:
        FloatingTranslationPanelController
    @StateObject private var onboardingWindowController:
        OnboardingWindowController
    private let launchObserver: NSObjectProtocol

    init() {
        let model = MenuBarModel(deferInitialDeviceReload: true)
        let onboardingWindowController = OnboardingWindowController(
            progressStore: UserDefaultsOnboardingProgressStore(),
            stopAudioInputDiagnostic: { [weak model] in
                Task { @MainActor in
                    await model?.stopAudioInputTest()
                }
            }
        )
        let onboardingWindow = OnboardingAppWindowPresenter(
            rootView: AnyView(
                OnboardingView(
                    model: model,
                    controller: onboardingWindowController
                )
            ),
            closeAction: { [weak onboardingWindowController] in
                onboardingWindowController?.skipForNow()
            }
        )
        onboardingWindowController.attachWindow(onboardingWindow)

        _model = StateObject(wrappedValue: model)
        _floatingPanelController = StateObject(
            wrappedValue: FloatingTranslationPanelController(model: model)
        )
        _onboardingWindowController = StateObject(
            wrappedValue: onboardingWindowController
        )
        launchObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didFinishLaunchingNotification,
            object: nil,
            queue: .main
        ) { [weak model, weak onboardingWindowController] _ in
            Task { @MainActor in
                guard let model, let onboardingWindowController else {
                    return
                }
                await model.loadConfiguration()
                await model.reloadDevicesAsync()
                await model.refreshMicrophonePermissionState()
                onboardingWindowController.showIfNeeded()
            }
        }
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarRootView(model: model) {
                onboardingWindowController.show()
            }
        } label: {
            Image(nsImage: MenuBarLogo.image)
                .accessibilityLabel("EMKE Translation")
        }
        .menuBarExtraStyle(.window)
    }
}
