import SwiftUI

struct MenuBarRootView: View {
    @ObservedObject var model: MenuBarModel
    @ObservedObject var updateController: AppUpdateController
    let openOnboarding: () -> Void

    init(
        model: MenuBarModel,
        updateController: AppUpdateController,
        openOnboarding: @escaping () -> Void = {}
    ) {
        self.model = model
        self.updateController = updateController
        self.openOnboarding = openOnboarding
    }

    var body: some View {
        Group {
            switch model.screen {
            case .dashboard:
                TranslationDashboardView(model: model)
            case .settings:
                TranslationSettingsView(
                    model: model,
                    updateController: updateController,
                    openOnboarding: openOnboarding
                )
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
