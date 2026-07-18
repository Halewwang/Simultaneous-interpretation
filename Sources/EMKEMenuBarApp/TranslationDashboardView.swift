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
            Spacer(minLength: 48)
            LiveWaveformView(
                level: value.primaryLevel,
                maximumHeight: 95
            )
            .offset(y: 5)
            HStack(spacing: 5) {
                Image(systemName: value.primaryStatusSymbol)
                    .font(.system(size: 10, weight: .medium))
                    .accessibilityHidden(true)
                Text(value.primaryStatus)
                    .font(.system(size: 14, weight: .medium))
            }
            .foregroundStyle(EMKEVisualStyle.secondaryText)
            .padding(.top, 4)
            .offset(x: -5)
            .offset(y: 5)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("翻译状态：\(value.primaryStatus)")
            if let errorText = value.errorText {
                Text(errorText)
                    .font(.system(size: 11))
                    .foregroundStyle(EMKEVisualStyle.failure)
                    .lineLimit(1)
                    .padding(.top, 4)
            }
            Spacer(minLength: 28)
            Divider().opacity(EMKEVisualStyle.dividerOpacity)
            languageDirection
            Divider().opacity(EMKEVisualStyle.dividerOpacity)
            channelRows
            Spacer(minLength: 16)
            primaryActionButton
            Divider().opacity(EMKEVisualStyle.dividerOpacity)
                .padding(.top, 20)
            privacyFooter
        }
        .padding(.leading, 22)
        .padding(.trailing, 24)
        .padding(.top, 18)
        .padding(.bottom, 20)
        .frame(
            width: EMKEVisualStyle.panelWidth,
            height: EMKEVisualStyle.panelHeight
        )
        .background(EMKEVisualStyle.panelBackground)
    }

    private var header: some View {
        HStack {
            Text("EMKE Translation")
                .font(.system(size: 13, weight: .semibold))
            Spacer()
            Button(action: settingsAction) {
                Image(systemName: "gearshape")
                    .font(.system(size: 19, weight: .light))
                    .frame(width: 32, height: 32)
                    .offset(x: 6)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("打开设置")
        }
        .offset(y: 4)
    }

    private var languageDirection: some View {
        HStack(alignment: .bottom, spacing: 12) {
            languagePicker(
                title: "我的母语",
                selection: $motherLanguage,
                leadingInset: 52
            )
            Image(systemName: "arrow.right")
                .font(.system(size: 17, weight: .light))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .padding(.bottom, 8)
                .accessibilityHidden(true)
            languagePicker(
                title: "会议输出",
                selection: $meetingOutputLanguage,
                leadingInset: 45
            )
        }
        .padding(.vertical, 17.5)
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
                Picker(title, selection: selection) {
                    ForEach(SupportedLanguage.allCases, id: \.self) { language in
                        Text(language.displayName).tag(language)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .font(.system(size: 22, weight: .semibold))
                .accessibilityLabel(title)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.leading, leadingInset)
    }

    private func lockedLanguageValue(
        title: String,
        language: SupportedLanguage
    ) -> some View {
        HStack(spacing: 8) {
            Text(language.displayName)
            Image(systemName: "chevron.down")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .accessibilityHidden(true)
        }
        .font(.system(size: 22, weight: .semibold))
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
            Divider().opacity(EMKEVisualStyle.dividerOpacity)
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
            .font(.system(size: 16, weight: .semibold))
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
                .font(.system(size: 9, weight: .medium))
                .accessibilityHidden(true)
            Text(value.privacyText)
                .font(.system(size: 13))
        }
        .foregroundStyle(EMKEVisualStyle.secondaryText)
        .frame(maxWidth: .infinity)
        .offset(x: -5)
        .padding(.top, 12)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(value.privacyText)
    }
}
