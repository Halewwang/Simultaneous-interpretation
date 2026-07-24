import AppKit
import EMKECoordinator
import EMKECore
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
                statusSymbol: item.1.statusSymbol,
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
            statusSymbol: ready.inbound.statusSymbol,
            actionTitle: ready.inbound.actionTitle
        ) == .compact
    )
    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            direction: ready.outboundDirection,
            status: ready.outbound.status,
            statusSymbol: ready.outbound.statusSymbol,
            actionTitle: ready.outbound.actionTitle
        ) == .compact
    )
}

@Test
func compactStatusMeasurementUsesRenderedSymbolWidthAtBoundary() {
    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            direction: "A",
            status: "MMMMMMMMA",
            statusSymbol: "stop.circle",
            actionTitle: "Go"
        ) == .expanded
    )
}

@Test
func dashboardChannelBudgetTracksPanelHeightAndErrorAllocation() {
    let panel = EMKEDashboardVerticalLayoutGeometry(
        panelHeight: EMKEVisualStyle.panelHeight
    )
    let shorterPanel = EMKEDashboardVerticalLayoutGeometry(
        panelHeight: EMKEVisualStyle.panelHeight - 20
    )

    #expect(
        panel.channelSectionHeightBudget(hasErrorText: false)
            - shorterPanel.channelSectionHeightBudget(hasErrorText: false)
            == 20
    )
    #expect(
        panel.channelSectionHeightBudget(hasErrorText: false)
            - panel.channelSectionHeightBudget(hasErrorText: true)
            == panel.errorTextAllocationHeight
    )

    let rowHeights: [CGFloat] = [82, 87]
    let requiredWithError = panel.totalRequiredHeight(
        channelRowHeights: rowHeights,
        hasErrorText: true
    )
    #expect(
        requiredWithError
            - panel.totalRequiredHeight(
                channelRowHeights: [rowHeights[0], 0],
                hasErrorText: true
            )
            == rowHeights[1]
    )
    #expect(
        requiredWithError
            == EMKEVisualStyle.panelHeight
                - panel.channelSectionHeightBudget(hasErrorText: true)
                + rowHeights.reduce(0, +)
                + EMKEVisualStyle.separatorThickness
    )
}

@Test(arguments: EnglishDashboardStressCase.allCases)
@MainActor
private func englishStressDashboardsRenderWithinSharedGeometry(
    scenario: EnglishDashboardStressCase
) throws {
    let fixture = scenario.fixture
    let bitmap = try dashboardBitmap(
        value: fixture.value,
        copy: fixture.copy,
        languagesLocked: fixture.languagesLocked,
        motherLanguage: fixture.motherLanguage,
        meetingOutputLanguage: fixture.meetingOutputLanguage
    )

    #expect(bitmap.pixelsWide == 840)
    #expect(bitmap.pixelsHigh == 1240)

    let layouts = [
        EMKEExpandedChannelLayoutGeometry.resolve(
            title: fixture.copy.text(.heardByMe),
            direction: fixture.value.inboundDirection,
            status: fixture.value.inbound.status,
            statusSymbol: fixture.value.inbound.statusSymbol,
            actionTitle: fixture.value.inbound.actionTitle
        ),
        EMKEExpandedChannelLayoutGeometry.resolve(
            title: fixture.copy.text(.heardByOther),
            direction: fixture.value.outboundDirection,
            status: fixture.value.outbound.status,
            statusSymbol: fixture.value.outbound.statusSymbol,
            actionTitle: fixture.value.outbound.actionTitle
        ),
    ]
    let hasErrorText = fixture.value.errorText != nil
    let expectsErrorText = scenario == .inboundFailed
        || scenario == .outboundFailed
    #expect(hasErrorText == expectsErrorText)
    let dashboardGeometry = EMKEDashboardVerticalLayoutGeometry(
        panelHeight: EMKEVisualStyle.panelHeight
    )

    for layout in layouts {
        #expect(layout.contentBounds.contains(layout.directionFrame))
        #expect(layout.contentBounds.contains(layout.statusFrame))
        #expect(layout.contentBounds.contains(layout.actionFrame))
        #expect(layout.contentBounds.contains(layout.waveformFrame))
        #expect(!layout.directionFrame.intersects(layout.statusFrame))
        #expect(!layout.directionFrame.intersects(layout.actionFrame))
        #expect(!layout.statusFrame.intersects(layout.actionFrame))
        #expect(!layout.statusFrame.intersects(layout.waveformFrame))
        #expect(!layout.actionFrame.intersects(layout.waveformFrame))
        #expect(layout.statusFrame.height >= layout.statusContentHeight)
        #expect(layout.actionFrame.height >= layout.actionContentHeight)
        #expect(
            layout.requiredHeight
                <= EMKEChannelMetrics.expandedMaximumRowHeight
        )
    }

    let rowHeights = layouts.map(\.requiredHeight)
    let channelSectionRequiredHeight = rowHeights.reduce(0, +)
        + EMKEVisualStyle.separatorThickness
    #expect(
        channelSectionRequiredHeight
            <= dashboardGeometry.channelSectionHeightBudget(
                hasErrorText: hasErrorText
            )
    )
    #expect(
        dashboardGeometry.totalRequiredHeight(
            channelRowHeights: rowHeights,
            hasErrorText: hasErrorText
        ) <= EMKEVisualStyle.panelHeight
    )
}

@MainActor
private func dashboardBitmap(
    value: TranslationDashboardPresentation,
    copy: AppCopy,
    languagesLocked: Bool,
    motherLanguage: SupportedLanguage = .chinese,
    meetingOutputLanguage: SupportedLanguage = .german
) throws -> NSBitmapImageRep {
    let view = dashboardView(
        value: value,
        copy: copy,
        languagesLocked: languagesLocked,
        motherLanguage: motherLanguage,
        meetingOutputLanguage: meetingOutputLanguage
    )

    let renderer = ImageRenderer(content: view)
    renderer.scale = EMKEVisualStyle.captureScale
    let data = try #require(renderer.nsImage?.tiffRepresentation)
    return try #require(NSBitmapImageRep(data: data))
}

@MainActor
private func dashboardView(
    value: TranslationDashboardPresentation,
    copy: AppCopy,
    languagesLocked: Bool,
    motherLanguage: SupportedLanguage,
    meetingOutputLanguage: SupportedLanguage
) -> AnyView {
    AnyView(
        TranslationDashboardContent(
            value: value,
            copy: copy,
            motherLanguage: .constant(motherLanguage),
            meetingOutputLanguage: .constant(meetingOutputLanguage),
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
    )
}

private enum EnglishDashboardStressCase: String, CaseIterable, Sendable {
    case ready
    case running
    case manualBypass
    case reconnecting
    case sameLanguagePassThrough
    case inboundFailed
    case outboundFailed

    var fixture: EnglishDashboardStressFixture {
        let copy = AppCopy(language: .english)
        switch self {
        case .ready:
            return EnglishDashboardStressFixture(
                value: DashboardFixture.ready.makePresentation(copy: copy),
                copy: copy,
                languagesLocked: false
            )
        case .running:
            return EnglishDashboardStressFixture(
                value: DashboardFixture.running.makePresentation(copy: copy),
                copy: copy
            )
        case .manualBypass:
            return EnglishDashboardStressFixture(
                value: DashboardFixture(
                    readiness: .active,
                    coordinatorState: TranslationCoordinatorState(
                        isRunning: true,
                        inbound: .bypassed,
                        outbound: .bypassed
                    ),
                    inboundBypassEnabled: true,
                    outboundBypassEnabled: true,
                    startedAt: DashboardFixture.now
                ).makePresentation(copy: copy),
                copy: copy
            )
        case .reconnecting:
            return EnglishDashboardStressFixture(
                value: DashboardFixture(
                    readiness: .active,
                    coordinatorState: TranslationCoordinatorState(
                        isRunning: true,
                        inbound: .reconnecting(attempt: 12),
                        outbound: .reconnecting(attempt: 12)
                    ),
                    startedAt: DashboardFixture.now
                ).makePresentation(copy: copy),
                copy: copy
            )
        case .sameLanguagePassThrough:
            let motherLanguage = SupportedLanguage.english
            let meetingOutputLanguage = SupportedLanguage.english
            let state = TranslationCoordinatorState(
                isRunning: true,
                inbound: .active,
                outbound: .bypassed
            )
            let value = TranslationDashboardPresentation.make(
                readiness: .active,
                coordinatorState: state,
                isStarting: false,
                isStopping: false,
                inboundBypassEnabled: false,
                outboundBypassEnabled: false,
                inboundLevel: 0.35,
                outboundLevel: 0.72,
                translationStartedAt: DashboardFixture.now,
                motherLanguage: motherLanguage,
                meetingOutputLanguage: meetingOutputLanguage,
                now: DashboardFixture.now,
                errorText: nil,
                copy: copy
            )
            return EnglishDashboardStressFixture(
                value: value,
                copy: copy,
                motherLanguage: motherLanguage,
                meetingOutputLanguage: meetingOutputLanguage
            )
        case .inboundFailed:
            return EnglishDashboardStressFixture(
                value: DashboardFixture.inboundFailed.makePresentation(
                    copy: copy
                ),
                copy: copy
            )
        case .outboundFailed:
            return EnglishDashboardStressFixture(
                value: DashboardFixture.outboundFailed.makePresentation(
                    copy: copy
                ),
                copy: copy
            )
        }
    }
}

private struct EnglishDashboardStressFixture {
    let value: TranslationDashboardPresentation
    let copy: AppCopy
    var languagesLocked = true
    var motherLanguage = SupportedLanguage.chinese
    var meetingOutputLanguage = SupportedLanguage.german
}
