using EMKE.Windows.App.Bootstrap;
using EMKE.Windows.App.Dashboard;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Tray;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // Test resources transfer ownership to the composition root.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.

[TestClass]
public sealed class WindowLifetimeTests
{
    private static readonly string[] DashboardCall = ["dashboard"];

    [TestMethod]
    public async Task TrayAddsOneIconAfterCompositionAndRecreatesItAfterExplorerRestart()
    {
        FakeTrayIconTransport transport = new();
        TrayHost host = CreateHost(transport, new FakeTrayActions());

        await host.StartAsync(CancellationToken.None);
        await host.StartAsync(CancellationToken.None);

        Assert.AreEqual(1, transport.AddCount);
        await transport.RaiseAsync(TrayInteraction.TaskbarCreated);
        Assert.AreEqual(2, transport.AddCount);
    }

    [TestMethod]
    public async Task TrayPrimaryActivationShowsDashboard()
    {
        FakeTrayIconTransport transport = new();
        FakeTrayActions actions = new();
        TrayHost host = CreateHost(transport, actions);
        await host.StartAsync(CancellationToken.None);

        await transport.RaiseAsync(TrayInteraction.PrimaryActivate);

        CollectionAssert.AreEqual(
            DashboardCall,
            actions.Calls);
    }

    [DataRow("OpenDashboard", "dashboard")]
    [DataRow("OpenSettings", "settings")]
    [DataRow("OpenOnboarding", "onboarding")]
    [DataRow("CheckForUpdates", "update")]
    [DataRow("Exit", "exit")]
    [TestMethod]
    public async Task EveryTrayMenuActionRoutesToItsOwnedSurface(
        string interactionName,
        string expectedCall)
    {
        TrayInteraction interaction = Enum.Parse<TrayInteraction>(
            interactionName);
        FakeTrayIconTransport transport = new();
        FakeTrayActions actions = new();
        TrayHost host = CreateHost(transport, actions);
        await host.StartAsync(CancellationToken.None);

        await transport.RaiseAsync(interaction);

        CollectionAssert.AreEqual(
            new[] { expectedCall },
            actions.Calls);
    }

    [TestMethod]
    public void ClosingDashboardHidesItWithoutRequestingRuntimeStop()
    {
        int hideCount = 0;
        DashboardWindowLifetime lifetime = new(() => hideCount++);

        bool cancelClose = lifetime.HandleClosing();

        Assert.IsTrue(cancelClose);
        Assert.AreEqual(1, hideCount);
    }

    [TestMethod]
    public async Task ExitRemovesTrayIconEvenWhenRuntimeStopTimesOut()
    {
        FakeTrayIconTransport transport = new();
        TrayHost tray = CreateHost(transport, new FakeTrayActions());
        await tray.StartAsync(CancellationToken.None);
        AppCompositionRoot root = new(
            new UiCommandGate(),
            new NoOpDiagnostics(),
            new NeverStoppingRuntime(),
            new NoOpDisposable(),
            new NoOpAsyncDisposable(),
            tray,
            new NoOpAsyncDisposable(),
            new NoOpApplicationShutdown(),
            TimeSpan.FromMilliseconds(20));

        AppExitReport report = await root.ExitAsync();

        Assert.AreEqual(1, transport.DeleteCount);
        Assert.IsTrue(
            report.Errors.Any(
                static error =>
                    error.Kind == AppExitErrorKind.RuntimeStopTimeout));
    }

    private static TrayHost CreateHost(
        FakeTrayIconTransport transport,
        FakeTrayActions actions)
    {
        LocalizationService localization = new();
        localization.ChangeLanguage(AppInterfaceLanguage.English);
        return new TrayHost(transport, actions, localization);
    }

    private sealed class FakeTrayIconTransport : ITrayIconTransport
    {
        private Func<TrayInteraction, ValueTask>? _interaction;

        public int AddCount { get; private set; }

        public int DeleteCount { get; private set; }

        public ValueTask StartAsync(
            Func<TrayInteraction, ValueTask> interaction,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _interaction = interaction;
            return ValueTask.CompletedTask;
        }

        public void AddIcon(TrayMenuLabels labels)
        {
            Assert.AreEqual("Open Dashboard", labels.OpenDashboard);
            Assert.AreEqual("Exit", labels.Exit);
            AddCount++;
        }

        public void UpdateIcon(TrayMenuLabels labels)
        {
        }

        public void DeleteIcon()
        {
            DeleteCount++;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask RaiseAsync(TrayInteraction interaction)
        {
            return _interaction?.Invoke(interaction)
                ?? throw new InvalidOperationException(
                    "The tray transport has not started.");
        }
    }

    private sealed class FakeTrayActions : ITrayActions
    {
        public List<string> Calls { get; } = [];

        public ValueTask CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("update");
            return ValueTask.CompletedTask;
        }

        public ValueTask ExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("exit");
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowDashboardAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("dashboard");
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowOnboardingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("onboarding");
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowSettingsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("settings");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverStoppingRuntime : IAppRuntimeLifetime
    {
        public Task<RuntimeStopResult> StopAsync(CancellationToken cancellationToken)
        {
            return new TaskCompletionSource<RuntimeStopResult>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoOpDiagnostics : IAppDiagnosticsLifetime
    {
        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpApplicationShutdown : IApplicationShutdown
    {
        public void Shutdown()
        {
        }
    }
}
