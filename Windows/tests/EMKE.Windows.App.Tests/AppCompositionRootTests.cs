using System.Diagnostics;
using EMKE.Windows.App.Bootstrap;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // Test resources transfer ownership to the composition root.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.

[TestClass]
public sealed class AppCompositionRootTests
{
    private static readonly string[] ExpectedExitOrder =
    [
        "command",
        "commands.disable",
        "runtime.stop",
        "diagnostics.stop",
        "snapshots.dispose",
        "runtime.dispose",
        "adapters.dispose",
        "tray.remove",
        "coordinator.dispose",
        "application.shutdown",
    ];

    [TestMethod]
    public async Task ExitDisablesCommandsBeforeStoppingOwnedResourcesInOrder()
    {
        List<string> calls = [];
        FakeRuntime runtime = new(calls);
        AppCompositionRoot root = CreateRoot(calls, runtime);
        bool commandAccepted = await root.TryRunUiCommandAsync(
            _ =>
            {
                calls.Add("command");
                return ValueTask.CompletedTask;
            });

        AppExitReport report = await root.ExitAsync();
        bool lateCommandAccepted = await root.TryRunUiCommandAsync(
            _ => ValueTask.CompletedTask);

        Assert.IsTrue(commandAccepted);
        Assert.IsFalse(lateCommandAccepted);
        Assert.IsEmpty(report.Errors);
        CollectionAssert.AreEqual(
            ExpectedExitOrder,
            calls);
    }

    [TestMethod]
    public async Task RuntimeStopDeadlineDoesNotBlockMandatoryCleanup()
    {
        List<string> calls = [];
        FakeRuntime runtime = new(calls)
        {
            StopTask = new TaskCompletionSource<RuntimeStopResult>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task,
        };
        AppCompositionRoot root = CreateRoot(
            calls,
            runtime,
            runtimeStopDeadline: TimeSpan.FromMilliseconds(25));
        Stopwatch stopwatch = Stopwatch.StartNew();

        AppExitReport report = await root.ExitAsync();

        stopwatch.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.AreEqual(AppExitErrorKind.RuntimeStopTimeout, report.Errors[0].Kind);
        CollectionAssert.Contains(calls, "runtime.dispose");
        CollectionAssert.Contains(calls, "tray.remove");
        CollectionAssert.Contains(calls, "coordinator.dispose");
        CollectionAssert.Contains(calls, "application.shutdown");
    }

    [TestMethod]
    public async Task CleanupFailuresCannotSkipTrayCoordinatorOrShutdown()
    {
        List<string> calls = [];
        FakeRuntime runtime = new(calls);
        AppCompositionRoot root = CreateRoot(
            calls,
            runtime,
            diagnosticsFailure: new InvalidOperationException("diagnostics"),
            trayFailure: new InvalidOperationException("tray"),
            coordinatorFailure: new InvalidOperationException("coordinator"));

        AppExitReport report = await root.ExitAsync();

        Assert.HasCount(3, report.Errors);
        CollectionAssert.Contains(calls, "runtime.stop");
        CollectionAssert.Contains(calls, "runtime.dispose");
        CollectionAssert.Contains(calls, "adapters.dispose");
        CollectionAssert.Contains(calls, "tray.remove");
        CollectionAssert.Contains(calls, "coordinator.dispose");
        CollectionAssert.Contains(calls, "application.shutdown");
    }

    [TestMethod]
    public async Task ConcurrentExitRequestsShareOneLifetimeSequence()
    {
        List<string> calls = [];
        FakeRuntime runtime = new(calls);
        AppCompositionRoot root = CreateRoot(calls, runtime);

        Task<AppExitReport> first = root.ExitAsync();
        Task<AppExitReport> second = root.ExitAsync();
        await Task.WhenAll(first, second);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, calls.Count(call => call == "runtime.stop"));
        Assert.AreEqual(1, calls.Count(call => call == "application.shutdown"));
    }

    private static AppCompositionRoot CreateRoot(
        List<string> calls,
        FakeRuntime runtime,
        TimeSpan? runtimeStopDeadline = null,
        Exception? diagnosticsFailure = null,
        Exception? trayFailure = null,
        Exception? coordinatorFailure = null)
    {
        return new AppCompositionRoot(
            new UiCommandGate(() => calls.Add("commands.disable")),
            new FakeDiagnostics(calls, diagnosticsFailure),
            runtime,
            new RecordingDisposable(calls, "snapshots.dispose"),
            new RecordingAsyncDisposable(calls, "adapters.dispose"),
            new FakeTrayHost(calls, trayFailure),
            new RecordingAsyncDisposable(
                calls,
                "coordinator.dispose",
                coordinatorFailure),
            new FakeApplicationShutdown(calls),
            runtimeStopDeadline ?? TimeSpan.FromSeconds(1));
    }

    private sealed class FakeRuntime : IAppRuntimeLifetime
    {
        private readonly List<string> _calls;

        public FakeRuntime(List<string> calls)
        {
            _calls = calls;
        }

        public Task<RuntimeStopResult> StopTask { get; init; } =
            Task.FromResult(RuntimeStopResult.Stopped);

        public Task<RuntimeStopResult> StopAsync(
            CancellationToken cancellationToken)
        {
            _calls.Add("runtime.stop");
            return StopTask;
        }

        public void Dispose()
        {
            _calls.Add("runtime.dispose");
        }
    }

    private sealed class FakeDiagnostics : IAppDiagnosticsLifetime
    {
        private readonly List<string> _calls;
        private readonly Exception? _failure;

        public FakeDiagnostics(List<string> calls, Exception? failure)
        {
            _calls = calls;
            _failure = failure;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add("diagnostics.stop");
            return _failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_failure);
        }
    }

    private sealed class FakeTrayHost : IAppTrayLifetime
    {
        private readonly List<string> _calls;
        private readonly Exception? _failure;

        public FakeTrayHost(List<string> calls, Exception? failure)
        {
            _calls = calls;
            _failure = failure;
        }

        public ValueTask RemoveAsync()
        {
            _calls.Add("tray.remove");
            return _failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_failure);
        }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeApplicationShutdown : IApplicationShutdown
    {
        private readonly List<string> _calls;

        public FakeApplicationShutdown(List<string> calls)
        {
            _calls = calls;
        }

        public void Shutdown()
        {
            _calls.Add("application.shutdown");
        }
    }

    private sealed class RecordingDisposable : IDisposable
    {
        private readonly List<string> _calls;
        private readonly string _label;

        public RecordingDisposable(List<string> calls, string label)
        {
            _calls = calls;
            _label = label;
        }

        public void Dispose()
        {
            _calls.Add(_label);
        }
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        private readonly List<string> _calls;
        private readonly string _label;
        private readonly Exception? _failure;

        public RecordingAsyncDisposable(
            List<string> calls,
            string label,
            Exception? failure = null)
        {
            _calls = calls;
            _label = label;
            _failure = failure;
        }

        public ValueTask DisposeAsync()
        {
            _calls.Add(_label);
            return _failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_failure);
        }
    }
}
