using EMKE.Setup;

namespace EMKE.Setup.Tests;

[TestClass]
public sealed class SetupStateTests
{
    [TestMethod]
    public void ExactForwardSequenceCompletes()
    {
        SetupStateMachine machine = new();

        machine.AdvanceTo(SetupState.Verified);
        machine.AdvanceTo(SetupState.MachineChangesStarted);
        machine.AdvanceTo(SetupState.DriverReady);
        machine.AdvanceTo(SetupState.UserPackageReady);
        machine.AdvanceTo(SetupState.EndpointVerified);
        machine.AdvanceTo(SetupState.Complete);

        Assert.AreEqual(SetupState.Complete, machine.State);
    }

    [TestMethod]
    public void SkippedReversedAndRepeatedTransitionsAreRejected()
    {
        SetupStateMachine machine = new();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => machine.AdvanceTo(SetupState.MachineChangesStarted));
        machine.AdvanceTo(SetupState.Verified);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => machine.AdvanceTo(SetupState.Preflight));
        machine.AdvanceTo(SetupState.MachineChangesStarted);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => machine.AdvanceTo(SetupState.MachineChangesStarted));
    }

    [TestMethod]
    public void CancellationBeforeMachineChangesIsClean()
    {
        SetupStateMachine preflight = new();
        SetupResult beforeVerification = preflight.Cancel(
            resumableRebootRequired: false);

        Assert.AreEqual(SetupOutcome.Cancelled, beforeVerification.Outcome);
        Assert.AreEqual(SetupState.Preflight, beforeVerification.State);

        SetupStateMachine verified = new();
        verified.AdvanceTo(SetupState.Verified);
        SetupResult afterVerification = verified.Cancel(
            resumableRebootRequired: false);

        Assert.AreEqual(SetupOutcome.Cancelled, afterVerification.Outcome);
        Assert.AreEqual(SetupState.Verified, afterVerification.State);
    }

    [TestMethod]
    public void CancellationAfterMachineChangesRequiresRollback()
    {
        SetupStateMachine machine = StartedMachineChanges();

        SetupResult result = machine.Cancel(resumableRebootRequired: false);

        Assert.AreEqual(SetupOutcome.RollbackRequired, result.Outcome);
        Assert.AreEqual(SetupState.RollbackRequired, result.State);
        Assert.AreEqual(SetupState.RollbackRequired, machine.State);
    }

    [TestMethod]
    public void ResumableRebootPreservesTheCheckpoint()
    {
        SetupStateMachine machine = StartedMachineChanges();

        SetupResult result = machine.Cancel(resumableRebootRequired: true);

        Assert.AreEqual(SetupOutcome.RebootRequired, result.Outcome);
        Assert.AreEqual(
            SetupState.MachineChangesStarted,
            result.State);
        Assert.AreEqual(
            SetupState.MachineChangesStarted,
            machine.State);
    }

    private static SetupStateMachine StartedMachineChanges()
    {
        SetupStateMachine machine = new();
        machine.AdvanceTo(SetupState.Verified);
        machine.AdvanceTo(SetupState.MachineChangesStarted);
        return machine;
    }
}
