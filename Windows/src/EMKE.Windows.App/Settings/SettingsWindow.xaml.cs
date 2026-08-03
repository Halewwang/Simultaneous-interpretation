using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;

namespace EMKE.Windows.App.Settings;

internal delegate void SettingsPasswordConsumer(ReadOnlySpan<char> secret);

internal static class SettingsPasswordTransfer
{
    public static unsafe void Transfer(
        Func<SecureString?> readSecret,
        SettingsPasswordConsumer consume)
    {
        ArgumentNullException.ThrowIfNull(readSecret);
        ArgumentNullException.ThrowIfNull(consume);

        using SecureString secret = readSecret()
            ?? throw new InvalidOperationException(
                "The password control did not return a secure value.");
        int length = secret.Length;
        IntPtr characters = IntPtr.Zero;
        try
        {
            characters =
                Marshal.SecureStringToGlobalAllocUnicode(secret);
            consume(
                new ReadOnlySpan<char>(
                    (void*)characters,
                    length));
        }
        finally
        {
            if (characters != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(characters);
            }
        }
    }
}

internal sealed class SettingsWindowLifetime
{
    private readonly Action _clearDraft;
    private readonly Action _hide;

    public SettingsWindowLifetime(Action clearDraft, Action hide)
    {
        _clearDraft =
            clearDraft ?? throw new ArgumentNullException(nameof(clearDraft));
        _hide = hide ?? throw new ArgumentNullException(nameof(hide));
    }

    public bool HandleClosing()
    {
        _clearDraft();
        _hide();
        return true;
    }
}

internal partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly SettingsWindowLifetime _lifetime;
    private bool _applicationExit;
    private bool _clearingPasswordBox;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel
            ?? throw new ArgumentNullException(nameof(viewModel));
        _lifetime = new SettingsWindowLifetime(
            _viewModel.Close,
            Hide);
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.ApiKeyClearRequested += OnApiKeyClearRequested;
        _viewModel.CloseRequested += OnCloseRequested;
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
        _viewModel.ApiKeyClearRequested -= OnApiKeyClearRequested;
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnApiKeyPasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        _ = e;
        if (_clearingPasswordBox)
        {
            return;
        }

        PasswordBox passwordBox = (PasswordBox)sender;
        SettingsPasswordTransfer.Transfer(
            () => passwordBox.SecurePassword,
            _viewModel.ReplaceApiKeyDraft);
    }

    private void OnApiKeyClearRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(ClearPasswordBox);
            return;
        }

        ClearPasswordBox();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Hide();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.Close();
    }

    private void ClearPasswordBox()
    {
        _clearingPasswordBox = true;
        try
        {
            ApiKeyPasswordBox.Clear();
        }
        finally
        {
            _clearingPasswordBox = false;
        }
    }
}
