import AppKit
import EMKECoordinator
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

    guard ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1" else {
        return
    }

    let data = try #require(bitmap.tiffRepresentation)
    try data.write(
        to: URL(fileURLWithPath: "/tmp/emke-english-ready-dashboard.tiff")
    )
}

@Test
func englishChannelCopyChoosesExpandedLayoutWhenCompactColumnsCannotFit() {
    let copy = AppCopy(language: .english)
    let ready = DashboardFixture.ready.makePresentation(copy: copy)
    let running = DashboardFixture.running.makePresentation(copy: copy)
    let inboundBypassed = DashboardFixture.inboundBypassed.makePresentation(
        copy: copy
    )
    let outboundBypassed = DashboardFixture.outboundBypassed.makePresentation(
        copy: copy
    )
    let reconnecting = DashboardFixture(
        readiness: .active,
        coordinatorState: TranslationCoordinatorState(
            isRunning: true,
            inbound: .reconnecting(attempt: 12),
            outbound: .reconnecting(attempt: 12)
        ),
        startedAt: DashboardFixture.now
    ).makePresentation(copy: copy)
    let inboundFailed = DashboardFixture.inboundFailed.makePresentation(
        copy: copy
    )
    let outboundFailed = DashboardFixture.outboundFailed.makePresentation(
        copy: copy
    )
    let sameLanguage = TranslationChannelPresentation.make(
        channel: .outbound,
        state: .bypassed,
        bypassEnabled: false,
        automaticBypass: true,
        copy: copy
    )

    #expect(ready.inboundDirection == "Other languages → Chinese")
    #expect(ready.outboundDirection == "Chinese → German")
    #expect(running.inbound.actionTitle == "Play original")
    #expect(running.outbound.actionTitle == "Send original")
    #expect(inboundBypassed.inbound.actionTitle == "Resume translation")
    #expect(outboundBypassed.outbound.actionTitle == "Resume translation")
    #expect(reconnecting.inbound.status == "Reconnecting (attempt 12)")
    #expect(sameLanguage.status == "Same-language pass-through")

    let cases: [(String, TranslationChannelPresentation)] = [
        (ready.inboundDirection, ready.inbound),
        (ready.outboundDirection, ready.outbound),
        (running.inboundDirection, running.inbound),
        (running.outboundDirection, running.outbound),
        (inboundBypassed.inboundDirection, inboundBypassed.inbound),
        (outboundBypassed.outboundDirection, outboundBypassed.outbound),
        (reconnecting.inboundDirection, reconnecting.inbound),
        (reconnecting.outboundDirection, reconnecting.outbound),
        (inboundFailed.inboundDirection, inboundFailed.inbound),
        (outboundFailed.outboundDirection, outboundFailed.outbound),
        ("English → English", sameLanguage),
    ]

    for item in cases {
        #expect(
            EMKEChannelRowLayoutDecision.resolve(
                direction: item.0,
                status: item.1.status,
                actionTitle: item.1.actionTitle
            ) == .expanded
        )
    }
}

@Test
func conciseChineseChannelCopyKeepsApprovedCompactLayout() {
    let copy = AppCopy(language: .zhHans)
    let ready = DashboardFixture.ready.makePresentation(copy: copy)

    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            direction: ready.inboundDirection,
            status: ready.inbound.status,
            actionTitle: ready.inbound.actionTitle
        ) == .compact
    )
    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            direction: ready.outboundDirection,
            status: ready.outbound.status,
            actionTitle: ready.outbound.actionTitle
        ) == .compact
    )
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
