import SwiftUI

@main
@MainActor
struct EMKEMenuBarApp: App {
    @StateObject private var model: MenuBarModel
    @StateObject private var floatingPanelController:
        FloatingTranslationPanelController

    init() {
        let model = MenuBarModel(deferInitialDeviceReload: true)
        _model = StateObject(wrappedValue: model)
        _floatingPanelController = StateObject(
            wrappedValue: FloatingTranslationPanelController(model: model)
        )
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarRootView(model: model)
        } label: {
            Image(nsImage: MenuBarLogo.image)
                .accessibilityLabel("EMKE Translation")
        }
        .menuBarExtraStyle(.window)
    }
}
