import EMKECore
import SwiftUI

enum EMKEDashboardChannelSlotPolicy {
    static func usesEqualExpandedSlots(
        interfaceLanguage: ResolvedInterfaceLanguage,
        inboundMode: EMKEChannelRowLayoutMode,
        outboundMode: EMKEChannelRowLayoutMode
    ) -> Bool {
        interfaceLanguage == .english
            && (
                inboundMode == .expanded
                    || outboundMode == .expanded
            )
    }
}

struct TranslationDashboardView: View {
    @ObservedObject var model: MenuBarModel
    @State private var now = Date()

    private var copy: AppCopy { model.copy }

    var body: some View {
        TranslationDashboardContent(
            value: model.dashboardPresentation(at: now),
            copy: copy,
            motherLanguage: $model.motherLanguage,
            meetingOutputLanguage: $model.meetingOutputLanguage,
            languagesLocked: model.selectionsLocked,
            settingsAction: model.showSettings,
            inboundAction: {
                Task {
                    await model.setInboundBypass(
                        !model.inboundBypassEnabled
                    )
                }
            },
            outboundAction: {
                Task {
                    await model.setOutboundBypass(
                        !model.outboundBypassEnabled
                    )
                }
            },
            primaryAction: {
                Task {
                    if model.coordinatorState.isRunning {
                        await model.stop()
                    } else {
                        await model.start()
                    }
                }
            }
        )
        .task(id: model.coordinatorState.isRunning) {
            guard model.coordinatorState.isRunning else { return }
            while !Task.isCancelled {
                do {
                    try await Task.sleep(for: .seconds(1))
                    now = Date()
                } catch {
                    return
                }
            }
        }
    }
}

struct TranslationDashboardContent: View {
    let value: TranslationDashboardPresentation
    let copy: AppCopy
    @Binding var motherLanguage: SupportedLanguage
    @Binding var meetingOutputLanguage: SupportedLanguage
    let languagesLocked: Bool
    let settingsAction: () -> Void
    let inboundAction: () -> Void
    let outboundAction: () -> Void
    let primaryAction: () -> Void

    private var verticalGeometry: EMKEDashboardVerticalLayoutGeometry {
        EMKEDashboardVerticalLayoutGeometry(
            panelHeight: EMKEVisualStyle.panelHeight
        )
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Spacer(
                minLength: EMKEDashboardMetrics.topSpacerMinimum
            )
            LiveWaveformView(
                level: value.primaryLevel,
                maximumHeight: EMKEDashboardMetrics.waveformMaximumHeight
            )
            .frame(height: EMKEDashboardMetrics.waveformMaximumHeight)
            .offset(y: EMKEDashboardMetrics.waveformOffsetY)
            HStack(spacing: 5) {
                Image(systemName: value.primaryStatusSymbol)
                    .font(.system(size: 10, weight: .medium))
                    .accessibilityHidden(true)
                Text(value.primaryStatus)
                    .font(
                        .system(
                            size: EMKEDashboardMetrics.primaryStatusSize,
                            weight: .medium
                        )
                    )
            }
            .frame(height: verticalGeometry.primaryStatusLineHeight)
            .foregroundStyle(EMKEVisualStyle.secondaryText)
            .padding(.top, EMKEDashboardMetrics.statusTopPadding)
            .offset(x: -5)
            .offset(y: EMKEDashboardMetrics.statusOffsetY)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(copy.translationStatus(value.primaryStatus))
            if let errorText = value.errorText {
                Text(errorText)
                    .font(
                        .system(size: EMKEDashboardMetrics.errorTextSize)
                    )
                    .foregroundStyle(EMKEVisualStyle.failure)
                    .lineLimit(1)
                    .frame(height: verticalGeometry.errorTextLineHeight)
                    .padding(
                        .top,
                        EMKEDashboardMetrics.errorTextTopPadding
                    )
            }
            Spacer(
                minLength: EMKEDashboardMetrics.lowerSpacerMinimum
            )
            EMKEDashboardSeparator()
            languageDirection
            EMKEDashboardSeparator()
            channelRows
                .frame(
                    maxHeight: verticalGeometry.channelSectionHeightBudget(
                        hasErrorText: value.errorText != nil
                    ),
                    alignment: .top
                )
            Spacer(
                minLength: EMKEDashboardMetrics
                    .channelToPrimarySpacerMinimum
            )
            primaryActionButton
            EMKEDashboardSeparator()
                .padding(.top, EMKEDashboardMetrics.footerDividerTopPadding)
            privacyFooter
        }
        .padding(.leading, EMKEDashboardMetrics.leadingPadding)
        .padding(.trailing, EMKEDashboardMetrics.trailingPadding)
        .padding(.top, EMKEDashboardMetrics.topPadding)
        .padding(.bottom, EMKEDashboardMetrics.bottomPadding)
        .frame(
            width: EMKEVisualStyle.panelWidth,
            height: EMKEVisualStyle.panelHeight
        )
        .background(EMKEVisualStyle.panelBackground)
    }

    private var header: some View {
        HStack(spacing: 7) {
            Image(nsImage: MenuBarLogo.image)
                .resizable()
                .frame(width: 18, height: 18)
                .accessibilityHidden(true)
            Text("EMKE Translation")
                .font(
                    .system(
                        size: EMKEDashboardMetrics.headerTitleSize,
                        weight: .semibold
                    )
                )
            Spacer()
            Button(action: settingsAction) {
                Image(systemName: "gearshape")
                    .font(
                        .system(
                            size: EMKEDashboardMetrics.gearSize,
                            weight: .light
                        )
                    )
                    .frame(
                        width: EMKEDashboardMetrics.headerHeight,
                        height: EMKEDashboardMetrics.headerHeight
                    )
                    .offset(x: EMKEDashboardMetrics.gearOffsetX)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(copy.text(.openSettings))
        }
        .frame(height: EMKEDashboardMetrics.headerHeight)
        .offset(y: EMKEDashboardMetrics.headerOffsetY)
    }

    private var languageDirection: some View {
        HStack(alignment: .bottom, spacing: 12) {
            languagePicker(
                title: copy.text(.myLanguage),
                selection: $motherLanguage,
                leadingInset: EMKEDashboardMetrics.inputLanguageInset
            )
            Image(systemName: "arrow.right")
                .font(
                    .system(
                        size: EMKEDashboardMetrics.directionArrowSize,
                        weight: .light
                    )
                )
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .padding(.bottom, 8)
                .accessibilityHidden(true)
            languagePicker(
                title: copy.text(.meetingOutput),
                selection: $meetingOutputLanguage,
                leadingInset: EMKEDashboardMetrics.outputLanguageInset
            )
        }
        .padding(.vertical, EMKEDashboardMetrics.languageVerticalPadding)
        .frame(height: verticalGeometry.languageDirectionHeight)
    }

    private func languagePicker(
        title: String,
        selection: Binding<SupportedLanguage>,
        leadingInset: CGFloat
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: EMKEDashboardMetrics.languageContentSpacing
        ) {
            Text(title)
                .font(
                    .system(size: EMKEDashboardMetrics.languageTitleSize)
                )
                .foregroundStyle(EMKEVisualStyle.secondaryText)
            if languagesLocked {
                lockedLanguageValue(
                    title: title,
                    language: selection.wrappedValue
                )
            } else {
                LanguageMenuButton(
                    copy: copy,
                    title: title,
                    selection: selection
                )
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.leading, leadingInset)
    }

    private func lockedLanguageValue(
        title: String,
        language: SupportedLanguage
    ) -> some View {
        LanguageValueLabel(copy: copy, language: language)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(title)
            .accessibilityValue(copy.languageName(language))
            .accessibilityHint(copy.text(.languageLockedHint))
    }

    private var channelRows: some View {
        let layout = channelLayout
        let slotHeight = layout.usesEqualExpandedSlots
            ? verticalGeometry.channelRowSlotHeight(
                hasErrorText: value.errorText != nil
            )
            : nil

        return VStack(spacing: 0) {
            TranslationChannelRow(
                copy: copy,
                title: copy.text(.heardByMe),
                direction: value.inboundDirection,
                level: value.inboundLevel,
                presentation: value.inbound,
                slotHeight: slotHeight,
                layoutMode: layout.inboundMode,
                action: inboundAction
            )
            EMKEDashboardSeparator()
            TranslationChannelRow(
                copy: copy,
                title: copy.text(.heardByOther),
                direction: value.outboundDirection,
                level: value.outboundLevel,
                presentation: value.outbound,
                slotHeight: slotHeight,
                layoutMode: layout.outboundMode,
                action: outboundAction
            )
        }
    }

    private var channelLayout: (
        inboundMode: EMKEChannelRowLayoutMode,
        outboundMode: EMKEChannelRowLayoutMode,
        usesEqualExpandedSlots: Bool
    ) {
        let inboundMode = EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: value.inboundDirection,
            status: value.inbound.status,
            statusSymbol: value.inbound.statusSymbol,
            actionTitle: value.inbound.actionTitle,
            isBlockingFailure: value.inbound.isBlockingFailure
        )
        let outboundMode = EMKEChannelRowLayoutDecision.resolve(
            interfaceLanguage: copy.language,
            direction: value.outboundDirection,
            status: value.outbound.status,
            statusSymbol: value.outbound.statusSymbol,
            actionTitle: value.outbound.actionTitle,
            isBlockingFailure: value.outbound.isBlockingFailure
        )
        let usesEqualExpandedSlots =
            EMKEDashboardChannelSlotPolicy.usesEqualExpandedSlots(
                interfaceLanguage: copy.language,
                inboundMode: inboundMode,
                outboundMode: outboundMode
            )

        return (
            inboundMode: usesEqualExpandedSlots ? .expanded : inboundMode,
            outboundMode: usesEqualExpandedSlots ? .expanded : outboundMode,
            usesEqualExpandedSlots: usesEqualExpandedSlots
        )
    }

    private var primaryActionButton: some View {
        Button(value.primaryActionTitle, action: primaryAction)
            .buttonStyle(.plain)
            .font(
                .system(
                    size: EMKEDashboardMetrics.primaryActionSize,
                    weight: .semibold
                )
            )
            .foregroundStyle(Color(nsColor: .windowBackgroundColor))
            .frame(maxWidth: .infinity)
            .frame(height: EMKEVisualStyle.primaryButtonHeight)
            .background(
                Capsule().fill(EMKEVisualStyle.primaryText)
            )
            .contentShape(Capsule())
            .disabled(!value.primaryActionEnabled)
            .opacity(value.primaryActionEnabled ? 1 : 0.55)
    }

    private var privacyFooter: some View {
        HStack(spacing: 5) {
            Image(systemName: "lock")
                .font(
                    .system(
                        size: EMKEDashboardMetrics.privacyIconSize,
                        weight: .medium
                    )
                )
                .accessibilityHidden(true)
            Text(value.privacyText)
                .font(.system(size: EMKEDashboardMetrics.privacyTextSize))
        }
        .frame(height: verticalGeometry.privacyTextLineHeight)
        .foregroundStyle(EMKEVisualStyle.secondaryText)
        .frame(maxWidth: .infinity)
        .offset(x: EMKEDashboardMetrics.privacyOffsetX)
        .padding(.top, EMKEDashboardMetrics.privacyTopPadding)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(value.privacyText)
    }
}

private struct LanguageValueLabel: View {
    let copy: AppCopy
    let language: SupportedLanguage

    var body: some View {
        HStack(spacing: 8) {
            Text(copy.languageName(language))
            Image(systemName: "chevron.down")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .accessibilityHidden(true)
        }
        .font(
            .system(
                size: EMKEDashboardMetrics.languageValueSize,
                weight: .semibold
            )
        )
        .contentShape(Rectangle())
    }
}

private struct LanguageMenuButton: View {
    let copy: AppCopy
    let title: String
    @Binding var selection: SupportedLanguage
    @State private var isPresented = false

    var body: some View {
        Button {
            isPresented.toggle()
        } label: {
            LanguageValueLabel(copy: copy, language: selection)
        }
        .buttonStyle(.plain)
        .fixedSize(horizontal: true, vertical: false)
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
                    .padding(.horizontal, 10)
                    .padding(.top, 8)
                    .padding(.bottom, 3)
                ForEach(SupportedLanguage.allCases, id: \.self) { language in
                    Button {
                        selection = language
                        isPresented = false
                    } label: {
                        HStack(spacing: 10) {
                            Text(copy.languageName(language))
                            Spacer(minLength: 16)
                            if selection == language {
                                Image(systemName: "checkmark")
                                    .font(.system(size: 11, weight: .semibold))
                                    .accessibilityHidden(true)
                            }
                        }
                        .font(.system(size: 14, weight: .medium))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 7)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(copy.languageName(language))
                    .accessibilityValue(
                        selection == language ? copy.text(.selected) : ""
                    )
                }
            }
            .padding(4)
            .frame(width: 148)
        }
        .accessibilityLabel(title)
        .accessibilityValue(copy.languageName(selection))
        .accessibilityHint(copy.text(.chooseTranslationLanguage))
    }
}
