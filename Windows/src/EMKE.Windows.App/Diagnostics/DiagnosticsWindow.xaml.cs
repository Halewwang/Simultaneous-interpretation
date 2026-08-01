using System.ComponentModel;
using System.Windows;

namespace EMKE.Windows.App.Diagnostics;

internal partial class DiagnosticsWindow : Window, IDisposable
{
    private readonly DiagnosticsViewModel _viewModel;
    private readonly SemaphoreSlim _navigation = new(1, 1);
    private bool _applicationExit;
    private int _disposed;

    public DiagnosticsWindow(DiagnosticsViewModel viewModel)
    {
        _viewModel = viewModel
            ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    public void ShowOrActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        _ = ObserveAsync(_viewModel.RefreshAsync(CancellationToken.None));
    }

    public async ValueTask StopAndHideAsync(
        CancellationToken cancellationToken)
    {
        await _navigation.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await _viewModel.StopAsync(cancellationToken).ConfigureAwait(true);
            Hide();
        }
        finally
        {
            _navigation.Release();
        }
    }

    public void CloseForApplicationExit()
    {
        _applicationExit = true;
        Close();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _navigation.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_applicationExit)
        {
            e.Cancel = true;
            await ObserveAsync(
                StopAndHideAsync(CancellationToken.None).AsTask())
                .ConfigureAwait(true);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ObserveAsync(
            _viewModel.RefreshAsync(CancellationToken.None))
            .ConfigureAwait(true);
    }

    private async void OnTestConnectionClicked(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ObserveAsync(
            _viewModel.TestConnectionAsync(CancellationToken.None))
            .ConfigureAwait(true);
    }

    private async void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ObserveAsync(
            StopAndHideAsync(CancellationToken.None).AsTask())
            .ConfigureAwait(true);
    }

#pragma warning disable CA1031 // WPF event boundaries keep stable UI state without exposing provider details.
    private static async Task ObserveAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }
#pragma warning restore CA1031
}
