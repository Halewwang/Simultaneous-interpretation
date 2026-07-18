import SwiftUI

enum WaveformBarLayout {
    private static let weights: [Double] = [
        0.12, 0.22, 0.18, 0.58, 0.96, 0.52,
        0.25, 0.10, 0.46, 0.32, 0.20, 0.58,
        0.94, 0.60, 0.28, 0.52, 0.34, 0.22,
        0.16, 0.12, 0.09, 0.18, 0.12, 0.10,
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
        HStack(alignment: .center, spacing: compact ? 2 : 8) {
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
                        width: compact ? 1.5 : 4,
                        height: CGFloat(height)
                    )
            }
        }
        .frame(maxWidth: .infinity, minHeight: maximumHeight)
    }
}
