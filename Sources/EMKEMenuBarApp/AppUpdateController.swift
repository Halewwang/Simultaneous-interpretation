import Combine
import Sparkle

@MainActor
protocol AppUpdateDriving: AnyObject {
    var canCheckForUpdates: Bool { get }
    var canCheckForUpdatesPublisher: AnyPublisher<Bool, Never> { get }
    func checkForUpdates()
}

@MainActor
final class SparkleUpdateDriver: AppUpdateDriving {
    private let controller = SPUStandardUpdaterController(
        startingUpdater: true,
        updaterDelegate: nil,
        userDriverDelegate: nil
    )

    var canCheckForUpdates: Bool {
        controller.updater.canCheckForUpdates
    }

    var canCheckForUpdatesPublisher: AnyPublisher<Bool, Never> {
        controller.updater
            .publisher(for: \.canCheckForUpdates)
            .eraseToAnyPublisher()
    }

    func checkForUpdates() {
        controller.checkForUpdates(nil)
    }
}

@MainActor
final class AppUpdateController: ObservableObject {
    @Published private(set) var canCheckForUpdates = false

    private let driver: any AppUpdateDriving
    private var availabilityCancellable: AnyCancellable?

    init(driver: any AppUpdateDriving = SparkleUpdateDriver()) {
        self.driver = driver
        refreshAvailability()
        availabilityCancellable = driver.canCheckForUpdatesPublisher
            .receive(on: RunLoop.main)
            .sink { [weak self] canCheckForUpdates in
                self?.canCheckForUpdates = canCheckForUpdates
            }
    }

    func refreshAvailability() {
        canCheckForUpdates = driver.canCheckForUpdates
    }

    func checkForUpdates() {
        guard canCheckForUpdates else { return }
        driver.checkForUpdates()
        refreshAvailability()
    }
}
