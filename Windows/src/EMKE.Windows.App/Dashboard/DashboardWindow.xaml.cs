using System.ComponentModel;
using System.Windows;

namespace EMKE.Windows.App.Dashboard;

internal partial class DashboardWindow : Window
{
    private readonly DashboardViewModel _viewModel;
    private readonly DashboardWindowLifetime _lifetime;
    private bool _applicationExit;

    public DashboardWindow(DashboardViewModel viewModel)
    {
        _viewModel = viewModel
            ?? throw new ArgumentNullException(nameof(viewModel));
        _lifetime = new DashboardWindowLifetime(Hide);
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
    }

    public void CloseForApplicationExit()
    {
        _applicationExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_applicationExit)
        {
            e.Cancel = _lifetime.HandleClosing();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
