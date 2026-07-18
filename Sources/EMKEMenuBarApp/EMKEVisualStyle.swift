import AppKit
import SwiftUI

enum EMKEVisualStyle {
    static let panelWidth: CGFloat = 420
    static let panelHeight: CGFloat = 620
    static let horizontalPadding: CGFloat = 24
    static let primarySpacing: CGFloat = 24
    static let groupSpacing: CGFloat = 16
    static let compactSpacing: CGFloat = 8
    static let primaryButtonHeight: CGFloat = 45
    static let dividerOpacity = 0.14

    static let primaryText = Color.primary
    static let secondaryText = Color.secondary
    static let panelBackground = Color(nsColor: .windowBackgroundColor)
    static let surfaceBackground = Color(nsColor: .controlBackgroundColor)
    static let warning = Color(nsColor: .systemOrange)
    static let failure = Color(nsColor: .systemRed)
    static let activityBlue = Color(
        red: 0.25,
        green: 0.45,
        blue: 0.92
    )
}
