import Combine
import Testing
@testable import EMKEMenuBarApp

@MainActor
private final class UpdaterDriverStub: AppUpdateDriving {
    let availability = CurrentValueSubject<Bool, Never>(false)
    private(set) var checkCount = 0

    var canCheckForUpdates: Bool {
        availability.value
    }

    var canCheckForUpdatesPublisher: AnyPublisher<Bool, Never> {
        availability.eraseToAnyPublisher()
    }

    func checkForUpdates() {
        checkCount += 1
    }
}

@Test @MainActor
func updateControllerMirrorsAvailabilityAndForwardsManualCheck() {
    let driver = UpdaterDriverStub()
    let controller = AppUpdateController(driver: driver)

    driver.availability.send(true)
    controller.refreshAvailability()
    controller.checkForUpdates()

    #expect(controller.canCheckForUpdates)
    #expect(driver.checkCount == 1)
}

@Test @MainActor
func updateControllerDoesNotForwardManualCheckWhileUnavailable() {
    let driver = UpdaterDriverStub()
    let controller = AppUpdateController(driver: driver)

    controller.checkForUpdates()

    #expect(!controller.canCheckForUpdates)
    #expect(driver.checkCount == 0)
}

@Test @MainActor
func updateControllerRechecksLiveAvailabilityBeforeManualCheck() {
    let driver = UpdaterDriverStub()
    driver.availability.send(true)
    let controller = AppUpdateController(driver: driver)

    driver.availability.send(false)
    controller.checkForUpdates()

    #expect(driver.checkCount == 0)
    #expect(!controller.canCheckForUpdates)
}
