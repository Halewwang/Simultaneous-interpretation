import Testing
@testable import EMKEMenuBarApp

@MainActor
private final class OnboardingProgressStoreStub:
    OnboardingProgressStoring
{
    var shouldPresentValue: Bool
    private(set) var completedVersions: [Int] = []

    init(shouldPresent: Bool = true) {
        shouldPresentValue = shouldPresent
    }

    func shouldPresent(currentVersion: Int) -> Bool {
        shouldPresentValue
    }

    func markCompleted(version: Int) {
        completedVersions.append(version)
        shouldPresentValue = false
    }
}

@MainActor
private final class OnboardingWindowPresenterStub:
    OnboardingWindowPresenting
{
    private(set) var showCount = 0
    private(set) var hideCount = 0

    func show() {
        showCount += 1
    }

    func hide() {
        hideCount += 1
    }
}

@MainActor
private func makeOnboardingController(
    store: OnboardingProgressStoreStub =
        OnboardingProgressStoreStub()
) -> OnboardingWindowController {
    let controller = OnboardingWindowController(progressStore: store)
    controller.attachWindow(OnboardingWindowPresenterStub())
    return controller
}

@Test @MainActor
func onboardingShowsOnlyWhenCurrentVersionIsIncomplete() {
    let store = OnboardingProgressStoreStub(shouldPresent: true)
    let controller = makeOnboardingController(store: store)

    controller.showIfNeeded()

    #expect(controller.isVisible)
    #expect(controller.flow.step == .overview)
}

@Test @MainActor
func completedOnboardingDoesNotPresentAgain() {
    let store = OnboardingProgressStoreStub(shouldPresent: false)
    let presenter = OnboardingWindowPresenterStub()
    let controller = OnboardingWindowController(progressStore: store)
    controller.attachWindow(presenter)

    controller.showIfNeeded()

    #expect(!controller.isVisible)
    #expect(presenter.showCount == 0)
}

@Test @MainActor
func skipForNowDoesNotCompleteButDoNotShowAgainDoes() {
    let store = OnboardingProgressStoreStub(shouldPresent: true)
    let controller = makeOnboardingController(store: store)

    controller.show()
    controller.skipForNow()
    #expect(store.completedVersions.isEmpty)

    controller.show()
    controller.doNotShowAgain()
    #expect(store.completedVersions == [1])
}

@Test @MainActor
func completePersistsCurrentVersionAndHides() {
    let store = OnboardingProgressStoreStub(shouldPresent: true)
    let presenter = OnboardingWindowPresenterStub()
    let controller = OnboardingWindowController(progressStore: store)
    controller.attachWindow(presenter)

    controller.show()
    controller.complete()

    #expect(store.completedVersions == [OnboardingVersion.current])
    #expect(!controller.isVisible)
    #expect(presenter.hideCount == 1)
}

@Test @MainActor
func settingsReopenRestartsAtFirstStep() {
    let controller = makeOnboardingController()
    controller.show()
    controller.moveForward()
    controller.moveForward()

    controller.show()

    #expect(controller.flow.step == .overview)
    #expect(controller.isVisible)
}
