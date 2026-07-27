using System.Text.Json;
using EMKE.Core;
using EMKE.Routing;

namespace EMKE.Contract.Tests;

internal static class RoutingFixtureAdapter
{
    public static void ValidateInboundLanguageGate(JsonElement fixture)
    {
        Assert.AreEqual(
            "routing.inbound-language-gate.v1",
            fixture.GetProperty("fixtureId").GetString());
        foreach (JsonElement fixtureCase in fixture.GetProperty("cases").EnumerateArray())
        {
            string name = RequiredString(fixtureCase, "name");
            JsonElement input = fixtureCase.GetProperty("input");
            JsonElement expected = fixtureCase.GetProperty("expected");
            if (input.TryGetProperty("confidenceByTag", out JsonElement confidenceByTag))
            {
                ValidateConfidenceCase(input, expected, confidenceByTag, name);
            }
            else if (input.TryGetProperty("voiced", out JsonElement voiced))
            {
                ValidateDeadlineCase(input, expected, voiced.GetBoolean(), name);
            }
            else if (input.TryGetProperty("event", out JsonElement eventName))
            {
                ValidateTailCase(input, expected, RequiredString(eventName), name);
            }
            else if (input.TryGetProperty("recoveryEvent", out _))
            {
                ValidateRecoveryCase(input, expected, name);
            }
            else
            {
                Assert.Fail($"Unsupported inbound language fixture case: {name}");
            }
        }
    }

    public static void ValidateChannelFailureSafety(JsonElement fixture)
    {
        Assert.AreEqual(
            "routing.channel-failure-safety.v1",
            fixture.GetProperty("fixtureId").GetString());
        foreach (JsonElement fixtureCase in fixture.GetProperty("cases").EnumerateArray())
        {
            string name = RequiredString(fixtureCase, "name");
            JsonElement input = fixtureCase.GetProperty("input");
            JsonElement expected = fixtureCase.GetProperty("expected");
            RoutingPolicy policy = new();
            RoutingPolicySnapshot actual = policy.Start(outboundLocalBypass: false);

            if (input.TryGetProperty("event", out JsonElement eventElement))
            {
                actual = RequiredString(eventElement) switch
                {
                    "inbound.networkFailure" =>
                        policy.FailInbound(ErrorCategory.Network),
                    "outbound.networkFailure" =>
                        policy.FailOutbound(ErrorCategory.Network),
                    "outbound.underrun" => policy.HandleOutboundUnderrun(),
                    "outbound.bypassEnabled" => policy.EnableOutboundBypass(),
                    "stop" => policy.Stop(),
                    _ => throw new InvalidDataException(
                        "Unknown channel safety event."),
                };
            }
            else
            {
                Assert.AreEqual(
                    "originalBypass",
                    RequiredString(input, "initialOutboundRoute"),
                    name);
                _ = policy.EnableOutboundBypass();
                foreach (JsonElement sequenceEvent in input.GetProperty("events").EnumerateArray())
                {
                    actual = RequiredString(sequenceEvent) switch
                    {
                        "disconnect" => policy.DisconnectOutbound(),
                        "reconnect" => policy.ReconnectOutbound(),
                        _ => throw new InvalidDataException(
                            "Unknown channel safety sequence event."),
                    };
                }
            }

            AssertExpectedProjection(expected, actual, name);
        }
    }

    public static void ValidateRealtimeProjection(JsonElement fixture)
    {
        foreach (JsonElement fixtureCase in fixture.GetProperty("cases").EnumerateArray())
        {
            string name = RequiredString(fixtureCase, "name");
            JsonElement configuration = fixtureCase.GetProperty("configuration");
            bool sameLanguage = string.Equals(
                RequiredString(configuration, "nativeLanguage"),
                RequiredString(configuration, "meetingLanguage"),
                StringComparison.Ordinal);
            RoutingPolicy policy = new();
            RoutingPolicySnapshot actual = policy.Start(sameLanguage);
            JsonElement expected = fixtureCase.GetProperty("expected");
            if (expected.TryGetProperty("errorCategory", out JsonElement error)
                && RequiredString(error) == "protocol")
            {
                actual = policy.FailInbound(ErrorCategory.Protocol);
            }

            AssertExpectedProjection(expected, actual, name);
        }
    }

    private static void ValidateConfidenceCase(
        JsonElement input,
        JsonElement expected,
        JsonElement confidenceByTag,
        string name)
    {
        LanguageCode native = ParseLanguage(RequiredString(input, "nativeLanguage"));
        Dictionary<string, double> raw = confidenceByTag
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.GetDouble(),
                StringComparer.Ordinal);
        IReadOnlyDictionary<string, double> aggregated =
            InboundLanguageGate.AggregateProbabilities(raw);
        JsonElement expectedAggregated =
            expected.GetProperty("aggregatedConfidenceByLanguage");
        Assert.HasCount(
            expectedAggregated.EnumerateObject().Count(),
            aggregated,
            name);
        foreach (JsonProperty pair in expectedAggregated.EnumerateObject())
        {
            Assert.AreEqual(pair.Value.GetDouble(), aggregated[pair.Name], 0.000_000_001, name);
        }

        double fixtureThreshold = input.GetProperty("threshold").GetDouble();
        Assert.IsTrue(
            fixtureThreshold == InboundLanguageGate.NativeThreshold
            || fixtureThreshold == InboundLanguageGate.ForeignThreshold,
            name);
        InboundLanguageGate gate = new(native, new FixtureClock());
        InboundLanguageGateSnapshot snapshot = gate.Observe(raw);
        AssertExpectedGate(expected, snapshot, name);
    }

    private static void ValidateDeadlineCase(
        JsonElement input,
        JsonElement expected,
        bool voiced,
        string name)
    {
        Assert.AreEqual(
            InboundLanguageGate.DecisionDeadlineMilliseconds,
            input.GetProperty("deadlineMs").GetInt32(),
            name);
        FixtureClock clock = new();
        InboundLanguageGate gate = new(
            ParseLanguage(RequiredString(input, "nativeLanguage")),
            clock);
        clock.AdvanceTo(input.GetProperty("decisionAtMs").GetInt32());
        InboundLanguageGateSnapshot snapshot =
            gate.ResolveDecisionDeadline(voiced);
        AssertExpectedGate(expected, snapshot, name);
    }

    private static void ValidateTailCase(
        JsonElement input,
        JsonElement expected,
        string eventName,
        string name)
    {
        FixtureClock clock = new();
        InboundLanguageGate gate = new(LanguageCode.Zh, clock);
        InboundLanguageGateSnapshot expectedMoment = gate.EndVoice();
        Assert.AreEqual(
            InboundLanguageGate.LateInputWindowMilliseconds,
            input.GetProperty("restartMs").GetInt32(),
            name);

        if (eventName is "late.audio" or "late.transcript")
        {
            int arrival = input.GetProperty("arrivalAfterVadEndMs").GetInt32();
            clock.AdvanceTo(arrival);
            expectedMoment = eventName == "late.audio"
                ? gate.ObserveLateAudio()
                : gate.ObserveLateTranscript();
            clock.AdvanceTo(
                arrival + InboundLanguageGate.LateInputWindowMilliseconds - 1);
            Assert.AreEqual(
                InboundTailState.Waiting,
                gate.TryCompleteTail().TailState,
                name);
            clock.AdvanceTo(
                arrival + InboundLanguageGate.LateInputWindowMilliseconds);
            Assert.AreEqual(
                InboundTailState.Draining,
                gate.TryCompleteTail().TailState,
                name);
            Assert.AreEqual(
                InboundLanguageGate.LateInputWindowMilliseconds,
                expected.GetProperty("restartWindowMs").GetInt32(),
                name);
        }
        else
        {
            Assert.AreEqual("vad.end", eventName, name);
            Assert.AreEqual(
                InboundLanguageGate.LateInputWindowMilliseconds,
                expected.GetProperty("waitForLateInputMs").GetInt32(),
                name);
        }

        AssertExpectedGate(expected, expectedMoment, name);
    }

    private static void ValidateRecoveryCase(
        JsonElement input,
        JsonElement expected,
        string name)
    {
        Assert.AreEqual(
            "originalFailOpen",
            RequiredString(input, "inboundRoute"),
            name);
        Assert.AreEqual("connected", RequiredString(input, "recoveryEvent"), name);
        RoutingPolicy policy = new();
        _ = policy.Start(outboundLocalBypass: false);
        _ = policy.FailInbound(ErrorCategory.Network);
        RoutingPolicySnapshot route = policy.RecoverInbound();

        FixtureClock clock = new();
        InboundLanguageGate gate = new(LanguageCode.Zh, clock);
        _ = gate.Observe(new Dictionary<string, double> { ["zh"] = 1 });
        _ = gate.EndVoice();
        clock.AdvanceTo(InboundLanguageGate.LateInputWindowMilliseconds);
        InboundLanguageGateSnapshot gateSnapshot = gate.TryCompleteTail();

        Assert.AreEqual(
            RequiredString(expected, "inboundRoute"),
            Stable(route.InboundRoute),
            name);
        AssertExpectedGate(expected, gateSnapshot, name);
    }

    private static void AssertExpectedGate(
        JsonElement expected,
        InboundLanguageGateSnapshot actual,
        string name)
    {
        Assert.AreEqual(
            RequiredString(expected, "gateDecision"),
            Stable(actual.Decision),
            name);
        Assert.AreEqual(
            RequiredString(expected, "tailState"),
            Stable(actual.TailState),
            name);
        Assert.AreEqual(
            RequiredString(expected, "nextUtterancePolicy"),
            Stable(actual.NextUtterancePolicy),
            name);
    }

    private static void AssertExpectedProjection(
        JsonElement expected,
        RoutingPolicySnapshot actual,
        string name)
    {
        if (expected.TryGetProperty("inboundChannelState", out JsonElement inboundState))
        {
            Assert.AreEqual(RequiredString(inboundState), Stable(actual.InboundChannelState), name);
        }

        if (expected.TryGetProperty("outboundChannelState", out JsonElement outboundState))
        {
            Assert.AreEqual(RequiredString(outboundState), Stable(actual.OutboundChannelState), name);
        }

        if (expected.TryGetProperty("inboundRoute", out JsonElement inboundRoute))
        {
            Assert.AreEqual(RequiredString(inboundRoute), Stable(actual.InboundRoute), name);
        }

        if (expected.TryGetProperty("outboundRoute", out JsonElement outboundRoute))
        {
            Assert.AreEqual(RequiredString(outboundRoute), Stable(actual.OutboundRoute), name);
        }

        if (expected.TryGetProperty("errorCategory", out JsonElement errorCategory))
        {
            Assert.AreEqual(RequiredString(errorCategory), Stable(actual.ErrorCategory), name);
        }

        if (expected.TryGetProperty("outputSamples", out JsonElement outputSamples))
        {
            Assert.AreEqual("zeros", RequiredString(outputSamples), name);
            Assert.IsTrue(actual.OutputsZeros, name);
        }

        if (expected.TryGetProperty("physicalMicrophone", out JsonElement microphone))
        {
            Assert.AreEqual("forbidden", RequiredString(microphone), name);
            Assert.IsFalse(actual.PhysicalMicrophoneAllowed, name);
        }

        if (expected.TryGetProperty("bypassPersisted", out JsonElement bypassPersisted))
        {
            Assert.AreEqual(bypassPersisted.GetBoolean(), actual.BypassPersisted, name);
        }
    }

    private static LanguageCode ParseLanguage(string value)
    {
        return value switch
        {
            "zh" => LanguageCode.Zh,
            "en" => LanguageCode.En,
            "de" => LanguageCode.De,
            _ => throw new InvalidDataException("Unknown fixture language."),
        };
    }

    private static string RequiredString(JsonElement owner, string property)
    {
        return RequiredString(owner.GetProperty(property));
    }

    private static string RequiredString(JsonElement value)
    {
        return value.GetString()
            ?? throw new InvalidDataException(
                "Fixture string values cannot be null.");
    }

    private static string Stable(ChannelState value)
    {
        return value switch
        {
            ChannelState.Inactive => "inactive",
            ChannelState.Connecting => "connecting",
            ChannelState.Connected => "connected",
            ChannelState.Reconnecting => "reconnecting",
            ChannelState.Bypassed => "bypassed",
            ChannelState.Degraded => "degraded",
            ChannelState.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static string Stable(InboundRoute value)
    {
        return value switch
        {
            InboundRoute.Stopped => "stopped",
            InboundRoute.Translated => "translated",
            InboundRoute.OriginalFailOpen => "originalFailOpen",
            InboundRoute.OriginalBypass => "originalBypass",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static string Stable(OutboundRoute value)
    {
        return value switch
        {
            OutboundRoute.Stopped => "stopped",
            OutboundRoute.Translated => "translated",
            OutboundRoute.MutedFailClosed => "mutedFailClosed",
            OutboundRoute.OriginalBypass => "originalBypass",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static string Stable(ErrorCategory? value)
    {
        return value switch
        {
            ErrorCategory.Protocol => "protocol",
            ErrorCategory.Network => "network",
            ErrorCategory.Backpressure => "backpressure",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static string Stable(InboundGateDecision value)
    {
        return value switch
        {
            InboundGateDecision.Undecided => "undecided",
            InboundGateDecision.Original => "original",
            InboundGateDecision.Translated => "translated",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static string Stable(InboundTailState value)
    {
        return value switch
        {
            InboundTailState.None => "none",
            InboundTailState.Waiting => "waiting",
            InboundTailState.Draining => "draining",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static string Stable(InboundNextUtterancePolicy value)
    {
        return value switch
        {
            InboundNextUtterancePolicy.LanguageGate => "languageGate",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private sealed class FixtureClock : IClock
    {
        public int NowMilliseconds { get; private set; }

        public TimeSpan MonotonicNow =>
            TimeSpan.FromMilliseconds(NowMilliseconds);

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromException(
                new NotSupportedException(
                    "Routing fixtures inspect monotonic time directly."));
        }

        public void AdvanceTo(int milliseconds)
        {
            Assert.IsGreaterThanOrEqualTo(
                NowMilliseconds,
                milliseconds);
            NowMilliseconds = milliseconds;
        }
    }
}
