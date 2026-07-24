enum OnboardingStep: Int, CaseIterable, Sendable {
    case overview
    case microphone
    case audioSetup
    case meetingSetup
}

struct OnboardingFlowState: Equatable, Sendable {
    private(set) var step: OnboardingStep = .overview

    var canMoveBackward: Bool { step.rawValue > 0 }
    var canMoveForward: Bool {
        step.rawValue < OnboardingStep.allCases.count - 1
    }

    mutating func moveForward() {
        guard canMoveForward,
              let next = OnboardingStep(rawValue: step.rawValue + 1) else {
            return
        }
        step = next
    }

    mutating func moveBackward() {
        guard canMoveBackward,
              let previous = OnboardingStep(rawValue: step.rawValue - 1) else {
            return
        }
        step = previous
    }

    mutating func restart() {
        step = .overview
    }
}

enum OnboardingMicrophoneAction: Equatable, Sendable {
    case requestAccess
    case openSystemSettings
    case continueFlow
}

struct OnboardingMicrophonePresentation: Equatable, Sendable {
    let action: OnboardingMicrophoneAction

    static func make(
        _ state: MicrophonePermissionState
    ) -> OnboardingMicrophonePresentation {
        switch state {
        case .notDetermined:
            .init(action: .requestAccess)
        case .denied:
            .init(action: .openSystemSettings)
        case .restricted, .authorized:
            .init(action: .continueFlow)
        }
    }
}
