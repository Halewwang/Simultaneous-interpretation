using System.Collections.Frozen;
using EMKE.Core;

namespace EMKE.Routing;

public enum InboundGateDecision
{
    Undecided,
    Original,
    Translated,
}

public enum InboundTailState
{
    None,
    Waiting,
    Draining,
}

public enum InboundNextUtterancePolicy
{
    LanguageGate,
}

public sealed record InboundLanguageGateSnapshot(
    InboundGateDecision Decision,
    InboundTailState TailState,
    InboundNextUtterancePolicy NextUtterancePolicy);

public sealed class InboundLanguageGate
{
    public const int DecisionDeadlineMilliseconds = 250;
    public const int LateInputWindowMilliseconds = 500;
    public const double NativeThreshold = 0.75;
    public const double ForeignThreshold = 0.60;

    private readonly IClock _clock;
    private TimeSpan _decisionStartedAt;
    private TimeSpan? _tailDeadline;

    public InboundLanguageGate(LanguageCode nativeLanguage, IClock clock)
    {
        if (!Enum.IsDefined(nativeLanguage))
        {
            throw new ArgumentOutOfRangeException(nameof(nativeLanguage));
        }

        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        NativeLanguage = nativeLanguage;
        _decisionStartedAt = clock.MonotonicNow;
    }

    public LanguageCode NativeLanguage { get; }

    public InboundLanguageGateSnapshot Snapshot { get; private set; } =
        new(
            InboundGateDecision.Undecided,
            InboundTailState.None,
            InboundNextUtterancePolicy.LanguageGate);

    public static IReadOnlyDictionary<string, double> AggregateProbabilities(
        IReadOnlyDictionary<string, double> confidenceByTag)
    {
        ArgumentNullException.ThrowIfNull(confidenceByTag);

        Dictionary<string, double> result = new(StringComparer.Ordinal);
        foreach ((string tag, double confidence) in confidenceByTag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException(
                    "Language tags must not be empty.",
                    nameof(confidenceByTag));
            }

            if (!double.IsFinite(confidence) || confidence < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(confidenceByTag),
                    "Language confidence must be finite and nonnegative.");
            }

#pragma warning disable CA1308 // BCP 47 primary tags are contractually lowercase.
            string normalized = tag.ToLowerInvariant();
#pragma warning restore CA1308
            int separator = normalized.IndexOf('-', StringComparison.Ordinal);
            string primary = separator < 0 ? normalized : normalized[..separator];
            if (primary.Length == 0)
            {
                throw new ArgumentException(
                    "Language tags must include a primary subtag.",
                    nameof(confidenceByTag));
            }

            result[primary] = Math.Min(
                1,
                result.GetValueOrDefault(primary) + confidence);
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public InboundLanguageGateSnapshot Observe(
        IReadOnlyDictionary<string, double> confidenceByTag)
    {
        if (Snapshot.Decision != InboundGateDecision.Undecided)
        {
            return Snapshot;
        }

        IReadOnlyDictionary<string, double> aggregated =
            AggregateProbabilities(confidenceByTag);
        string native = ToStableLanguage(NativeLanguage);
        if (aggregated.GetValueOrDefault(native) >= NativeThreshold)
        {
            Snapshot = Snapshot with { Decision = InboundGateDecision.Original };
            return Snapshot;
        }

        double strongestForeign = aggregated
            .Where(pair => !string.Equals(pair.Key, native, StringComparison.Ordinal))
            .Select(static pair => pair.Value)
            .DefaultIfEmpty()
            .Max();
        if (strongestForeign >= ForeignThreshold)
        {
            Snapshot = Snapshot with { Decision = InboundGateDecision.Translated };
        }

        return Snapshot;
    }

    public InboundLanguageGateSnapshot ResolveDecisionDeadline(bool voiced)
    {
        if (Snapshot.Decision == InboundGateDecision.Undecided
            && _clock.MonotonicNow - _decisionStartedAt
                >= TimeSpan.FromMilliseconds(DecisionDeadlineMilliseconds))
        {
            Snapshot = Snapshot with
            {
                Decision = voiced
                    ? InboundGateDecision.Translated
                    : InboundGateDecision.Original,
            };
        }

        return Snapshot;
    }

    internal InboundLanguageGateSnapshot ForceDecision(bool voiced)
    {
        if (Snapshot.Decision == InboundGateDecision.Undecided)
        {
            Snapshot = Snapshot with
            {
                Decision = voiced
                    ? InboundGateDecision.Translated
                    : InboundGateDecision.Original,
            };
        }

        return Snapshot;
    }

    public InboundLanguageGateSnapshot EndVoice()
    {
        Snapshot = Snapshot with { TailState = InboundTailState.Waiting };
        RestartTailWindow(_clock.MonotonicNow);
        return Snapshot;
    }

    public InboundLanguageGateSnapshot ObserveLateAudio()
    {
        SettleAndRestartTailWindowIfWaiting();
        return Snapshot;
    }

    public InboundLanguageGateSnapshot ObserveLateTranscript()
    {
        SettleAndRestartTailWindowIfWaiting();
        return Snapshot;
    }

    public InboundLanguageGateSnapshot TryCompleteTail()
    {
        SettleTailDeadline(_clock.MonotonicNow);
        return Snapshot;
    }

    public void Reset()
    {
        Snapshot = new(
            InboundGateDecision.Undecided,
            InboundTailState.None,
            InboundNextUtterancePolicy.LanguageGate);
        _decisionStartedAt = _clock.MonotonicNow;
        _tailDeadline = null;
    }

    private static string ToStableLanguage(LanguageCode language)
    {
        return language switch
        {
            LanguageCode.Zh => "zh",
            LanguageCode.En => "en",
            LanguageCode.De => "de",
            _ => throw new ArgumentOutOfRangeException(nameof(language)),
        };
    }

    private void SettleAndRestartTailWindowIfWaiting()
    {
        TimeSpan now = _clock.MonotonicNow;
        SettleTailDeadline(now);
        if (Snapshot.TailState == InboundTailState.Waiting)
        {
            RestartTailWindow(now);
        }
    }

    private void SettleTailDeadline(TimeSpan now)
    {
        if (Snapshot.TailState == InboundTailState.Waiting
            && _tailDeadline is TimeSpan deadline
            && now >= deadline)
        {
            Snapshot = Snapshot with { TailState = InboundTailState.Draining };
            _tailDeadline = null;
        }
    }

    private void RestartTailWindow(TimeSpan now)
    {
        _tailDeadline = now
            + TimeSpan.FromMilliseconds(LateInputWindowMilliseconds);
    }
}
