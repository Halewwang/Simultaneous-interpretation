using EMKE.Core;

namespace EMKE.Routing.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class InboundLanguageGateTests
{
    [TestMethod]
    public void RegionalTagsAggregateByLowercasePrimaryTagAndClampAtOne()
    {
        IReadOnlyDictionary<string, double> result =
            InboundLanguageGate.AggregateProbabilities(
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["ZH-Hans"] = 0.45,
                    ["zh-Hant"] = 0.40,
                    ["EN-us"] = 0.80,
                    ["en-GB"] = 0.70,
                });

        Assert.AreEqual(0.85, result["zh"], 0.000_000_1);
        Assert.AreEqual(1, result["en"], 0.000_000_1);
    }

    [TestMethod]
    public void NativeAndForeignThresholdsAreInclusiveAndLockTheDecision()
    {
        FakeClock clock = new();
        InboundLanguageGate native = new(LanguageCode.Zh, clock);
        InboundLanguageGate foreign = new(LanguageCode.Zh, clock);

        Assert.AreEqual(
            InboundGateDecision.Original,
            native.Observe(new Dictionary<string, double> { ["zh"] = 0.75 }).Decision);
        Assert.AreEqual(
            InboundGateDecision.Original,
            native.Observe(new Dictionary<string, double> { ["de"] = 1 }).Decision);

        Assert.AreEqual(
            InboundGateDecision.Translated,
            foreign.Observe(new Dictionary<string, double> { ["en"] = 0.60 }).Decision);
    }

    [TestMethod]
    public void DecisionDeadlineUsesTheExact250MillisecondMonotonicBoundary()
    {
        FakeClock clock = new();
        InboundLanguageGate voiced = new(LanguageCode.Zh, clock);
        InboundLanguageGate unvoiced = new(LanguageCode.Zh, clock);

        clock.AdvanceTo(249);
        Assert.AreEqual(
            InboundGateDecision.Undecided,
            voiced.ResolveDecisionDeadline(voiced: true).Decision);
        Assert.AreEqual(
            InboundGateDecision.Undecided,
            unvoiced.ResolveDecisionDeadline(voiced: false).Decision);

        clock.AdvanceTo(250);
        Assert.AreEqual(
            InboundGateDecision.Translated,
            voiced.ResolveDecisionDeadline(voiced: true).Decision);
        Assert.AreEqual(
            InboundGateDecision.Original,
            unvoiced.ResolveDecisionDeadline(voiced: false).Decision);
    }

    [TestMethod]
    public void VoiceEndAndLateInputUseExactRestartable500MillisecondWindow()
    {
        FakeClock clock = new();
        InboundLanguageGate gate = new(LanguageCode.Zh, clock);

        gate.EndVoice();
        Assert.AreEqual(InboundTailState.Waiting, gate.Snapshot.TailState);

        clock.AdvanceTo(450);
        gate.ObserveLateAudio();
        clock.AdvanceTo(949);
        Assert.AreEqual(InboundTailState.Waiting, gate.TryCompleteTail().TailState);
        clock.AdvanceTo(950);
        Assert.AreEqual(InboundTailState.Draining, gate.TryCompleteTail().TailState);

        gate.Reset();
        clock.AdvanceTo(1_000);
        gate.EndVoice();
        clock.AdvanceTo(1_450);
        gate.ObserveLateTranscript();
        clock.AdvanceTo(1_950);
        Assert.AreEqual(InboundTailState.Draining, gate.TryCompleteTail().TailState);
    }

    [TestMethod]
    [DataRow(499, InboundTailState.Waiting)]
    [DataRow(500, InboundTailState.Draining)]
    [DataRow(501, InboundTailState.Draining)]
    public void LateAudioAtomicallySettlesTheExistingTailDeadlineBeforeRestart(
        int arrivalMilliseconds,
        InboundTailState expected)
    {
        FakeClock clock = new();
        InboundLanguageGate gate = new(LanguageCode.Zh, clock);
        _ = gate.EndVoice();

        clock.AdvanceTo(arrivalMilliseconds);
        InboundLanguageGateSnapshot arrival = gate.ObserveLateAudio();

        Assert.AreEqual(expected, arrival.TailState);
        if (expected == InboundTailState.Waiting)
        {
            clock.AdvanceTo(arrivalMilliseconds + 499);
            Assert.AreEqual(
                InboundTailState.Waiting,
                gate.TryCompleteTail().TailState);
            clock.AdvanceTo(arrivalMilliseconds + 500);
            Assert.AreEqual(
                InboundTailState.Draining,
                gate.TryCompleteTail().TailState);
        }
    }

    [TestMethod]
    public void OutOfOrderLateTranscriptCannotRestartAnAlreadyDrainingTail()
    {
        FakeClock clock = new();
        InboundLanguageGate gate = new(LanguageCode.En, clock);
        _ = gate.EndVoice();
        clock.AdvanceTo(500);
        _ = gate.TryCompleteTail();

        clock.AdvanceTo(501);
        InboundLanguageGateSnapshot late = gate.ObserveLateTranscript();

        Assert.AreEqual(InboundTailState.Draining, late.TailState);
        clock.AdvanceTo(1_001);
        Assert.AreEqual(
            InboundTailState.Draining,
            gate.TryCompleteTail().TailState);
    }

    [TestMethod]
    public void ResetStartsANewDecisionWindowAndClearsTailState()
    {
        FakeClock clock = new();
        InboundLanguageGate gate = new(LanguageCode.De, clock);
        _ = gate.Observe(new Dictionary<string, double> { ["de"] = 0.9 });
        gate.EndVoice();

        clock.AdvanceTo(800);
        gate.Reset();

        Assert.AreEqual(InboundGateDecision.Undecided, gate.Snapshot.Decision);
        Assert.AreEqual(InboundTailState.None, gate.Snapshot.TailState);
        clock.AdvanceTo(1_049);
        Assert.AreEqual(
            InboundGateDecision.Undecided,
            gate.ResolveDecisionDeadline(voiced: true).Decision);
        clock.AdvanceTo(1_050);
        Assert.AreEqual(
            InboundGateDecision.Translated,
            gate.ResolveDecisionDeadline(voiced: true).Decision);
    }

    [TestMethod]
    public void RoutingPolicyFailsInboundOpenAndOutboundClosed()
    {
        RoutingPolicy policy = new();
        policy.Start(outboundLocalBypass: false);

        RoutingPolicySnapshot inbound =
            policy.FailInbound(ErrorCategory.Network);
        Assert.AreEqual(ChannelState.Failed, inbound.InboundChannelState);
        Assert.AreEqual(InboundRoute.OriginalFailOpen, inbound.InboundRoute);
        Assert.AreEqual(ErrorCategory.Network, inbound.ErrorCategory);

        RoutingPolicySnapshot outbound =
            policy.FailOutbound(ErrorCategory.Network);
        Assert.AreEqual(ChannelState.Failed, outbound.OutboundChannelState);
        Assert.AreEqual(OutboundRoute.MutedFailClosed, outbound.OutboundRoute);
        Assert.AreEqual(ErrorCategory.Network, outbound.ErrorCategory);
    }

    [TestMethod]
    public void OutboundUnderrunOutputsZerosAndNeverUsesThePhysicalMicrophone()
    {
        RoutingPolicy policy = new();
        policy.Start(outboundLocalBypass: false);

        RoutingPolicySnapshot result = policy.HandleOutboundUnderrun();

        Assert.AreEqual(ChannelState.Degraded, result.OutboundChannelState);
        Assert.AreEqual(OutboundRoute.MutedFailClosed, result.OutboundRoute);
        Assert.AreEqual(ErrorCategory.Backpressure, result.ErrorCategory);
        Assert.IsTrue(result.OutputsZeros);
        Assert.IsFalse(result.PhysicalMicrophoneAllowed);
    }

    [TestMethod]
    public void ExplicitOutboundBypassPersistsAcrossDisconnectAndReconnect()
    {
        RoutingPolicy policy = new();
        _ = policy.Start(outboundLocalBypass: false);
        _ = policy.EnableOutboundBypass();
        _ = policy.DisconnectOutbound();

        RoutingPolicySnapshot result = policy.ReconnectOutbound();

        Assert.AreEqual(ChannelState.Bypassed, result.OutboundChannelState);
        Assert.AreEqual(OutboundRoute.OriginalBypass, result.OutboundRoute);
        Assert.IsTrue(result.BypassPersisted);
    }

    [TestMethod]
    public void InboundRecoveryDoesNotChangeTheCurrentUtteranceRoute()
    {
        RoutingPolicy policy = new();
        _ = policy.Start(outboundLocalBypass: false);
        _ = policy.FailInbound(ErrorCategory.Network);

        RoutingPolicySnapshot recovered = policy.RecoverInbound();

        Assert.AreEqual(ChannelState.Connected, recovered.InboundChannelState);
        Assert.AreEqual(
            InboundRoute.OriginalFailOpen,
            recovered.InboundRoute);
        Assert.AreEqual(
            InboundRoute.Translated,
            policy.CompleteInboundUtterance().InboundRoute);
    }

    [TestMethod]
    public void StopMakesBothChannelsInactiveAndBothRoutesStopped()
    {
        RoutingPolicy policy = new();
        _ = policy.Start(outboundLocalBypass: false);

        RoutingPolicySnapshot result = policy.Stop();

        Assert.AreEqual(ChannelState.Inactive, result.InboundChannelState);
        Assert.AreEqual(ChannelState.Inactive, result.OutboundChannelState);
        Assert.AreEqual(InboundRoute.Stopped, result.InboundRoute);
        Assert.AreEqual(OutboundRoute.Stopped, result.OutboundRoute);
    }

    [TestMethod]
    public void RecoveryAndReconnectCannotResurrectInitialOrStoppedChannels()
    {
        RoutingPolicy policy = new();

        Assert.AreEqual(ChannelState.Inactive, policy.RecoverInbound().InboundChannelState);
        Assert.AreEqual(InboundRoute.Stopped, policy.Snapshot.InboundRoute);
        Assert.AreEqual(ChannelState.Inactive, policy.ReconnectOutbound().OutboundChannelState);
        Assert.AreEqual(OutboundRoute.Stopped, policy.Snapshot.OutboundRoute);

        _ = policy.Start(outboundLocalBypass: false);
        _ = policy.Stop();

        Assert.AreEqual(ChannelState.Inactive, policy.RecoverInbound().InboundChannelState);
        Assert.AreEqual(InboundRoute.Stopped, policy.Snapshot.InboundRoute);
        Assert.AreEqual(ChannelState.Inactive, policy.ReconnectOutbound().OutboundChannelState);
        Assert.AreEqual(OutboundRoute.Stopped, policy.Snapshot.OutboundRoute);
    }

    [TestMethod]
    public void RecoveringOneChannelPreservesTheOtherActiveFailure()
    {
        RoutingPolicy policy = new();
        _ = policy.Start(outboundLocalBypass: false);
        _ = policy.FailInbound(ErrorCategory.Network);
        _ = policy.FailOutbound(ErrorCategory.Protocol);

        RoutingPolicySnapshot inboundRecovered = policy.RecoverInbound();

        Assert.AreEqual(ChannelState.Connected, inboundRecovered.InboundChannelState);
        Assert.AreEqual(ChannelState.Failed, inboundRecovered.OutboundChannelState);
        Assert.AreEqual(ErrorCategory.Protocol, inboundRecovered.ErrorCategory);

        RoutingPolicy outboundPolicy = new();
        _ = outboundPolicy.Start(outboundLocalBypass: false);
        _ = outboundPolicy.FailInbound(ErrorCategory.Network);
        _ = outboundPolicy.DisconnectOutbound();
        RoutingPolicySnapshot outboundRecovered =
            outboundPolicy.ReconnectOutbound();

        Assert.AreEqual(ChannelState.Failed, outboundRecovered.InboundChannelState);
        Assert.AreEqual(ChannelState.Connected, outboundRecovered.OutboundChannelState);
        Assert.AreEqual(ErrorCategory.Network, outboundRecovered.ErrorCategory);
    }

    [TestMethod]
    public void StaleGenerationRecoveryCannotMutateANewerRun()
    {
        RoutingPolicy policy = new();
        _ = policy.Start(outboundLocalBypass: false);
        System.Reflection.PropertyInfo? generationProperty =
            typeof(RoutingPolicySnapshot).GetProperty("Generation");
        Assert.IsNotNull(generationProperty);
        long staleGeneration =
            (long)generationProperty.GetValue(policy.Snapshot)!;
        _ = policy.FailInbound(ErrorCategory.Network);
        _ = policy.Stop();
        _ = policy.Start(outboundLocalBypass: false);
        _ = policy.FailInbound(ErrorCategory.Protocol);

        System.Reflection.MethodInfo? recover =
            typeof(RoutingPolicy).GetMethod(
                nameof(RoutingPolicy.RecoverInbound),
                [typeof(long)]);
        Assert.IsNotNull(recover);
        RoutingPolicySnapshot result = (RoutingPolicySnapshot)recover.Invoke(
            policy,
            [staleGeneration])!;

        Assert.AreEqual(ChannelState.Failed, result.InboundChannelState);
        Assert.AreEqual(InboundRoute.OriginalFailOpen, result.InboundRoute);
        Assert.AreEqual(ErrorCategory.Protocol, result.ErrorCategory);
    }

    private sealed class FakeClock : IClock
    {
        public int NowMilliseconds { get; private set; }

        public TimeSpan MonotonicNow => TimeSpan.FromMilliseconds(NowMilliseconds);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return ValueTask.FromException(
                new NotSupportedException("Gate tests inspect monotonic time directly."));
        }

        public void AdvanceTo(int milliseconds)
        {
            Assert.IsGreaterThanOrEqualTo(NowMilliseconds, milliseconds);
            NowMilliseconds = milliseconds;
        }
    }
}

#pragma warning restore CA1515
