import SwiftUI

struct FloatingTranslationStatusView: View {
    let presentation: FloatingTranslationPresentation
    let stopAction: () -> Void

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var isPulsing = false

    var body: some View {
        HStack(spacing: 10) {
            Circle()
                .fill(toneColor)
                .frame(width: 7, height: 7)
                .overlay {
                    if presentation.showsActivityPulse && !reduceMotion {
                        Circle()
                            .stroke(toneColor.opacity(0.55), lineWidth: 1)
                            .scaleEffect(isPulsing ? 1.9 : 1)
                            .opacity(isPulsing ? 0 : 1)
                            .onAppear {
                                withAnimation(
                                    .easeOut(duration: 1)
                                        .repeatForever(autoreverses: false)
                                ) {
                                    isPulsing = true
                                }
                            }
                            .onDisappear {
                                isPulsing = false
                            }
                    }
                }
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 1) {
                Text(presentation.status)
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(1)
                Text(presentation.elapsed ?? " ")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
            .frame(
                width: EMKEFloatingMetrics.statusWidth,
                alignment: .leading
            )
            .accessibilityElement(children: .combine)
            .accessibilityLabel(presentation.statusAccessibilityLabel)
            .accessibilityValue(presentation.elapsed ?? "")

            LiveWaveformView(
                level: presentation.level,
                maximumHeight: 24,
                compact: true,
                minimumBarHeight: 0.5
            )
            .frame(width: EMKEFloatingMetrics.waveformWidth)
            .environment(\.colorScheme, .dark)
            .accessibilityHidden(true)

            Button(action: stopAction) {
                RoundedRectangle(cornerRadius: 2)
                    .fill(Color(nsColor: .systemRed))
                    .frame(width: 8, height: 8)
                    .frame(
                        width: EMKEFloatingMetrics.stopTarget,
                        height: EMKEFloatingMetrics.stopTarget
                    )
                    .background(Circle().fill(.white.opacity(0.12)))
            }
            .buttonStyle(.plain)
            .disabled(!presentation.stopEnabled)
            .accessibilityLabel(presentation.stopAccessibilityLabel)
        }
        .padding(.horizontal, 10)
        .frame(
            width: EMKEFloatingMetrics.width,
            height: EMKEFloatingMetrics.height
        )
        .foregroundStyle(.white)
        .background(Color.black.opacity(0.94), in: Capsule())
        .overlay(Capsule().stroke(.white.opacity(0.12), lineWidth: 1))
        .preferredColorScheme(.dark)
    }

    private var toneColor: Color {
        switch presentation.tone {
        case .neutral:
            Color(nsColor: .systemGray)
        case .healthy:
            Color(red: 0.51, green: 0.90, blue: 0.74)
        case .degraded:
            Color(nsColor: .systemOrange)
        case .failure:
            Color(nsColor: .systemRed)
        }
    }
}
