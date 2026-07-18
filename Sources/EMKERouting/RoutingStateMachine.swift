public enum InboundOutputMode: Equatable, Sendable {
    case stopped
    case translated
    case originalFailOpen
    case originalBypass
}

public enum OutboundOutputMode: Equatable, Sendable {
    case stopped
    case translated
    case mutedFailClosed
    case originalBypass
}

public enum RoutingEvent: Sendable {
    case translationStarted
    case translationStopped
    case inboundConnectionFailed
    case outboundConnectionFailed
    case inboundConnectionRecovered
    case outboundConnectionRecovered
    case utteranceEnded
    case inboundBypassEnabled
    case inboundBypassDisabled
    case outboundBypassEnabled
    case outboundBypassDisabled
    case outboundAutomaticBypassEnabled
}

public struct RoutingStateMachine: Sendable {
    public private(set) var inbound: InboundOutputMode = .stopped
    public private(set) var outbound: OutboundOutputMode = .stopped

    private var inboundRecoveryPending = false
    private var inboundConnected = false
    private var outboundConnected = false
    private var inboundBypassRequested = false
    private var outboundBypassRequested = false

    public init() {}

    public mutating func handle(_ event: RoutingEvent) {
        switch event {
        case .translationStarted:
            inboundBypassRequested = false
            outboundBypassRequested = false
            inboundConnected = true
            outboundConnected = true
            inbound = .translated
            outbound = .translated
        case .translationStopped:
            inboundBypassRequested = false
            outboundBypassRequested = false
            inboundConnected = false
            outboundConnected = false
            inboundRecoveryPending = false
            inbound = .stopped
            outbound = .stopped
        case .inboundConnectionFailed:
            inboundConnected = false
            inboundRecoveryPending = false
            inbound = .originalFailOpen
        case .outboundConnectionFailed:
            outboundConnected = false
            outbound = .mutedFailClosed
        case .inboundConnectionRecovered:
            inboundConnected = true
            inboundRecoveryPending = true
        case .outboundConnectionRecovered:
            outboundConnected = true
            outbound = outboundBypassRequested
                ? .originalBypass
                : .translated
        case .utteranceEnded:
            if inboundRecoveryPending {
                inbound = inboundBypassRequested
                    ? .originalBypass
                    : .translated
                inboundRecoveryPending = false
            }
        case .inboundBypassEnabled:
            inboundBypassRequested = true
            inbound = .originalBypass
        case .inboundBypassDisabled:
            inboundBypassRequested = false
            inbound = inboundConnected ? .translated : .originalFailOpen
        case .outboundBypassEnabled:
            outboundBypassRequested = true
            outbound = .originalBypass
        case .outboundBypassDisabled:
            outboundBypassRequested = false
            outbound = outboundConnected ? .translated : .mutedFailClosed
        case .outboundAutomaticBypassEnabled:
            outbound = .originalBypass
        }
    }
}
