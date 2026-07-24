import AppKit
import Foundation
import SwiftUI
import Testing
@testable import EMKEMenuBarApp

@Test @MainActor
func captureRunningDashboardForVisualReview() throws {
    let copy = AppCopy(language: .zhHans)
    let bitmap = try dashboardBitmap(
        value: DashboardFixture.running.makePresentation(
            inboundLevel: 0.42,
            outboundLevel: 0.68,
            copy: copy
        ),
        copy: copy,
        languagesLocked: true
    )

    #expect(bitmap.pixelsWide == 840)
    #expect(bitmap.pixelsHigh == 1240)

    guard ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1" else { return }

    let data = try #require(bitmap.tiffRepresentation)
    try data.write(
        to: URL(fileURLWithPath: "/tmp/emke-running-dashboard.tiff")
    )
}

@Test @MainActor
func captureReadyDashboardForVisualReview() throws {
    let copy = AppCopy(language: .zhHans)
    let bitmap = try dashboardBitmap(
        value: DashboardFixture.ready.makePresentation(
            inboundLevel: 0,
            outboundLevel: 0,
            copy: copy
        ),
        copy: copy,
        languagesLocked: false
    )

    #expect(bitmap.pixelsWide == 840)
    #expect(bitmap.pixelsHigh == 1240)

    guard ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1" else { return }

    let data = try #require(bitmap.tiffRepresentation)
    try data.write(
        to: URL(fileURLWithPath: "/tmp/emke-ready-dashboard.tiff")
    )
}

@Test @MainActor
func readyLanguageControlsRenderWithoutFallbackPlaceholders() throws {
    let copy = AppCopy(language: .zhHans)
    let bitmap = try dashboardBitmap(
        value: DashboardFixture.ready.makePresentation(
            inboundLevel: 0,
            outboundLevel: 0,
            copy: copy
        ),
        copy: copy,
        languagesLocked: false
    )

    var saturatedPlaceholderPixels = 0
    for y in 440..<620 {
        for x in 120..<720 {
            guard let color = bitmap.colorAt(x: x, y: y) else { continue }
            if color.redComponent > 0.85,
               color.greenComponent > 0.55,
               color.blueComponent < 0.25 {
                saturatedPlaceholderPixels += 1
            }
        }
    }
    #expect(saturatedPlaceholderPixels == 0)
}

@Test @MainActor
func englishReadyDashboardKeepsApprovedRenderDimensions() throws {
    let copy = AppCopy(language: .english)
    let bitmap = try dashboardBitmap(
        value: DashboardFixture.ready.makePresentation(
            inboundLevel: 0,
            outboundLevel: 0,
            copy: copy
        ),
        copy: copy,
        languagesLocked: false
    )

    #expect(bitmap.pixelsWide == 840)
    #expect(bitmap.pixelsHigh == 1240)
}

@MainActor
private func dashboardBitmap(
    value: TranslationDashboardPresentation,
    copy: AppCopy,
    languagesLocked: Bool
) throws -> NSBitmapImageRep {
    let view = TranslationDashboardContent(
        value: value,
        copy: copy,
        motherLanguage: .constant(.chinese),
        meetingOutputLanguage: .constant(.german),
        languagesLocked: languagesLocked,
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
