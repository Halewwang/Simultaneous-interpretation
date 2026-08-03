using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EMKE.Windows.App.Floating;

internal partial class FloatingStatusWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const long NoActivateExtendedStyle = 0x08000000;

    private readonly FloatingStatusViewModel _viewModel;
    private bool _applicationExit;

    public FloatingStatusWindow(FloatingStatusViewModel viewModel)
    {
        _viewModel = viewModel
            ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint window = new WindowInteropHelper(this).Handle;
        nint extendedStyle = GetWindowLongPtr(window, ExtendedStyleIndex);
        _ = SetWindowLongPtr(
            window,
            ExtendedStyleIndex,
            extendedStyle | new nint(NoActivateExtendedStyle));
        ApplyVisibility();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(
                FloatingStatusViewModel.ShouldBeVisible))
        {
            if (Dispatcher.CheckAccess())
            {
                ApplyVisibility();
            }
            else
            {
                _ = Dispatcher.BeginInvoke(ApplyVisibility);
            }
        }
    }

    private void ApplyVisibility()
    {
        if (_viewModel.ShouldBeVisible)
        {
            if (!IsVisible)
            {
                Show();
            }
        }
        else
        {
            Hide();
        }
    }

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongPtrW",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern nint GetWindowLongPtr(
        nint window,
        int index);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetWindowLongPtrW",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern nint SetWindowLongPtr(
        nint window,
        int index,
        nint newValue);
}
