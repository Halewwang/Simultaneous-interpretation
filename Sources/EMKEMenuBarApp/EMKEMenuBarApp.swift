import SwiftUI

@main
@MainActor
struct EMKEMenuBarApp: App {
    @StateObject private var model = MenuBarModel()

    var body: some Scene {
        MenuBarExtra(
            "EMKE Translation",
            systemImage: model.systemImage
        ) {
            MenuBarRootView(model: model)
        }
        .menuBarExtraStyle(.window)
    }
}
