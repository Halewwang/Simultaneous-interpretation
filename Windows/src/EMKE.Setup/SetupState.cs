namespace EMKE.Setup;

internal enum SetupState
{
    Preflight,
    Verified,
    MachineChangesStarted,
    DriverReady,
    UserPackageReady,
    EndpointVerified,
    Complete,
    RollbackRequired,
}

internal enum SetupOutcome
{
    Succeeded,
    Cancelled,
    RollbackRequired,
    RebootRequired,
    Failed,
}

internal sealed class SetupStateMachine
{
    public SetupState State { get; private set; } = SetupState.Preflight;

    public void AdvanceTo(SetupState nextState)
    {
        SetupState expected = State switch
        {
            SetupState.Preflight => SetupState.Verified,
            SetupState.Verified => SetupState.MachineChangesStarted,
            SetupState.MachineChangesStarted => SetupState.DriverReady,
            SetupState.DriverReady => SetupState.UserPackageReady,
            SetupState.UserPackageReady => SetupState.EndpointVerified,
            SetupState.EndpointVerified => SetupState.Complete,
            _ => throw new InvalidOperationException(
                $"Setup cannot advance from terminal state '{State}'."),
        };
        if (nextState != expected)
        {
            throw new InvalidOperationException(
                $"Setup must advance from '{State}' to '{expected}', not '{nextState}'.");
        }

        State = nextState;
    }

    public SetupResult Cancel(bool resumableRebootRequired)
    {
        if (State is SetupState.Preflight or SetupState.Verified)
        {
            return SetupResult.Cancelled(State);
        }

        if (State is SetupState.Complete or SetupState.RollbackRequired)
        {
            throw new InvalidOperationException(
                $"Setup cannot be cancelled from terminal state '{State}'.");
        }

        if (resumableRebootRequired)
        {
            return SetupResult.RebootRequired(State);
        }

        State = SetupState.RollbackRequired;
        return SetupResult.RequiresRollback(State);
    }
}
