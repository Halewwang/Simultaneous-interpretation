import AppKit
import EMKEAudioEngine
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
                AudioDeviceMenuButton(
                    title: "真实麦克风",
                    devices: model.physicalInputs,
                    selection: $model.selectedInputUID
                )
                .disabled(model.audioDeviceControlsLocked)
            }

            settingField("真实耳机 / 扬声器") {
                AudioDeviceMenuButton(
                    title: "真实耳机 / 扬声器",
                    devices: model.physicalOutputs,
                    selection: $model.selectedOutputUID
                )
                .disabled(model.audioDeviceControlsLocked)
            }

            Button(model.isReloadingDevices ? "正在检测设备…" : "刷新设备") {
                Task { await model.reloadDevicesAsync() }
            }
            .disabled(model.audioDeviceControlsLocked)

            localAudioDiagnostics

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

    private var localAudioDiagnostics: some View {
        VStack(alignment: .leading, spacing: 14) {
            Divider().opacity(EMKEVisualStyle.dividerOpacity)
            VStack(alignment: .leading, spacing: 4) {
                Text("本地音频诊断")
                    .font(.system(size: 13, weight: .semibold))
                Text("仅检查本机音频，不连接翻译服务")
                    .font(.system(size: 11))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
            }

            HStack(spacing: 12) {
                Button(
                    model.isTestingAudioInput ? "停止测试" : "测试麦克风"
                ) {
                    Task {
                        if model.isTestingAudioInput {
                            await model.stopAudioInputTest()
                        } else {
                            await model.startAudioInputTest()
                        }
                    }
                }
                .disabled(
                    !model.isTestingAudioInput && !model.canTestAudioInput
                )

                LiveWaveformView(
                    level: model.audioInputDiagnosticLevel,
                    maximumHeight: 28,
                    compact: true
                )
                .frame(width: WaveformBarLayout.compactRequiredWidth)

                Spacer(minLength: 0)
                diagnosticStatus(model.audioInputDiagnosticText)
            }

            HStack(spacing: 12) {
                Button(
                    model.isPlayingAudioOutputTest
                        ? "正在播放…"
                        : "播放测试音"
                ) {
                    Task { await model.playAudioOutputTest() }
                }
                .disabled(!model.canTestAudioOutput)
                Spacer()
                diagnosticStatus(model.audioOutputDiagnosticText)
            }

            if let error = model.audioDiagnosticError {
                errorLabel(error)
            }
        }
    }

    private func diagnosticStatus(_ text: String) -> some View {
        Label(
            text,
            systemImage: text.contains("检测到") || text.contains("已播放")
                ? "checkmark.circle.fill"
                : "circle.dotted"
        )
        .font(.system(size: 11))
        .foregroundStyle(EMKEVisualStyle.secondaryText)
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

private struct AudioDeviceMenuButton: View {
    let title: String
    let devices: [AudioDevice]
    @Binding var selection: String?
    @State private var isPresented = false

    private var selectedName: String {
        devices.first { $0.uid == selection }?.name ?? "请选择"
    }

    var body: some View {
        Button {
            isPresented.toggle()
        } label: {
            valueLabel
        }
        .buttonStyle(.plain)
        .frame(maxWidth: .infinity, alignment: .leading)
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            menuContent
        }
        .accessibilityLabel(title)
        .accessibilityValue(selectedName)
        .accessibilityHint("选择音频设备")
    }

    private var valueLabel: some View {
        HStack(spacing: 8) {
            Text(selectedName)
                .lineLimit(1)
            Spacer(minLength: 12)
            Image(systemName: "chevron.down")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .accessibilityHidden(true)
        }
        .font(.system(size: 14, weight: .medium))
        .padding(.vertical, 7)
        .contentShape(Rectangle())
        .overlay(alignment: .bottom) {
            Rectangle()
                .fill(EMKEVisualStyle.separator)
                .frame(height: EMKEVisualStyle.separatorThickness)
        }
    }

    private var menuContent: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .padding(.horizontal, 10)
                .padding(.top, 8)
                .padding(.bottom, 3)
            ForEach(devices) { device in
                deviceButton(device)
            }
        }
        .padding(4)
        .frame(width: 280)
    }

    private func deviceButton(_ device: AudioDevice) -> some View {
        Button {
            selection = device.uid
            isPresented = false
        } label: {
            HStack(spacing: 10) {
                Text(device.name)
                    .lineLimit(1)
                Spacer(minLength: 16)
                if selection == device.uid {
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
        .accessibilityLabel(device.name)
        .accessibilityValue(selection == device.uid ? "已选择" : "")
    }
}
