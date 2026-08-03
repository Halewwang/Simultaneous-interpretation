namespace EMKE.Setup;

internal sealed class SetupCleanupOutcome
{
    private static readonly IReadOnlyList<string> EmptyLogicalNames =
        Array.AsReadOnly(Array.Empty<string>());

    private SetupCleanupOutcome(
        bool completed,
        bool residualRetained,
        string? failureCode,
        IReadOnlyList<string> retainedLogicalNames)
    {
        Completed = completed;
        ResidualRetained = residualRetained;
        FailureCode = failureCode;
        RetainedLogicalNames = retainedLogicalNames;
    }

    public bool Completed { get; }

    public bool ResidualRetained { get; }

    public string? FailureCode { get; }

    public IReadOnlyList<string> RetainedLogicalNames { get; }

    public static SetupCleanupOutcome NotAttempted { get; } = new(
        completed: false,
        residualRetained: false,
        failureCode: null,
        EmptyLogicalNames);

    public static SetupCleanupOutcome Cleaned { get; } = new(
        completed: true,
        residualRetained: false,
        failureCode: null,
        EmptyLogicalNames);

    public static SetupCleanupOutcome Residual(
        string failureCode,
        IEnumerable<string> retainedLogicalNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentNullException.ThrowIfNull(retainedLogicalNames);
        if (failureCode is not (
            "payloadCleanupUncertain"
            or "unexpectedExtractionEntriesRetained"
            or "rootCleanupUncertain"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCode),
                "The cleanup failure code is not stable.");
        }

        string[] copiedNames = retainedLogicalNames
            .Select(static name =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                return name;
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new SetupCleanupOutcome(
            completed: false,
            residualRetained: true,
            failureCode,
            Array.AsReadOnly(copiedNames));
    }
}
