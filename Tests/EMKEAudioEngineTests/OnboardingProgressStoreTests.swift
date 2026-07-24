import Foundation
import Testing
@testable import EMKEMenuBarApp

@Test @MainActor
func missingAndInvalidOnboardingVersionsRequirePresentation() {
    let suite = "OnboardingProgressStoreTests.\(UUID())"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    let store = UserDefaultsOnboardingProgressStore(defaults: defaults)

    #expect(store.shouldPresent(currentVersion: 1))
    defaults.set("invalid", forKey: "completedOnboardingVersion")
    #expect(store.shouldPresent(currentVersion: 1))
}

@Test
func onboardingVersionStartsAtOne() {
    #expect(OnboardingVersion.current == 1)
}

@Test @MainActor
func completionSuppressesCurrentVersionButNotFutureVersion() {
    let suite = "OnboardingProgressStoreTests.\(UUID())"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    let store = UserDefaultsOnboardingProgressStore(defaults: defaults)

    store.markCompleted(version: 1)

    #expect(!store.shouldPresent(currentVersion: 1))
    #expect(store.shouldPresent(currentVersion: 2))
}
