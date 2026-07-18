import SwiftUI

enum WaveformBarLayout {
    private static let weights: [Double] = [
        0.12, 0.22, 0.18, 0.58, 0.96, 0.52,
        0.25, 0.10, 0.46, 0.32, 0.20, 0.58,
        0.94, 0.60, 0.28, 0.52, 0.34, 0.22,
        0.16, 0.12, 0.09, 0.18, 0.12, 0.10,
    ]
    private static let opacities: [Double] = [
        0.96, 0.46, 0.42, 0.94, 1.00, 0.46,
        0.40, 0.96, 0.92, 0.88, 0.42, 0.92,
        1.00, 0.50, 0.44, 0.96, 0.90, 0.88,
        0.84, 0.80, 0.72, 0.74, 0.72, 0.68,
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

    static func opacity(at index: Int, compact: Bool) -> Double {
        let value = opacities[index]
        return compact ? min(max(value, 0.42), 0.72) : value
    }
}

struct LiveWaveformView: View {
    let level: Double
    let maximumHeight: CGFloat
    var compact = false

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            if reduceMotion {
                waveformBars
            } else {
                waveformBars.animation(
                    .easeOut(duration: 0.08),
                    value: level
                )
            }
        }
        .accessibilityHidden(true)
    }

    private var waveformBars: some View {
        HStack(alignment: .center, spacing: compact ? 2.75 : 8) {
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
                                WaveformBarLayout.opacity(
                                    at: index,
                                    compact: compact
                                )
                            )
                    )
                    .frame(
                        width: compact ? 1.5 : 4,
                        height: CGFloat(height)
                    )
            }
        }
        .frame(maxWidth: .infinity, minHeight: maximumHeight)
    }
}
