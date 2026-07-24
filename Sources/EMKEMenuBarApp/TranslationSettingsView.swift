import AppKit
import EMKEAudioEngine
import EMKECoordinator
import SwiftUI

struct TranslationSettingsView: View {
    @ObservedObject var model: MenuBarModel
    @ObservedObject var updateController: AppUpdateController
    let openOnboarding: () -> Void

    init(
        model: MenuBarModel,
        updateController: AppUpdateController,
        openOnboarding: @escaping () -> Void = {}
    ) {
        self.model = model
        self.updateController = updateController
        self.openOnboarding = openOnboarding
    }

    private var copy: AppCopy { model.copy }

    var body: some View {
        VStack(spacing: 0) {
            settingsHeader
            Divider().opacity(EMKEVisualStyle.dividerOpacity)
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    interfaceSection
                    Divider().opacity(EMKEVisualStyle.dividerOpacity)
                    if model.selectionsLocked {
                        Label(
                            copy.text(.translationSettingsLocked),
                            systemImage: "lock.fill"
                        )
                            .font(.system(size: 12, weight: .medium))
                            .foregroundStyle(EMKEVisualStyle.secondaryText)
                    }
                    providerSection
                    Divider().opacity(EMKEVisualStyle.dividerOpacity)
                    audioSection
                    ExitApplicationButton(copy: copy) {
                        NSApplication.shared.terminate(nil)
                    }
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
            .accessibilityLabel(copy.text(.backToDashboard))

            Text(copy.text(.settings))
                .font(.system(size: 18, weight: .semibold))
            Spacer()
        }
        .padding(.horizontal, EMKEVisualStyle.horizontalPadding)
        .padding(.vertical, 14)
    }

    private var interfaceSection: some View {
        VStack(alignment: .leading, spacing: 16) {
            sectionTitle(
                copy.text(.interface),
                systemImage: "character.bubble"
            )

            settingField(copy.text(.interfaceLanguage)) {
                InterfaceLanguageMenuButton(
                    copy: copy,
                    selection: $model.interfaceLanguage
                )
            }

            Button(action: openOnboarding) {
                Label(
                    copy.text(.openGettingStarted),
                    systemImage: "questionmark.circle"
                )
            }
            .buttonStyle(.plain)
            .foregroundStyle(EMKEVisualStyle.activityBlue)

            Button(copy.text(.checkForUpdates)) {
                updateController.checkForUpdates()
            }
            .disabled(!updateController.canCheckForUpdates)
        }
    }

    private var providerSection: some View {
        VStack(alignment: .leading, spacing: 16) {
            sectionTitle(copy.text(.provider), systemImage: "network")

            settingField("API Key") {
                SecureField(
                    copy.text(.enterNewAPIKey),
                    text: $model.apiKeyDraft
                )
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

            settingField(copy.text(.modelID)) {
                TextField(
                    copy.text(.translationModel),
                    text: $model.modelID
                )
                    .textFieldStyle(.roundedBorder)
                    .disabled(model.selectionsLocked)
            }

            Button(
                model.isTestingConnection
                    ? copy.text(.testing)
                    : copy.text(.testConnection)
            ) {
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
            sectionTitle(copy.text(.audioDevices), systemImage: "waveform")

            settingField(copy.text(.physicalMicrophone)) {
                AudioDeviceMenuButton(
                    copy: copy,
                    title: copy.text(.physicalMicrophone),
                    devices: model.physicalInputs,
                    selection: $model.selectedInputUID
                )
                .disabled(model.audioDeviceControlsLocked)
            }

            settingField(copy.text(.physicalOutput)) {
                AudioDeviceMenuButton(
                    copy: copy,
                    title: copy.text(.physicalOutput),
                    devices: model.physicalOutputs,
                    selection: $model.selectedOutputUID
                )
                .disabled(model.audioDeviceControlsLocked)
            }

            Button(
                model.isReloadingDevices
                    ? copy.text(.detectingDevices)
                    : copy.text(.refreshDevices)
            ) {
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
                Label(
                    copy.text(.localAudioDiagnostics),
                    systemImage: "waveform.badge.magnifyingglass"
                )
                .font(.system(size: 13, weight: .semibold))
                Text(copy.text(.localAudioOnly))
                    .font(.system(size: 11))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
            }

            HStack(spacing: 12) {
                Button(
                    model.isTestingAudioInput
                        ? copy.text(.stopTest)
                        : copy.text(.testMicrophone)
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
                diagnosticStatus(
                    model.audioInputDiagnosticText,
                    succeeded: model.audioInputDiagnosticSucceeded
                )
            }

            HStack(spacing: 12) {
                Button(
                    model.isPlayingAudioOutputTest
                        ? copy.text(.playing)
                        : copy.text(.playTestTone)
                ) {
                    Task { await model.playAudioOutputTest() }
                }
                .disabled(!model.canTestAudioOutput)
                Spacer()
                diagnosticStatus(
                    model.audioOutputDiagnosticText,
                    succeeded: model.audioOutputDiagnosticSucceeded
                )
            }

            if let error = model.audioDiagnosticError {
                errorLabel(error)
            }
        }
    }

    private func diagnosticStatus(
        _ text: String,
        succeeded: Bool
    ) -> some View {
        Label(
            text,
            systemImage: succeeded
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
            compatibilityRow(
                copy.text(.authentication),
                status: report.authentication
            )
            compatibilityRow(
                copy.text(.protocolHandshake),
                status: report.handshake
            )
            compatibilityRow(
                copy.text(.targetLanguage),
                status: report.targetLanguage
            )
            compatibilityRow(
                copy.text(.dualChannel),
                status: report.dualSession
            )
            compatibilityRow(
                copy.text(.sourceTranscript),
                status: report.sourceTranscript
            )
            compatibilityRow(
                copy.text(.audioOutput),
                status: report.audioOutput
            )
            compatibilityRow(
                copy.text(.secureClose),
                status: report.gracefulClose
            )
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
            (
                "minus.circle",
                copy.text(.notTested),
                EMKEVisualStyle.secondaryText
            )
        case .passed:
            ("checkmark.circle.fill", copy.text(.passed), .green)
        case .requiresInteractiveAudio:
            (
                "waveform.circle",
                copy.text(.needsAudioTest),
                EMKEVisualStyle.warning
            )
        case .failed:
            (
                "xmark.circle.fill",
                copy.text(.incompatible),
                EMKEVisualStyle.failure
            )
        }
    }

    private func errorLabel(_ error: String) -> some View {
        Label(error, systemImage: "exclamationmark.triangle")
            .font(.system(size: 12))
            .foregroundStyle(EMKEVisualStyle.failure)
            .fixedSize(horizontal: false, vertical: true)
    }
}

private struct InterfaceLanguageMenuButton: View {
    let copy: AppCopy
    @Binding var selection: AppInterfaceLanguage
    @State private var isPresented = false

    private var selectedName: String {
        optionName(for: selection)
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
        .accessibilityLabel(copy.text(.interfaceLanguage))
        .accessibilityValue(selectedName)
        .accessibilityHint(copy.text(.chooseInterfaceLanguage))
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
            Text(copy.text(.interfaceLanguage))
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .padding(.horizontal, 10)
                .padding(.top, 8)
                .padding(.bottom, 3)
            ForEach(AppInterfaceLanguage.allCases, id: \.rawValue) {
                language in
                optionButton(language)
            }
        }
        .padding(4)
        .frame(width: 190)
    }

    private func optionButton(
        _ language: AppInterfaceLanguage
    ) -> some View {
        let name = optionName(for: language)
        return Button {
            selection = language
            isPresented = false
        } label: {
            HStack(spacing: 10) {
                Text(name)
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
        .accessibilityLabel(name)
        .accessibilityValue(
            selection == language ? copy.text(.selected) : ""
        )
    }

    private func optionName(
        for language: AppInterfaceLanguage
    ) -> String {
        switch language {
        case .system:
            copy.text(.followSystem)
        case .zhHans:
            "中文"
        case .english:
            "English"
        }
    }
}

private struct ExitApplicationButton: View {
    let copy: AppCopy
    let action: () -> Void
    @State private var isHovered = false

    var body: some View {
        Button(action: action) {
            Label(copy.text(.quitEMKE), systemImage: "power")
                .font(.system(size: 13, weight: .medium))
                .frame(maxWidth: .infinity, minHeight: 40)
                .contentShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .background(RoundedRectangle(cornerRadius: 10).fill(isHovered ? EMKEVisualStyle.surfaceBackground.opacity(0.82) : EMKEVisualStyle.surfaceBackground))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(EMKEVisualStyle.separator, lineWidth: 1))
        .onHover { isHovered = $0 }
        .accessibilityLabel(copy.text(.quitEMKE))
    }
}

private struct AudioDeviceMenuButton: View {
    let copy: AppCopy
    let title: String
    let devices: [AudioDevice]
    @Binding var selection: String?
    @State private var isPresented = false

    private var selectedName: String {
        devices.first { $0.uid == selection }?.name
            ?? copy.text(.chooseDevice)
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
        .accessibilityHint(copy.text(.chooseAudioDevice))
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
        .accessibilityValue(
            selection == device.uid ? copy.text(.selected) : ""
        )
    }
}
