using System.Globalization;
using EMKE.Core;
using EMKE.Windows.App.Commands;
using EMKE.Windows.App.Floating;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Presentation;
using EMKE.Windows.App.State;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.

[TestClass]
public sealed class FloatingStatusViewModelTests
{
    [TestMethod]
    public void StoppedHidesFloatingSurface()
    {
        using FloatingFixture fixture = new();

        fixture.Publish(Snapshot(RuntimeState.Stopped));

        Assert.IsFalse(fixture.ViewModel.ShouldBeVisible);
        Assert.IsFalse(fixture.ViewModel.StopCommand.CanExecute(null));
    }

    [DataRow(RuntimeState.Starting)]
    [DataRow(RuntimeState.Running)]
    [DataRow(RuntimeState.Stopping)]
    [DataRow(RuntimeState.Degraded)]
    [DataRow(RuntimeState.Failed)]
    [TestMethod]
    public void EveryNonStoppedStateKeepsFloatingSurfaceVisible(
        RuntimeState state)
    {
        using FloatingFixture fixture = new();

        fixture.Publish(Snapshot(state));

        Assert.IsTrue(fixture.ViewModel.ShouldBeVisible);
    }

    [TestMethod]
    public async Task FloatingStopUsesThePriorityRuntimeCommand()
    {
        using FloatingFixture fixture = new();
        fixture.Publish(Snapshot(RuntimeState.Running));

        Assert.IsTrue(await fixture.ViewModel.StopCommand.ExecuteAsync());

        Assert.HasCount(1, fixture.Sink.Commands);
        Assert.IsInstanceOfType<RuntimeCommand.Stop>(
            fixture.Sink.Commands[0]);
        Assert.IsTrue(fixture.ViewModel.StopCommand.IsPriority);
    }

    [TestMethod]
    public void FloatingCaptionsAreShortAndUnicodeSafe()
    {
        using FloatingFixture fixture = new();
        string longCaption = string.Concat(
            Enumerable.Repeat("👩🏽‍💻", 150));

        fixture.Publish(
            Snapshot(
                RuntimeState.Running,
                sourceCaption: longCaption,
                translatedCaption: longCaption));

        Assert.IsLessThanOrEqualTo(
            96,
            new StringInfo(fixture.ViewModel.SourceCaption)
                .LengthInTextElements);
        Assert.IsLessThanOrEqualTo(
            96,
            new StringInfo(fixture.ViewModel.TranslatedCaption)
                .LengthInTextElements);
        StringAssert.EndsWith(
            fixture.ViewModel.SourceCaption,
            "…",
            StringComparison.Ordinal);
        StringAssert.EndsWith(
            fixture.ViewModel.TranslatedCaption,
            "…",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void FloatingStateMirrorsDirectionalStatusAndLevels()
    {
        using FloatingFixture fixture = new();

        fixture.Publish(
            Snapshot(
                RuntimeState.Degraded,
                inboundState: ChannelState.Reconnecting,
                outboundState: ChannelState.Failed,
                inboundRoute: InboundRoute.OriginalFailOpen,
                outboundRoute: OutboundRoute.MutedFailClosed,
                inboundLevel: 0.2,
                outboundLevel: 0.9));

        Assert.AreEqual("Translation degraded", fixture.ViewModel.RuntimeStatus);
        Assert.AreEqual("Reconnecting", fixture.ViewModel.InboundStatus);
        Assert.AreEqual("Failed", fixture.ViewModel.OutboundStatus);
        Assert.AreEqual(0.2, fixture.ViewModel.InboundLevel);
        Assert.AreEqual(0.9, fixture.ViewModel.OutboundLevel);
    }

    private static AppSnapshot Snapshot(
        RuntimeState state,
        ChannelState inboundState = ChannelState.Inactive,
        ChannelState outboundState = ChannelState.Inactive,
        InboundRoute inboundRoute = InboundRoute.Stopped,
        OutboundRoute outboundRoute = OutboundRoute.Stopped,
        double inboundLevel = 0.25,
        double outboundLevel = 0.75,
        string sourceCaption = "source",
        string translatedCaption = "translated")
    {
        return new AppSnapshot(
            contractVersion: 1,
            version: SnapshotVersion.Next(),
            state,
            inboundState,
            outboundState,
            inboundRoute,
            outboundRoute,
            inboundLevel,
            outboundLevel,
            sourceCaption,
            translatedCaption,
            new AudioSelection("input", "output"),
            new DriverCompatibility(true, "compatible"),
            connectionReport: null,
            new AudioDiagnostics(true, 0),
            new UpdateAvailability(false, string.Empty),
            error: null);
    }

    private static class SnapshotVersion
    {
        private static long s_value;

        public static ulong Next()
        {
            return checked((ulong)Interlocked.Increment(ref s_value));
        }
    }

    private sealed class FloatingFixture : IDisposable
    {
        private readonly AppSnapshotStore _store;
        private readonly PresentationCoordinator _coordinator;

        public FloatingFixture()
        {
            LocalizationService localization = new(
                () => CultureInfo.GetCultureInfo("en-US"));
            localization.ChangeLanguage(AppInterfaceLanguage.English);
            _store = new AppSnapshotStore(new ImmediateDispatcher());
            _coordinator = new PresentationCoordinator(
                _store,
                localization,
                new AppPresentationMapper(localization));
            Sink = new RecordingRuntimeCommandSink();
            ViewModel = new FloatingStatusViewModel(_coordinator, Sink);
        }

        public RecordingRuntimeCommandSink Sink { get; }

        public FloatingStatusViewModel ViewModel { get; }

        public void Publish(AppSnapshot snapshot)
        {
            _store.Publish(snapshot);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _coordinator.Dispose();
            _store.Dispose();
        }
    }

    private sealed class ImmediateDispatcher : IAppDispatcher
    {
        public void Post(Action callback)
        {
            callback();
        }
    }

    private sealed class RecordingRuntimeCommandSink : IRuntimeCommandSink
    {
        public List<RuntimeCommand> Commands { get; } = [];

        public Task<RuntimeError?> SubmitAsync(
            RuntimeCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult<RuntimeError?>(null);
        }
    }
}
