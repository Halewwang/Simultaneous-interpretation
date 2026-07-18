import SwiftUI

enum WaveformBarLayout {
    private static let weights: [Double] = [
        0.28, 0.42, 0.58, 0.36, 0.72, 0.50,
        0.84, 0.62, 0.94, 0.70, 1.00, 0.78,
        0.88, 0.66, 0.96, 0.74, 0.82, 0.56,
        0.76, 0.48, 0.64, 0.38, 0.52, 0.30,
    ]

    static func heights(
        level: Double,
        minimum: Double = 4,
        maximum: Double = 72
    ) -> [Double] {
        let clamped = min(max(level, 0), 1)
        return weights.map { weight in
            let baseline = minimum + (weight * 2)
            return min(
                baseline + clamped * weight * (maximum - baseline),
                maximum
            )
        }
    }
}

struct LiveWaveformView: View {
    let level: Double
    let maximumHeight: CGFloat
    var compact = false

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(alignment: .center, spacing: compact ? 2 : 4) {
            ForEach(
                Array(
                    WaveformBarLayout.heights(
                        level: level,
                        maximum: Double(maximumHeight)
                    ).enumerated()
                ),
                id: \.offset
            ) { index, height in
                Capsule()
                    .fill(
                        index == 23 && level > 0.08
                            ? EMKEVisualStyle.activityBlue
                            : EMKEVisualStyle.primaryText.opacity(
                                compact ? 0.64 : 0.82
                            )
                    )
                    .frame(
                        width: compact ? 3 : 6,
                        height: CGFloat(height)
                    )
            }
        }
        .frame(maxWidth: .infinity, minHeight: maximumHeight)
        .animation(
            reduceMotion ? nil : .easeOut(duration: 0.08),
            value: level
        )
        .accessibilityHidden(true)
    }
}
