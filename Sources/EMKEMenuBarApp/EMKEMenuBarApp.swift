import SwiftUI

@main
@MainActor
struct EMKEMenuBarApp: App {
    @StateObject private var model = MenuBarModel(
        deferInitialDeviceReload: true
    )

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
