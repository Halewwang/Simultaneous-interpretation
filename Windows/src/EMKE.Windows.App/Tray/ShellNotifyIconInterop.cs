using System.ComponentModel;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace EMKE.Windows.App.Tray;

internal sealed class ShellNotifyIconInterop : ITrayIconTransport
{
    private const uint NotifyIconAdd = 0x00000000;
    private const uint NotifyIconModify = 0x00000001;
    private const uint NotifyIconDelete = 0x00000002;
    private const uint NotifyIconSetVersion = 0x00000004;
    private const uint NotifyIconFlagMessage = 0x00000001;
    private const uint NotifyIconFlagIcon = 0x00000002;
    private const uint NotifyIconFlagTip = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint WindowMessageApp = 0x8000;
    private const uint TrayCallbackMessage = WindowMessageApp + 1;
    private const uint WindowMessageLeftButtonUp = 0x0202;
    private const uint WindowMessageRightButtonUp = 0x0205;
    private const uint WindowMessageContextMenu = 0x007B;
    private const uint NotifyIconSelect = 0x0400;
    private const uint MenuString = 0;
    private const uint MenuSeparator = 0x00000800;
    private const uint TrackPopupRightButton = 0x0002;
    private const uint TrackPopupReturnCommand = 0x0100;
    private const int DefaultApplicationIcon = 32512;
    private const int OpenDashboardCommand = 1001;
    private const int OpenSettingsCommand = 1002;
    private const int OpenOnboardingCommand = 1003;
    private const int CheckForUpdatesCommand = 1004;
    private const int ExitCommand = 1005;

    private readonly object _sync = new();
    private readonly WindowProcedure _windowProcedure;
    private Func<TrayInteraction, ValueTask>? _interaction;
    private TrayMenuLabels? _labels;
    private string? _windowClass;
    private nint _module;
    private nint _window;
    private nint _icon;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private int _disposed;

    public ShellNotifyIconInterop()
    {
        _windowProcedure = WindowProc;
    }

    public ValueTask StartAsync(
        Func<TrayInteraction, ValueTask> interaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        lock (_sync)
        {
            if (_window != 0)
            {
                throw new InvalidOperationException(
                    "The tray message window has already started.");
            }

            _interaction = interaction;
            _module = GetModuleHandle(null);
            _windowClass =
                $"EMKE.Translation.Tray.{Environment.ProcessId}.{Guid.NewGuid():N}";
            WindowClass registration = new()
            {
                Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                WindowProcedure = _windowProcedure,
                Instance = _module,
                ClassName = _windowClass,
            };
            ushort atom = RegisterClass(ref registration);
            if (atom == 0)
            {
                throw NewWin32Exception("register the tray window class");
            }

            _window = CreateWindow(
                extendedStyle: 0,
                _windowClass,
                string.Empty,
                style: 0,
                x: 0,
                y: 0,
                width: 0,
                height: 0,
                parent: 0,
                menu: 0,
                _module,
                parameter: 0);
            if (_window == 0)
            {
                _ = UnregisterClass(_windowClass, _module);
                _windowClass = null;
                throw NewWin32Exception("create the tray message window");
            }

            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreatedMessage == 0)
            {
                DestroyWindowAndClass();
                throw NewWin32Exception(
                    "register the TaskbarCreated message");
            }

            _icon = LoadIcon(0, new nint(DefaultApplicationIcon));
            if (_icon == 0)
            {
                DestroyWindowAndClass();
                throw NewWin32Exception("load the tray icon");
            }
        }

        return ValueTask.CompletedTask;
    }

    public void AddIcon(TrayMenuLabels labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        lock (_sync)
        {
            EnsureStarted();
            _labels = labels;
            NotifyIconData data = CreateNotifyData(labels);
            if (!ShellNotifyIcon(NotifyIconAdd, ref data))
            {
                throw NewWin32Exception("add the tray icon");
            }

            data.VersionOrTimeout = NotifyIconVersion4;
            if (!ShellNotifyIcon(NotifyIconSetVersion, ref data))
            {
                _ = ShellNotifyIcon(NotifyIconDelete, ref data);
                throw NewWin32Exception("set the tray icon version");
            }

            _iconAdded = true;
        }
    }

    public void UpdateIcon(TrayMenuLabels labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        lock (_sync)
        {
            EnsureStarted();
            _labels = labels;
            if (!_iconAdded)
            {
                return;
            }

            NotifyIconData data = CreateNotifyData(labels);
            if (!ShellNotifyIcon(NotifyIconModify, ref data))
            {
                throw NewWin32Exception("update the tray icon");
            }
        }
    }

    public void DeleteIcon()
    {
        lock (_sync)
        {
            if (_window == 0 || !_iconAdded)
            {
                return;
            }

            NotifyIconData data = CreateNotifyData(
                _labels ?? EmptyLabels());
            _ = ShellNotifyIcon(NotifyIconDelete, ref data);
            _iconAdded = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_sync)
        {
            DeleteIcon();
            DestroyWindowAndClass();
            _interaction = null;
            _labels = null;
        }

        return ValueTask.CompletedTask;
    }

    private nint WindowProc(
        nint window,
        uint message,
        nint wordParameter,
        nint longParameter)
    {
        if (message == _taskbarCreatedMessage
            && _taskbarCreatedMessage != 0)
        {
            _iconAdded = false;
            Dispatch(TrayInteraction.TaskbarCreated);
            return 0;
        }

        if (message == TrayCallbackMessage)
        {
            uint notification = unchecked((uint)longParameter.ToInt64())
                & 0xFFFF;
            switch (notification)
            {
                case WindowMessageLeftButtonUp:
                case NotifyIconSelect:
                    Dispatch(TrayInteraction.PrimaryActivate);
                    return 0;
                case WindowMessageRightButtonUp:
                case WindowMessageContextMenu:
                    ShowContextMenu(window);
                    return 0;
            }
        }

        return DefWindowProc(
            window,
            message,
            wordParameter,
            longParameter);
    }

    private void ShowContextMenu(nint window)
    {
        TrayMenuLabels labels = _labels ?? EmptyLabels();
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = AppendMenu(
                menu,
                MenuString,
                OpenDashboardCommand,
                labels.OpenDashboard);
            _ = AppendMenu(
                menu,
                MenuString,
                OpenSettingsCommand,
                labels.OpenSettings);
            _ = AppendMenu(
                menu,
                MenuString,
                OpenOnboardingCommand,
                labels.OpenOnboarding);
            _ = AppendMenu(
                menu,
                MenuString,
                CheckForUpdatesCommand,
                labels.CheckForUpdates);
            _ = AppendMenu(menu, MenuSeparator, 0, null);
            _ = AppendMenu(
                menu,
                MenuString,
                ExitCommand,
                labels.Exit);
            _ = SetForegroundWindow(window);
            if (!GetCursorPosition(out Point cursor))
            {
                return;
            }

            int selected = TrackPopupMenu(
                menu,
                TrackPopupRightButton | TrackPopupReturnCommand,
                cursor.X,
                cursor.Y,
                reserved: 0,
                window,
                rectangle: 0);
            TrayInteraction? interaction = selected switch
            {
                OpenDashboardCommand => TrayInteraction.OpenDashboard,
                OpenSettingsCommand => TrayInteraction.OpenSettings,
                OpenOnboardingCommand => TrayInteraction.OpenOnboarding,
                CheckForUpdatesCommand => TrayInteraction.CheckForUpdates,
                ExitCommand => TrayInteraction.Exit,
                _ => null,
            };
            if (interaction is not null)
            {
                Dispatch(interaction.Value);
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void Dispatch(TrayInteraction interaction)
    {
        Func<TrayInteraction, ValueTask>? callback = _interaction;
        if (callback is not null)
        {
            _ = ObserveAsync(callback(interaction));
        }
    }

#pragma warning disable CA1031 // Native window callbacks cannot propagate async failures.
    private static async Task ObserveAsync(ValueTask action)
    {
        try
        {
            await action.ConfigureAwait(true);
        }
        catch (Exception)
        {
        }
    }
#pragma warning restore CA1031

    private NotifyIconData CreateNotifyData(TrayMenuLabels labels)
    {
        return new NotifyIconData
        {
            Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
            Window = _window,
            Identifier = 1,
            Flags = NotifyIconFlagMessage
                | NotifyIconFlagIcon
                | NotifyIconFlagTip,
            CallbackMessage = TrayCallbackMessage,
            Icon = _icon,
            Tip = labels.ToolTip,
        };
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_window == 0)
        {
            throw new InvalidOperationException(
                "The tray message window has not started.");
        }
    }

    private void DestroyWindowAndClass()
    {
        if (_window != 0)
        {
            _ = DestroyWindow(_window);
            _window = 0;
        }

        if (_windowClass is not null)
        {
            _ = UnregisterClass(_windowClass, _module);
            _windowClass = null;
        }
    }

    private static TrayMenuLabels EmptyLabels()
    {
        return new TrayMenuLabels(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    private static Win32Exception NewWin32Exception(string operation)
    {
        return new Win32Exception(
            Marshal.GetLastPInvokeError(),
            $"Could not {operation}.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Identifier;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Information;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InformationTitle;

        public uint InformationFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WindowProcedure WindowProcedure;

        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nint wordParameter,
        nint longParameter);

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(
        uint message,
        ref NotifyIconData data);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport(
        "user32.dll",
        EntryPoint = "RegisterClassExW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);

    [DllImport(
        "user32.dll",
        EntryPoint = "UnregisterClassW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(
        string className,
        nint instance);

    [DllImport(
        "user32.dll",
        EntryPoint = "CreateWindowExW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern nint CreateWindow(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport(
        "user32.dll",
        EntryPoint = "DestroyWindow",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport(
        "user32.dll",
        EntryPoint = "DefWindowProcW",
        ExactSpelling = true)]
    private static extern nint DefWindowProc(
        nint window,
        uint message,
        nint wordParameter,
        nint longParameter);

    [DllImport(
        "user32.dll",
        EntryPoint = "RegisterWindowMessageW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport(
        "user32.dll",
        EntryPoint = "LoadIconW",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport(
        "user32.dll",
        EntryPoint = "CreatePopupMenu",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport(
        "user32.dll",
        EntryPoint = "AppendMenuW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        nint menu,
        uint flags,
        nuint identifier,
        string? text);

    [DllImport(
        "user32.dll",
        EntryPoint = "TrackPopupMenuEx",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern int TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint window,
        nint rectangle);

    [DllImport(
        "user32.dll",
        EntryPoint = "DestroyMenu",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetCursorPos",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPosition(out Point point);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetForegroundWindow",
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
