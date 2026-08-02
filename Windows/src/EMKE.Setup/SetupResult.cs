namespace EMKE.Setup;

public sealed record SetupResult
{
    private SetupResult(
        SetupOutcome outcome,
        SetupState state,
        string? detail)
    {
        Outcome = outcome;
        State = state;
        Detail = detail;
    }

    public SetupOutcome Outcome { get; }

    public SetupState State { get; }

    public string? Detail { get; }

    public static SetupResult Succeeded()
    {
        return new SetupResult(
            SetupOutcome.Succeeded,
            SetupState.Complete,
            detail: null);
    }

    public static SetupResult Cancelled(SetupState state)
    {
        return new SetupResult(
            SetupOutcome.Cancelled,
            state,
            detail: null);
    }

    public static SetupResult RequiresRollback(SetupState state)
    {
        return new SetupResult(
            SetupOutcome.RollbackRequired,
            state,
            detail: null);
    }

    public static SetupResult RebootRequired(SetupState state)
    {
        return new SetupResult(
            SetupOutcome.RebootRequired,
            state,
            detail: "rebootRequired");
    }

    public static SetupResult Failed(SetupState state, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new SetupResult(SetupOutcome.Failed, state, detail);
    }
}
