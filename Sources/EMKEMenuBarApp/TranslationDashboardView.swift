import EMKECore
import SwiftUI

struct TranslationDashboardView: View {
    @ObservedObject var model: MenuBarModel
    @State private var now = Date()

    var body: some View {
        TranslationDashboardContent(
            value: model.dashboardPresentation(at: now),
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
    @Binding var motherLanguage: SupportedLanguage
    @Binding var meetingOutputLanguage: SupportedLanguage
    let languagesLocked: Bool
    let settingsAction: () -> Void
    let inboundAction: () -> Void
    let outboundAction: () -> Void
    let primaryAction: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            header
            Spacer(minLength: EMKEDashboardMetrics.topSpacer)
            LiveWaveformView(
                level: value.primaryLevel,
                maximumHeight: EMKEDashboardMetrics.waveformMaximumHeight
            )
            .offset(y: EMKEDashboardMetrics.waveformOffsetY)
            HStack(spacing: 5) {
                Image(systemName: value.primaryStatusSymbol)
                    .font(.system(size: 10, weight: .medium))
                    .accessibilityHidden(true)
                Text(value.primaryStatus)
                    .font(.system(size: 14, weight: .medium))
            }
            .foregroundStyle(EMKEVisualStyle.secondaryText)
            .padding(.top, EMKEDashboardMetrics.statusTopPadding)
            .offset(x: -5)
            .offset(y: EMKEDashboardMetrics.statusOffsetY)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("翻译状态：\(value.primaryStatus)")
            if let errorText = value.errorText {
                Text(errorText)
                    .font(.system(size: 11))
                    .foregroundStyle(EMKEVisualStyle.failure)
                    .lineLimit(1)
                    .padding(.top, 4)
            }
            Spacer(minLength: EMKEDashboardMetrics.lowerSpacer)
            EMKEDashboardSeparator()
            languageDirection
            EMKEDashboardSeparator()
            channelRows
            Spacer(minLength: 16)
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
        HStack {
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
                    .frame(width: 32, height: 32)
                    .offset(x: EMKEDashboardMetrics.gearOffsetX)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("打开设置")
        }
        .offset(y: EMKEDashboardMetrics.headerOffsetY)
    }

    private var languageDirection: some View {
        HStack(alignment: .bottom, spacing: 12) {
            languagePicker(
                title: "我的母语",
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
                title: "会议输出",
                selection: $meetingOutputLanguage,
                leadingInset: EMKEDashboardMetrics.outputLanguageInset
            )
        }
        .padding(.vertical, EMKEDashboardMetrics.languageVerticalPadding)
    }

    private func languagePicker(
        title: String,
        selection: Binding<SupportedLanguage>,
        leadingInset: CGFloat
    ) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.system(size: 12))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
            if languagesLocked {
                lockedLanguageValue(
                    title: title,
                    language: selection.wrappedValue
                )
            } else {
                LanguageMenuButton(
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
        LanguageValueLabel(language: language)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(title)
            .accessibilityValue(language.displayName)
            .accessibilityHint("翻译运行期间不可修改")
    }

    private var channelRows: some View {
        VStack(spacing: 0) {
            TranslationChannelRow(
                title: "我听到",
                direction: value.inboundDirection,
                level: value.inboundLevel,
                presentation: value.inbound,
                action: inboundAction
            )
            EMKEDashboardSeparator()
            TranslationChannelRow(
                title: "对方听到",
                direction: value.outboundDirection,
                level: value.outboundLevel,
                presentation: value.outbound,
                action: outboundAction
            )
        }
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
        .foregroundStyle(EMKEVisualStyle.secondaryText)
        .frame(maxWidth: .infinity)
        .offset(x: EMKEDashboardMetrics.privacyOffsetX)
        .padding(.top, EMKEDashboardMetrics.privacyTopPadding)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(value.privacyText)
    }
}

private struct LanguageValueLabel: View {
    let language: SupportedLanguage

    var body: some View {
        HStack(spacing: 8) {
            Text(language.displayName)
            Image(systemName: "chevron.down")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .accessibilityHidden(true)
        }
        .font(.system(size: 22, weight: .semibold))
        .contentShape(Rectangle())
    }
}

private struct LanguageMenuButton: View {
    let title: String
    @Binding var selection: SupportedLanguage
    @State private var isPresented = false

    var body: some View {
        Button {
            isPresented.toggle()
        } label: {
            LanguageValueLabel(language: selection)
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
                            Text(language.displayName)
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
                    .accessibilityLabel(language.displayName)
                    .accessibilityValue(selection == language ? "已选择" : "")
                }
            }
            .padding(4)
            .frame(width: 148)
        }
        .accessibilityLabel(title)
        .accessibilityValue(selection.displayName)
        .accessibilityHint("选择翻译语言")
    }
}
