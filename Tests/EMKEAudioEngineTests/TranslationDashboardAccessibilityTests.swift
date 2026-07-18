import Foundation
import Testing
@testable import EMKEMenuBarApp

@Test
func runningWaveformRetainsTargetDynamicRange() {
    let heights = WaveformBarLayout.heights(
        level: 0.68,
        maximum: 72
    )

    #expect(heights.min() ?? .infinity <= 12)
    #expect(heights.max() ?? 0 >= 48)
}

@Test
func compactWaveformFitsItsChannelColumn() throws {
    let source = try sourceFile(named: "LiveWaveformView.swift")
    let spacing = try compactMetric(
        matching: #"spacing:\s*compact\s*\?\s*([0-9.]+)\s*:\s*[0-9.]+"#,
        in: source
    )
    let width = try compactMetric(
        matching: #"width:\s*compact\s*\?\s*([0-9.]+)\s*:\s*[0-9.]+"#,
        in: source
    )

    let requiredWidth = (24 * width) + (23 * spacing)
    #expect(requiredWidth <= 90)
}

@Test
func reduceMotionDoesNotAttachAnExplicitAnimation() throws {
    let source = try sourceFile(named: "LiveWaveformView.swift")

    #expect(source.contains("if reduceMotion"))
    #expect(!source.contains("reduceMotion ? nil :"))
}

@Test
func dashboardIconsAndStatusExposeAccessibleCopy() throws {
    let dashboard = try sourceFile(named: "TranslationDashboardView.swift")
    let channel = try sourceFile(named: "TranslationChannelRow.swift")

    #expect(dashboard.contains(".accessibilityLabel(\"打开设置\")"))
    #expect(dashboard.contains(".accessibilityLabel(value.privacyText)"))
    #expect(channel.contains(".accessibilityHidden(true)"))
    #expect(
        channel.contains(#""\(title)状态：\(presentation.status)""#)
    )
}

@Test
func settingsShowsVisibleLockedState() throws {
    let source = try sourceFile(named: "TranslationSettingsView.swift")

    #expect(source.contains("if model.selectionsLocked"))
    #expect(
        source.contains(
            "Label(\"翻译运行期间设置已锁定\", systemImage: \"lock.fill\")"
        )
    )
    #expect(source.contains(".accessibilityLabel(\"返回翻译控制台\")"))
}

@Test
func dashboardKeepsAllSixVisualZonesSeparated() throws {
    let source = try sourceFile(named: "TranslationDashboardView.swift")
    let dividerCount = source.components(
        separatedBy: "Divider().opacity(EMKEVisualStyle.dividerOpacity)"
    ).count - 1

    #expect(dividerCount == 4)
}

@Test
func lockedLanguagesRemainLegibleWithoutAnEnabledControlStyle() throws {
    let source = try sourceFile(named: "TranslationDashboardView.swift")

    #expect(source.contains("if languagesLocked"))
    #expect(source.contains("lockedLanguageValue"))
    #expect(source.contains("翻译运行期间不可修改"))
}

@Test
func channelRowsReserveVisualColumnsForIconAndStatus() throws {
    let source = try sourceFile(named: "TranslationChannelRow.swift")

    #expect(source.contains(".frame(width: 48)"))
    #expect(source.contains("private var channelStatus"))
    #expect(source.contains(".frame(width: 90)"))
}

@Test
func compactWaveformMatchesConfirmedReferenceScale() throws {
    let source = try sourceFile(named: "LiveWaveformView.swift")
    let spacing = try compactMetric(
        matching: #"spacing:\s*compact\s*\?\s*([0-9.]+)\s*:\s*[0-9.]+"#,
        in: source
    )
    let width = try compactMetric(
        matching: #"width:\s*compact\s*\?\s*([0-9.]+)\s*:\s*[0-9.]+"#,
        in: source
    )

    let requiredWidth = (24 * width) + (23 * spacing)
    #expect(requiredWidth >= 80)
    #expect(requiredWidth <= 90)
}

@Test
func dashboardHeaderMatchesConfirmedReferenceScale() throws {
    let source = try sourceFile(named: "TranslationDashboardView.swift")

    #expect(source.contains(".font(.system(size: 15, weight: .semibold))"))
    #expect(source.contains(".font(.system(size: 18))"))
}

@Test
func privacyFooterHasItsOwnVisualBoundary() throws {
    let source = try sourceFile(named: "TranslationDashboardView.swift")

    #expect(
        source.contains(
            "primaryActionButton\n            Divider().opacity"
        )
    )
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

private func compactMetric(
    matching pattern: String,
    in source: String
) throws -> Double {
    let expression = try NSRegularExpression(pattern: pattern)
    let range = NSRange(source.startIndex..., in: source)
    let match = try #require(expression.firstMatch(in: source, range: range))
    let valueRange = try #require(Range(match.range(at: 1), in: source))
    return try #require(Double(source[valueRange]))
}
