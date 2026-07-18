import CoreAudio
@testable import EMKEAudioEngine
import Testing

private struct DeviceProviderStub: AudioDeviceProviding {
    let inventory: [AudioDevice]

    func devices() throws -> [AudioDevice] {
        inventory
    }
}

private extension AudioDevice {
    static func fixture(
        id: AudioObjectID,
        uid: String,
        name: String? = nil,
        input: Int,
        output: Int,
        sampleRate: Double = 48_000
    ) -> AudioDevice {
        AudioDevice(
            id: id,
            uid: uid,
            name: name ?? uid,
            inputChannelCount: input,
            outputChannelCount: output,
            nominalSampleRate: sampleRate
        )
    }
}

@Test func selectionUsesUIDsAndExcludesVirtualDevicesFromPhysicalChoices() throws {
    let provider = DeviceProviderStub(inventory: [
        .fixture(
            id: 10,
            uid: AudioDevice.virtualSpeakerUID,
            input: 2,
            output: 2
        ),
        .fixture(
            id: 11,
            uid: AudioDevice.virtualMicrophoneUID,
            input: 2,
            output: 2
        ),
        .fixture(id: 20, uid: "physical.mic", input: 1, output: 0),
        .fixture(
            id: 21,
            uid: "physical.headphones",
            input: 0,
            output: 2
        ),
    ])
    let catalog = AudioDeviceCatalog(provider: provider)

    let selection = try catalog.resolve(
        physicalInputUID: "physical.mic",
        physicalOutputUID: "physical.headphones"
    )

    #expect(selection.virtualSpeaker.id == 10)
    #expect(selection.virtualMicrophone.id == 11)
    #expect(selection.physicalInput.id == 20)
    #expect(selection.physicalOutput.id == 21)
    #expect(try catalog.physicalInputs().map(\.uid) == ["physical.mic"])
    #expect(
        try catalog.physicalOutputs().map(\.uid)
            == ["physical.headphones"]
    )
}

@Test func physicalChoicesAreSortedByNameThenUID() throws {
    let provider = DeviceProviderStub(inventory: [
        .fixture(
            id: 1,
            uid: "output.zulu",
            name: "Zulu",
            input: 0,
            output: 2
        ),
        .fixture(
            id: 2,
            uid: "output.alpha.2",
            name: "Alpha",
            input: 0,
            output: 2
        ),
        .fixture(
            id: 3,
            uid: "output.alpha.1",
            name: "Alpha",
            input: 0,
            output: 2
        ),
    ])

    let outputs = try AudioDeviceCatalog(provider: provider).physicalOutputs()

    #expect(outputs.map(\.uid) == [
        "output.alpha.1",
        "output.alpha.2",
        "output.zulu",
    ])
}

@Test func missingVirtualSpeakerIsReportedPrecisely() {
    let provider = DeviceProviderStub(inventory: [
        .fixture(
            id: 11,
            uid: AudioDevice.virtualMicrophoneUID,
            input: 2,
            output: 2
        ),
        .fixture(id: 20, uid: "physical.mic", input: 1, output: 0),
        .fixture(id: 21, uid: "physical.output", input: 0, output: 2),
    ])

    #expect(throws: AudioDeviceCatalogError.virtualSpeakerUnavailable) {
        try AudioDeviceCatalog(provider: provider).resolve(
            physicalInputUID: "physical.mic",
            physicalOutputUID: "physical.output"
        )
    }
}

@Test func missingVirtualMicrophoneIsReportedPrecisely() {
    let provider = DeviceProviderStub(inventory: [
        .fixture(
            id: 10,
            uid: AudioDevice.virtualSpeakerUID,
            input: 2,
            output: 2
        ),
        .fixture(id: 20, uid: "physical.mic", input: 1, output: 0),
        .fixture(id: 21, uid: "physical.output", input: 0, output: 2),
    ])

    #expect(throws: AudioDeviceCatalogError.virtualMicrophoneUnavailable) {
        try AudioDeviceCatalog(provider: provider).resolve(
            physicalInputUID: "physical.mic",
            physicalOutputUID: "physical.output"
        )
    }
}

@Test func missingSavedPhysicalUIDIsReportedPrecisely() {
    let provider = DeviceProviderStub(inventory: [
        .fixture(
            id: 10,
            uid: AudioDevice.virtualSpeakerUID,
            input: 2,
            output: 2
        ),
        .fixture(
            id: 11,
            uid: AudioDevice.virtualMicrophoneUID,
            input: 2,
            output: 2
        ),
        .fixture(id: 21, uid: "physical.output", input: 0, output: 2),
    ])

    #expect(
        throws: AudioDeviceCatalogError.physicalInputUnavailable(
            uid: "missing.mic"
        )
    ) {
        try AudioDeviceCatalog(provider: provider).resolve(
            physicalInputUID: "missing.mic",
            physicalOutputUID: "physical.output"
        )
    }
}

@Test func missingSavedPhysicalOutputUIDIsReportedPrecisely() {
    let provider = DeviceProviderStub(inventory: [
        .fixture(
            id: 10,
            uid: AudioDevice.virtualSpeakerUID,
            input: 2,
            output: 2
        ),
        .fixture(
            id: 11,
            uid: AudioDevice.virtualMicrophoneUID,
            input: 2,
            output: 2
        ),
        .fixture(id: 20, uid: "physical.mic", input: 1, output: 0),
    ])

    #expect(
        throws: AudioDeviceCatalogError.physicalOutputUnavailable(
            uid: "missing.output"
        )
    ) {
        try AudioDeviceCatalog(provider: provider).resolve(
            physicalInputUID: "physical.mic",
            physicalOutputUID: "missing.output"
        )
    }
}

@Test func selectedDevicesMustHaveChannelsInTheirRequestedDirection() {
    let provider = DeviceProviderStub(inventory: [
        .fixture(
            id: 10,
            uid: AudioDevice.virtualSpeakerUID,
            input: 2,
            output: 2
        ),
        .fixture(
            id: 11,
            uid: AudioDevice.virtualMicrophoneUID,
            input: 2,
            output: 2
        ),
        .fixture(id: 20, uid: "physical.mic", input: 0, output: 2),
        .fixture(id: 21, uid: "physical.output", input: 2, output: 0),
    ])
    let catalog = AudioDeviceCatalog(provider: provider)

    #expect(
        throws: AudioDeviceCatalogError.deviceHasNoInput(
            uid: "physical.mic"
        )
    ) {
        try catalog.resolve(
            physicalInputUID: "physical.mic",
            physicalOutputUID: "physical.output"
        )
    }
}

@Test func selectedOutputMustHaveOutputChannels() {
    let provider = DeviceProviderStub(inventory: [
        .fixture(
            id: 10,
            uid: AudioDevice.virtualSpeakerUID,
            input: 2,
            output: 2
        ),
        .fixture(
            id: 11,
            uid: AudioDevice.virtualMicrophoneUID,
            input: 2,
            output: 2
        ),
        .fixture(id: 20, uid: "physical.mic", input: 1, output: 0),
        .fixture(id: 21, uid: "physical.output", input: 2, output: 0),
    ])

    #expect(
        throws: AudioDeviceCatalogError.deviceHasNoOutput(
            uid: "physical.output"
        )
    ) {
        try AudioDeviceCatalog(provider: provider).resolve(
            physicalInputUID: "physical.mic",
            physicalOutputUID: "physical.output"
        )
    }
}

private let installedDriverIsAvailable: Bool = {
    guard let devices = try? CoreAudioDeviceProvider().devices() else {
        return false
    }
    let uids = Set(devices.map(\.uid))
    return uids.contains(AudioDevice.virtualSpeakerUID)
        && uids.contains(AudioDevice.virtualMicrophoneUID)
}()

@Test(
    .enabled(
        if: installedDriverIsAvailable,
        "EMKE virtual audio driver is not installed"
    )
)
func installedDriverAppearsInCoreAudio() throws {
    let devices = try CoreAudioDeviceProvider().devices()
    let speaker = try #require(
        devices.first { $0.uid == AudioDevice.virtualSpeakerUID }
    )
    let microphone = try #require(
        devices.first { $0.uid == AudioDevice.virtualMicrophoneUID }
    )

    for device in [speaker, microphone] {
        #expect(device.nominalSampleRate == 48_000)
        #expect(device.inputChannelCount > 0)
        #expect(device.outputChannelCount > 0)
    }
}

private let expectedDriverState =
    ProcessInfo.processInfo.environment["EMKE_EXPECT_DRIVER_STATE"]

private func driverStateMatchesExpectedState(
    _ uids: Set<String>,
    expectedState: String?
) -> Bool {
    let hasSpeaker = uids.contains(AudioDevice.virtualSpeakerUID)
    let hasMicrophone = uids.contains(AudioDevice.virtualMicrophoneUID)

    switch expectedState {
    case "installed":
        return hasSpeaker && hasMicrophone
    case "absent":
        return !hasSpeaker && !hasMicrophone
    default:
        return false
    }
}

@Test func absentDriverStateRejectsEitherPartialDriverPresence() {
    #expect(!driverStateMatchesExpectedState(
        [AudioDevice.virtualSpeakerUID],
        expectedState: "absent"
    ))
    #expect(!driverStateMatchesExpectedState(
        [AudioDevice.virtualMicrophoneUID],
        expectedState: "absent"
    ))
}

@Test(
    .enabled(
        if: expectedDriverState == "installed" || expectedDriverState == "absent",
        "Set EMKE_EXPECT_DRIVER_STATE=installed or absent"
    )
)
func installedDriverMatchesExpectedState() throws {
    let devices = try CoreAudioDeviceProvider().devices()
    let uids = Set(devices.map(\.uid))
    #expect(driverStateMatchesExpectedState(
        uids,
        expectedState: expectedDriverState
    ))
}
