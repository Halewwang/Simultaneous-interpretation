import Foundation

public struct AudioLevelSnapshot: Equatable, Sendable {
    public var inbound: Double
    public var outbound: Double

    public init(inbound: Double = 0, outbound: Double = 0) {
        self.inbound = min(max(inbound, 0), 1)
        self.outbound = min(max(outbound, 0), 1)
    }

    public var combined: Double {
        max(inbound, outbound)
    }
}

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
    public var audioEngineStarted: Bool
    public var inbound: TranslationChannelState
    public var outbound: TranslationChannelState
    public var subtitles: SubtitleSnapshot
    public var latency: TranslationLatencyDiagnostics

    public init(
        isRunning: Bool = false,
        audioEngineStarted: Bool = false,
        inbound: TranslationChannelState = .stopped,
        outbound: TranslationChannelState = .stopped,
        subtitles: SubtitleSnapshot = SubtitleSnapshot(),
        latency: TranslationLatencyDiagnostics = .empty
    ) {
        self.isRunning = isRunning
        self.audioEngineStarted = audioEngineStarted
        self.inbound = inbound
        self.outbound = outbound
        self.subtitles = subtitles
        self.latency = latency
    }

    public var canListen: Bool {
        guard audioEngineStarted else { return false }
        return switch inbound {
        case .active, .bypassed, .reconnecting, .failed:
            true
        case .stopped, .connecting:
            false
        }
    }

    public var canSpeak: Bool {
        guard audioEngineStarted else { return false }
        return switch outbound {
        case .active, .bypassed:
            true
        case .stopped, .connecting, .reconnecting, .failed:
            false
        }
    }
}

public enum TranslationCoordinatorEvent: Equatable, Sendable {
    case stateChanged(TranslationCoordinatorState)
    case audioLevels(AudioLevelSnapshot)
    case audioBackpressure(droppedFrames: Int)
    case stopped
}
