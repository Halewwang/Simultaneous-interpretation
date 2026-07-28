using System.Windows;
using EMKE.Windows.App.Bootstrap;

namespace EMKE.Windows.App;

#pragma warning disable CA1031 // WPF startup is the last boundary before explicit shutdown.
#pragma warning disable CA1515 // WPF markup activation requires the application type to remain public.
#pragma warning disable CA1724 // WPF convention names the Application subclass App.
#pragma warning disable CA2000 // Primary-instance coordinator ownership transfers to the composition root.

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan RuntimeStopDeadline =
        TimeSpan.FromSeconds(3);

    private readonly IAppAdapterFactory _adapterFactory;
    private AppCompositionRoot? _compositionRoot;
    private int _exitRequestedBeforeComposition;

    public App()
    {
        _adapterFactory = AppStartupFactory.CreateProduction(
            new WpfUiDispatcher(Dispatcher),
            RequestExitAsync);
    }

    internal App(IAppAdapterFactory adapterFactory)
    {
        _adapterFactory =
            adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SingleInstanceCoordinator coordinator = new(
            WindowsPackageChannel.Internal);
        int dashboardRequestedBeforeComposition = 0;
        try
        {
            SingleInstanceStartResult instanceResult =
                await coordinator.StartAsync(
                    HandleShowDashboardAsync).ConfigureAwait(true);
            if (instanceResult == SingleInstanceStartResult.Secondary)
            {
                await coordinator.DisposeAsync().ConfigureAwait(true);
                Shutdown();
                return;
            }

            _compositionRoot =
                await AppCompositionRoot.CreateForProcessAsync(
                    _adapterFactory,
                    coordinator,
                    RuntimeStopDeadline,
                    callback => _ = Dispatcher.BeginInvoke(callback),
                    Shutdown,
                    CancellationToken.None).ConfigureAwait(true);
            if (Interlocked.Exchange(
                    ref _exitRequestedBeforeComposition,
                    0) != 0)
            {
                await _compositionRoot.ExitAsync().ConfigureAwait(true);
                return;
            }

            await _compositionRoot.ShowInitialSurfaceAsync()
                .ConfigureAwait(true);
            if (Interlocked.Exchange(
                    ref dashboardRequestedBeforeComposition,
                    0) != 0)
            {
                await _compositionRoot.ShowDashboardAsync()
                    .ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            if (_compositionRoot is null)
            {
                await coordinator.DisposeAsync().ConfigureAwait(true);
                Shutdown(exitCode: 1);
            }
            else
            {
                await _compositionRoot.ExitAsync().ConfigureAwait(true);
            }
        }

        async ValueTask HandleShowDashboardAsync(
            CancellationToken cancellationToken)
        {
            AppCompositionRoot? root = Volatile.Read(ref _compositionRoot);
            if (root is null)
            {
                Interlocked.Exchange(
                    ref dashboardRequestedBeforeComposition,
                    1);
                return;
            }

            await root.ShowDashboardAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal Task<AppExitReport> ExitAsync()
    {
        AppCompositionRoot root = _compositionRoot
            ?? throw new InvalidOperationException(
                "The application composition root has not started.");
        return root.ExitAsync();
    }

    private async Task RequestExitAsync()
    {
        AppCompositionRoot? root = Volatile.Read(ref _compositionRoot);
        if (root is null)
        {
            Interlocked.Exchange(
                ref _exitRequestedBeforeComposition,
                1);
            return;
        }

        _ = await root.ExitAsync().ConfigureAwait(true);
    }
}
