import AppKit
import Foundation
import SwiftUI
import Testing
@testable import EMKEMenuBarApp

@Test @MainActor
func captureRunningDashboardForVisualReview() throws {
    guard ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1" else {
        return
    }

    let value = DashboardFixture.running.makePresentation(
        inboundLevel: 0.42,
        outboundLevel: 0.68
    )
    let view = TranslationDashboardContent(
        value: value,
        motherLanguage: .constant(.chinese),
        meetingOutputLanguage: .constant(.german),
        languagesLocked: true,
        settingsAction: {},
        inboundAction: {},
        outboundAction: {},
        primaryAction: {}
    )
    .frame(
        width: EMKEVisualStyle.panelWidth,
        height: EMKEVisualStyle.panelHeight
    )
    .background(Color(nsColor: .windowBackgroundColor))

    let renderer = ImageRenderer(content: view)
    renderer.scale = 2
    let bitmap = try #require(renderer.nsImage?.tiffRepresentation)
    try bitmap.write(
        to: URL(fileURLWithPath: "/tmp/emke-running-dashboard.tiff")
    )
}
