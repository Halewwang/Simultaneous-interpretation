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
        bypassEnabled: Bool,
        automaticBypass: Bool = false,
        copy: AppCopy
    ) -> TranslationChannelPresentation {
        let channelSymbol = channel == .inbound ? "headphones" : "mic"
        let actionTitle: String
        let actionAccessibilityLabel: String

        if bypassEnabled {
            actionTitle = copy.text(.restoreTranslation)
            actionAccessibilityLabel = channel == .inbound
                ? copy.text(.restoreInbound)
                : copy.text(.restoreOutbound)
        } else {
            actionTitle = channel == .inbound
                ? copy.text(.playOriginal)
                : copy.text(.sendOriginal)
            actionAccessibilityLabel = channel == .inbound
                ? copy.text(.playInboundOriginal)
                : copy.text(.sendOutboundOriginal)
        }

        switch state {
        case .stopped:
            return makeValue(
                status: copy.text(.stopped),
                statusSymbol: "stop.circle",
                statusColor: EMKEVisualStyle.secondaryText,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: false
            )
        case .connecting:
            return makeValue(
                status: copy.text(.channelConnecting),
                statusSymbol: "arrow.triangle.2.circlepath",
                statusColor: EMKEVisualStyle.secondaryText,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: false
            )
        case .active:
            if bypassEnabled {
                return makeValue(
                    status: copy.text(.originalBypass),
                    statusSymbol: "speaker.wave.2",
                    statusColor: EMKEVisualStyle.secondaryText,
                    symbol: channelSymbol,
                    actionTitle: actionTitle,
                    actionAccessibilityLabel: actionAccessibilityLabel,
                    actionEnabled: true
                )
            }
            return makeValue(
                status: copy.text(.stable),
                statusSymbol: "checkmark.circle",
                statusColor: EMKEVisualStyle.secondaryText,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: true
            )
        case .bypassed:
            if automaticBypass {
                return makeValue(
                    status: copy.text(.sameLanguagePassThrough),
                    statusSymbol: "arrow.left.arrow.right",
                    statusColor: EMKEVisualStyle.secondaryText,
                    symbol: channelSymbol,
                    actionTitle: copy.text(.noTranslationNeeded),
                    actionAccessibilityLabel: copy.text(
                        .outboundSameLanguageNoTranslation
                    ),
                    actionEnabled: false
                )
            }
            return makeValue(
                status: copy.text(.originalBypass),
                statusSymbol: "speaker.wave.2",
                statusColor: EMKEVisualStyle.secondaryText,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: true
            )
        case .reconnecting(let attempt):
            return makeValue(
                status: copy.reconnecting(attempt: attempt),
                statusSymbol: "arrow.triangle.2.circlepath",
                statusColor: EMKEVisualStyle.warning,
                symbol: channelSymbol,
                actionTitle: actionTitle,
                actionAccessibilityLabel: actionAccessibilityLabel,
                actionEnabled: true
            )
        case .failed:
            return makeValue(
                status: channel == .inbound
                    ? copy.text(.playOriginal)
                    : copy.text(.muted),
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
    let copy: AppCopy
    let title: String
    let direction: String
    let level: Double
    let presentation: TranslationChannelPresentation
    let action: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: presentation.symbol)
                .font(.system(size: EMKEChannelMetrics.iconSize, weight: .light))
                .frame(width: EMKEChannelMetrics.iconWidth)
                .offset(x: EMKEChannelMetrics.iconOffsetX)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.system(size: EMKEChannelMetrics.titleSize, weight: .semibold))
                Text(direction)
                    .font(.system(size: EMKEChannelMetrics.directionSize))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
            }
            .frame(width: 112, alignment: .leading)
            channelStatus
            Spacer(minLength: 0)
            Button(presentation.actionTitle, action: action)
                .buttonStyle(.plain)
                .font(.system(size: EMKEChannelMetrics.actionSize))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .lineLimit(1)
                .frame(width: 64, alignment: .trailing)
                .offset(y: EMKEChannelMetrics.actionOffsetY)
                .disabled(!presentation.actionEnabled)
                .accessibilityLabel(
                    presentation.actionAccessibilityLabel
                )
        }
        .padding(.vertical, EMKEChannelMetrics.verticalPadding)
    }

    private var channelStatus: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 4) {
                Image(systemName: presentation.statusSymbol)
                    .font(.system(size: EMKEChannelMetrics.statusIconSize, weight: .medium))
                    .accessibilityHidden(true)
                Text(presentation.status)
                    .font(.system(size: 12))
            }
            .foregroundStyle(presentation.statusColor)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(
                copy.channelStatus(
                    title: title,
                    status: presentation.status
                )
            )
            .offset(x: EMKEChannelMetrics.statusOffsetX)
            LiveWaveformView(
                level: level,
                maximumHeight: 24,
                compact: true
            )
            .offset(x: EMKEChannelMetrics.meterOffsetX)
        }
        .frame(width: EMKEChannelMetrics.statusWidth)
        .offset(y: EMKEChannelMetrics.statusOffsetY)
    }
}
