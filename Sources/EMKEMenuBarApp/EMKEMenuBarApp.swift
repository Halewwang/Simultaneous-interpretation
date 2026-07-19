import SwiftUI

@main
@MainActor
struct EMKEMenuBarApp: App {
    @StateObject private var model = MenuBarModel()

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
