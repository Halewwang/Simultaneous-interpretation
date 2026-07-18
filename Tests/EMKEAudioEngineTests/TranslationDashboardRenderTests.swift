import AppKit
import Foundation
import SwiftUI
import Testing
@testable import EMKEMenuBarApp

@Test @MainActor
func captureRunningDashboardForVisualReview() throws {
    let bitmap = try runningDashboardBitmap()

    #expect(bitmap.pixelsWide == 840)
    #expect(bitmap.pixelsHigh == 1240)

    guard ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1" else { return }

    let data = try #require(bitmap.tiffRepresentation)
    try data.write(
        to: URL(fileURLWithPath: "/tmp/emke-running-dashboard.tiff")
    )
}

@MainActor
private func runningDashboardBitmap() throws -> NSBitmapImageRep {

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
    renderer.scale = EMKEVisualStyle.captureScale
    let data = try #require(renderer.nsImage?.tiffRepresentation)
    return try #require(NSBitmapImageRep(data: data))
}
