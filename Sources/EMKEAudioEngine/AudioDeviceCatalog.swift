import Foundation

public enum AudioDeviceCatalogError: Error, Equatable, Sendable {
    case virtualSpeakerUnavailable
    case virtualMicrophoneUnavailable
    case physicalInputUnavailable(uid: String)
    case physicalOutputUnavailable(uid: String)
    case deviceHasNoInput(uid: String)
    case deviceHasNoOutput(uid: String)
}

public struct AudioDeviceSelection: Equatable, Sendable {
    public let virtualSpeaker: AudioDevice
    public let virtualMicrophone: AudioDevice
    public let physicalInput: AudioDevice
    public let physicalOutput: AudioDevice

    public init(
        virtualSpeaker: AudioDevice,
        virtualMicrophone: AudioDevice,
        physicalInput: AudioDevice,
        physicalOutput: AudioDevice
    ) {
        self.virtualSpeaker = virtualSpeaker
        self.virtualMicrophone = virtualMicrophone
        self.physicalInput = physicalInput
        self.physicalOutput = physicalOutput
    }
}

public struct AudioDeviceCatalog: Sendable {
    private let provider: any AudioDeviceProviding

    public init(provider: any AudioDeviceProviding) {
        self.provider = provider
    }

    public func physicalInputs() throws -> [AudioDevice] {
        try sortedPhysicalDevices().filter { $0.inputChannelCount > 0 }
    }

    public func physicalOutputs() throws -> [AudioDevice] {
        try sortedPhysicalDevices().filter { $0.outputChannelCount > 0 }
    }

    public func resolve(
        physicalInputUID: String,
        physicalOutputUID: String
    ) throws -> AudioDeviceSelection {
        let devices = try provider.devices()
        guard let virtualSpeaker = devices.first(where: {
            $0.uid == AudioDevice.virtualSpeakerUID
        }) else {
            throw AudioDeviceCatalogError.virtualSpeakerUnavailable
        }
        guard let virtualMicrophone = devices.first(where: {
            $0.uid == AudioDevice.virtualMicrophoneUID
        }) else {
            throw AudioDeviceCatalogError.virtualMicrophoneUnavailable
        }
        guard let physicalInput = devices.first(where: {
            $0.uid == physicalInputUID
        }) else {
            throw AudioDeviceCatalogError.physicalInputUnavailable(
                uid: physicalInputUID
            )
        }
        guard physicalInput.inputChannelCount > 0 else {
            throw AudioDeviceCatalogError.deviceHasNoInput(
                uid: physicalInputUID
            )
        }
        guard let physicalOutput = devices.first(where: {
            $0.uid == physicalOutputUID
        }) else {
            throw AudioDeviceCatalogError.physicalOutputUnavailable(
                uid: physicalOutputUID
            )
        }
        guard physicalOutput.outputChannelCount > 0 else {
            throw AudioDeviceCatalogError.deviceHasNoOutput(
                uid: physicalOutputUID
            )
        }

        return AudioDeviceSelection(
            virtualSpeaker: virtualSpeaker,
            virtualMicrophone: virtualMicrophone,
            physicalInput: physicalInput,
            physicalOutput: physicalOutput
        )
    }

    private func sortedPhysicalDevices() throws -> [AudioDevice] {
        try provider.devices()
            .filter { !$0.isEMKEVirtualDevice }
            .sorted { lhs, rhs in
                let nameOrder = lhs.name.localizedStandardCompare(rhs.name)
                if nameOrder == .orderedSame {
                    return lhs.uid < rhs.uid
                }
                return nameOrder == .orderedAscending
            }
    }
}
