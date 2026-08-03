using System.Collections.Concurrent;
using EMKE.Windows.App.Bootstrap;
using EMKE.Windows.App.Dashboard;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Tray;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // Test resources transfer ownership to the composition root.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.
#pragma warning disable CA1031 // Test dispatcher transports arbitrary action failures.

[TestClass]
public sealed class WindowLifetimeTests
{
    private static readonly string[] DashboardCall = ["dashboard"];
    private static readonly string[] NativeLifecycleCalls =
        ["create", "add", "update", "delete", "destroy"];

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
        ITrayIconTransport transport,
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

        public ValueTask AddIconAsync(TrayMenuLabels labels)
        {
            Assert.AreEqual("Open Dashboard", labels.OpenDashboard);
            Assert.AreEqual("Exit", labels.Exit);
            AddCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask UpdateIconAsync(TrayMenuLabels labels)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteIconAsync()
        {
            DeleteCount++;
            return ValueTask.CompletedTask;
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

    [TestMethod]
    public async Task ShellInteropMarshalsCreateUpdateDeleteAndDestroyToOneDispatcherThread()
    {
        await using DedicatedTestUiDispatcher dispatcher =
            await DedicatedTestUiDispatcher.CreateAsync();
        RecordingNativeTraySession native = new();
        await using ShellNotifyIconInterop transport = new(dispatcher, native);
        TrayMenuLabels labels = EnglishLabels();
        int testThread = Environment.CurrentManagedThreadId;

        await transport.StartAsync(
            _ => ValueTask.CompletedTask,
            CancellationToken.None);
        await transport.AddIconAsync(labels);
        await transport.UpdateIconAsync(labels);
        await transport.DeleteIconAsync();
        await transport.DisposeAsync();

        Assert.AreNotEqual(testThread, dispatcher.ThreadId);
        CollectionAssert.AreEqual(
            NativeLifecycleCalls,
            native.Calls);
        Assert.IsTrue(
            native.ThreadIds.All(
                threadId => threadId == dispatcher.ThreadId));
    }

    [TestMethod]
    public async Task ApplicationExitDeletesAndDestroysTrayOnDispatcherThread()
    {
        await using DedicatedTestUiDispatcher dispatcher =
            await DedicatedTestUiDispatcher.CreateAsync();
        RecordingNativeTraySession native = new();
        ShellNotifyIconInterop transport = new(dispatcher, native);
        TrayHost tray = CreateHost(transport, new FakeTrayActions());
        await tray.StartAsync(CancellationToken.None);
        AppCompositionRoot root = new(
            new UiCommandGate(),
            new NoOpDiagnostics(),
            new ImmediatelyStoppingRuntime(),
            new NoOpDisposable(),
            new NoOpAsyncDisposable(),
            tray,
            new NoOpAsyncDisposable(),
            new NoOpApplicationShutdown(),
            TimeSpan.FromSeconds(1));

        AppExitReport report = await root.ExitAsync();

        Assert.IsEmpty(report.Errors);
        CollectionAssert.Contains(native.Calls, "delete");
        CollectionAssert.Contains(native.Calls, "destroy");
        Assert.IsTrue(
            native.ThreadIds.All(
                threadId => threadId == dispatcher.ThreadId));
    }

    private static TrayMenuLabels EnglishLabels()
    {
        return new TrayMenuLabels(
            "EMKE Translation",
            "Open Dashboard",
            "Settings",
            "Open Setup",
            "Check for Updates",
            "Exit");
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

    private sealed class ImmediatelyStoppingRuntime : IAppRuntimeLifetime
    {
        public Task<RuntimeStopResult> StopAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RuntimeStopResult.Stopped);
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

    private sealed class RecordingNativeTraySession :
        IShellNotifyIconNativeSession
    {
        public List<string> Calls { get; } = [];

        public List<int> ThreadIds { get; } = [];

        public void Create(Func<TrayInteraction, ValueTask> interaction)
        {
            Record("create");
        }

        public void Add(TrayMenuLabels labels)
        {
            Record("add");
        }

        public void Update(TrayMenuLabels labels)
        {
            Record("update");
        }

        public void Delete()
        {
            Record("delete");
        }

        public void Destroy()
        {
            Record("destroy");
        }

        private void Record(string call)
        {
            Calls.Add(call);
            ThreadIds.Add(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class DedicatedTestUiDispatcher :
        IUiDispatcher,
        IAsyncDisposable
    {
        private readonly BlockingCollection<WorkItem> _work = [];
        private readonly TaskCompletionSource<int> _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        private DedicatedTestUiDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "EMKE tray dispatcher test",
            };
            _thread.Start();
        }

        public int ThreadId { get; private set; }

        public static async Task<DedicatedTestUiDispatcher> CreateAsync()
        {
            DedicatedTestUiDispatcher dispatcher = new();
            dispatcher.ThreadId = await dispatcher._started.Task;
            return dispatcher;
        }

        public ValueTask InvokeAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                action();
                return ValueTask.CompletedTask;
            }

            TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(
                new WorkItem(action, completion),
                cancellationToken);
            return new ValueTask(
                completion.Task.WaitAsync(cancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            _work.CompleteAdding();
            await _finished.Task;
            _work.Dispose();
        }

        private void Run()
        {
            _started.SetResult(Environment.CurrentManagedThreadId);
            try
            {
                foreach (WorkItem work in _work.GetConsumingEnumerable())
                {
                    try
                    {
                        work.Action();
                        work.Completion.SetResult();
                    }
                    catch (Exception exception)
                    {
                        work.Completion.SetException(exception);
                    }
                }
            }
            finally
            {
                _finished.SetResult();
            }
        }

        private sealed record WorkItem(
            Action Action,
            TaskCompletionSource Completion);
    }
}
