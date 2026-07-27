using EMKE.Application;
using EMKE.Core;
using EMKE.Routing;

namespace EMKE.Application.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class RuntimeStateReducerTests
{
    [TestMethod]
    public void OldGenerationStartResultCannotOverwriteStoppedSnapshot()
    {
        RuntimeStateReducer reducer = new();

        long startGeneration = reducer.BeginStart();
        long stopGeneration = reducer.BeginStop();
        AppSnapshot stopped = reducer.CompleteStop(stopGeneration);

        bool applied = reducer.TryCompleteStart(
            startGeneration,
            ConnectedRouting(generation: startGeneration),
            error: null,
            out AppSnapshot afterStaleResult);

        Assert.IsFalse(applied);
        Assert.AreSame(stopped, afterStaleResult);
        Assert.AreEqual(RuntimeState.Stopped, afterStaleResult.RuntimeState);
    }

    [TestMethod]
    public void StopClearsCaptionsLevelsRoutesAndChannelState()
    {
        RuntimeStateReducer reducer = new();
        long generation = reducer.BeginStart();
        Assert.IsTrue(reducer.TryCompleteStart(
            generation,
            ConnectedRouting(generation),
            error: null,
            out _));
        reducer.UpdateCaptions(generation, "source", "translated");
        reducer.UpdateLevels(generation, 0.8, 0.6);
        reducer.UpdateAudioDiagnostics(generation, droppedFrameCount: 7);

        long stopGeneration = reducer.BeginStop();
        AppSnapshot stopped = reducer.CompleteStop(stopGeneration);

        Assert.AreEqual(RuntimeState.Stopped, stopped.RuntimeState);
        Assert.AreEqual(ChannelState.Inactive, stopped.InboundChannelState);
        Assert.AreEqual(ChannelState.Inactive, stopped.OutboundChannelState);
        Assert.AreEqual(InboundRoute.Stopped, stopped.InboundRoute);
        Assert.AreEqual(OutboundRoute.Stopped, stopped.OutboundRoute);
        Assert.AreEqual(0, stopped.InboundLevel);
        Assert.AreEqual(0, stopped.OutboundLevel);
        Assert.AreEqual(string.Empty, stopped.SourceCaption);
        Assert.AreEqual(string.Empty, stopped.TranslatedCaption);
        Assert.IsTrue(stopped.AudioDiagnostics.IsHealthy);
        Assert.AreEqual(0UL, stopped.AudioDiagnostics.DroppedFrameCount);
    }

    private static RoutingPolicySnapshot ConnectedRouting(long generation)
    {
        return new RoutingPolicySnapshot(
            ChannelState.Connected,
            ChannelState.Connected,
            InboundRoute.Translated,
            OutboundRoute.Translated,
            InboundErrorCategory: null,
            OutboundErrorCategory: null,
            OutputsZeros: false,
            PhysicalMicrophoneAllowed: false,
            BypassPersisted: false,
            Generation: generation);
    }
}
