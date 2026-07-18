import EMKECoordinator
import SwiftUI

struct TranslationChannelPresentation: Equatable {
    let status: String
    let statusSymbol: String
    let statusColor: Color
    let symbol: String
    let actionTitle: String
    let actionAccessibilityLabel: String
    let actionEnabled: Bool
    let isBlockingFailure: Bool

    static func make(
        channel: MenuBarChannel,
        state: TranslationChannelState,
        bypassEnabled: Bool
    ) -> TranslationChannelPresentation {
        let channelSymbol = channel == .inbound ? "headphones" : "mic"
        let actionTitle: String
        let actionAccessibilityLabel: String

        if bypassEnabled {
            actionTitle = "恢复翻译"
            actionAccessibilityLabel = channel == .inbound
                ? "恢复入站翻译"
                : "恢复出站翻译"
        } else {
            actionTitle = channel == .inbound ? "播放原音" : "发送原音"
            actionAccessibilityLabel = channel == .inbound
                ? "播放入站原音"
                : "发送出站原音"
        }

        switch state {
        case .stopped:
            return makeValue(
                status: "已停止",
                statusSymbol: "stop.circle",
                statusColor: EMKEVisualStyle.secondaryText,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: false
            )
        case .connecting:
            return makeValue(
                status: "连接中",
                statusSymbol: "arrow.triangle.2.circlepath",
                statusColor: EMKEVisualStyle.secondaryText,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: false
            )
        case .active:
            return makeValue(
                status: "稳定",
                statusSymbol: "checkmark.circle",
                statusColor: EMKEVisualStyle.secondaryText,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: true
            )
        case .bypassed:
            return makeValue(
                status: "原音旁路",
                statusSymbol: "speaker.wave.2",
                statusColor: EMKEVisualStyle.secondaryText,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: true
            )
        case .reconnecting(let attempt):
            return makeValue(
                status: "重连中（第 \(attempt) 次）",
                statusSymbol: "arrow.triangle.2.circlepath",
                statusColor: EMKEVisualStyle.warning,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: true
            )
        case .failed:
            return makeValue(
                status: channel == .inbound ? "播放原音" : "已静音",
                statusSymbol: "exclamationmark.triangle",
                statusColor: EMKEVisualStyle.failure,
                symbol: channel == .inbound ? "headphones" : "mic.slash",
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: false,
                isBlockingFailure: true
            )
        }
    }

    private static func makeValue(
        status: String,
        statusSymbol: String,
        statusColor: Color,
        symbol: String,
        actionTitle: String,
        actionAccessibilityLabel: String,
        actionEnabled: Bool,
        isBlockingFailure: Bool = false
    ) -> TranslationChannelPresentation {
        TranslationChannelPresentation(
            status: status,
            statusSymbol: statusSymbol,
            statusColor: statusColor,
            symbol: symbol,
            actionTitle: actionTitle,
            actionAccessibilityLabel: actionAccessibilityLabel,
            actionEnabled: actionEnabled,
            isBlockingFailure: isBlockingFailure
        )
    }
}

struct TranslationChannelRow: View {
    let title: String
    let direction: String
    let level: Double
    let presentation: TranslationChannelPresentation
    let action: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: presentation.symbol)
                .font(.system(size: 35, weight: .light))
                .frame(width: 48)
                .offset(x: -5)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.system(size: 17, weight: .semibold))
                Text(direction)
                    .font(.system(size: 14))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
            }
            .frame(width: 112, alignment: .leading)
            channelStatus
            Spacer(minLength: 0)
            Button(presentation.actionTitle, action: action)
                .buttonStyle(.plain)
                .font(.system(size: 12.5))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .lineLimit(1)
                .frame(width: 64, alignment: .trailing)
                .offset(y: 14)
                .disabled(!presentation.actionEnabled)
                .accessibilityLabel(
                    presentation.actionAccessibilityLabel
                )
        }
        .padding(.vertical, 23.5)
    }

    private var channelStatus: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 4) {
                Image(systemName: presentation.statusSymbol)
                    .font(.system(size: 9, weight: .medium))
                    .accessibilityHidden(true)
                Text(presentation.status)
                    .font(.system(size: 12))
            }
            .foregroundStyle(presentation.statusColor)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(
                "\(title)状态：\(presentation.status)"
            )
            .offset(x: -18)
            LiveWaveformView(
                level: level,
                maximumHeight: 24,
                compact: true
            )
            .offset(x: -10)
        }
        .frame(width: 105)
        .offset(y: 6)
    }
}
