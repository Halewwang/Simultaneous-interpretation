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
}

public struct RoutingStateMachine: Sendable {
    public private(set) var inbound: InboundOutputMode = .stopped
    public private(set) var outbound: OutboundOutputMode = .stopped

    private var inboundRecoveryPending = false
    private var inboundConnected = false
    private var outboundConnected = false

    public init() {}

    public mutating func handle(_ event: RoutingEvent) {
        switch event {
        case .translationStarted:
            inboundConnected = true
            outboundConnected = true
            inbound = .translated
            outbound = .translated
        case .translationStopped:
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
            if outbound == .mutedFailClosed {
                outbound = .translated
            }
        case .utteranceEnded:
            if inboundRecoveryPending,
               inbound == .originalFailOpen {
                inbound = .translated
                inboundRecoveryPending = false
            }
        case .inboundBypassEnabled:
            inbound = .originalBypass
        case .inboundBypassDisabled:
            inbound = inboundConnected ? .translated : .originalFailOpen
        case .outboundBypassEnabled:
            outbound = .originalBypass
        case .outboundBypassDisabled:
            outbound = outboundConnected ? .translated : .mutedFailClosed
        }
    }
}
