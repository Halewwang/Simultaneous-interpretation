import AppKit
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

enum EMKEChannelRowLayoutMode: Equatable {
    case compact
    case expanded
}

enum EMKEChannelRowLayoutDecision {
    static func resolve(
        direction: String,
        status: String,
        actionTitle: String
    ) -> EMKEChannelRowLayoutMode {
        let directionFits = textWidth(
            direction,
            size: EMKEChannelMetrics.directionSize
        ) <= EMKEChannelMetrics.directionWidth
        let statusFits = textWidth(status, size: 12)
            + EMKEChannelMetrics.statusIconSize
            + EMKEChannelMetrics.statusIconSpacing
            <= EMKEChannelMetrics.statusWidth
        let actionFits = textWidth(
            actionTitle,
            size: EMKEChannelMetrics.actionSize
        ) <= EMKEChannelMetrics.actionWidth

        return directionFits && statusFits && actionFits
            ? .compact
            : .expanded
    }

    private static func textWidth(
        _ text: String,
        size: CGFloat
    ) -> CGFloat {
        (text as NSString).size(
            withAttributes: [
                .font: NSFont.systemFont(ofSize: size),
            ]
        ).width
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
        switch EMKEChannelRowLayoutDecision.resolve(
            direction: direction,
            status: presentation.status,
            actionTitle: presentation.actionTitle
        ) {
        case .compact:
            compactBody
        case .expanded:
            expandedBody
        }
    }

    private var compactBody: some View {
        HStack(spacing: 12) {
            channelIcon
            channelDescription
                .frame(
                    width: EMKEChannelMetrics.directionWidth,
                    alignment: .leading
                )
            compactChannelStatus
            Spacer(minLength: 0)
            channelAction(compact: true)
        }
        .padding(.vertical, EMKEChannelMetrics.verticalPadding)
    }

    private var expandedBody: some View {
        HStack(spacing: EMKEChannelMetrics.expandedHorizontalSpacing) {
            channelIcon
            VStack(alignment: .leading, spacing: EMKEChannelMetrics.expandedCopySpacing) {
                channelDescription
                HStack(
                    alignment: .top,
                    spacing: EMKEChannelMetrics.expandedCopySpacing
                ) {
                    channelStatusLabel
                    Spacer(
                        minLength: EMKEChannelMetrics.expandedCopySpacing
                    )
                    channelAction(compact: false)
                }
                channelWaveform
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var channelIcon: some View {
        Image(systemName: presentation.symbol)
            .font(.system(size: EMKEChannelMetrics.iconSize, weight: .light))
            .frame(width: EMKEChannelMetrics.iconWidth)
            .offset(x: EMKEChannelMetrics.iconOffsetX)
            .accessibilityHidden(true)
    }

    private var channelDescription: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(
                    .system(
                        size: EMKEChannelMetrics.titleSize,
                        weight: .semibold
                    )
                )
            Text(direction)
                .font(.system(size: EMKEChannelMetrics.directionSize))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var compactChannelStatus: some View {
        VStack(alignment: .leading, spacing: 6) {
            channelStatusLabel
                .offset(x: EMKEChannelMetrics.statusOffsetX)
            channelWaveform
                .offset(x: EMKEChannelMetrics.meterOffsetX)
        }
        .frame(width: EMKEChannelMetrics.statusWidth)
        .offset(y: EMKEChannelMetrics.statusOffsetY)
    }

    private var channelStatusLabel: some View {
        HStack(spacing: EMKEChannelMetrics.statusIconSpacing) {
            Image(systemName: presentation.statusSymbol)
                .font(
                    .system(
                        size: EMKEChannelMetrics.statusIconSize,
                        weight: .medium
                    )
                )
                .accessibilityHidden(true)
            Text(presentation.status)
                .font(.system(size: 12))
                .fixedSize(horizontal: false, vertical: true)
        }
        .foregroundStyle(presentation.statusColor)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(
            copy.channelStatus(
                title: title,
                status: presentation.status
            )
        )
    }

    private var channelWaveform: some View {
        LiveWaveformView(
            level: level,
            maximumHeight: 24,
            compact: true
        )
    }

    private func channelAction(compact: Bool) -> some View {
        Button(action: action) {
            Text(presentation.actionTitle)
                .multilineTextAlignment(.trailing)
                .fixedSize(horizontal: false, vertical: true)
        }
        .buttonStyle(.plain)
        .font(.system(size: EMKEChannelMetrics.actionSize))
        .foregroundStyle(EMKEVisualStyle.secondaryText)
        .frame(
            width: compact ? EMKEChannelMetrics.actionWidth : nil,
            alignment: .trailing
        )
        .offset(y: compact ? EMKEChannelMetrics.actionOffsetY : 0)
        .disabled(!presentation.actionEnabled)
        .accessibilityLabel(presentation.actionAccessibilityLabel)
    }
}
