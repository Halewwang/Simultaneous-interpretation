import Foundation

enum OnboardingVersion {
    static let current = 1
}

@MainActor
protocol OnboardingProgressStoring {
    func shouldPresent(currentVersion: Int) -> Bool
    func markCompleted(version: Int)
}

@MainActor
struct UserDefaultsOnboardingProgressStore: OnboardingProgressStoring {
    private let defaults: UserDefaults
    private let key = "completedOnboardingVersion"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func shouldPresent(currentVersion: Int) -> Bool {
        guard let value = defaults.object(forKey: key) as? Int else {
            return true
        }
        return value < currentVersion
    }

    func markCompleted(version: Int) {
        defaults.set(version, forKey: key)
    }
}
