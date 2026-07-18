import Testing
@testable import EMKERouting

@Test
func inboundFailureFailsOpen() {
    var machine = RoutingStateMachine()
    machine.handle(.translationStarted)
    machine.handle(.inboundConnectionFailed)
    #expect(machine.inbound == .originalFailOpen)
}

@Test
func outboundFailureFailsClosed() {
    var machine = RoutingStateMachine()
    machine.handle(.translationStarted)
    machine.handle(.outboundConnectionFailed)
    #expect(machine.outbound == .mutedFailClosed)
}

@Test
func outboundOriginalRequiresExplicitBypass() {
    var machine = RoutingStateMachine()
    machine.handle(.outboundConnectionFailed)
    #expect(machine.outbound == .mutedFailClosed)
    machine.handle(.outboundBypassEnabled)
    #expect(machine.outbound == .originalBypass)
}

@Test
func inboundReconnectWaitsForUtteranceBoundary() {
    var machine = RoutingStateMachine()
    machine.handle(.inboundConnectionFailed)
    machine.handle(.inboundConnectionRecovered)
    #expect(machine.inbound == .originalFailOpen)
    machine.handle(.utteranceEnded)
    #expect(machine.inbound == .translated)
}

@Test
func disablingInboundBypassWhileDisconnectedStaysFailOpen() {
    var machine = RoutingStateMachine()
    machine.handle(.inboundConnectionFailed)
    machine.handle(.inboundBypassEnabled)
    machine.handle(.inboundBypassDisabled)
    #expect(machine.inbound == .originalFailOpen)
}

@Test
func disablingOutboundBypassWhileDisconnectedStaysMuted() {
    var machine = RoutingStateMachine()
    machine.handle(.outboundConnectionFailed)
    machine.handle(.outboundBypassEnabled)
    machine.handle(.outboundBypassDisabled)
    #expect(machine.outbound == .mutedFailClosed)
}

@Test
func explicitInboundBypassSurvivesRecoveryBoundary() {
    var machine = RoutingStateMachine()
    machine.handle(.inboundConnectionFailed)
    machine.handle(.inboundConnectionRecovered)
    machine.handle(.inboundBypassEnabled)
    machine.handle(.utteranceEnded)
    #expect(machine.inbound == .originalBypass)
}

@Test
func requestedInboundBypassReturnsAfterFailureRecoveryBoundary() {
    var machine = RoutingStateMachine()
    machine.handle(.translationStarted)
    machine.handle(.inboundBypassEnabled)
    machine.handle(.inboundConnectionFailed)
    #expect(machine.inbound == .originalFailOpen)

    machine.handle(.inboundConnectionRecovered)
    #expect(machine.inbound == .originalFailOpen)
    machine.handle(.utteranceEnded)

    #expect(machine.inbound == .originalBypass)
}

@Test
func requestedOutboundBypassReturnsAfterFailureRecovery() {
    var machine = RoutingStateMachine()
    machine.handle(.translationStarted)
    machine.handle(.outboundBypassEnabled)
    machine.handle(.outboundConnectionFailed)
    #expect(machine.outbound == .mutedFailClosed)

    machine.handle(.outboundConnectionRecovered)

    #expect(machine.outbound == .originalBypass)
}

@Test
func disablingInboundBypassDuringFailureRecoversToTranslation() {
    var machine = RoutingStateMachine()
    machine.handle(.translationStarted)
    machine.handle(.inboundBypassEnabled)
    machine.handle(.inboundConnectionFailed)
    machine.handle(.inboundBypassDisabled)
    #expect(machine.inbound == .originalFailOpen)

    machine.handle(.inboundConnectionRecovered)
    machine.handle(.utteranceEnded)

    #expect(machine.inbound == .translated)
}

@Test
func disablingOutboundBypassDuringFailureRecoversToTranslation() {
    var machine = RoutingStateMachine()
    machine.handle(.translationStarted)
    machine.handle(.outboundBypassEnabled)
    machine.handle(.outboundConnectionFailed)
    machine.handle(.outboundBypassDisabled)
    #expect(machine.outbound == .mutedFailClosed)

    machine.handle(.outboundConnectionRecovered)

    #expect(machine.outbound == .translated)
}

@Test
func automaticOutboundDirectPathDoesNotCreateManualBypassIntent() {
    var machine = RoutingStateMachine()
    machine.handle(.translationStarted)
    machine.handle(.outboundAutomaticBypassEnabled)
    #expect(machine.outbound == .originalBypass)

    machine.handle(.outboundConnectionFailed)
    machine.handle(.outboundConnectionRecovered)

    #expect(machine.outbound == .translated)
}
