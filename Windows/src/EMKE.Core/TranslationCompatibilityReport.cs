namespace EMKE.Core;

public enum TranslationCapabilityOutcome
{
    Passed,
    Failed,
    RequiresInteractiveAudio,
    NotRun,
}

public enum TranslationCompatibilityOverall
{
    Incompatible,
    ProtocolCompatibleRequiresAudio,
    Compatible,
}

public sealed record TranslationCompatibilityStageResult
{
    public TranslationCompatibilityStageResult(
        string stableName,
        TranslationCapabilityOutcome outcome,
        string? failureCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableName);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        StableName = stableName;
        Outcome = outcome;
        FailureCode = failureCode;
    }

    public string StableName { get; }

    public TranslationCapabilityOutcome Outcome { get; }

    public string? FailureCode { get; }
}
