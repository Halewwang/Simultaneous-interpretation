using EMKE.Application;
using EMKE.Core;
using EMKE.Windows.App.State;

namespace EMKE.Windows.App.Bootstrap;

#pragma warning disable CA1031 // Exit is a best-effort boundary that must reach every cleanup step.

internal enum RuntimeStopResult
{
    Stopped,
    Failed,
}

internal enum AppExitErrorKind
{
    DiagnosticsStop,
    RuntimeStop,
    RuntimeStopTimeout,
    SnapshotDispose,
    RuntimeDispose,
    AdapterDispose,
    TrayRemove,
    CoordinatorDispose,
    ApplicationShutdown,
}

internal sealed record AppExitError(
    AppExitErrorKind Kind,
    Exception? Exception);

internal sealed record AppExitReport(IReadOnlyList<AppExitError> Errors);

internal interface IAppRuntimeLifetime : IDisposable
{
    Task<RuntimeStopResult> StopAsync(CancellationToken cancellationToken);
}

internal interface IAppDiagnosticsLifetime
{
    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal interface IAppTrayLifetime
{
    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask RemoveAsync();
}

internal interface IAppViewLifetime
{
    ValueTask ShowInitialSurfaceAsync(CancellationToken cancellationToken);

    ValueTask ShowDashboardAsync(CancellationToken cancellationToken);
}

internal interface IApplicationShutdown
{
    void Shutdown();
}

internal interface IAppAdapterFactory
{
    ValueTask<AppAdapterBundle> CreateAsync(
        CancellationToken cancellationToken);
}

internal sealed class AppAdapterBundle : IAsyncDisposable
{
    private readonly IAsyncDisposable _ownedAdapters;

    public AppAdapterBundle(
        TranslationRuntimeDependencies runtimeDependencies,
        IAppDiagnosticsLifetime diagnostics,
        IAppTrayLifetime tray,
        IAppViewLifetime views,
        IAsyncDisposable ownedAdapters)
    {
        RuntimeDependencies = runtimeDependencies
            ?? throw new ArgumentNullException(nameof(runtimeDependencies));
        Diagnostics =
            diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Tray = tray ?? throw new ArgumentNullException(nameof(tray));
        Views = views ?? throw new ArgumentNullException(nameof(views));
        _ownedAdapters = ownedAdapters
            ?? throw new ArgumentNullException(nameof(ownedAdapters));
    }

    public TranslationRuntimeDependencies RuntimeDependencies { get; }

    public IAppDiagnosticsLifetime Diagnostics { get; }

    public IAppTrayLifetime Tray { get; }

    public IAppViewLifetime Views { get; }

    public ValueTask DisposeAsync()
    {
        return _ownedAdapters.DisposeAsync();
    }
}

internal sealed class UiCommandGate
{
    private readonly object _sync = new();
    private readonly Action? _disabled;
    private bool _accepting = true;

    public UiCommandGate(Action? disabled = null)
    {
        _disabled = disabled;
    }

    public void Disable()
    {
        bool notify = false;
        lock (_sync)
        {
            if (_accepting)
            {
                _accepting = false;
                notify = true;
            }
        }

        if (notify)
        {
            _disabled?.Invoke();
        }
    }

    public async Task<bool> TryRunAsync(
        Func<CancellationToken, ValueTask> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_sync)
        {
            if (!_accepting)
            {
                return false;
            }
        }

        await command(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

internal sealed class AppCompositionRoot
{
    private static int s_processRuntimeClaimed;

    private readonly object _exitSync = new();
    private readonly UiCommandGate _commandGate;
    private readonly IAppDiagnosticsLifetime _diagnostics;
    private readonly IAppRuntimeLifetime _runtime;
    private readonly IDisposable _snapshots;
    private readonly IAsyncDisposable _adapters;
    private readonly IAppTrayLifetime _tray;
    private readonly IAsyncDisposable _coordinator;
    private readonly IApplicationShutdown _application;
    private readonly IAppViewLifetime? _views;
    private readonly TimeSpan _runtimeStopDeadline;
    private Task<AppExitReport>? _exitTask;

    internal AppCompositionRoot(
        UiCommandGate commandGate,
        IAppDiagnosticsLifetime diagnostics,
        IAppRuntimeLifetime runtime,
        IDisposable snapshots,
        IAsyncDisposable adapters,
        IAppTrayLifetime tray,
        IAsyncDisposable coordinator,
        IApplicationShutdown application,
        TimeSpan runtimeStopDeadline,
        IAppViewLifetime? views = null)
    {
        _commandGate =
            commandGate ?? throw new ArgumentNullException(nameof(commandGate));
        _diagnostics =
            diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _snapshots =
            snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _adapters =
            adapters ?? throw new ArgumentNullException(nameof(adapters));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _coordinator =
            coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _application =
            application ?? throw new ArgumentNullException(nameof(application));
        _views = views;
        if (runtimeStopDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeStopDeadline),
                runtimeStopDeadline,
                "The local runtime stop deadline must be positive.");
        }

        _runtimeStopDeadline = runtimeStopDeadline;
    }

    public static async ValueTask<AppCompositionRoot> CreateForProcessAsync(
        IAppAdapterFactory adapterFactory,
        IAsyncDisposable coordinator,
        TimeSpan runtimeStopDeadline,
        Action<Action> postToUi,
        Action shutdownApplication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapterFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(postToUi);
        ArgumentNullException.ThrowIfNull(shutdownApplication);
        if (Interlocked.CompareExchange(
                ref s_processRuntimeClaimed,
                1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "This process already owns an application runtime.");
        }

        AppAdapterBundle? adapters = null;
        TranslationRuntimeLifetime? runtime = null;
        SnapshotLifetime? snapshots = null;
        try
        {
            adapters = await adapterFactory.CreateAsync(cancellationToken)
                .ConfigureAwait(false);
            runtime = new TranslationRuntimeLifetime(
                new TranslationRuntime(adapters.RuntimeDependencies));
            AppSnapshotStore snapshotStore = new(
                new CallbackAppDispatcher(postToUi));
            snapshots = new SnapshotLifetime(
                runtime.Snapshots.Subscribe(snapshotStore),
                snapshotStore);
            await adapters.Tray.StartAsync(cancellationToken)
                .ConfigureAwait(false);
            return new AppCompositionRoot(
                new UiCommandGate(),
                adapters.Diagnostics,
                runtime,
                snapshots,
                adapters,
                adapters.Tray,
                coordinator,
                new CallbackApplicationShutdown(shutdownApplication),
                runtimeStopDeadline,
                adapters.Views);
        }
        catch
        {
            snapshots?.Dispose();
            runtime?.Dispose();
            if (adapters is not null)
            {
                await adapters.DisposeAsync().ConfigureAwait(false);
            }

            Interlocked.Exchange(ref s_processRuntimeClaimed, 0);
            throw;
        }
    }

    public Task<bool> TryRunUiCommandAsync(
        Func<CancellationToken, ValueTask> command,
        CancellationToken cancellationToken = default)
    {
        return _commandGate.TryRunAsync(command, cancellationToken);
    }

    public Task<bool> ShowInitialSurfaceAsync(
        CancellationToken cancellationToken = default)
    {
        IAppViewLifetime views = _views
            ?? throw new InvalidOperationException(
                "No application view lifetime was composed.");
        return TryRunUiCommandAsync(
            views.ShowInitialSurfaceAsync,
            cancellationToken);
    }

    public Task<bool> ShowDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        IAppViewLifetime views = _views
            ?? throw new InvalidOperationException(
                "No application view lifetime was composed.");
        return TryRunUiCommandAsync(
            views.ShowDashboardAsync,
            cancellationToken);
    }

    public Task<AppExitReport> ExitAsync()
    {
        lock (_exitSync)
        {
            return _exitTask ??= ExitCoreAsync();
        }
    }

    private async Task<AppExitReport> ExitCoreAsync()
    {
        List<AppExitError> errors = [];
        _commandGate.Disable();

        await RunAsync(
            AppExitErrorKind.DiagnosticsStop,
            cancellationToken => _diagnostics.StopAsync(cancellationToken),
            CancellationToken.None).ConfigureAwait(false);

        using (CancellationTokenSource stopCancellation = new(
                   _runtimeStopDeadline))
        {
            try
            {
                RuntimeStopResult result = await _runtime.StopAsync(
                        stopCancellation.Token)
                    .WaitAsync(_runtimeStopDeadline)
                    .ConfigureAwait(false);
                if (result == RuntimeStopResult.Failed)
                {
                    errors.Add(new AppExitError(
                        AppExitErrorKind.RuntimeStop,
                        Exception: null));
                }
            }
            catch (TimeoutException exception)
            {
                errors.Add(new AppExitError(
                    AppExitErrorKind.RuntimeStopTimeout,
                    exception));
            }
            catch (OperationCanceledException exception)
                when (stopCancellation.IsCancellationRequested)
            {
                errors.Add(new AppExitError(
                    AppExitErrorKind.RuntimeStopTimeout,
                    exception));
            }
            catch (Exception exception)
            {
                errors.Add(new AppExitError(
                    AppExitErrorKind.RuntimeStop,
                    exception));
            }
        }

        Run(
            AppExitErrorKind.SnapshotDispose,
            _snapshots.Dispose);
        Run(
            AppExitErrorKind.RuntimeDispose,
            _runtime.Dispose);
        await RunAsync(
            AppExitErrorKind.AdapterDispose,
            _ => _adapters.DisposeAsync(),
            CancellationToken.None).ConfigureAwait(false);
        await RunAsync(
            AppExitErrorKind.TrayRemove,
            _ => _tray.RemoveAsync(),
            CancellationToken.None).ConfigureAwait(false);
        await RunAsync(
            AppExitErrorKind.CoordinatorDispose,
            _ => _coordinator.DisposeAsync(),
            CancellationToken.None).ConfigureAwait(false);
        Run(
            AppExitErrorKind.ApplicationShutdown,
            _application.Shutdown);

        return new AppExitReport(errors.AsReadOnly());

        void Run(AppExitErrorKind kind, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                errors.Add(new AppExitError(kind, exception));
            }
        }

        async ValueTask RunAsync(
            AppExitErrorKind kind,
            Func<CancellationToken, ValueTask> action,
            CancellationToken cancellationToken)
        {
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(new AppExitError(kind, exception));
            }
        }
    }

    private sealed class TranslationRuntimeLifetime : IAppRuntimeLifetime
    {
        private readonly TranslationRuntime _runtime;

        public TranslationRuntimeLifetime(TranslationRuntime runtime)
        {
            _runtime =
                runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public IObservable<AppSnapshot> Snapshots => _runtime.Snapshots;

        public async Task<RuntimeStopResult> StopAsync(
            CancellationToken cancellationToken)
        {
            RuntimeError? error = await _runtime.StopAsync(cancellationToken)
                .ConfigureAwait(false);
            return error is null
                ? RuntimeStopResult.Stopped
                : RuntimeStopResult.Failed;
        }

        public void Dispose()
        {
            _runtime.Dispose();
        }
    }

    private sealed class SnapshotLifetime : IDisposable
    {
        private IDisposable? _subscription;
        private AppSnapshotStore? _store;

        public SnapshotLifetime(
            IDisposable subscription,
            AppSnapshotStore store)
        {
            _subscription = subscription
                ?? throw new ArgumentNullException(nameof(subscription));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _subscription, null)?.Dispose();
            Interlocked.Exchange(ref _store, null)?.Dispose();
        }
    }

    private sealed class CallbackAppDispatcher : IAppDispatcher
    {
        private readonly Action<Action> _post;

        public CallbackAppDispatcher(Action<Action> post)
        {
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        public void Post(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _post(callback);
        }
    }

    private sealed class CallbackApplicationShutdown : IApplicationShutdown
    {
        private readonly Action _shutdown;

        public CallbackApplicationShutdown(Action shutdown)
        {
            _shutdown =
                shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        }

        public void Shutdown()
        {
            _shutdown();
        }
    }
}
