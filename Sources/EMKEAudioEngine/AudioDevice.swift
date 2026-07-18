import CoreAudio

public struct AudioDevice: Equatable, Sendable, Identifiable {
    public static let virtualSpeakerUID =
        "com.emke.translation.virtual-speaker"
    public static let virtualMicrophoneUID =
        "com.emke.translation.virtual-microphone"

    public let id: AudioObjectID
    public let uid: String
    public let name: String
    public let inputChannelCount: Int
    public let outputChannelCount: Int
    public let nominalSampleRate: Double

    public init(
        id: AudioObjectID,
        uid: String,
        name: String,
        inputChannelCount: Int,
        outputChannelCount: Int,
        nominalSampleRate: Double
    ) {
        self.id = id
        self.uid = uid
        self.name = name
        self.inputChannelCount = inputChannelCount
        self.outputChannelCount = outputChannelCount
        self.nominalSampleRate = nominalSampleRate
    }

    public var isEMKEVirtualDevice: Bool {
        uid == Self.virtualSpeakerUID || uid == Self.virtualMicrophoneUID
    }
}
