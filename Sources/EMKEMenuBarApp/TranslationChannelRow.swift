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

struct EMKEChannelCompactLayoutProfile: Equatable {
    let descriptionWidth: CGFloat
    let statusWidth: CGFloat
    let actionWidth: CGFloat
    let horizontalSpacing: CGFloat
    let usesLegacyArrangement: Bool

    var totalWidth: CGFloat {
        EMKEChannelMetrics.iconWidth
            + descriptionWidth
            + statusWidth
            + actionWidth
            + (horizontalSpacing * 3)
    }

    static func resolve(
        interfaceLanguage: ResolvedInterfaceLanguage
    ) -> EMKEChannelCompactLayoutProfile {
        switch interfaceLanguage {
        case .zhHans:
            EMKEChannelCompactLayoutProfile(
                descriptionWidth: EMKEChannelMetrics.directionWidth,
                statusWidth: EMKEChannelMetrics.statusWidth,
                actionWidth: EMKEChannelMetrics.actionWidth,
                horizontalSpacing: 12,
                usesLegacyArrangement: true
            )
        case .english:
            EMKEChannelCompactLayoutProfile(
                descriptionWidth: 128,
                statusWidth: 96,
                actionWidth: 78,
                horizontalSpacing: 8,
                usesLegacyArrangement: false
            )
        }
    }
}

@MainActor
enum EMKEChannelRowLayoutDecision {
    static func resolve(
        interfaceLanguage: ResolvedInterfaceLanguage,
        direction: String,
        status: String,
        statusSymbol: String,
        actionTitle: String,
        isBlockingFailure: Bool = false
    ) -> EMKEChannelRowLayoutMode {
        guard !isBlockingFailure else {
            return .expanded
        }
        let profile = EMKEChannelCompactLayoutProfile.resolve(
            interfaceLanguage: interfaceLanguage
        )
        let directionFits = EMKEChannelContentMeasurement.textWidth(
            direction,
            font: .systemFont(
                ofSize: EMKEChannelMetrics.directionSize
            )
        ) <= profile.descriptionWidth
        let statusFits = EMKEChannelContentMeasurement.textWidth(
            status,
            font: .systemFont(ofSize: 12)
        )
            + EMKEChannelContentMeasurement.symbolSize(
                statusSymbol,
                size: EMKEChannelMetrics.statusIconSize
            ).width
            + EMKEChannelMetrics.statusIconSpacing
            <= profile.statusWidth
        let actionFits = EMKEChannelContentMeasurement.textWidth(
            actionTitle,
            font: .systemFont(
                ofSize: EMKEChannelMetrics.actionSize
            )
        ) <= profile.actionWidth

        return directionFits && statusFits && actionFits
            ? .compact
            : .expanded
    }
}

@MainActor
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

@MainActor
struct EMKEExpandedChannelLayoutGeometry: Equatable {
    let contentBounds: CGRect
    let directionFrame: CGRect
    let statusFrame: CGRect
    let actionFrame: CGRect
    let waveformFrame: CGRect
    let statusContentHeight: CGFloat
    let actionContentHeight: CGFloat
    let descriptionHeight: CGFloat
    let verticalCopySpacing: CGFloat
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
        let singleLineStatusHeight = EMKEChannelContentMeasurement.textHeight(
            "Hg",
            font: statusFont,
            width: .greatestFiniteMagnitude
        )
        let singleLineActionHeight = EMKEChannelContentMeasurement.textHeight(
            "Hg",
            font: actionFont,
            width: .greatestFiniteMagnitude
        )
        let verticalCopySpacing = (
            statusContentHeight > max(
                statusSymbolSize.height,
                singleLineStatusHeight
            )
                || actionContentHeight > singleLineActionHeight
        )
            ? EMKEChannelMetrics.expandedMultilineCopySpacing
            : EMKEChannelMetrics.expandedCopySpacing
        let statusActionHeight = max(
            statusContentHeight,
            actionContentHeight
        )
        let statusActionY = descriptionHeight
            + verticalCopySpacing
        let waveformY = statusActionY
            + statusActionHeight
            + verticalCopySpacing
        let waveformOffsetX = -(
            EMKEChannelMetrics.iconWidth
                + EMKEChannelMetrics.expandedHorizontalSpacing
        ) / 2
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
                x: waveformOffsetX,
                y: waveformY,
                width: contentWidth,
                height: EMKEChannelMetrics.expandedWaveformHeight
            ),
            statusContentHeight: statusContentHeight,
            actionContentHeight: actionContentHeight,
            descriptionHeight: descriptionHeight,
            verticalCopySpacing: verticalCopySpacing,
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
    let slotHeight: CGFloat?
    let layoutMode: EMKEChannelRowLayoutMode?
    let action: () -> Void

    init(
        copy: AppCopy,
        title: String,
        direction: String,
        level: Double,
        presentation: TranslationChannelPresentation,
        slotHeight: CGFloat? = nil,
        layoutMode: EMKEChannelRowLayoutMode? = nil,
        action: @escaping () -> Void
    ) {
        self.copy = copy
        self.title = title
        self.direction = direction
        self.level = level
        self.presentation = presentation
        self.slotHeight = slotHeight
        self.layoutMode = layoutMode
        self.action = action
    }

    private var compactProfile: EMKEChannelCompactLayoutProfile {
        EMKEChannelCompactLayoutProfile.resolve(
            interfaceLanguage: copy.language
        )
    }

    private var expandedGeometry: EMKEExpandedChannelLayoutGeometry {
        EMKEExpandedChannelLayoutGeometry.resolve(
            title: title,
            direction: direction,
            status: presentation.status,
            statusSymbol: presentation.statusSymbol,
            actionTitle: presentation.actionTitle
        )
    }

    @ViewBuilder
    var body: some View {
        if let slotHeight {
            rowContent
                .frame(height: slotHeight, alignment: .center)
        } else {
            rowContent
        }
    }

    @ViewBuilder
    private var rowContent: some View {
        switch resolvedLayoutMode {
        case .compact:
            compactBody
        case .expanded:
            expandedBody
        }
    }

    private var resolvedLayoutMode: EMKEChannelRowLayoutMode {
        layoutMode ?? EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: direction,
            status: presentation.status,
            statusSymbol: presentation.statusSymbol,
            actionTitle: presentation.actionTitle,
            isBlockingFailure: presentation.isBlockingFailure
        )
    }

    @ViewBuilder
    private var compactBody: some View {
        if compactProfile.usesLegacyArrangement {
            legacyCompactBody
        } else {
            englishCompactBody
        }
    }

    private var legacyCompactBody: some View {
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

    private var englishCompactBody: some View {
        HStack(spacing: compactProfile.horizontalSpacing) {
            channelIcon
            channelDescription
                .frame(
                    width: compactProfile.descriptionWidth,
                    alignment: .leading
                )
            englishCompactChannelStatus
            channelAction(
                compact: false,
                width: compactProfile.actionWidth
            )
        }
        .frame(width: compactProfile.totalWidth, alignment: .leading)
        .padding(.vertical, EMKEChannelMetrics.verticalPadding)
    }

    private var expandedBody: some View {
        HStack(spacing: EMKEChannelMetrics.expandedHorizontalSpacing) {
            channelIcon
            VStack(
                alignment: .leading,
                spacing: expandedGeometry.verticalCopySpacing
            ) {
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
                    .offset(x: expandedGeometry.waveformFrame.minX)
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

    private var englishCompactChannelStatus: some View {
        VStack(alignment: .leading, spacing: 6) {
            channelStatusLabel
            channelWaveform
        }
        .frame(
            width: compactProfile.statusWidth,
            alignment: .leading
        )
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
