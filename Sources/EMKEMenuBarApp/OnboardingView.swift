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

private extension OnboardingStep {
    var copyKey: AppCopyKey {
        switch self {
        case .overview:
            .onboardingStepOverview
        case .microphone:
            .onboardingStepMicrophone
        case .audioSetup:
            .onboardingStepAudio
        case .meetingSetup:
            .onboardingStepMeeting
        }
    }

    var systemImage: String {
        switch self {
        case .overview:
            "arrow.triangle.branch"
        case .microphone:
            "mic.fill"
        case .audioSetup:
            "waveform"
        case .meetingSetup:
            "person.2.fill"
        }
    }
}

struct OnboardingView: View {
    @ObservedObject var model: MenuBarModel
    @ObservedObject var controller: OnboardingWindowController
    let refreshesStateOnStepChange: Bool
    @State private var isProviderEditorPresented = false
    @State private var isRequestingMicrophonePermission = false

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
        HStack(spacing: 0) {
            stepRail
                .frame(width: OnboardingLayoutMetrics.stepRailWidth)
            mainContent
        }
        .frame(
            width: OnboardingLayoutMetrics.windowWidth,
            height: OnboardingLayoutMetrics.windowHeight
        )
        .background(EMKEVisualStyle.panelBackground)
        .ignoresSafeArea()
        .task(id: controller.flow.step) {
            guard refreshesStateOnStepChange else { return }
            await refreshCurrentStep()
        }
    }

    private var stepRail: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: "waveform.path")
                    .font(.system(size: 18, weight: .semibold))
                    .frame(width: 30, height: 30)
                    .background(
                        RoundedRectangle(cornerRadius: 9)
                            .fill(EMKEVisualStyle.primaryText)
                    )
                    .foregroundStyle(Color.white)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 1) {
                    Text("EMKE")
                        .font(.system(size: 13, weight: .bold))
                    Text("Translation")
                        .font(.system(size: 9, weight: .medium))
                        .foregroundStyle(EMKEVisualStyle.secondaryText)
                }
            }

            VStack(alignment: .leading, spacing: 18) {
                ForEach(OnboardingStep.allCases, id: \.rawValue) { step in
                    stepRailItem(step)
                }
            }
            .padding(.top, 34)

            Spacer(minLength: 16)

            Text(copy.text(.audioDirectToProvider))
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(EMKEVisualStyle.secondaryText)

            Button(
                copy.text(.onboardingDoNotShowAgain),
                action: controller.doNotShowAgain
            )
            .buttonStyle(.plain)
            .font(.system(size: 10, weight: .medium))
            .foregroundStyle(EMKEVisualStyle.secondaryText)
            .padding(.top, 10)
        }
        .padding(.horizontal, 18)
        .padding(.top, 22)
        .padding(.bottom, 18)
        .frame(maxHeight: .infinity, alignment: .topLeading)
        .background(Color(nsColor: .underPageBackgroundColor))
    }

    private func stepRailItem(_ step: OnboardingStep) -> some View {
        let isCurrent = step == controller.flow.step
        let isComplete = step.rawValue < controller.flow.step.rawValue

        return HStack(spacing: 9) {
            ZStack {
                Circle()
                    .fill(
                        isCurrent
                            ? EMKEVisualStyle.activityBlue
                            : Color.white.opacity(0.75)
                    )
                    .overlay(
                        Circle().stroke(
                            isComplete || isCurrent
                                ? EMKEVisualStyle.activityBlue
                                : EMKEVisualStyle.separator,
                            lineWidth: 1
                        )
                    )
                Image(
                    systemName: isComplete
                        ? "checkmark"
                        : step.systemImage
                )
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(
                    isCurrent
                        ? Color.white
                        : isComplete
                            ? EMKEVisualStyle.activityBlue
                            : EMKEVisualStyle.secondaryText
                )
            }
            .frame(width: 23, height: 23)

            Text(copy.text(step.copyKey))
                .font(
                    .system(
                        size: 11,
                        weight: isCurrent ? .semibold : .medium
                    )
                )
                .foregroundStyle(
                    isCurrent
                        ? EMKEVisualStyle.primaryText
                        : EMKEVisualStyle.secondaryText
                )
                .lineLimit(2)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(copy.text(step.copyKey))
        .accessibilityValue(
            "\(step.rawValue + 1) / \(OnboardingStep.allCases.count)"
        )
        .accessibilityAddTraits(isCurrent ? .isSelected : [])
    }

    private var mainContent: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(copy.text(.gettingStarted))
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(EMKEVisualStyle.activityBlue)
                Spacer()
                let progressText =
                    "\(controller.flow.step.rawValue + 1) / \(OnboardingStep.allCases.count)"
                Text(progressText)
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
                    .padding(.horizontal, 9)
                    .padding(.vertical, 5)
                    .background(
                        Capsule().fill(EMKEVisualStyle.surfaceBackground)
                    )
                    .accessibilityLabel(copy.text(.onboardingProgress))
                    .accessibilityValue(progressText)
            }

            stepContent
                .frame(
                    maxWidth: .infinity,
                    maxHeight: .infinity,
                    alignment: .topLeading
                )
                .padding(.top, 14)

            footer
                .frame(height: OnboardingLayoutMetrics.footerHeight)
        }
        .padding(
            .horizontal,
            OnboardingLayoutMetrics.mainHorizontalPadding
        )
        .padding(.top, OnboardingLayoutMetrics.mainVerticalPadding)
        .padding(.bottom, 16)
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
        VStack(alignment: .leading, spacing: 14) {
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
        }
    }

    private var microphoneStep: some View {
        VStack(alignment: .leading, spacing: 16) {
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
        VStack(alignment: .leading, spacing: 9) {
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
        VStack(alignment: .leading, spacing: 10) {
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

            VStack(spacing: 8) {
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
            .frame(maxWidth: .infinity)
        }
    }

    private func stepHeading(
        _ title: String,
        body: String,
        systemImage: String
    ) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            Label(title, systemImage: systemImage)
                .font(.system(size: 21, weight: .semibold))
            Text(body)
                .font(.system(size: 12))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
                .lineSpacing(1.5)
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
        .padding(11)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 11)
                .fill(Color.white.opacity(0.72))
                .overlay(
                    RoundedRectangle(cornerRadius: 11)
                        .stroke(
                            EMKEVisualStyle.separator,
                            lineWidth: 1
                        )
                )
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
            Button(
                isRequestingMicrophonePermission
                    ? copy.text(.onboardingWaitingForMicrophone)
                    : copy.text(.onboardingAllowMicrophone)
            ) {
                guard !isRequestingMicrophonePermission else { return }
                isRequestingMicrophonePermission = true
                Task {
                    await model.requestMicrophonePermissionForOnboarding()
                    isRequestingMicrophonePermission = false
                    controller.restoreAfterExternalPrompt()
                }
            }
            .buttonStyle(.borderedProminent)
            .disabled(isRequestingMicrophonePermission)
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
        .padding(11)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 11)
                .fill(Color.white.opacity(0.72))
                .overlay(
                    RoundedRectangle(cornerRadius: 11)
                        .stroke(
                            EMKEVisualStyle.separator,
                            lineWidth: 1
                        )
                )
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
                    .fill(Color.white.opacity(0.72))
                    .overlay(
                        RoundedRectangle(cornerRadius: 10)
                            .stroke(
                                EMKEVisualStyle.separator,
                                lineWidth: 1
                            )
                    )
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
                .fill(Color.white.opacity(0.72))
                .overlay(
                    RoundedRectangle(cornerRadius: 10)
                        .stroke(
                            EMKEVisualStyle.separator,
                            lineWidth: 1
                        )
                )
        )
    }

    private var footer: some View {
        HStack(spacing: 12) {
            Button(
                copy.text(.onboardingSkipForNow),
                action: controller.skipForNow
            )
            .buttonStyle(.plain)
            .foregroundStyle(EMKEVisualStyle.secondaryText)

            if controller.flow.canMoveBackward {
                Button(
                    copy.text(.onboardingBack),
                    action: controller.moveBackward
                )
            }

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
        .font(.system(size: 11, weight: .medium))
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
