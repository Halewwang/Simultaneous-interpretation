import AppKit
import EMKEAudioEngine
import SwiftUI

enum OnboardingDeviceSelectionPolicy {
    @MainActor
    static func makeAction(
        isLocked: @escaping @MainActor () -> Bool,
        updateSelection: @escaping @MainActor (String) -> Void
    ) -> @MainActor (String) -> Bool {
        { deviceUID in
            guard !isLocked() else { return false }
            updateSelection(deviceUID)
            return true
        }
    }
}

struct OnboardingView: View {
    @ObservedObject var model: MenuBarModel
    @ObservedObject var controller: OnboardingWindowController
    let refreshesStateOnStepChange: Bool
    @State private var isProviderEditorPresented = false

    init(
        model: MenuBarModel,
        controller: OnboardingWindowController,
        refreshesStateOnStepChange: Bool = true
    ) {
        self.model = model
        self.controller = controller
        self.refreshesStateOnStepChange = refreshesStateOnStepChange
    }

    private var copy: AppCopy { model.copy }

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .topLeading) {
                header
                    .position(x: 280, y: 38)
                Divider()
                    .opacity(EMKEVisualStyle.dividerOpacity)
                    .frame(width: 560, height: 1)
                    .position(x: 280, y: 76.5)
                stepContent
                    .frame(
                        width: 492,
                        height: 418,
                        alignment: .topLeading
                    )
                    .clipped()
                    .position(x: 280, y: 310)
                Divider()
                    .opacity(EMKEVisualStyle.dividerOpacity)
                    .frame(width: 560, height: 1)
                    .position(x: 280, y: 543.5)
                footer
                    .clipped()
                    .position(x: 280, y: 582)
            }
            .frame(
                width: geometry.size.width,
                height: geometry.size.height,
                alignment: .topLeading
            )
        }
        .frame(width: 560, height: 620)
        .clipped()
        .background(EMKEVisualStyle.panelBackground)
        .task(id: controller.flow.step) {
            guard refreshesStateOnStepChange else { return }
            await refreshCurrentStep()
        }
    }

    private var header: some View {
        HStack(spacing: 14) {
            Image(systemName: "waveform.path")
                .font(.system(size: 24, weight: .semibold))
                .frame(width: 28, height: 28)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text("EMKE Translation")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
                Text(copy.text(.gettingStarted))
                    .font(.system(size: 20, weight: .semibold))
                    .lineLimit(1)
                    .minimumScaleFactor(0.85)
            }
            Spacer()
        }
        .frame(width: 492, height: 76, alignment: .leading)
        .padding(.horizontal, 34)
        .clipped()
    }

    @ViewBuilder
    private var stepContent: some View {
        switch controller.flow.step {
        case .overview:
            overviewStep
        case .microphone:
            microphoneStep
        case .audioSetup:
            audioStep
        case .meetingSetup:
            meetingStep
        }
    }

    private var overviewStep: some View {
        VStack(alignment: .leading, spacing: 22) {
            stepHeading(
                copy.text(.onboardingOverviewTitle),
                body: copy.text(.onboardingOverviewBody),
                systemImage: "arrow.triangle.branch"
            )
            audioPath(
                from: copy.text(.physicalMicrophone),
                through: copy.text(.provider),
                to: "EMKE Virtual Microphone",
                systemImage: "mic.fill"
            )
            audioPath(
                from: "EMKE Virtual Speaker",
                through: copy.text(.provider),
                to: copy.text(.physicalOutput),
                systemImage: "headphones"
            )
            Label(
                copy.text(.audioDirectToProvider),
                systemImage: "lock.shield"
            )
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(EMKEVisualStyle.secondaryText)
        }
    }

    private var microphoneStep: some View {
        VStack(alignment: .leading, spacing: 24) {
            stepHeading(
                copy.text(.onboardingMicrophoneTitle),
                body: copy.text(.onboardingMicrophoneBody),
                systemImage: "mic.badge.plus"
            )
            microphoneStatus
            microphoneAction
        }
    }

    private var audioStep: some View {
        VStack(alignment: .leading, spacing: 12) {
            stepHeading(
                copy.text(.onboardingAudioTitle),
                body: copy.text(.onboardingAudioBody),
                systemImage: "waveform.badge.magnifyingglass"
            )
            statusCard(
                title: copy.text(.audioDevices),
                value: model.repairMessage
                    ?? "EMKE Virtual Speaker + EMKE Virtual Microphone",
                systemImage: model.repairMessage == nil
                    ? "checkmark.circle.fill"
                    : "exclamationmark.triangle.fill",
                tone: model.repairMessage == nil ? .green : EMKEVisualStyle.warning
            )
            devicePicker(
                title: copy.text(.physicalMicrophone),
                devices: model.physicalInputs,
                selection: $model.selectedInputUID
            )
            devicePicker(
                title: copy.text(.physicalOutput),
                devices: model.physicalOutputs,
                selection: $model.selectedOutputUID
            )
            localAudioActions
        }
    }

    private var meetingStep: some View {
        VStack(alignment: .leading, spacing: 16) {
            stepHeading(
                copy.text(.onboardingMeetingTitle),
                body: copy.text(.onboardingMeetingBody),
                systemImage: "person.2.wave.2"
            )
            providerFields
            Button(
                model.isTestingConnection
                    ? copy.text(.testing)
                    : copy.text(.testConnection)
            ) {
                Task { await model.testConnection() }
            }
            .disabled(model.selectionsLocked || !model.canTestConnection)

            if !model.connectionTestMessage.isEmpty {
                Label(
                    model.connectionTestMessage,
                    systemImage: "network.badge.shield.half.filled"
                )
                .font(.system(size: 11))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 12) {
                routingCard(
                    title: copy.text(.meetingAppSpeaker),
                    value: "EMKE Virtual Speaker",
                    systemImage: "speaker.wave.2.fill"
                )
                routingCard(
                    title: copy.text(.meetingAppMicrophone),
                    value: "EMKE Virtual Microphone",
                    systemImage: "mic.fill"
                )
            }
        }
    }

    private func stepHeading(
        _ title: String,
        body: String,
        systemImage: String
    ) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Label(title, systemImage: systemImage)
                .font(.system(size: 22, weight: .semibold))
            Text(body)
                .font(.system(size: 13))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
                .lineSpacing(2)
        }
    }

    private func audioPath(
        from: String,
        through: String,
        to: String,
        systemImage: String
    ) -> some View {
        HStack(spacing: 10) {
            Image(systemName: systemImage)
                .frame(width: 22)
                .foregroundStyle(EMKEVisualStyle.activityBlue)
            Text("\(from)  →  \(through)  →  \(to)")
                .font(.system(size: 12, weight: .medium))
                .fixedSize(horizontal: false, vertical: true)
                .lineSpacing(2)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .fill(EMKEVisualStyle.surfaceBackground)
        )
    }

    @ViewBuilder
    private var microphoneStatus: some View {
        switch model.microphonePermissionState {
        case .notDetermined:
            statusCard(
                title: copy.text(.onboardingMicrophoneTitle),
                value: copy.text(.notTested),
                systemImage: "circle.dotted",
                tone: EMKEVisualStyle.secondaryText
            )
        case .authorized:
            statusCard(
                title: copy.text(.onboardingMicrophoneTitle),
                value: copy.text(.onboardingAuthorized),
                systemImage: "checkmark.circle.fill",
                tone: .green
            )
        case .denied:
            statusCard(
                title: copy.text(.onboardingMicrophoneTitle),
                value: copy.text(.onboardingDenied),
                systemImage: "exclamationmark.circle.fill",
                tone: EMKEVisualStyle.failure
            )
        case .restricted:
            statusCard(
                title: copy.text(.onboardingMicrophoneTitle),
                value: copy.text(.onboardingRestricted),
                systemImage: "lock.circle.fill",
                tone: EMKEVisualStyle.warning
            )
        }
    }

    @ViewBuilder
    private var microphoneAction: some View {
        let presentation = OnboardingMicrophonePresentation.make(
            model.microphonePermissionState
        )
        switch presentation.action {
        case .requestAccess:
            Button(copy.text(.onboardingAllowMicrophone)) {
                Task {
                    await model.requestMicrophonePermissionForOnboarding()
                }
            }
            .buttonStyle(.borderedProminent)
        case .openSystemSettings:
            Button(copy.text(.onboardingOpenSystemSettings)) {
                openMicrophoneSystemSettings()
            }
            .buttonStyle(.borderedProminent)
        case .continueFlow:
            EmptyView()
        }
    }

    private func statusCard(
        title: String,
        value: String,
        systemImage: String,
        tone: Color
    ) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: systemImage)
                .foregroundStyle(tone)
                .font(.system(size: 17))
                .frame(width: 22)
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.system(size: 12, weight: .medium))
                Text(value)
                    .font(.system(size: 12))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .fill(EMKEVisualStyle.surfaceBackground)
        )
    }

    private func devicePicker(
        title: String,
        devices: [AudioDevice],
        selection: Binding<String?>
    ) -> some View {
        OnboardingDevicePicker(
            copy: copy,
            title: title,
            devices: devices,
            selection: selection,
            isDisabled: model.audioDeviceControlsLocked,
            selectDevice: OnboardingDeviceSelectionPolicy.makeAction(
                isLocked: { model.audioDeviceControlsLocked },
                updateSelection: { selection.wrappedValue = $0 }
            )
        )
    }

    private var localAudioActions: some View {
        VStack(alignment: .leading, spacing: 10) {
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
                Spacer()
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

    private var providerFields: some View {
        Button {
            isProviderEditorPresented.toggle()
        } label: {
            VStack(alignment: .leading, spacing: 7) {
                providerSummaryRow("API Key", value: model.apiKeyStatusText)
                providerSummaryRow("Base URL", value: model.baseURLString)
                providerSummaryRow(copy.text(.modelID), value: model.modelID)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 10)
                    .fill(EMKEVisualStyle.surfaceBackground)
            )
            .overlay(alignment: .topTrailing) {
                Image(systemName: "pencil")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
                    .padding(12)
                    .accessibilityHidden(true)
            }
        }
        .buttonStyle(.plain)
        .disabled(model.selectionsLocked)
        .popover(
            isPresented: $isProviderEditorPresented,
            arrowEdge: .bottom
        ) {
            providerEditor
        }
        .accessibilityLabel(copy.text(.provider))
    }

    private func providerSummaryRow(
        _ title: String,
        value: String
    ) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(title)
                .font(.system(size: 11))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .frame(width: 70, alignment: .leading)
            Text(value)
                .font(.system(size: 11, weight: .medium))
                .lineLimit(1)
            Spacer(minLength: 18)
        }
    }

    private var providerEditor: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(copy.text(.provider))
                .font(.system(size: 14, weight: .semibold))
            SecureField(
                copy.text(.enterNewAPIKey),
                text: $model.apiKeyDraft
            )
            .textFieldStyle(.roundedBorder)
            .disabled(model.selectionsLocked)
            TextField("Base URL", text: $model.baseURLString)
                .textFieldStyle(.roundedBorder)
                .disabled(model.selectionsLocked)
            TextField(copy.text(.modelID), text: $model.modelID)
                .textFieldStyle(.roundedBorder)
                .disabled(model.selectionsLocked)
        }
        .padding(16)
        .frame(width: 360)
    }

    private func routingCard(
        title: String,
        value: String,
        systemImage: String
    ) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            Label(title, systemImage: systemImage)
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
            Text(value)
                .font(.system(size: 12, weight: .semibold))
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(EMKEVisualStyle.surfaceBackground)
        )
    }

    private var footer: some View {
        VStack(spacing: 8) {
            HStack {
                Button(
                    copy.text(.onboardingSkipForNow),
                    action: controller.skipForNow
                )
                .buttonStyle(.plain)
                Spacer()
                Button(
                    copy.text(.onboardingDoNotShowAgain),
                    action: controller.doNotShowAgain
                )
                .buttonStyle(.plain)
            }
            .font(.system(size: 11))
            .foregroundStyle(EMKEVisualStyle.secondaryText)

            HStack(spacing: 12) {
                if controller.flow.canMoveBackward {
                    Button(
                        copy.text(.onboardingBack),
                        action: controller.moveBackward
                    )
                }
                let progressText =
                    "\(controller.flow.step.rawValue + 1) / \(OnboardingStep.allCases.count)"
                Text(progressText)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .accessibilityLabel(copy.text(.onboardingProgress))
                .accessibilityValue(progressText)
                Spacer()
                if controller.flow.canMoveForward {
                    Button(
                        copy.text(.onboardingContinue),
                        action: controller.moveForward
                    )
                    .buttonStyle(.borderedProminent)
                } else {
                    Button(
                        copy.text(.onboardingFinish),
                        action: controller.complete
                    )
                    .buttonStyle(.borderedProminent)
                }
            }
        }
        .frame(width: 512, height: 56, alignment: .topLeading)
        .padding(.horizontal, 24)
        .padding(.vertical, 10)
    }

    private func refreshCurrentStep() async {
        switch controller.flow.step {
        case .overview:
            return
        case .microphone:
            await model.refreshMicrophonePermissionState()
        case .audioSetup:
            await model.reloadDevicesAsync()
        case .meetingSetup:
            return
        }
    }

    private func openMicrophoneSystemSettings() {
        guard let url = URL(
            string:
                "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone"
        ) else { return }
        NSWorkspace.shared.open(url)
    }
}

private struct OnboardingDevicePicker: View {
    let copy: AppCopy
    let title: String
    let devices: [AudioDevice]
    @Binding var selection: String?
    let isDisabled: Bool
    let selectDevice: @MainActor (String) -> Bool
    @State private var isPresented = false

    private var selectedName: String {
        devices.first { $0.uid == selection }?.name
            ?? copy.text(.chooseDevice)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title)
                .font(.system(size: 11))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
            Button {
                isPresented.toggle()
            } label: {
                HStack(spacing: 8) {
                    Text(selectedName)
                        .lineLimit(1)
                    Spacer()
                    Image(systemName: "chevron.down")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(EMKEVisualStyle.secondaryText)
                        .accessibilityHidden(true)
                }
                .font(.system(size: 12, weight: .medium))
                .padding(.horizontal, 10)
                .frame(height: 30)
                .background(
                    RoundedRectangle(cornerRadius: 7)
                        .fill(EMKEVisualStyle.surfaceBackground)
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 7)
                        .stroke(EMKEVisualStyle.separator, lineWidth: 1)
                )
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(devices.isEmpty || isDisabled)
            .popover(isPresented: $isPresented, arrowEdge: .bottom) {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(devices) { device in
                        Button {
                            guard selectDevice(device.uid) else {
                                isPresented = false
                                return
                            }
                            isPresented = false
                        } label: {
                            HStack {
                                Text(device.name)
                                Spacer()
                                if selection == device.uid {
                                    Image(systemName: "checkmark")
                                }
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 7)
                        }
                        .buttonStyle(.plain)
                        .disabled(isDisabled)
                    }
                }
                .padding(4)
                .frame(width: 280)
            }
            .onChange(of: isDisabled) { _, isDisabled in
                if isDisabled { isPresented = false }
            }
            .accessibilityLabel(title)
            .accessibilityValue(selectedName)
        }
    }
}
