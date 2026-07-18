import Foundation

public enum TranslationChannelState: Equatable, Sendable {
    case stopped
    case connecting
    case active
    case bypassed
    case reconnecting(attempt: Int)
    case failed(message: String)
}

public struct SubtitleSnapshot: Equatable, Sendable {
    public var inboundSource: String
    public var inboundTranslation: String
    public var outboundSource: String
    public var outboundTranslation: String

    public init(
        inboundSource: String = "",
        inboundTranslation: String = "",
        outboundSource: String = "",
        outboundTranslation: String = ""
    ) {
        self.inboundSource = inboundSource
        self.inboundTranslation = inboundTranslation
        self.outboundSource = outboundSource
        self.outboundTranslation = outboundTranslation
    }
}

public struct TranslationCoordinatorState: Equatable, Sendable {
    public var isRunning: Bool
    public var inbound: TranslationChannelState
    public var outbound: TranslationChannelState
    public var subtitles: SubtitleSnapshot

    public init(
        isRunning: Bool = false,
        inbound: TranslationChannelState = .stopped,
        outbound: TranslationChannelState = .stopped,
        subtitles: SubtitleSnapshot = SubtitleSnapshot()
    ) {
        self.isRunning = isRunning
        self.inbound = inbound
        self.outbound = outbound
        self.subtitles = subtitles
    }
}

public enum TranslationCoordinatorEvent: Equatable, Sendable {
    case stateChanged(TranslationCoordinatorState)
    case audioBackpressure(droppedFrames: Int)
    case stopped
}
