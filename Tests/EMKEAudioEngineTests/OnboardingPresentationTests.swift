import Testing
@testable import EMKEMenuBarApp

@Test
func onboardingFlowHasFourBoundedSteps() {
    var flow = OnboardingFlowState()
    #expect(flow.step == .overview)
    #expect(!flow.canMoveBackward)

    flow.moveForward()
    #expect(flow.step == .microphone)
    flow.moveForward()
    #expect(flow.step == .audioSetup)
    flow.moveForward()
    #expect(flow.step == .meetingSetup)
    #expect(!flow.canMoveForward)
}

@Test
func onboardingFlowMovesBackwardWithoutCrossingOverview() {
    var flow = OnboardingFlowState()

    flow.moveBackward()
    #expect(flow.step == .overview)

    flow.moveForward()
    flow.moveBackward()
    #expect(flow.step == .overview)
}

@Test
func onboardingFlowRestartReturnsToOverview() {
    var flow = OnboardingFlowState()
    flow.moveForward()
    flow.restart()

    #expect(flow.step == .overview)
}

@Test
func microphonePresentationNeverOffersRepeatPromptAfterDenial() {
    #expect(
        OnboardingMicrophonePresentation.make(.notDetermined).action
            == .requestAccess
    )
    #expect(
        OnboardingMicrophonePresentation.make(.authorized).action
            == .continueFlow
    )
    #expect(
        OnboardingMicrophonePresentation.make(.denied).action
            == .openSystemSettings
    )
    #expect(
        OnboardingMicrophonePresentation.make(.restricted).action
            == .continueFlow
    )
}
