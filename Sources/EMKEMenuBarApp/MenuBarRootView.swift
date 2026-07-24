import SwiftUI

struct MenuBarRootView: View {
    @ObservedObject var model: MenuBarModel

    var body: some View {
        Group {
            switch model.screen {
            case .dashboard:
                TranslationDashboardView(model: model)
            case .settings:
                TranslationSettingsView(model: model)
            }
        }
        .frame(
            width: EMKEVisualStyle.panelWidth,
            height: EMKEVisualStyle.panelHeight
        )
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear {
            Task {
                await model.setMenuBarVisible(true)
                await model.loadConfiguration()
                await model.reloadDevicesAsync()
            }
        }
        .onDisappear {
            Task { await model.setMenuBarVisible(false) }
        }
    }
}
