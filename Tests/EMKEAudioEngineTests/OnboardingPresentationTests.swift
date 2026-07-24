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

@Test
@MainActor
func deviceSelectionActionQueriesAuthoritativeLockAtInvocation() {
    let state = AuthoritativeDeviceSelectionState(
        selection: "physical.input.old"
    )
    let action = OnboardingDeviceSelectionPolicy.makeAction(
        isLocked: { state.isLocked },
        updateSelection: { state.selection = $0 }
    )

    state.isLocked = true
    #expect(!action("physical.input.locked"))
    #expect(state.selection == "physical.input.old")

    state.isLocked = false
    #expect(action("physical.input.new"))
    #expect(state.selection == "physical.input.new")
}

@MainActor
private final class AuthoritativeDeviceSelectionState {
    var isLocked = false
    var selection: String?

    init(selection: String?) {
        self.selection = selection
    }
}
