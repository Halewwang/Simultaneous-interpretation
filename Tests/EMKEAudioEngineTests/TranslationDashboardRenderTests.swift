import AppKit
import EMKEAudioEngine
import EMKECoordinator
import EMKECore
import EMKESecurity
import Foundation
import SwiftUI
import Testing
@testable import EMKEMenuBarApp

final class CaptureArtifacts: @unchecked Sendable {
    static let requiredFilenames: Set<String> = [
        "dashboard-ready-zh.tiff",
        "dashboard-ready-en.tiff",
        "settings-zh.tiff",
        "settings-en.tiff",
        "floating-connecting.tiff",
        "floating-running.tiff",
        "floating-degraded.tiff",
        "floating-stopping.tiff",
    ]
    static let shared = CaptureArtifacts(
        directory: URL(
            fileURLWithPath: "/tmp/emke-interface-floating-qa",
            isDirectory: true
        )
    )

    static var isEnabled: Bool {
        ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1"
    }

    private let directory: URL
    private let lock = NSLock()
    private var isPrepared = false

    init(directory: URL) {
        self.directory = directory
    }

    func prepare() throws {
        try lock.withLock {
            try prepareWhileLocked()
        }
    }

    func write(_ data: Data, named filename: String) throws {
        try lock.withLock {
            guard Self.requiredFilenames.contains(filename) else {
                throw CocoaError(.fileWriteInvalidFileName)
            }
            try prepareWhileLocked()
            try data.write(
                to: directory.appendingPathComponent(filename),
                options: .atomic
            )
        }
    }

    func finish() throws -> Set<String> {
        try lock.withLock {
            try prepareWhileLocked()
            return Set(
                try FileManager.default.contentsOfDirectory(
                    at: directory,
                    includingPropertiesForKeys: nil,
                    options: []
                )
                .map(\.lastPathComponent)
            )
        }
    }

    private func prepareWhileLocked() throws {
        guard !isPrepared else {
            return
        }

        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: directory.path) {
            try fileManager.removeItem(at: directory)
        }
        try fileManager.createDirectory(
            at: directory,
            withIntermediateDirectories: true
        )
        isPrepared = true
    }
}

@Test
func captureArtifactsPrepareRemovesStaleFiles() throws {
    let fileManager = FileManager.default
    let directory = fileManager.temporaryDirectory
        .appendingPathComponent(UUID().uuidString, isDirectory: true)
    defer { try? fileManager.removeItem(at: directory) }

    try fileManager.createDirectory(
        at: directory,
        withIntermediateDirectories: true
    )
    let staleArtifact = directory.appendingPathComponent("stale.tiff")
    try Data("stale".utf8).write(to: staleArtifact)

    let artifacts = CaptureArtifacts(directory: directory)
    try artifacts.prepare()

    #expect(!fileManager.fileExists(atPath: staleArtifact.path))
}

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

    try writeQACapture(bitmap, named: "dashboard-ready-zh.tiff")
    if ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1" {
        #expect(
            FileManager.default.fileExists(
                atPath:
                    "/tmp/emke-interface-floating-qa/dashboard-ready-zh.tiff"
            )
        )
    }
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

    try writeQACapture(bitmap, named: "dashboard-ready-en.tiff")
}

@Test @MainActor
func settingsRenderInBothInterfaceLanguagesAtApprovedDimensions() throws {
    for (language, filename) in [
        (AppInterfaceLanguage.zhHans, "settings-zh.tiff"),
        (AppInterfaceLanguage.english, "settings-en.tiff"),
    ] {
        let render = try settingsRender(language: language)
        let bitmap = render.bitmap
        let quitControl = quitControlEvidence(in: bitmap)

        #expect(bitmap.pixelsWide == 840)
        #expect(bitmap.pixelsHigh == 1240)
        #expect(
            render.scrollGeometry.expectedBottomOffset > 0,
            "Settings must have enough content to require bottom scrolling"
        )
        #expect(
            render.scrollGeometry.reachedBottom,
            "The real settings scroll view must reach its bottom for \(language)"
        )
        #expect(
            quitControl.isVisible,
            """
            The quit control must have a light local background, its expected \
            border, and locally contrasting glyph/text for \(language); \
            background=\(quitControl.background), \
            border=\(quitControl.borderPixels), \
            foreground=\(quitControl.foregroundPixels)
            """
        )

        try writeQACapture(bitmap, named: filename)
    }
}

@Test @MainActor
func captureArtifactDirectoryMatchesExactExpectedSet() throws {
    guard CaptureArtifacts.isEnabled else {
        return
    }

    try captureReadyDashboardForVisualReview()
    try englishReadyDashboardKeepsApprovedRenderDimensions()
    try settingsRenderInBothInterfaceLanguagesAtApprovedDimensions()
    try floatingCapsuleRendersAtRetinaDimensions()

    let actualFilenames = try CaptureArtifacts.shared.finish()
    #expect(actualFilenames == CaptureArtifacts.requiredFilenames)
}

private struct QuitControlEvidence {
    let background: CGFloat
    let borderPixels: Int
    let foregroundPixels: Int

    var isVisible: Bool {
        background > 0.9 && borderPixels > 300 && foregroundPixels > 300
    }
}

private func quitControlEvidence(
    in bitmap: NSBitmapImageRep
) -> QuitControlEvidence {
    func luminance(x: Int, y: Int) -> CGFloat? {
        guard let color = bitmap.colorAt(x: x, y: y)?
            .usingColorSpace(.deviceRGB)
        else {
            return nil
        }
        return 0.2126 * color.redComponent
            + 0.7152 * color.greenComponent
            + 0.0722 * color.blueComponent
    }

    var backgroundTotal: CGFloat = 0
    var backgroundCount = 0
    for y in 150..<158 {
        for x in 100..<740 {
            guard let value = luminance(x: x, y: y) else { continue }
            backgroundTotal += value
            backgroundCount += 1
        }
    }
    let background = backgroundCount == 0
        ? 0
        : backgroundTotal / CGFloat(backgroundCount)

    func contrastingPixels(
        xRange: Range<Int>,
        yRange: Range<Int>,
        difference: CGFloat
    ) -> Int {
        var count = 0
        for y in yRange {
            for x in xRange {
                guard let value = luminance(x: x, y: y) else { continue }
                count += background - value >= difference ? 1 : 0
            }
        }
        return count
    }

    return QuitControlEvidence(
        background: background,
        borderPixels: contrastingPixels(
            xRange: 54..<786,
            yRange: 140..<146,
            difference: 0.05
        ),
        foregroundPixels: contrastingPixels(
            xRange: 60..<330,
            yRange: 44..<82,
            difference: 0.25
        )
    )
}

@Test
func quitControlEvidenceRejectsUniformDarkBackground() throws {
    for color in [NSColor.black, NSColor.white] {
        let bitmap = try solidBitmap(color: color)
        #expect(!quitControlEvidence(in: bitmap).isVisible)
    }
}

@Test
func settingsScrollGeometryRejectsAnUnscrolledDocument() {
    let geometry = SettingsScrollGeometry(
        documentHeight: 1_800,
        viewportHeight: 1_000,
        expectedBottomOffset: 800,
        actualBottomOffset: 0
    )

    #expect(!geometry.reachedBottom)
}

private func solidBitmap(color: NSColor) throws -> NSBitmapImageRep {
    let bitmap = try #require(
        NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: 840,
            pixelsHigh: 1240,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bitmapFormat: [],
            bytesPerRow: 0,
            bitsPerPixel: 0
        )
    )
    NSGraphicsContext.saveGraphicsState()
    let context = try #require(NSGraphicsContext(bitmapImageRep: bitmap))
    NSGraphicsContext.current = context
    color.setFill()
    NSRect(x: 0, y: 0, width: 840, height: 1240).fill()
    context.flushGraphics()
    NSGraphicsContext.restoreGraphicsState()

    return bitmap
}

@MainActor
private func settingsRender(
    language: AppInterfaceLanguage
) throws -> SettingsRender {
    var settings = AppSettings.default
    settings.interfaceLanguage = language
    let model = MenuBarModel(
        provider: RenderAudioDeviceProvider(),
        secretStore: RenderSecretStore(),
        settingsStore: RenderSettingsStore(settings: settings),
        microphonePermissionProvider: RenderMicrophonePermissionProvider(),
        deferInitialDeviceReload: true
    )
    let content = TranslationSettingsView(model: model)
        .frame(
            width: EMKEVisualStyle.panelWidth,
            height: EMKEVisualStyle.panelHeight
        )
        .background(Color(nsColor: .windowBackgroundColor))
        .environment(\.colorScheme, .light)
    let hostingView = NSHostingView(rootView: content)
    let aquaAppearance = try #require(NSAppearance(named: .aqua))
    hostingView.appearance = aquaAppearance
    let bounds = NSRect(
        x: 0,
        y: 0,
        width: EMKEVisualStyle.panelWidth,
        height: EMKEVisualStyle.panelHeight
    )
    hostingView.frame = bounds
    let window = NSWindow(
        contentRect: bounds,
        styleMask: [.borderless],
        backing: .buffered,
        defer: false
    )
    window.appearance = aquaAppearance
    window.contentView = hostingView
    window.contentView?.layoutSubtreeIfNeeded()
    let scrollView = try #require(firstScrollView(in: hostingView))
    let documentView = try #require(scrollView.documentView)
    let bottomY = documentView.isFlipped
        ? max(0, documentView.bounds.height - scrollView.contentView.bounds.height)
        : 0
    scrollView.contentView.scroll(to: NSPoint(x: 0, y: bottomY))
    scrollView.reflectScrolledClipView(scrollView.contentView)
    hostingView.layoutSubtreeIfNeeded()
    let actualBottomOffset = scrollView.contentView.bounds.origin.y
    let bitmap = try #require(
        NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: Int(
                EMKEVisualStyle.panelWidth * EMKEVisualStyle.captureScale
            ),
            pixelsHigh: Int(
                EMKEVisualStyle.panelHeight * EMKEVisualStyle.captureScale
            ),
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bitmapFormat: [],
            bytesPerRow: 0,
            bitsPerPixel: 0
        )
    )
    bitmap.size = bounds.size
    hostingView.cacheDisplay(in: bounds, to: bitmap)
    return SettingsRender(
        bitmap: bitmap,
        scrollGeometry: SettingsScrollGeometry(
            documentHeight: documentView.bounds.height,
            viewportHeight: scrollView.contentView.bounds.height,
            expectedBottomOffset: bottomY,
            actualBottomOffset: actualBottomOffset
        )
    )
}

private struct SettingsRender {
    let bitmap: NSBitmapImageRep
    let scrollGeometry: SettingsScrollGeometry
}

private struct SettingsScrollGeometry {
    let documentHeight: CGFloat
    let viewportHeight: CGFloat
    let expectedBottomOffset: CGFloat
    let actualBottomOffset: CGFloat

    var reachedBottom: Bool {
        documentHeight > viewportHeight
            && abs(actualBottomOffset - expectedBottomOffset) < 0.5
    }
}

@MainActor
private func firstScrollView(in view: NSView) -> NSScrollView? {
    if let scrollView = view as? NSScrollView {
        return scrollView
    }
    for subview in view.subviews {
        if let scrollView = firstScrollView(in: subview) {
            return scrollView
        }
    }
    return nil
}

private struct RenderAudioDeviceProvider: AudioDeviceProviding {
    func devices() throws -> [AudioDevice] {
        []
    }
}

private actor RenderSecretStore: SecretStore {
    func saveAPIKey(_ value: String) async throws {}
    func loadAPIKey() async throws -> String? { nil }
    func deleteAPIKey() async throws {}
}

private struct RenderMicrophonePermissionProvider:
    MicrophonePermissionProviding
{
    func authorizationStatus() async -> MicrophonePermissionState {
        .notDetermined
    }

    func requestAccess() async -> Bool {
        false
    }
}

@MainActor
private final class RenderSettingsStore: AppSettingsStoring {
    private var settings: AppSettings

    init(settings: AppSettings) {
        self.settings = settings
    }

    func load() -> AppSettings {
        settings
    }

    func save(_ settings: AppSettings) {
        self.settings = settings
    }
}

private func writeQACapture(
    _ bitmap: NSBitmapImageRep,
    named filename: String
) throws {
    guard CaptureArtifacts.isEnabled else {
        return
    }
    let data = try #require(bitmap.tiffRepresentation)
    try CaptureArtifacts.shared.write(data, named: filename)
}

@Test @MainActor
func englishReadyAndRunningUseCompactRowsWhileLongCopyStaysExpanded() {
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

    #expect(ready.inboundDirection == "Other → Chinese")
    #expect(ready.outboundDirection == "Chinese → German")
    #expect(running.inbound.actionTitle == "Play original")
    #expect(running.outbound.actionTitle == "Send original")
    #expect(inboundBypassed.inbound.actionTitle == "Resume translation")
    #expect(outboundBypassed.outbound.actionTitle == "Resume translation")
    #expect(reconnecting.inbound.status == "Reconnecting (attempt 12)")
    #expect(sameLanguage.status == "Same-language pass-through")

    let compactCases: [(String, TranslationChannelPresentation)] = [
        (ready.inboundDirection, ready.inbound),
        (ready.outboundDirection, ready.outbound),
        (running.inboundDirection, running.inbound),
        (running.outboundDirection, running.outbound),
    ]
    let expandedCases: [(String, TranslationChannelPresentation)] = [
        (inboundBypassed.inboundDirection, inboundBypassed.inbound),
        (outboundBypassed.outboundDirection, outboundBypassed.outbound),
        (reconnecting.inboundDirection, reconnecting.inbound),
        (reconnecting.outboundDirection, reconnecting.outbound),
        (inboundFailed.inboundDirection, inboundFailed.inbound),
        (outboundFailed.outboundDirection, outboundFailed.outbound),
        ("English → English", sameLanguage),
    ]

    for item in compactCases {
        #expect(
            EMKEChannelRowLayoutDecision.resolve(
                interfaceLanguage: copy.language,
                direction: item.0,
                status: item.1.status,
                statusSymbol: item.1.statusSymbol,
                actionTitle: item.1.actionTitle,
                isBlockingFailure: item.1.isBlockingFailure
            ) == .compact
        )
    }
    for item in expandedCases {
        #expect(
            EMKEChannelRowLayoutDecision.resolve(
                interfaceLanguage: copy.language,
                direction: item.0,
                status: item.1.status,
                statusSymbol: item.1.statusSymbol,
                actionTitle: item.1.actionTitle,
                isBlockingFailure: item.1.isBlockingFailure
            ) == .expanded
        )
    }
}

@Test
func englishCompactColumnProfileFitsDashboardContentWidth() {
    let profile = EMKEChannelCompactLayoutProfile.resolve(
        interfaceLanguage: .english
    )
    let availableWidth = EMKEVisualStyle.panelWidth
        - EMKEDashboardMetrics.leadingPadding
        - EMKEDashboardMetrics.trailingPadding

    #expect(profile.descriptionWidth == 125)
    #expect(profile.statusWidth == WaveformBarLayout.compactRequiredWidth)
    #expect(profile.actionWidth == 78)
    #expect(profile.horizontalSpacing == 8)
    #expect(profile.statusWidth >= WaveformBarLayout.compactRequiredWidth)
    #expect(profile.totalWidth == availableWidth)
    #expect(profile.totalWidth <= 374)
}

@Test @MainActor
func englishReadyCompactRowsCenterDescriptionAndStatusBlocks() throws {
    let copy = AppCopy(language: .english)
    let profile = EMKEChannelCompactLayoutProfile.resolve(
        interfaceLanguage: copy.language
    )
    let bitmap = try dashboardBitmap(
        value: DashboardFixture.ready.makePresentation(copy: copy),
        copy: copy,
        languagesLocked: false
    )
    let separatorRows = renderedSeparatorRows(in: bitmap)
    try #require(separatorRows.count >= 3)
    let channelTop = separatorRows[1]
    let channelMiddle = separatorRows[2]
    let rowPixelHeight = channelMiddle - channelTop - 1
    let rowRanges = [
        (channelTop + 1)..<channelMiddle,
        (channelMiddle + 1)..<(channelMiddle + 1 + rowPixelHeight),
    ]
    let scale = EMKEVisualStyle.captureScale
    let contentStartX = Int(
        EMKEDashboardMetrics.leadingPadding * scale
    )
    let contentEndX = bitmap.pixelsWide - Int(
        EMKEDashboardMetrics.trailingPadding * scale
    )
    let descriptionStartX = contentStartX + Int(
        (
            EMKEChannelMetrics.iconWidth
                + 8
        ) * scale
    )
    let descriptionEndX = descriptionStartX + Int(
        profile.descriptionWidth * scale
    )
    let statusStartX = descriptionEndX + Int(
        profile.horizontalSpacing * scale
    )
    let statusEndX = statusStartX + Int(profile.statusWidth * scale)
    let actionStartX = statusEndX + Int(
        profile.horizontalSpacing * scale
    )

    for rowRange in rowRanges {
        let descriptionInk = try #require(
            renderedInkBounds(
                in: bitmap,
                xRange: descriptionStartX..<descriptionEndX,
                yRange: rowRange
            )
        )
        let statusInk = try #require(
            renderedInkBounds(
                in: bitmap,
                xRange: statusStartX..<statusEndX,
                yRange: rowRange
            )
        )
        let actionInk = try #require(
            renderedInkBounds(
                in: bitmap,
                xRange: actionStartX..<contentEndX,
                yRange: rowRange
            )
        )

        #expect(abs(descriptionInk.midY - statusInk.midY) <= 4)
        #expect(actionInk.maxX >= CGFloat(contentEndX - 6))
        #expect(actionInk.maxX < CGFloat(contentEndX))
    }
}

@Test @MainActor
func conciseChineseChannelCopyKeepsApprovedCompactLayout() {
    let copy = AppCopy(language: .zhHans)
    let ready = DashboardFixture.ready.makePresentation(copy: copy)

    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: ready.inboundDirection,
            status: ready.inbound.status,
            statusSymbol: ready.inbound.statusSymbol,
            actionTitle: ready.inbound.actionTitle,
            isBlockingFailure: ready.inbound.isBlockingFailure
        ) == .compact
    )
    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: ready.outboundDirection,
            status: ready.outbound.status,
            statusSymbol: ready.outbound.statusSymbol,
            actionTitle: ready.outbound.actionTitle,
            isBlockingFailure: ready.outbound.isBlockingFailure
        ) == .compact
    )
}

@Test @MainActor
func everyChineseDashboardFixtureKeepsLegacyChannelSlotPolicy() {
    let copy = AppCopy(language: .zhHans)
    let fixtures = [
        DashboardFixture.unconfigured,
        .ready,
        .connecting,
        .running,
        .inboundFailed,
        .outboundFailed,
        .inboundBypassed,
        .outboundBypassed,
        .stopping,
    ]

    for fixture in fixtures {
        let value = fixture.makePresentation(copy: copy)
        let inboundMode = EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: value.inboundDirection,
            status: value.inbound.status,
            statusSymbol: value.inbound.statusSymbol,
            actionTitle: value.inbound.actionTitle,
            isBlockingFailure: value.inbound.isBlockingFailure
        )
        let outboundMode = EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: value.outboundDirection,
            status: value.outbound.status,
            statusSymbol: value.outbound.statusSymbol,
            actionTitle: value.outbound.actionTitle,
            isBlockingFailure: value.outbound.isBlockingFailure
        )

        #expect(
            !EMKEDashboardChannelSlotPolicy.usesEqualExpandedSlots(
                interfaceLanguage: copy.language,
                inboundMode: inboundMode,
                outboundMode: outboundMode
            )
        )
    }

    #expect(
        !EMKEDashboardChannelSlotPolicy.usesEqualExpandedSlots(
            interfaceLanguage: .zhHans,
            inboundMode: .expanded,
            outboundMode: .expanded
        )
    )
    #expect(
        EMKEDashboardChannelSlotPolicy.usesEqualExpandedSlots(
            interfaceLanguage: .english,
            inboundMode: .expanded,
            outboundMode: .expanded
        )
    )
    #expect(
        EMKEDashboardChannelSlotPolicy.usesEqualExpandedSlots(
            interfaceLanguage: .english,
            inboundMode: .expanded,
            outboundMode: .compact
        )
    )
}

@Test @MainActor
func chineseBlockingFailuresKeepLegacyCompactRows() {
    let copy = AppCopy(language: .zhHans)
    let inboundFailure = DashboardFixture.inboundFailed.makePresentation(
        copy: copy
    )
    let outboundFailure = DashboardFixture.outboundFailed.makePresentation(
        copy: copy
    )

    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: inboundFailure.inboundDirection,
            status: inboundFailure.inbound.status,
            statusSymbol: inboundFailure.inbound.statusSymbol,
            actionTitle: inboundFailure.inbound.actionTitle,
            isBlockingFailure: inboundFailure.inbound.isBlockingFailure
        ) == .compact
    )
    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: outboundFailure.outboundDirection,
            status: outboundFailure.outbound.status,
            statusSymbol: outboundFailure.outbound.statusSymbol,
            actionTitle: outboundFailure.outbound.actionTitle,
            isBlockingFailure: outboundFailure.outbound.isBlockingFailure
        ) == .compact
    )
}

@Test @MainActor
func chineseCompactDashboardMatchesPre84ProductionSeparatorRows() throws {
    let copy = AppCopy(language: .zhHans)
    let bitmap = try dashboardBitmap(
        value: DashboardFixture.ready.makePresentation(copy: copy),
        copy: copy,
        languagesLocked: false
    )

    #expect(renderedSeparatorRows(in: bitmap) == [436, 597, 782, 1143])
}

@Test @MainActor
func compactStatusMeasurementUsesRenderedSymbolWidthAtBoundary() {
    #expect(
        EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: .zhHans,
            direction: "A",
            status: "MMMMMMMMA",
            statusSymbol: "stop.circle",
            actionTitle: "Go"
        ) == .expanded
    )
}

@Test @MainActor
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

@Test @MainActor
func dashboardChannelBudgetSplitsIntoTwoEqualRowSlots() {
    let panel = EMKEDashboardVerticalLayoutGeometry(
        panelHeight: EMKEVisualStyle.panelHeight
    )

    for hasErrorText in [false, true] {
        let budget = panel.channelSectionHeightBudget(
            hasErrorText: hasErrorText
        )
        let slotHeight = panel.channelRowSlotHeight(
            hasErrorText: hasErrorText
        )

        #expect(slotHeight > 0)
        #expect(
            (slotHeight * 2) + EMKEVisualStyle.separatorThickness
                == budget
        )
    }
}

@Test @MainActor
func expandedWaveformMapsToFullDashboardContentCenter() {
    let copy = AppCopy(language: .english)
    let ready = DashboardFixture.ready.makePresentation(copy: copy)
    let layout = EMKEExpandedChannelLayoutGeometry.resolve(
        title: copy.text(.heardByMe),
        direction: ready.inboundDirection,
        status: ready.inbound.status,
        statusSymbol: ready.inbound.statusSymbol,
        actionTitle: ready.inbound.actionTitle
    )
    let availableRowWidth = EMKEVisualStyle.panelWidth
        - EMKEDashboardMetrics.leadingPadding
        - EMKEDashboardMetrics.trailingPadding
    let waveformCenterInRow = EMKEChannelMetrics.iconWidth
        + EMKEChannelMetrics.expandedHorizontalSpacing
        + layout.waveformFrame.midX

    #expect(waveformCenterInRow == availableRowWidth / 2)
}

@Test(arguments: EnglishDashboardStressCase.longCopyCases)
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
    let availableRowBounds = CGRect(
        x: 0,
        y: 0,
        width: EMKEVisualStyle.panelWidth
            - EMKEDashboardMetrics.leadingPadding
            - EMKEDashboardMetrics.trailingPadding,
        height: dashboardGeometry.channelRowSlotHeight(
            hasErrorText: hasErrorText
        )
    )

    for layout in layouts {
        #expect(layout.contentBounds.contains(layout.directionFrame))
        #expect(layout.contentBounds.contains(layout.statusFrame))
        #expect(layout.contentBounds.contains(layout.actionFrame))
        let waveformFrameInRow = layout.waveformFrame.offsetBy(
            dx: EMKEChannelMetrics.iconWidth
                + EMKEChannelMetrics.expandedHorizontalSpacing,
            dy: 0
        )
        #expect(availableRowBounds.contains(waveformFrameInRow))
        #expect(waveformFrameInRow.midX == availableRowBounds.midX)
        #expect(!layout.directionFrame.intersects(layout.statusFrame))
        #expect(!layout.directionFrame.intersects(layout.actionFrame))
        #expect(!layout.statusFrame.intersects(layout.actionFrame))
        #expect(!layout.statusFrame.intersects(layout.waveformFrame))
        #expect(!layout.actionFrame.intersects(layout.waveformFrame))
        #expect(layout.statusFrame.height >= layout.statusContentHeight)
        #expect(layout.actionFrame.height >= layout.actionContentHeight)
        #expect(
            layout.requiredHeight
                <= dashboardGeometry.channelRowSlotHeight(
                    hasErrorText: hasErrorText
                )
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

@Test(arguments: EnglishDashboardStressCase.allCases)
@MainActor
private func englishStressRendersBoundedCopyAndTrailingActions(
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
    let separatorRows = renderedSeparatorRows(in: bitmap)
    try #require(separatorRows.count >= 3)
    let channelTop = separatorRows[1]
    let channelMiddle = separatorRows[2]
    let rowPixelHeight = channelMiddle - channelTop - 1
    let rowRanges = [
        (channelTop + 1)..<channelMiddle,
        (channelMiddle + 1)..<(channelMiddle + 1 + rowPixelHeight),
    ]
    let contentStartX = Int(
        EMKEDashboardMetrics.leadingPadding
            * EMKEVisualStyle.captureScale
    )
    let contentEndX = bitmap.pixelsWide - Int(
        EMKEDashboardMetrics.trailingPadding
            * EMKEVisualStyle.captureScale
    )
    let actionStartX = Int(CGFloat(contentEndX) * 0.70)

    for rowRange in rowRanges {
        let rowInk = try #require(
            renderedInkBounds(
                in: bitmap,
                xRange: contentStartX..<contentEndX,
                yRange: rowRange
            )
        )
        let actionInk = try #require(
            renderedInkBounds(
                in: bitmap,
                xRange: actionStartX..<contentEndX,
                yRange: rowRange
            )
        )

        #expect(rowInk.minY > CGFloat(rowRange.lowerBound))
        #expect(rowInk.maxY < CGFloat(rowRange.upperBound))
        #expect(actionInk.minY > CGFloat(rowRange.lowerBound))
        #expect(actionInk.maxY < CGFloat(rowRange.upperBound))
        #expect(actionInk.maxX < CGFloat(contentEndX))
        #expect(actionInk.maxX >= CGFloat(contentEndX - 6))
    }

    let independentContentWidth = EMKEVisualStyle.panelWidth
        - EMKEDashboardMetrics.leadingPadding
        - EMKEDashboardMetrics.trailingPadding
        - EMKEChannelMetrics.iconWidth
        - EMKEChannelMetrics.expandedHorizontalSpacing
    let independentActionWidth = (
        independentContentWidth
            - (EMKEChannelMetrics.expandedCopySpacing * 3)
    ) * 0.45
    let channelCopies = [
        (
            fixture.copy.text(.heardByMe),
            fixture.value.inboundDirection,
            fixture.value.inbound.status,
            fixture.value.inbound.statusSymbol,
            fixture.value.inbound.actionTitle
        ),
        (
            fixture.copy.text(.heardByOther),
            fixture.value.outboundDirection,
            fixture.value.outbound.status,
            fixture.value.outbound.statusSymbol,
            fixture.value.outbound.actionTitle
        ),
    ]
    let independentSlotHeight = CGFloat(rowPixelHeight)
        / EMKEVisualStyle.captureScale

    for item in channelCopies {
        let titleHeight = independentTextHeight(
            item.0,
            font: .systemFont(
                ofSize: EMKEChannelMetrics.titleSize,
                weight: .semibold
            ),
            width: independentContentWidth
        )
        let directionHeight = independentTextHeight(
            item.1,
            font: .systemFont(
                ofSize: EMKEChannelMetrics.directionSize
            ),
            width: independentContentWidth
        )
        let naturalActionWidth = (item.4 as NSString).size(
            withAttributes: [
                .font: NSFont.systemFont(
                    ofSize: EMKEChannelMetrics.actionSize
                ),
            ]
        ).width
        let actionWidth = min(
            naturalActionWidth,
            independentActionWidth
        )
        let statusSymbolSize = NSImage(
            systemSymbolName: item.3,
            accessibilityDescription: nil
        )?
            .withSymbolConfiguration(
                NSImage.SymbolConfiguration(
                    pointSize: EMKEChannelMetrics.statusIconSize,
                    weight: .medium
                )
            )?
            .size
            ?? NSSize(
                width: EMKEChannelMetrics.statusIconSize + 3,
                height: EMKEChannelMetrics.statusIconSize
            )
        let independentStatusWidth = independentContentWidth
            - (EMKEChannelMetrics.expandedCopySpacing * 3)
            - actionWidth
        let statusTextWidth = independentStatusWidth
            - statusSymbolSize.width
            - EMKEChannelMetrics.statusIconSpacing
        let statusHeight = max(
            statusSymbolSize.height,
            independentTextHeight(
                item.2,
                font: .systemFont(ofSize: 12),
                width: statusTextWidth
            )
        )
        let actionHeight = independentTextHeight(
            item.4,
            font: .systemFont(
                ofSize: EMKEChannelMetrics.actionSize
            ),
            width: actionWidth
        )
        let singleStatusHeight = independentTextHeight(
            "Hg",
            font: .systemFont(ofSize: 12),
            width: .greatestFiniteMagnitude
        )
        let singleActionHeight = independentTextHeight(
            "Hg",
            font: .systemFont(
                ofSize: EMKEChannelMetrics.actionSize
            ),
            width: .greatestFiniteMagnitude
        )
        let independentVerticalSpacing = (
            statusHeight > max(
                statusSymbolSize.height,
                singleStatusHeight
            )
                || actionHeight > singleActionHeight
        )
            ? EMKEChannelMetrics.expandedMultilineCopySpacing
            : EMKEChannelMetrics.expandedCopySpacing
        let independentlyRequiredHeight = titleHeight
            + 4
            + directionHeight
            + independentVerticalSpacing
            + max(statusHeight, actionHeight)
            + independentVerticalSpacing
            + EMKEChannelMetrics.expandedWaveformHeight

        #expect(
            titleHeight + 4 + directionHeight
                <= EMKEChannelMetrics.expandedMaximumRowHeight / 2
        )
        #expect(naturalActionWidth <= independentActionWidth)
        #expect(statusTextWidth > 0)
        #expect(statusHeight <= singleStatusHeight * 2)
        #expect(independentlyRequiredHeight <= independentSlotHeight)
    }
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

private func renderedSeparatorRows(
    in bitmap: NSBitmapImageRep
) -> [Int] {
    let startX = Int(
        EMKEDashboardMetrics.leadingPadding
            * EMKEVisualStyle.captureScale
    )
    let endX = bitmap.pixelsWide - Int(
        EMKEDashboardMetrics.trailingPadding
            * EMKEVisualStyle.captureScale
    )

    return (0..<bitmap.pixelsHigh).filter { y in
        (startX..<endX).allSatisfy { x in
            guard let color = bitmap.colorAt(x: x, y: y)?
                .usingColorSpace(.deviceRGB)
            else {
                return false
            }

            return (0.90..<0.95).contains(color.redComponent)
                && (0.90..<0.95).contains(color.greenComponent)
                && (0.90..<0.95).contains(color.blueComponent)
        }
    }
}

private func renderedInkBounds(
    in bitmap: NSBitmapImageRep,
    xRange: Range<Int>,
    yRange: Range<Int>
) -> CGRect? {
    var minX = Int.max
    var maxX = Int.min
    var minY = Int.max
    var maxY = Int.min

    for y in yRange {
        for x in xRange {
            guard let color = bitmap.colorAt(x: x, y: y)?
                .usingColorSpace(.deviceRGB)
            else {
                continue
            }
            let hasVisibleInk = color.redComponent < 0.96
                || color.greenComponent < 0.96
                || color.blueComponent < 0.96
            guard hasVisibleInk else {
                continue
            }

            minX = min(minX, x)
            maxX = max(maxX, x)
            minY = min(minY, y)
            maxY = max(maxY, y)
        }
    }

    guard minX <= maxX, minY <= maxY else {
        return nil
    }

    return CGRect(
        x: minX,
        y: minY,
        width: maxX - minX + 1,
        height: maxY - minY + 1
    )
}

private func independentTextHeight(
    _ text: String,
    font: NSFont,
    width: CGFloat
) -> CGFloat {
    ceil(
        (text as NSString).boundingRect(
            with: NSSize(
                width: width,
                height: .greatestFiniteMagnitude
            ),
            options: [
                .usesLineFragmentOrigin,
                .usesFontLeading,
            ],
            attributes: [
                .font: font,
            ]
        ).height
    )
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

    static let longCopyCases: [Self] = [
        .manualBypass,
        .reconnecting,
        .sameLanguagePassThrough,
        .inboundFailed,
        .outboundFailed,
    ]

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
