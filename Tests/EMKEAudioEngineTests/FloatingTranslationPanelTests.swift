import AppKit
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
