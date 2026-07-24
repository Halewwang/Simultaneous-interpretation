import AppKit
import SwiftUI

enum EMKEVisualStyle {
    static let panelWidth: CGFloat = 420
    static let panelHeight: CGFloat = 620
    static let captureScale: CGFloat = 2
    static let horizontalPadding: CGFloat = 24
    static let primarySpacing: CGFloat = 24
    static let groupSpacing: CGFloat = 16
    static let compactSpacing: CGFloat = 8
    static let primaryButtonHeight: CGFloat = 45
    static let dividerOpacity = 0.14
    static let separatorThickness: CGFloat = 0.5

    static let primaryText = Color.primary
    static let secondaryText = Color.secondary
    static let panelBackground = Color(nsColor: .windowBackgroundColor)
    static let separator = Color(nsColor: .separatorColor)
    static let surfaceBackground = Color(nsColor: .controlBackgroundColor)
    static let warning = Color(nsColor: .systemOrange)
    static let failure = Color(nsColor: .systemRed)
    static let activityBlue = Color(
        red: 0.25,
        green: 0.45,
        blue: 0.92
    )
}

enum EMKEDashboardMetrics {
    static let headerTitleSize: CGFloat = 13
    static let headerHeight: CGFloat = 32
    static let gearSize: CGFloat = 19
    static let gearOffsetX: CGFloat = 6
    static let headerOffsetY: CGFloat = 4
    static let flexibleSpacerCompression: CGFloat = 12
    static let topSpacer: CGFloat = 48
    static let topSpacerMinimum: CGFloat =
        topSpacer - flexibleSpacerCompression
    static let waveformMaximumHeight: CGFloat = 95
    static let waveformOffsetY: CGFloat = 5
    static let primaryStatusSize: CGFloat = 14
    static let statusTopPadding: CGFloat = 4
    static let statusOffsetY: CGFloat = 5
    static let errorTextSize: CGFloat = 11
    static let errorTextTopPadding: CGFloat = 4
    static let lowerSpacer: CGFloat = 28
    static let lowerSpacerMinimum: CGFloat =
        lowerSpacer - flexibleSpacerCompression
    static let inputLanguageInset: CGFloat = 52
    static let outputLanguageInset: CGFloat = 45
    static let languageTitleSize: CGFloat = 12
    static let languageValueSize: CGFloat = 22
    static let languageContentSpacing: CGFloat = 4
    static let directionArrowSize: CGFloat = 17
    static let languageVerticalPadding: CGFloat = 17.5
    static let leadingPadding: CGFloat = 22
    static let trailingPadding: CGFloat = 24
    static let topPadding: CGFloat = 18
    static let bottomPadding: CGFloat = 20
    static let channelToPrimarySpacer: CGFloat = 16
    static let channelToPrimarySpacerMinimum: CGFloat =
        channelToPrimarySpacer - flexibleSpacerCompression
    static let primaryActionSize: CGFloat = 16
    static let footerDividerTopPadding: CGFloat = 20
    static let privacyIconSize: CGFloat = 9
    static let privacyTextSize: CGFloat = 13
    static let privacyOffsetX: CGFloat = -5
    static let privacyTopPadding: CGFloat = 12
}

enum EMKEFloatingMetrics {
    static let width: CGFloat = 264
    static let height: CGFloat = 52
    static let cornerRadius: CGFloat = 26
    static let statusWidth: CGFloat = 72
    static let waveformWidth: CGFloat = 99
    static let stopTarget: CGFloat = 32
}

enum EMKEChannelMetrics {
    static let iconSize: CGFloat = 35
    static let iconWidth: CGFloat = 48
    static let iconOffsetX: CGFloat = -5
    static let titleSize: CGFloat = 17
    static let directionSize: CGFloat = 14
    static let directionWidth: CGFloat = 112
    static let statusIconSize: CGFloat = 9
    static let statusIconSpacing: CGFloat = 4
    static let statusWidth: CGFloat = 105
    static let statusOffsetX: CGFloat = -18
    static let statusOffsetY: CGFloat = 6
    static let meterOffsetX: CGFloat = -10
    static let actionSize: CGFloat = 12.5
    static let actionWidth: CGFloat = 64
    static let actionOffsetY: CGFloat = 14
    static let expandedHorizontalSpacing: CGFloat = 8
    static let expandedCopySpacing: CGFloat = 6
    static let expandedContentWidth: CGFloat =
        EMKEVisualStyle.panelWidth
        - EMKEDashboardMetrics.leadingPadding
        - EMKEDashboardMetrics.trailingPadding
        - iconWidth
        - expandedHorizontalSpacing
    static let expandedStatusActionSpacing: CGFloat =
        expandedCopySpacing * 3
    static let expandedWaveformHeight: CGFloat = 24
    static let expandedMaximumRowHeight: CGFloat = 112
    static let verticalPadding: CGFloat = 23.5
}

@MainActor
struct EMKEDashboardVerticalLayoutGeometry: Equatable {
    let panelHeight: CGFloat

    var primaryStatusLineHeight: CGFloat {
        Self.textLineHeight(
            size: EMKEDashboardMetrics.primaryStatusSize,
            weight: .medium
        )
    }

    var errorTextLineHeight: CGFloat {
        Self.textLineHeight(size: EMKEDashboardMetrics.errorTextSize)
    }

    var errorTextAllocationHeight: CGFloat {
        EMKEDashboardMetrics.errorTextTopPadding
            + errorTextLineHeight
    }

    var languageDirectionHeight: CGFloat {
        Self.textLineHeight(size: EMKEDashboardMetrics.languageTitleSize)
            + EMKEDashboardMetrics.languageContentSpacing
            + Self.textLineHeight(
                size: EMKEDashboardMetrics.languageValueSize,
                weight: .semibold
            )
            + (EMKEDashboardMetrics.languageVerticalPadding * 2)
    }

    var privacyTextLineHeight: CGFloat {
        Self.textLineHeight(size: EMKEDashboardMetrics.privacyTextSize)
    }

    func channelSectionHeightBudget(
        hasErrorText: Bool
    ) -> CGFloat {
        max(
            panelHeight - fixedHeightOutsideChannelSection(
                hasErrorText: hasErrorText
            ),
            0
        )
    }

    func totalRequiredHeight(
        channelRowHeights: [CGFloat],
        hasErrorText: Bool
    ) -> CGFloat {
        fixedHeightOutsideChannelSection(hasErrorText: hasErrorText)
            + channelRowHeights.reduce(0, +)
            + EMKEVisualStyle.separatorThickness
    }

    private func fixedHeightOutsideChannelSection(
        hasErrorText: Bool
    ) -> CGFloat {
        EMKEDashboardMetrics.topPadding
            + EMKEDashboardMetrics.bottomPadding
            + EMKEDashboardMetrics.headerHeight
            + EMKEDashboardMetrics.topSpacerMinimum
            + EMKEDashboardMetrics.waveformMaximumHeight
            + EMKEDashboardMetrics.statusTopPadding
            + primaryStatusLineHeight
            + (hasErrorText ? errorTextAllocationHeight : 0)
            + EMKEDashboardMetrics.lowerSpacerMinimum
            + (EMKEVisualStyle.separatorThickness * 3)
            + languageDirectionHeight
            + EMKEDashboardMetrics.channelToPrimarySpacerMinimum
            + EMKEVisualStyle.primaryButtonHeight
            + EMKEDashboardMetrics.footerDividerTopPadding
            + EMKEDashboardMetrics.privacyTopPadding
            + privacyTextLineHeight
    }

    private static func textLineHeight(
        size: CGFloat,
        weight: NSFont.Weight = .regular
    ) -> CGFloat {
        ceil(
            ("Hg" as NSString).boundingRect(
                with: NSSize(
                    width: CGFloat.greatestFiniteMagnitude,
                    height: CGFloat.greatestFiniteMagnitude
                ),
                options: [
                    .usesLineFragmentOrigin,
                    .usesFontLeading,
                ],
                attributes: [
                    .font: NSFont.systemFont(
                        ofSize: size,
                        weight: weight
                    ),
                ]
            ).height
        )
    }
}

struct EMKEDashboardSeparator: View {
    var body: some View {
        Rectangle()
            .fill(EMKEVisualStyle.separator)
            .frame(height: EMKEVisualStyle.separatorThickness)
            .accessibilityHidden(true)
    }
}
