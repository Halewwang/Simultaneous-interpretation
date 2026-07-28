using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using EMKE.Core;
using EMKE.Windows.App.Commands;
using EMKE.Windows.App.Dashboard;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Presentation;
using EMKE.Windows.App.State;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.

[TestClass]
public sealed class DashboardViewModelTests
{
    [TestMethod]
    public void ActiveRuntimeDisablesLanguageSelectorsAndKeepsStopEnabled()
    {
        using DashboardFixture fixture = new();
        fixture.Publish(Snapshot(RuntimeState.Running));

        Assert.IsFalse(fixture.ViewModel.AreLanguageSelectorsEnabled);
        Assert.IsFalse(fixture.ViewModel.StartCommand.CanExecute(null));
        Assert.IsTrue(fixture.ViewModel.StopCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ConcurrentStartRequestsSubmitExactlyOnce()
    {
        TaskCompletionSource gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using DashboardFixture fixture = new(
            (command, _) =>
                command is RuntimeCommand.Start
                    ? gate.Task
                    : Task.CompletedTask);
        fixture.Publish(Snapshot(RuntimeState.Stopped));

        Task<bool> first = fixture.ViewModel.StartCommand.ExecuteAsync();
        Task<bool> duplicate = fixture.ViewModel.StartCommand.ExecuteAsync();
        await fixture.Sink.WaitForCountAsync(1);

        Assert.IsFalse(await duplicate);
        Assert.HasCount(1, fixture.Sink.Commands);
        Assert.IsInstanceOfType<RuntimeCommand.Start>(
            fixture.Sink.Commands[0]);

        gate.SetResult();
        Assert.IsTrue(await first);
    }

    [TestMethod]
    public async Task StopPreemptsAnActiveStartCommand()
    {
        TaskCompletionSource startGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using DashboardFixture fixture = new(
            (command, _) =>
                command is RuntimeCommand.Start
                    ? startGate.Task
                    : Task.CompletedTask);
        fixture.Publish(Snapshot(RuntimeState.Stopped));
        Task<bool> start = fixture.ViewModel.StartCommand.ExecuteAsync();
        await fixture.Sink.WaitForCountAsync(1);
        fixture.Publish(Snapshot(RuntimeState.Starting));

        Assert.IsTrue(fixture.ViewModel.StopCommand.CanExecute(null));
        Assert.IsTrue(await fixture.ViewModel.StopCommand.ExecuteAsync());

        CollectionAssert.AreEqual(
            new[] { typeof(RuntimeCommand.Start), typeof(RuntimeCommand.Stop) },
            fixture.Sink.Commands.Select(static command => command.GetType())
                .ToArray());
        startGate.SetResult();
        Assert.IsTrue(await start);
    }

    [TestMethod]
    public async Task InboundAndOutboundBypassCommandsRemainIndependent()
    {
        using DashboardFixture fixture = new();
        fixture.Publish(Snapshot(RuntimeState.Running));

        Assert.IsTrue(
            await fixture.ViewModel.InboundBypassCommand.ExecuteAsync());
        Assert.IsTrue(
            await fixture.ViewModel.OutboundBypassCommand.ExecuteAsync());

        Assert.HasCount(2, fixture.Sink.Commands);
        Assert.AreEqual(
            new RuntimeCommand.SetInboundBypass(true),
            fixture.Sink.Commands[0]);
        Assert.AreEqual(
            new RuntimeCommand.SetOutboundBypass(true),
            fixture.Sink.Commands[1]);
    }

    [TestMethod]
    public async Task BypassCommandTogglesOnlyItsCurrentDirection()
    {
        using DashboardFixture fixture = new();
        fixture.Publish(
            Snapshot(
                RuntimeState.Running,
                inboundRoute: InboundRoute.OriginalBypass,
                outboundRoute: OutboundRoute.Translated));

        await fixture.ViewModel.InboundBypassCommand.ExecuteAsync();
        await fixture.ViewModel.OutboundBypassCommand.ExecuteAsync();

        Assert.AreEqual(
            new RuntimeCommand.SetInboundBypass(false),
            fixture.Sink.Commands[0]);
        Assert.AreEqual(
            new RuntimeCommand.SetOutboundBypass(true),
            fixture.Sink.Commands[1]);
    }

    [TestMethod]
    public void CaptionsAreBoundedWithoutSplittingUnicodeTextElements()
    {
        using DashboardFixture fixture = new();
        string longCaption = string.Concat(
            Enumerable.Repeat("👩🏽‍💻", 400));
        fixture.Publish(
            Snapshot(
                RuntimeState.Running,
                sourceCaption: longCaption,
                translatedCaption: longCaption));

        Assert.IsLessThanOrEqualTo(
            320,
            new StringInfo(fixture.ViewModel.SourceCaption)
                .LengthInTextElements);
        Assert.IsLessThanOrEqualTo(
            320,
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
    public void LevelsAndDirectionalChannelFailuresMirrorOnePresentation()
    {
        using DashboardFixture fixture = new();
        fixture.Publish(
            Snapshot(
                RuntimeState.Degraded,
                inboundState: ChannelState.Degraded,
                outboundState: ChannelState.Failed,
                inboundRoute: InboundRoute.OriginalFailOpen,
                outboundRoute: OutboundRoute.MutedFailClosed,
                inboundLevel: 0.125,
                outboundLevel: 0.875));

        Assert.AreEqual(0.125, fixture.ViewModel.InboundLevel);
        Assert.AreEqual(0.875, fixture.ViewModel.OutboundLevel);
        Assert.AreEqual("Degraded", fixture.ViewModel.InboundStatus);
        Assert.AreEqual("Failed", fixture.ViewModel.OutboundStatus);
        Assert.AreEqual(
            "Original audio remains audible while inbound translation recovers.",
            fixture.ViewModel.InboundSafetyMessage);
        Assert.AreEqual(
            "Your meeting microphone is muted while outbound translation recovers.",
            fixture.ViewModel.OutboundSafetyMessage);
    }

    [TestMethod]
    public void DashboardXamlBindsBothDirectionalSafetyMessages()
    {
        XDocument dashboard = XDocument.Load(DashboardXamlPath());
        string[] bindings = dashboard
            .Descendants()
            .Attributes()
            .Select(static attribute => attribute.Value)
            .Where(static value =>
                value.Contains(
                    "SafetyMessage",
                    StringComparison.Ordinal))
            .ToArray();

        CollectionAssert.Contains(
            bindings,
            "{Binding InboundSafetyMessage}");
        CollectionAssert.Contains(
            bindings,
            "{Binding OutboundSafetyMessage}");
    }

    [TestMethod]
    public void DirectionalSafetyMessagesRemapInEnglishAndSimplifiedChinese()
    {
        using DashboardFixture fixture = new();
        fixture.Publish(
            Snapshot(
                RuntimeState.Degraded,
                inboundState: ChannelState.Degraded,
                outboundState: ChannelState.Degraded,
                inboundRoute: InboundRoute.OriginalFailOpen,
                outboundRoute: OutboundRoute.MutedFailClosed));

        Assert.AreEqual(
            "Original audio remains audible while inbound translation recovers.",
            fixture.ViewModel.InboundSafetyMessage);
        Assert.AreEqual(
            "Your meeting microphone is muted while outbound translation recovers.",
            fixture.ViewModel.OutboundSafetyMessage);

        fixture.Localization.ChangeLanguage(AppInterfaceLanguage.ZhHans);

        Assert.AreEqual(
            "入站翻译恢复期间，仍会播放会议原声。",
            fixture.ViewModel.InboundSafetyMessage);
        Assert.AreEqual(
            "出站翻译恢复期间，发送到会议的麦克风将保持静音。",
            fixture.ViewModel.OutboundSafetyMessage);
    }

    private static string DashboardXamlPath(
        [CallerFilePath] string sourceFile = "")
    {
        string testDirectory = Path.GetDirectoryName(sourceFile)
            ?? throw new InvalidOperationException(
                "The test source path is unavailable.");
        return Path.GetFullPath(
            Path.Combine(
                testDirectory,
                "..",
                "..",
                "src",
                "EMKE.Windows.App",
                "Dashboard",
                "DashboardWindow.xaml"));
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

    private sealed class DashboardFixture : IDisposable
    {
        private readonly AppSnapshotStore _store;
        private readonly PresentationCoordinator _coordinator;

        public DashboardFixture(
            Func<RuntimeCommand, CancellationToken, Task>? onSubmit = null)
        {
            Localization = new LocalizationService(
                () => CultureInfo.GetCultureInfo("en-US"));
            Localization.ChangeLanguage(AppInterfaceLanguage.English);
            _store = new AppSnapshotStore(new ImmediateDispatcher());
            _coordinator = new PresentationCoordinator(
                _store,
                Localization,
                new AppPresentationMapper(Localization));
            Sink = new RecordingRuntimeCommandSink(onSubmit);
            ViewModel = new DashboardViewModel(
                _coordinator,
                Localization,
                Sink,
                new NoOpSurfaceActions());
        }

        public LocalizationService Localization { get; }

        public RecordingRuntimeCommandSink Sink { get; }

        public DashboardViewModel ViewModel { get; }

        public void Publish(AppSnapshot snapshot)
        {
            _store.Publish(snapshot);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            Sink.Dispose();
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

    private sealed class RecordingRuntimeCommandSink :
        IRuntimeCommandSink,
        IDisposable
    {
        private readonly Func<RuntimeCommand, CancellationToken, Task>? _onSubmit;
        private readonly SemaphoreSlim _submitted = new(0);

        public RecordingRuntimeCommandSink(
            Func<RuntimeCommand, CancellationToken, Task>? onSubmit)
        {
            _onSubmit = onSubmit;
        }

        public List<RuntimeCommand> Commands { get; } = [];

        public async Task<RuntimeError?> SubmitAsync(
            RuntimeCommand command,
            CancellationToken cancellationToken)
        {
            lock (Commands)
            {
                Commands.Add(command);
            }

            _submitted.Release();
            if (_onSubmit is not null)
            {
                await _onSubmit(command, cancellationToken);
            }

            return null;
        }

        public async Task WaitForCountAsync(int count)
        {
            while (true)
            {
                lock (Commands)
                {
                    if (Commands.Count >= count)
                    {
                        return;
                    }
                }

                await _submitted.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        public void Dispose()
        {
            _submitted.Dispose();
        }
    }

    private sealed class NoOpSurfaceActions : IAppSurfaceActions
    {
        public ValueTask OpenDiagnosticsAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenSettingsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
