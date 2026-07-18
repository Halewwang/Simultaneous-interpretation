import AppKit
import EMKECoordinator
import SwiftUI

struct TranslationSettingsView: View {
    @ObservedObject var model: MenuBarModel

    var body: some View {
        VStack(spacing: 0) {
            settingsHeader
            Divider().opacity(EMKEVisualStyle.dividerOpacity)
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    if model.selectionsLocked {
                        Label("翻译运行期间设置已锁定", systemImage: "lock.fill")
                            .font(.system(size: 12, weight: .medium))
                            .foregroundStyle(EMKEVisualStyle.secondaryText)
                    }
                    providerSection
                    Divider().opacity(EMKEVisualStyle.dividerOpacity)
                    audioSection
                    Button("退出 EMKE") {
                        NSApplication.shared.terminate(nil)
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
                }
                .padding(EMKEVisualStyle.horizontalPadding)
            }
        }
        .background(EMKEVisualStyle.panelBackground)
    }

    private var settingsHeader: some View {
        HStack(spacing: 12) {
            Button(action: model.showDashboard) {
                Image(systemName: "chevron.left")
                    .font(.system(size: 15, weight: .semibold))
                    .frame(width: 32, height: 32)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("返回翻译控制台")

            Text("设置")
                .font(.system(size: 18, weight: .semibold))
            Spacer()
        }
        .padding(.horizontal, EMKEVisualStyle.horizontalPadding)
        .padding(.vertical, 14)
    }

    private var providerSection: some View {
        VStack(alignment: .leading, spacing: 16) {
            sectionTitle("服务商", systemImage: "network")

            settingField("API Key") {
                SecureField("输入新的 API Key", text: $model.apiKeyDraft)
                    .textFieldStyle(.roundedBorder)
                    .disabled(model.selectionsLocked)
            }
            Text(model.apiKeyStatusText)
                .font(.system(size: 11))
                .foregroundStyle(EMKEVisualStyle.secondaryText)

            settingField("Base URL") {
                TextField("https://…", text: $model.baseURLString)
                    .textFieldStyle(.roundedBorder)
                    .disabled(model.selectionsLocked)
            }

            settingField("Model ID") {
                TextField("翻译模型", text: $model.modelID)
                    .textFieldStyle(.roundedBorder)
                    .disabled(model.selectionsLocked)
            }

            Button(model.isTestingConnection ? "测试中…" : "测试连接") {
                Task { await model.testConnection() }
            }
            .disabled(!model.canTestConnection)

            if !model.connectionTestMessage.isEmpty {
                Label(
                    model.connectionTestMessage,
                    systemImage: compatibilitySummaryImage
                )
                .font(.system(size: 12))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
            }

            if let report = model.compatibilityReport {
                compatibilitySummary(report)
            }

            if let error = model.configurationError {
                errorLabel(error)
            }
        }
    }

    private var audioSection: some View {
        VStack(alignment: .leading, spacing: 16) {
            sectionTitle("音频设备", systemImage: "waveform")

            settingField("真实麦克风") {
                Picker("真实麦克风", selection: $model.selectedInputUID) {
                    Text("请选择").tag(String?.none)
                    ForEach(model.physicalInputs) { device in
                        Text(device.name).tag(Optional(device.uid))
                    }
                }
                .labelsHidden()
                .frame(maxWidth: .infinity, alignment: .leading)
                .disabled(model.selectionsLocked)
            }

            settingField("真实耳机 / 扬声器") {
                Picker(
                    "真实耳机 / 扬声器",
                    selection: $model.selectedOutputUID
                ) {
                    Text("请选择").tag(String?.none)
                    ForEach(model.physicalOutputs) { device in
                        Text(device.name).tag(Optional(device.uid))
                    }
                }
                .labelsHidden()
                .frame(maxWidth: .infinity, alignment: .leading)
                .disabled(model.selectionsLocked)
            }

            Button("刷新设备") {
                model.reloadDevices()
            }
            .disabled(model.selectionsLocked)

            if let repairMessage = model.repairMessage {
                Label(repairMessage, systemImage: "exclamationmark.triangle")
                    .font(.system(size: 12))
                    .foregroundStyle(EMKEVisualStyle.warning)
            }

            if let error = model.inventoryError {
                errorLabel(error)
            }
        }
    }

    private func sectionTitle(
        _ title: String,
        systemImage: String
    ) -> some View {
        Label(title, systemImage: systemImage)
            .font(.system(size: 15, weight: .semibold))
    }

    private func settingField<Content: View>(
        _ title: String,
        @ViewBuilder content: () -> Content
    ) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.system(size: 12))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
            content()
        }
    }

    private func compatibilitySummary(
        _ report: TranslationCompatibilityReport
    ) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            compatibilityRow("认证", status: report.authentication)
            compatibilityRow("协议握手", status: report.handshake)
            compatibilityRow("目标语言", status: report.targetLanguage)
            compatibilityRow("双通道", status: report.dualSession)
            compatibilityRow("源语转写", status: report.sourceTranscript)
            compatibilityRow("音频输出", status: report.audioOutput)
            compatibilityRow("安全关闭", status: report.gracefulClose)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(EMKEVisualStyle.surfaceBackground)
        )
    }

    private func compatibilityRow(
        _ title: String,
        status: TranslationCapabilityStatus
    ) -> some View {
        let presentation = compatibilityPresentation(status)
        return HStack(spacing: 8) {
            Image(systemName: presentation.image)
                .foregroundStyle(presentation.color)
            Text(title)
            Spacer()
            Text(presentation.text)
                .foregroundStyle(EMKEVisualStyle.secondaryText)
        }
        .font(.system(size: 11))
    }

    private var compatibilitySummaryImage: String {
        guard let report = model.compatibilityReport else {
            return "arrow.triangle.2.circlepath"
        }
        return report.isFullyCompatible
            ? "checkmark.circle"
            : "info.circle"
    }

    private func compatibilityPresentation(
        _ status: TranslationCapabilityStatus
    ) -> (image: String, text: String, color: Color) {
        switch status {
        case .notRun:
            ("minus.circle", "未测试", EMKEVisualStyle.secondaryText)
        case .passed:
            ("checkmark.circle.fill", "通过", .green)
        case .requiresInteractiveAudio:
            ("waveform.circle", "需要音频测试", EMKEVisualStyle.warning)
        case .failed:
            ("xmark.circle.fill", "不兼容", EMKEVisualStyle.failure)
        }
    }

    private func errorLabel(_ error: String) -> some View {
        Label(error, systemImage: "exclamationmark.triangle")
            .font(.system(size: 12))
            .foregroundStyle(EMKEVisualStyle.failure)
            .fixedSize(horizontal: false, vertical: true)
    }
}
