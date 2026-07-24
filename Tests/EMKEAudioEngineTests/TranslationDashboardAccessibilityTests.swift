import AppKit
import Foundation
import Testing
@testable import EMKEMenuBarApp

@Test
func runningWaveformRetainsTargetDynamicRange() {
    let heights = WaveformBarLayout.heights(
        level: 0.68,
        maximum: 92
    )

    #expect(heights.min() ?? .infinity <= 12)
    #expect(heights.max() ?? 0 >= 62)
}

@Test
func compactWaveformFitsItsChannelColumn() throws {
    #expect(WaveformBarLayout.compactRequiredWidth <= EMKEChannelMetrics.statusWidth)
}

@Test
func reduceMotionDoesNotAttachAnExplicitAnimation() throws {
    let source = try sourceFile(named: "LiveWaveformView.swift")

    #expect(source.contains("if reduceMotion"))
    #expect(!source.contains("reduceMotion ? nil :"))
}

@Test
func floatingCapsuleUsesAccessibleRealSessionContent() throws {
    let source = try sourceFile(named: "FloatingTranslationStatusView.swift")

    #expect(source.contains("level: presentation.level"))
    #expect(source.contains("maximumHeight: 24"))
    #expect(source.contains("compact: true"))
    #expect(source.contains("minimumBarHeight: 0.5"))
    #expect(source.contains(".environment(\\.colorScheme, .dark)"))
    #expect(source.contains(".accessibilityHidden(true)"))
    #expect(source.contains(".accessibilityElement(children: .combine)"))
    #expect(
        source.contains(
            ".accessibilityLabel(presentation.statusAccessibilityLabel)"
        )
    )
    #expect(
        source.contains(
            ".accessibilityValue(presentation.elapsed ?? \"\")"
        )
    )
    #expect(
        source.contains(
            ".accessibilityLabel(presentation.stopAccessibilityLabel)"
        )
    )
    #expect(source.contains(".disabled(!presentation.stopEnabled)"))
}

@Test
func floatingWaveformUsesASeparateNearFlatSilenceBaseline() throws {
    let source = try sourceFile(named: "LiveWaveformView.swift")

    #expect(source.contains("var minimumBarHeight: CGFloat = 4"))
    #expect(source.contains("minimum: Double(minimumBarHeight)"))
}

@Test
func floatingCapsulePulseRespectsPresentationAndReduceMotion() throws {
    let source = try sourceFile(named: "FloatingTranslationStatusView.swift")

    #expect(
        source.contains(
            "presentation.showsActivityPulse && !reduceMotion"
        )
    )
    #expect(!source.contains("presentation.tone == .neutral"))
    #expect(source.contains(".easeOut(duration: 1)"))
    #expect(source.contains(".repeatForever(autoreverses: false)"))
    #expect(source.contains(".onDisappear"))
    #expect(source.contains("isPulsing = false"))
}

@Test
func dashboardIconsAndStatusExposeAccessibleCopy() throws {
    let dashboard = try sourceFile(named: "TranslationDashboardView.swift")
    let channel = try sourceFile(named: "TranslationChannelRow.swift")

    #expect(dashboard.contains(".accessibilityLabel(copy.text(.openSettings))"))
    #expect(dashboard.contains(".accessibilityLabel(value.privacyText)"))
    #expect(
        dashboard.contains(
            "Image(systemName: value.primaryStatusSymbol)"
        )
    )
    #expect(
        dashboard.contains(
            ".accessibilityLabel(copy.translationStatus(value.primaryStatus))"
        )
    )
    #expect(channel.contains(".accessibilityHidden(true)"))
    #expect(channel.contains("copy.channelStatus("))
}

@Test
func channelRowsUseMeasuredExpandedFallbackForLongCopy() throws {
    let channel = try sourceFile(named: "TranslationChannelRow.swift")

    #expect(channel.contains("EMKEChannelRowLayoutDecision.resolve("))
    #expect(channel.contains("expandedBody"))
    #expect(!channel.contains(".lineLimit(1)"))
    #expect(!channel.contains(".minimumScaleFactor("))
    #expect(!channel.contains(".scaleEffect("))
}

@Test
func settingsShowsVisibleLockedState() throws {
    let source = try sourceFile(named: "TranslationSettingsView.swift")

    #expect(source.contains("if model.selectionsLocked"))
    #expect(source.contains("copy.text(.translationSettingsLocked)"))
    #expect(source.contains(".accessibilityLabel(copy.text(.backToDashboard))"))
}

@Test
func settingsAudioControlsMatchLanguageMenusAndExposeLocalDiagnostics() throws {
    let source = try sourceFile(named: "TranslationSettingsView.swift")

    #expect(source.contains("AudioDeviceMenuButton("))
    #expect(!source.contains("Picker("))
    #expect(source.contains("LiveWaveformView("))
    #expect(source.contains("copy.text(.testMicrophone)"))
    #expect(source.contains("copy.text(.playTestTone)"))
    #expect(source.contains("succeeded: model.audioInputDiagnosticSucceeded"))
    #expect(source.contains("succeeded: model.audioOutputDiagnosticSucceeded"))
}

@Test
func settingsUsesLocalizedInterfaceMenuAndStyledQuitButton() throws {
    let settings = try sourceFile(named: "TranslationSettingsView.swift")

    #expect(settings.contains("InterfaceLanguageMenuButton("))
    #expect(settings.contains("Label(copy.text(.quitEMKE), systemImage: \"power\")"))
    #expect(settings.contains(".frame(maxWidth: .infinity, minHeight: 40)"))
    #expect(!settings.contains("Button(\"退出 EMKE\")"))
}

@Test
func dashboardUsesLocalizedCopyInsteadOfChineseViewLiterals() throws {
    let dashboard = try sourceFile(named: "TranslationDashboardView.swift")

    #expect(dashboard.contains("copy.text(.openSettings)"))
    #expect(dashboard.contains("copy.languageName(language)"))
    #expect(!dashboard.contains(".accessibilityLabel(\"打开设置\")"))
    #expect(!dashboard.contains("Text(\"我的母语\")"))
    #expect(!dashboard.contains("Text(\"会议输出\")"))
}

@Test
func dashboardUsesSemanticPhysicalPixelSeparators() throws {
    let source = try sourceFile(named: "TranslationDashboardView.swift")
    let separatorCount = source.components(
        separatedBy: "EMKEDashboardSeparator()"
    ).count - 1

    #expect(separatorCount == 4)
    #expect(EMKEVisualStyle.separatorThickness == 0.5)
    #expect(!source.contains("Divider().opacity(EMKEVisualStyle.dividerOpacity)"))
}

@Test
func dashboardSeparatorRemainsVisibleAndSubtleInBothAppearances() throws {
    for appearanceName in [NSAppearance.Name.aqua, .darkAqua] {
        let appearance = try #require(NSAppearance(named: appearanceName))
        var resolvedContrast: Double?
        appearance.performAsCurrentDrawingAppearance {
            guard
                let separator = NSColor.separatorColor.usingColorSpace(.sRGB),
                let background = NSColor.windowBackgroundColor.usingColorSpace(.sRGB)
            else { return }
            resolvedContrast = compositedContrast(separator, over: background)
        }
        let contrast = try #require(resolvedContrast)
        #expect(contrast >= 1.15)
        #expect(contrast <= 1.5)
    }
}

@Test
func lockedLanguagesRemainLegibleWithoutAnEnabledControlStyle() throws {
    let source = try sourceFile(named: "TranslationDashboardView.swift")

    #expect(source.contains("if languagesLocked"))
    #expect(source.contains("lockedLanguageValue"))
    #expect(source.contains("copy.text(.languageLockedHint)"))
}

@Test
func editableLanguagesUseTheSameReferenceScaleAsLockedLanguages() throws {
    let source = try sourceFile(named: "TranslationDashboardView.swift")

    #expect(source.contains("LanguageMenuButton("))
    #expect(source.contains(".popover(isPresented:"))
    #expect(source.contains("LanguageValueLabel("))
}

@Test
func menuBarUsesTheApprovedLogoInsteadOfAStatusSymbol() throws {
    let source = try sourceFile(named: "EMKEMenuBarApp.swift")

    #expect(source.contains("MenuBarLogo.image"))
    #expect(!source.contains("systemImage: model.systemImage"))
}

@Test
func menuBarAppSharesOneModelWithFloatingPanel() throws {
    let source = try sourceFile(named: "EMKEMenuBarApp.swift")

    #expect(
        source.components(
            separatedBy: "let model = MenuBarModel("
        ).count - 1 == 1
    )
    #expect(source.contains("deferInitialDeviceReload: true"))
    #expect(source.contains("_model = StateObject(wrappedValue: model)"))
    #expect(
        source.contains(
            "FloatingTranslationPanelController(model: model)"
        )
    )
    #expect(source.contains("MenuBarRootView(model: model)"))
}

@Test
func floatingPanelHostsTheSharedModelAndWiresStop() throws {
    let source = try sourceFile(
        named: "FloatingTranslationPanelController.swift"
    )

    #expect(source.contains("@ObservedObject var model: MenuBarModel"))
    #expect(
        source.contains(
            "FloatingTranslationPanelContentMode.resolve(presentation)"
        )
    )
    #expect(source.contains("case .static:"))
    #expect(source.contains("Color.clear"))
    #expect(source.contains("case .live:"))
    #expect(source.contains("TimelineView(.periodic(from: .now, by: 1))"))
    #expect(
        source.contains(
            "model.floatingPresentation(at: context.date)"
        )
    )
    #expect(source.contains("Task { await model.stop() }"))
}

@Test
func floatingPanelLifecycleCoalescesRefreshAndPlacesOnlyOnce() throws {
    let source = try sourceFile(
        named: "FloatingTranslationPanelController.swift"
    )

    #expect(!source.contains("model.objectWillChange"))
    #expect(source.contains("model.$isStarting"))
    #expect(source.contains("model.$isStopping"))
    #expect(source.contains("model.$coordinatorState.map(\\.isRunning)"))
    #expect(source.contains(".removeDuplicates()"))
    #expect(source.contains("await Task.yield()"))
    #expect(source.contains("refreshTask?.cancel()"))
    #expect(source.contains("scheduleRefresh(to desiredVisibility: Bool)"))
    #expect(source.contains("guard !hasPlacedPanel"))
    #expect(source.contains("orderFrontRegardless()"))
    #expect(source.contains("orderOut(nil)"))
    #expect(source.contains("setFloatingWindowVisible(desiredVisibility)"))
    #expect(source.contains("deinit"))
    #expect(source.contains("visibilityObservation?.cancel()"))
    #expect(source.contains("visibilitySyncTask?.cancel()"))
    #expect(
        source.contains(
            "resetFloatingVisibilityAfterTeardown(model: model)"
        )
    )
    #expect(!source.contains("nonisolated(unsafe)"))
}

@Test @MainActor
func approvedMenuBarLogoUsesTemplateImageSizing() {
    #expect(MenuBarLogo.image.size == NSSize(width: 18, height: 18))
    #expect(MenuBarLogo.image.isTemplate)
}

@Test
func channelRowsReserveVisualColumnsForIconAndStatus() {
    #expect(EMKEChannelMetrics.iconWidth == 48)
    #expect(EMKEChannelMetrics.statusWidth == 105)
    #expect(EMKEChannelMetrics.verticalPadding == 23.5)
    #expect(EMKEChannelMetrics.iconSize == 35)
    #expect(EMKEChannelMetrics.statusIconSize == 9)
    #expect(EMKEChannelMetrics.actionOffsetY == 14)
}

@Test
func compactWaveformMatchesConfirmedReferenceScale() {
    #expect(WaveformBarLayout.compactRequiredWidth >= 98)
    #expect(WaveformBarLayout.compactRequiredWidth <= 101)
}

@Test
func dashboardHeaderMatchesConfirmedReferenceScale() {
    #expect(EMKEDashboardMetrics.headerTitleSize == 13)
    #expect(EMKEDashboardMetrics.gearSize == 19)
    #expect(EMKEDashboardMetrics.gearOffsetX == 6)
    #expect(EMKEDashboardMetrics.headerOffsetY == 4)
}

@Test
func dashboardMatchesMeasuredPassSixSlots() {
    #expect(EMKEDashboardMetrics.topSpacer == 48)
    #expect(EMKEDashboardMetrics.lowerSpacer == 28)
    #expect(EMKEDashboardMetrics.waveformMaximumHeight == 95)
    #expect(EMKEDashboardMetrics.waveformOffsetY == 5)
    #expect(EMKEDashboardMetrics.inputLanguageInset == 52)
    #expect(EMKEDashboardMetrics.outputLanguageInset == 45)
    #expect(EMKEDashboardMetrics.directionArrowSize == 17)
    #expect(EMKEDashboardMetrics.languageVerticalPadding == 17.5)
    #expect(EMKEDashboardMetrics.statusTopPadding == 4)
    #expect(EMKEDashboardMetrics.topPadding == 18)
    #expect(EMKEDashboardMetrics.leadingPadding == 22)
    #expect(EMKEDashboardMetrics.trailingPadding == 24)
    #expect(EMKEChannelMetrics.titleSize == 17)
    #expect(EMKEChannelMetrics.directionSize == 14)
    #expect(EMKEChannelMetrics.actionSize == 12.5)
    #expect(EMKEVisualStyle.primaryButtonHeight == 45)
}

@Test
func privacyFooterHasItsOwnVisualBoundary() throws {
    let source = try sourceFile(named: "TranslationDashboardView.swift")

    #expect(source.contains("primaryActionButton"))
    #expect(source.contains("EMKEDashboardSeparator()"))
    #expect(source.contains("Image(systemName: \"lock\")"))
    #expect(EMKEDashboardMetrics.privacyOffsetX == -5)
    #expect(EMKEDashboardMetrics.footerDividerTopPadding == 20)
    #expect(EMKEDashboardMetrics.privacyTopPadding == 12)
}

private func sourceFile(named name: String) throws -> String {
    let repositoryRoot = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
    let url = repositoryRoot
        .appendingPathComponent("Sources/EMKEMenuBarApp")
        .appendingPathComponent(name)
    return try String(contentsOf: url, encoding: .utf8)
}

private func compositedContrast(_ foreground: NSColor, over background: NSColor) -> Double {
    let alpha = foreground.alphaComponent
    let red = (foreground.redComponent * alpha) + (background.redComponent * (1 - alpha))
    let green = (foreground.greenComponent * alpha) + (background.greenComponent * (1 - alpha))
    let blue = (foreground.blueComponent * alpha) + (background.blueComponent * (1 - alpha))
    let foregroundLuminance = relativeLuminance(red: red, green: green, blue: blue)
    let backgroundLuminance = relativeLuminance(
        red: background.redComponent,
        green: background.greenComponent,
        blue: background.blueComponent
    )
    let lighter = max(foregroundLuminance, backgroundLuminance)
    let darker = min(foregroundLuminance, backgroundLuminance)
    return (lighter + 0.05) / (darker + 0.05)
}

private func relativeLuminance(red: Double, green: Double, blue: Double) -> Double {
    func linearize(_ component: Double) -> Double {
        component <= 0.04045
            ? component / 12.92
            : pow((component + 0.055) / 1.055, 2.4)
    }
    return (0.2126 * linearize(red))
        + (0.7152 * linearize(green))
        + (0.0722 * linearize(blue))
}
