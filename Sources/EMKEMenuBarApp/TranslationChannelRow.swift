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
        statusSymbol: String,
        actionTitle: String
    ) -> EMKEChannelRowLayoutMode {
        let directionFits = EMKEChannelContentMeasurement.textWidth(
            direction,
            font: .systemFont(
                ofSize: EMKEChannelMetrics.directionSize
            )
        ) <= EMKEChannelMetrics.directionWidth
        let statusFits = EMKEChannelContentMeasurement.textWidth(
            status,
            font: .systemFont(ofSize: 12)
        )
            + EMKEChannelContentMeasurement.symbolSize(
                statusSymbol,
                size: EMKEChannelMetrics.statusIconSize
            ).width
            + EMKEChannelMetrics.statusIconSpacing
            <= EMKEChannelMetrics.statusWidth
        let actionFits = EMKEChannelContentMeasurement.textWidth(
            actionTitle,
            font: .systemFont(
                ofSize: EMKEChannelMetrics.actionSize
            )
        ) <= EMKEChannelMetrics.actionWidth

        return directionFits && statusFits && actionFits
            ? .compact
            : .expanded
    }
}

private enum EMKEChannelContentMeasurement {
    static func textWidth(
        _ text: String,
        font: NSFont
    ) -> CGFloat {
        (text as NSString).size(
            withAttributes: [
                .font: font,
            ]
        ).width
    }

    static func textHeight(
        _ text: String,
        font: NSFont,
        width: CGFloat
    ) -> CGFloat {
        ceil(
            (text as NSString).boundingRect(
                with: NSSize(
                    width: max(width, 1),
                    height: .greatestFiniteMagnitude
                ),
                options: [
                    .usesLineFragmentOrigin,
                    .usesFontLeading,
                ],
                attributes: [
                    .font: font,
                ]
            ).height
        )
    }

    static func symbolSize(
        _ name: String,
        size: CGFloat
    ) -> NSSize {
        let configuration = NSImage.SymbolConfiguration(
            pointSize: size,
            weight: .medium
        )
        return NSImage(
            systemSymbolName: name,
            accessibilityDescription: nil
        )?
            .withSymbolConfiguration(configuration)?
            .size
            ?? NSSize(width: size + 3, height: size)
    }
}

struct EMKEExpandedChannelLayoutGeometry: Equatable {
    let contentBounds: CGRect
    let directionFrame: CGRect
    let statusFrame: CGRect
    let actionFrame: CGRect
    let waveformFrame: CGRect
    let statusContentHeight: CGFloat
    let actionContentHeight: CGFloat
    let descriptionHeight: CGFloat
    let requiredHeight: CGFloat

    static func resolve(
        title: String,
        direction: String,
        status: String,
        statusSymbol: String,
        actionTitle: String
    ) -> EMKEExpandedChannelLayoutGeometry {
        let contentWidth = EMKEChannelMetrics.expandedContentWidth
        let titleFont = NSFont.systemFont(
            ofSize: EMKEChannelMetrics.titleSize,
            weight: .semibold
        )
        let directionFont = NSFont.systemFont(
            ofSize: EMKEChannelMetrics.directionSize
        )
        let statusFont = NSFont.systemFont(ofSize: 12)
        let actionFont = NSFont.systemFont(
            ofSize: EMKEChannelMetrics.actionSize
        )
        let titleHeight = EMKEChannelContentMeasurement.textHeight(
            title,
            font: titleFont,
            width: contentWidth
        )
        let directionHeight = EMKEChannelContentMeasurement.textHeight(
            direction,
            font: directionFont,
            width: contentWidth
        )
        let descriptionHeight = titleHeight
            + 4
            + directionHeight
        let statusActionWidth = contentWidth
            - EMKEChannelMetrics.expandedStatusActionSpacing
        let naturalActionWidth = EMKEChannelContentMeasurement.textWidth(
            actionTitle,
            font: actionFont
        )
        let actionWidth = min(
            naturalActionWidth,
            statusActionWidth * 0.45
        )
        let statusWidth = statusActionWidth - actionWidth
        let statusSymbolSize = EMKEChannelContentMeasurement.symbolSize(
            statusSymbol,
            size: EMKEChannelMetrics.statusIconSize
        )
        let statusTextWidth = statusWidth
            - statusSymbolSize.width
            - EMKEChannelMetrics.statusIconSpacing
        let statusContentHeight = max(
            statusSymbolSize.height,
            EMKEChannelContentMeasurement.textHeight(
                status,
                font: statusFont,
                width: statusTextWidth
            )
        )
        let actionContentHeight = EMKEChannelContentMeasurement.textHeight(
            actionTitle,
            font: actionFont,
            width: actionWidth
        )
        let statusActionHeight = max(
            statusContentHeight,
            actionContentHeight
        )
        let statusActionY = descriptionHeight
            + EMKEChannelMetrics.expandedCopySpacing
        let waveformY = statusActionY
            + statusActionHeight
            + EMKEChannelMetrics.expandedCopySpacing
        let requiredHeight = waveformY
            + EMKEChannelMetrics.expandedWaveformHeight

        return EMKEExpandedChannelLayoutGeometry(
            contentBounds: CGRect(
                x: 0,
                y: 0,
                width: contentWidth,
                height: requiredHeight
            ),
            directionFrame: CGRect(
                x: 0,
                y: titleHeight + 4,
                width: contentWidth,
                height: directionHeight
            ),
            statusFrame: CGRect(
                x: 0,
                y: statusActionY,
                width: statusWidth,
                height: statusActionHeight
            ),
            actionFrame: CGRect(
                x: statusWidth
                    + EMKEChannelMetrics.expandedStatusActionSpacing,
                y: statusActionY,
                width: actionWidth,
                height: statusActionHeight
            ),
            waveformFrame: CGRect(
                x: 0,
                y: waveformY,
                width: contentWidth,
                height: EMKEChannelMetrics.expandedWaveformHeight
            ),
            statusContentHeight: statusContentHeight,
            actionContentHeight: actionContentHeight,
            descriptionHeight: descriptionHeight,
            requiredHeight: requiredHeight
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

    private var expandedGeometry: EMKEExpandedChannelLayoutGeometry {
        EMKEExpandedChannelLayoutGeometry.resolve(
            title: title,
            direction: direction,
            status: presentation.status,
            statusSymbol: presentation.statusSymbol,
            actionTitle: presentation.actionTitle
        )
    }

    var body: some View {
        switch EMKEChannelRowLayoutDecision.resolve(
            direction: direction,
            status: presentation.status,
            statusSymbol: presentation.statusSymbol,
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
                    .frame(
                        width: expandedGeometry.contentBounds.width,
                        height: expandedGeometry.descriptionHeight,
                        alignment: .topLeading
                    )
                HStack(
                    alignment: .top,
                    spacing: EMKEChannelMetrics.expandedCopySpacing
                ) {
                    channelStatusLabel
                        .frame(
                            width: expandedGeometry.statusFrame.width,
                            height: expandedGeometry.statusFrame.height,
                            alignment: .topLeading
                        )
                    Spacer(
                        minLength: EMKEChannelMetrics.expandedCopySpacing
                    )
                    channelAction(
                        compact: false,
                        width: expandedGeometry.actionFrame.width,
                        height: expandedGeometry.actionFrame.height
                    )
                }
                .frame(
                    width: expandedGeometry.contentBounds.width,
                    height: expandedGeometry.statusFrame.height,
                    alignment: .topLeading
                )
                channelWaveform
            }
            .frame(
                width: expandedGeometry.contentBounds.width,
                height: expandedGeometry.requiredHeight,
                alignment: .topLeading
            )
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
            maximumHeight: EMKEChannelMetrics.expandedWaveformHeight,
            compact: true
        )
    }

    private func channelAction(
        compact: Bool,
        width: CGFloat? = nil,
        height: CGFloat? = nil
    ) -> some View {
        Button(action: action) {
            Text(presentation.actionTitle)
                .multilineTextAlignment(.trailing)
                .fixedSize(horizontal: false, vertical: true)
        }
        .buttonStyle(.plain)
        .font(.system(size: EMKEChannelMetrics.actionSize))
        .foregroundStyle(EMKEVisualStyle.secondaryText)
        .frame(
            width: compact ? EMKEChannelMetrics.actionWidth : width,
            height: compact ? nil : height,
            alignment: .trailing
        )
        .offset(y: compact ? EMKEChannelMetrics.actionOffsetY : 0)
        .disabled(!presentation.actionEnabled)
        .accessibilityLabel(presentation.actionAccessibilityLabel)
    }
}
