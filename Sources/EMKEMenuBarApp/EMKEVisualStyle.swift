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
    static let gearSize: CGFloat = 19
    static let gearOffsetX: CGFloat = 6
    static let headerOffsetY: CGFloat = 4
    static let topSpacer: CGFloat = 48
    static let waveformMaximumHeight: CGFloat = 95
    static let waveformOffsetY: CGFloat = 5
    static let statusTopPadding: CGFloat = 4
    static let statusOffsetY: CGFloat = 5
    static let lowerSpacer: CGFloat = 28
    static let inputLanguageInset: CGFloat = 52
    static let outputLanguageInset: CGFloat = 45
    static let directionArrowSize: CGFloat = 17
    static let languageVerticalPadding: CGFloat = 17.5
    static let leadingPadding: CGFloat = 22
    static let trailingPadding: CGFloat = 24
    static let topPadding: CGFloat = 18
    static let bottomPadding: CGFloat = 20
    static let primaryActionSize: CGFloat = 16
    static let footerDividerTopPadding: CGFloat = 20
    static let privacyIconSize: CGFloat = 9
    static let privacyTextSize: CGFloat = 13
    static let privacyOffsetX: CGFloat = -5
    static let privacyTopPadding: CGFloat = 12
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
    static let verticalPadding: CGFloat = 23.5
}

struct EMKEDashboardSeparator: View {
    var body: some View {
        Rectangle()
            .fill(EMKEVisualStyle.separator)
            .frame(height: EMKEVisualStyle.separatorThickness)
            .accessibilityHidden(true)
    }
}
