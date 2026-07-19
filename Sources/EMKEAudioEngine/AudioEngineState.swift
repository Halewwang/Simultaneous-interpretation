import CoreAudio
import Foundation

public enum AudioEndpointRole: Equatable, Sendable {
    case virtualSpeakerInput
    case physicalMicrophoneInput
    case physicalOutput
    case virtualMicrophoneOutput
}

public enum AudioEngineFailure: Error, Equatable, Sendable {
    case endpointCreationFailed(role: AudioEndpointRole)
    case endpointStartFailed(role: AudioEndpointRole)
    case notRunning
}

public enum AudioEngineState: Equatable, Sendable {
    case stopped
    case starting
    case running
    case failed(AudioEngineFailure)
}

public enum AudioEngineEvent: Equatable, Sendable {
    case inboundNetworkAudio(Data)
    case outboundNetworkAudio(Data)
    case outputBackpressure(role: AudioEndpointRole, droppedFrames: Int)
    case stopped
}

public struct AudioEngineConfiguration: Equatable, Sendable {
    public let selection: AudioDeviceSelection
    public let capacityFrames: UInt32
    public let playbackCapacityFrames: UInt32

    public init(
        selection: AudioDeviceSelection,
        capacityFrames: UInt32 = 4_800
    ) {
        self.init(
            selection: selection,
            capacityFrames: capacityFrames,
            playbackCapacityFrames: 96_000
        )
    }

    public init(
        selection: AudioDeviceSelection,
        capacityFrames: UInt32,
        playbackCapacityFrames: UInt32
    ) {
        self.selection = selection
        self.capacityFrames = capacityFrames
        self.playbackCapacityFrames = playbackCapacityFrames
    }
}
