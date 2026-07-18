import CoreAudio
@testable import EMKEAudioEngine
@testable import EMKEMenuBarApp
import Testing

private struct MenuBarDeviceProviderStub: AudioDeviceProviding {
    let inventory: [AudioDevice]

    func devices() throws -> [AudioDevice] {
        inventory
    }
}

private actor AudioEngineControllerStub: AudioEngineControlling {
    private var configurations: [AudioEngineConfiguration] = []
    private var stopCount = 0

    func start(configuration: AudioEngineConfiguration) async throws {
        configurations.append(configuration)
    }

    func stop() async {
        stopCount += 1
    }

    func snapshot() -> ([AudioEngineConfiguration], Int) {
        (configurations, stopCount)
    }
}

private func menuBarDevices(includeDriver: Bool = true) -> [AudioDevice] {
    var devices: [AudioDevice] = [
        AudioDevice(
            id: 20,
            uid: "physical.input",
            name: "Physical Input",
            inputChannelCount: 1,
            outputChannelCount: 0,
            nominalSampleRate: 48_000
        ),
        AudioDevice(
            id: 21,
            uid: "physical.output",
            name: "Physical Output",
            inputChannelCount: 0,
            outputChannelCount: 2,
            nominalSampleRate: 48_000
        ),
    ]
    if includeDriver {
        devices.append(contentsOf: [
            AudioDevice(
                id: 10,
                uid: AudioDevice.virtualSpeakerUID,
                name: "EMKE Virtual Speaker",
                inputChannelCount: 2,
                outputChannelCount: 2,
                nominalSampleRate: 48_000
            ),
            AudioDevice(
                id: 11,
                uid: AudioDevice.virtualMicrophoneUID,
                name: "EMKE Virtual Microphone",
                inputChannelCount: 2,
                outputChannelCount: 2,
                nominalSampleRate: 48_000
            ),
        ])
    }
    return devices
}

@Test @MainActor
func absentDriverDisablesStartAndShowsRepairMessage() {
    let model = MenuBarModel(
        provider: MenuBarDeviceProviderStub(
            inventory: menuBarDevices(includeDriver: false)
        ),
        engine: AudioEngineControllerStub()
    )

    #expect(model.readiness == .driverUnavailable)
    #expect(!model.canStart)
    #expect(model.repairMessage == "未检测到 EMKE 虚拟音频驱动")
}

@Test @MainActor
func missingPhysicalSelectionsDisableStartInOrder() {
    let model = MenuBarModel(
        provider: MenuBarDeviceProviderStub(inventory: menuBarDevices()),
        engine: AudioEngineControllerStub()
    )

    #expect(model.readiness == .selectPhysicalInput)
    model.selectedInputUID = "physical.input"
    #expect(model.readiness == .selectPhysicalOutput)
    #expect(!model.canStart)
}

@Test @MainActor
func completeSelectionsEnableStart() {
    let model = MenuBarModel(
        provider: MenuBarDeviceProviderStub(inventory: menuBarDevices()),
        engine: AudioEngineControllerStub()
    )
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"

    #expect(model.readiness == .ready)
    #expect(model.canStart)
}

@Test @MainActor
func activeAudioLocksDeviceSelections() {
    let model = MenuBarModel(
        provider: MenuBarDeviceProviderStub(inventory: menuBarDevices()),
        engine: AudioEngineControllerStub()
    )

    model.state = .running

    #expect(model.selectionsLocked)
    #expect(!model.canStart)
    #expect(model.statusText == "本地音频运行中")
}

@Test @MainActor
func stoppedAndErrorStatesNeverClaimTranslationIsActive() {
    let model = MenuBarModel(
        provider: MenuBarDeviceProviderStub(inventory: menuBarDevices()),
        engine: AudioEngineControllerStub()
    )

    model.state = .stopped
    #expect(model.statusText != "翻译中")
    model.state = .failed(
        .endpointStartFailed(role: .physicalOutput)
    )
    #expect(model.statusText != "翻译中")
}

@Test @MainActor
func startAndStopUseResolvedDeviceSelection() async throws {
    let engine = AudioEngineControllerStub()
    let model = MenuBarModel(
        provider: MenuBarDeviceProviderStub(inventory: menuBarDevices()),
        engine: engine
    )
    model.selectedInputUID = "physical.input"
    model.selectedOutputUID = "physical.output"

    await model.start()
    let started = await engine.snapshot()

    #expect(model.state == .running)
    #expect(started.0.count == 1)
    #expect(started.0.first?.selection.physicalInput.id == 20)
    #expect(started.0.first?.selection.physicalOutput.id == 21)

    await model.stop()
    let stopped = await engine.snapshot()
    #expect(model.state == .stopped)
    #expect(stopped.1 == 1)
}
