using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Compatibility;
using EMKE.Platform.Driver;
using EMKE.Platform.Diagnostics;
using EMKE.Platform.Native;
using EMKE.Platform.Security;
using EMKE.Platform.Settings;
using EMKE.Windows.App.Commands;
using EMKE.Windows.App.Dashboard;
using EMKE.Windows.App.Diagnostics;
using EMKE.Windows.App.Floating;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Presentation;
using EMKE.Windows.App.Settings;
using EMKE.Windows.App.Tray;

namespace EMKE.Windows.App.Bootstrap;

internal static class AppStartupFactory
{
    public static IAppAdapterFactory CreateProduction(
        IUiDispatcher dispatcher,
        Func<Task> exitAsync)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(exitAsync);
        return new ProductionAppAdapterFactory(
            ProductionCoreAdapters.CreateAsync,
            (context, cancellationToken) =>
                ProductionUiAdapters.CreateAsync(
                    dispatcher,
                    exitAsync,
                    context,
                    cancellationToken));
    }

    internal static IAppAdapterFactory Create(
        Func<CancellationToken, ValueTask<AppCoreAdapterBundle>> createCore,
        Func<
            AppUiCompositionContext,
            CancellationToken,
            ValueTask<AppUiAdapterBundle>> createUi)
    {
        return new ProductionAppAdapterFactory(createCore, createUi);
    }
}

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher =
            dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public ValueTask InvokeAsync(
        Action action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.CheckAccess())
        {
            action();
            return ValueTask.CompletedTask;
        }

        return new ValueTask(
            _dispatcher.InvokeAsync(
                action,
                DispatcherPriority.Normal,
                cancellationToken).Task);
    }
}

#pragma warning disable CA2000 // Adapter bundles take ownership of created native lifetimes.

internal static class ProductionCoreAdapters
{
    public static ValueTask<AppCoreAdapterBundle> CreateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeAudioEngine audio = new();
        CompatibilityManifest compatibilityManifest =
            CompatibilityManifest.LoadEmbedded(
                typeof(ProductionCoreAdapters).Assembly,
                "EMKE.Windows.App.compatibility.internal.json");
        WindowsDriverManager driverManager = new(
            new WindowsDriverSnapshotSource(),
            compatibilityManifest);
        WindowsSettingsStore settingsStore = new(
            new FileSystemWindowsSettingsPersistence());
        CredentialManagerSecretStore secretStore = new(
            WindowsCredentialChannel.Internal);
        TranslationRuntimeDependencies dependencies = new(
            new WindowsHostBuildGate(
                compatibilityManifest,
                new WindowsHostCompatibilityProbe()),
            settingsStore,
            driverManager,
            new PendingAudioDeviceCatalog(),
            audio,
            new PendingTranslationSessionFactory(),
            new PendingLanguageClassifier(),
            new SystemRuntimeClock(),
            new TraceRuntimeLog());
        return ValueTask.FromResult(
            new AppCoreAdapterBundle(
                dependencies,
                PendingDiagnosticsLifetime.Instance,
                new NativeAudioLifetime(audio),
                settingsStore,
                secretStore));
    }

    private sealed class PendingAudioDeviceCatalog : IAudioDeviceCatalog
    {
        public Task<AudioDeviceSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AudioDeviceSnapshot([]));
        }
    }

    private sealed class PendingTranslationSessionFactory :
        ITranslationSessionFactory
    {
        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<ITranslationSession>(
                new InvalidOperationException(
                    "Translation session composition is not available in this Internal build."));
        }
    }

    private sealed class PendingLanguageClassifier : ILanguageClassifier
    {
        public ValueTask<LanguageProbabilities> ClassifyAsync(
            string text,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(text);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new LanguageProbabilities(
                    1d / 3,
                    1d / 3,
                    1d / 3));
        }
    }

    private sealed class SystemRuntimeClock : IClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public TimeSpan MonotonicNow => _stopwatch.Elapsed;

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TraceRuntimeLog : IRuntimeLog
    {
        public void Write(
            RuntimeLogLevel level,
            string eventName,
            IReadOnlyDictionary<string, string> safeFields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
            ArgumentNullException.ThrowIfNull(safeFields);
            Trace.WriteLine(
                FormattableString.Invariant(
                    $"EMKE runtime {level}: {eventName}"));
        }
    }

    private sealed class NativeAudioLifetime : IAsyncDisposable
    {
        private NativeAudioEngine? _audio;

        public NativeAudioLifetime(NativeAudioEngine audio)
        {
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        }

        public async ValueTask DisposeAsync()
        {
            NativeAudioEngine? audio =
                Interlocked.Exchange(ref _audio, null);
            if (audio is not null)
            {
                await audio.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class PendingDiagnosticsLifetime :
        IAppDiagnosticsLifetime
    {
        public static PendingDiagnosticsLifetime Instance { get; } = new();

        private PendingDiagnosticsLifetime()
        {
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA2000

internal static class ProductionUiAdapters
{
    public static async ValueTask<AppUiAdapterBundle> CreateAsync(
        IUiDispatcher dispatcher,
        Func<Task> exitAsync,
        AppUiCompositionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(exitAsync);
        ArgumentNullException.ThrowIfNull(context);
        IWindowsProductSettingsStore productSettings =
            context.ProductSettings
            ?? throw new InvalidOperationException(
                "Production product settings were not composed.");
        ISecretStore secretStore = context.SecretStore
            ?? throw new InvalidOperationException(
                "Production secret storage was not composed.");
        ITranslationSessionFactory sessionFactory = context.SessionFactory
            ?? throw new InvalidOperationException(
                "Production Translation session factory was not composed.");
        WindowsProductSettings initialSettings =
            await productSettings.LoadProductSettingsAsync(cancellationToken)
                .ConfigureAwait(false);

        AppUiAdapterBundle? result = null;
        await dispatcher.InvokeAsync(
            () =>
            {
                WpfProductViews? views = null;
                DelegatingSurfaceActions surfaceActions = new(
                    token =>
                        views?.ShowSettingsAsync(token)
                        ?? ValueTask.FromException(
                            new InvalidOperationException(
                                "Product views have not finished composing.")),
                    token =>
                        views?.ShowDiagnosticsAsync(token)
                        ?? ValueTask.FromException(
                            new InvalidOperationException(
                                "Product views have not finished composing.")));
                DelegatingSettingsSystemActions settingsSystemActions = new(
                    token =>
                        views?.ShowDiagnosticsAsync(token)
                        ?? ValueTask.FromException(
                            new InvalidOperationException(
                                "Product views have not finished composing.")),
                    (surface, token) =>
                        views?.ShowPlaceholderAsync(surface, token)
                        ?? ValueTask.FromException(
                            new InvalidOperationException(
                                "Product views have not finished composing.")));
                DashboardViewModel dashboardViewModel = new(
                    context.Presentation,
                    context.Localization,
                    context.RuntimeCommands,
                    surfaceActions);
                FloatingStatusVisibilityController floatingVisibility = new(
                    initialSettings.FloatingStatusEnabled);
                FloatingStatusViewModel floatingViewModel = new(
                    context.Presentation,
                    context.RuntimeCommands,
                    floatingVisibility);
                TranslationConnectionProbe connectionProbe = new(
                    sessionFactory);
                WindowsAudioDiagnostics audioDiagnostics = new(
                    new WindowsNativeAudioDiagnosticBackend(),
                    () => IsTranslationActive(context.Snapshots.Current));
                DiagnosticsViewModel diagnosticsViewModel = new(
                    audioDiagnostics,
                    connectionProbe,
                    SettingsTranslationCapabilityTester.Inbound(
                        initialSettings),
                    SettingsTranslationCapabilityTester.Outbound(
                        initialSettings),
                    context.Localization);
                SettingsViewModel settingsViewModel = new(
                    initialSettings,
                    productSettings,
                    secretStore,
                    context.RuntimeCommands,
                    new SettingsTranslationCapabilityTester(
                        productSettings,
                        connectionProbe),
                    settingsSystemActions,
                    floatingVisibility,
                    context.Localization);
                views = new WpfProductViews(
                    dispatcher,
                    context.Localization,
                    new DashboardWindow(dashboardViewModel),
                    new FloatingStatusWindow(floatingViewModel),
                    new SettingsWindow(settingsViewModel),
                    new DiagnosticsWindow(diagnosticsViewModel),
                    diagnosticsViewModel,
                    audioDiagnostics);
                ProductionTrayActions trayActions = new(
                    views,
                    exitAsync);
                TrayHost tray = new(
                    new ShellNotifyIconInterop(dispatcher),
                    trayActions,
                    context.Localization);
                result = new AppUiAdapterBundle(
                    tray,
                    views,
                    views,
                    views);
            },
            cancellationToken).ConfigureAwait(false);
        return result
            ?? throw new InvalidOperationException(
                "The WPF product UI was not composed.");
    }

    private enum PlaceholderSurface
    {
        Onboarding,
        Updates,
    }

    private static bool IsTranslationActive(AppSnapshot? snapshot)
    {
        return snapshot is not null
            && snapshot.RuntimeState != RuntimeState.Stopped;
    }

    private sealed class DelegatingSurfaceActions : IAppSurfaceActions
    {
        private readonly Func<
            CancellationToken,
            ValueTask> _openSettings;
        private readonly Func<
            CancellationToken,
            ValueTask> _openDiagnostics;

        public DelegatingSurfaceActions(
            Func<CancellationToken, ValueTask> openSettings,
            Func<CancellationToken, ValueTask> openDiagnostics)
        {
            _openSettings = openSettings
                ?? throw new ArgumentNullException(nameof(openSettings));
            _openDiagnostics = openDiagnostics
                ?? throw new ArgumentNullException(nameof(openDiagnostics));
        }

        public ValueTask OpenSettingsAsync(
            CancellationToken cancellationToken)
        {
            return _openSettings(cancellationToken);
        }

        public ValueTask OpenDiagnosticsAsync(
            CancellationToken cancellationToken)
        {
            return _openDiagnostics(cancellationToken);
        }
    }

    private sealed class DelegatingSettingsSystemActions
        : ISettingsSystemActions
    {
        private readonly Func<
            CancellationToken,
            ValueTask> _showDiagnostics;
        private readonly Func<
            PlaceholderSurface,
            CancellationToken,
            ValueTask> _show;

        public DelegatingSettingsSystemActions(
            Func<CancellationToken, ValueTask> showDiagnostics,
            Func<
                PlaceholderSurface,
                CancellationToken,
                ValueTask> show)
        {
            _showDiagnostics = showDiagnostics
                ?? throw new ArgumentNullException(nameof(showDiagnostics));
            _show = show ?? throw new ArgumentNullException(nameof(show));
        }

        public ValueTask RunLocalDiagnosticsAsync(
            CancellationToken cancellationToken)
        {
            return _showDiagnostics(cancellationToken);
        }

        public ValueTask CheckForUpdatesAsync(
            CancellationToken cancellationToken)
        {
            return _show(PlaceholderSurface.Updates, cancellationToken);
        }

        public ValueTask ReopenOnboardingAsync(
            CancellationToken cancellationToken)
        {
            return _show(PlaceholderSurface.Onboarding, cancellationToken);
        }

        public ValueTask ExportDiagnosticsAsync(
            CancellationToken cancellationToken)
        {
            return _showDiagnostics(cancellationToken);
        }
    }

    private sealed class ProductionTrayActions : ITrayActions
    {
        private readonly WpfProductViews _views;
        private readonly Func<Task> _exitAsync;

        public ProductionTrayActions(
            WpfProductViews views,
            Func<Task> exitAsync)
        {
            _views = views ?? throw new ArgumentNullException(nameof(views));
            _exitAsync =
                exitAsync ?? throw new ArgumentNullException(nameof(exitAsync));
        }

        public ValueTask ShowDashboardAsync(
            CancellationToken cancellationToken)
        {
            return _views.ShowDashboardAsync(cancellationToken);
        }

        public ValueTask ShowSettingsAsync(
            CancellationToken cancellationToken)
        {
            return _views.ShowSettingsAsync(cancellationToken);
        }

        public ValueTask ShowOnboardingAsync(
            CancellationToken cancellationToken)
        {
            return _views.ShowPlaceholderAsync(
                PlaceholderSurface.Onboarding,
                cancellationToken);
        }

        public ValueTask CheckForUpdatesAsync(
            CancellationToken cancellationToken)
        {
            return _views.ShowPlaceholderAsync(
                PlaceholderSurface.Updates,
                cancellationToken);
        }

        public ValueTask ExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask(_exitAsync());
        }
    }

    private sealed class WpfProductViews :
        IAppViewLifetime,
        IAppDiagnosticsLifetime,
        IAsyncDisposable
    {
        private readonly IUiDispatcher _dispatcher;
        private readonly LocalizationService _localization;
        private readonly DashboardWindow _dashboard;
        private readonly FloatingStatusWindow _floating;
        private readonly SettingsWindow _settings;
        private readonly DiagnosticsWindow _diagnostics;
        private readonly DiagnosticsViewModel _diagnosticsViewModel;
        private readonly IAsyncDisposable _audioDiagnostics;
        private readonly PlaceholderSurfaceWindow _placeholder = new();
        private int _disposed;

        public WpfProductViews(
            IUiDispatcher dispatcher,
            LocalizationService localization,
            DashboardWindow dashboard,
            FloatingStatusWindow floating,
            SettingsWindow settings,
            DiagnosticsWindow diagnostics,
            DiagnosticsViewModel diagnosticsViewModel,
            IAsyncDisposable audioDiagnostics)
        {
            _dispatcher = dispatcher
                ?? throw new ArgumentNullException(nameof(dispatcher));
            _localization = localization
                ?? throw new ArgumentNullException(nameof(localization));
            _dashboard = dashboard
                ?? throw new ArgumentNullException(nameof(dashboard));
            _floating = floating
                ?? throw new ArgumentNullException(nameof(floating));
            _settings = settings
                ?? throw new ArgumentNullException(nameof(settings));
            _diagnostics = diagnostics
                ?? throw new ArgumentNullException(nameof(diagnostics));
            _diagnosticsViewModel = diagnosticsViewModel
                ?? throw new ArgumentNullException(nameof(diagnosticsViewModel));
            _audioDiagnostics = audioDiagnostics
                ?? throw new ArgumentNullException(nameof(audioDiagnostics));
        }

        public ValueTask ShowInitialSurfaceAsync(
            CancellationToken cancellationToken)
        {
            return ShowDashboardAsync(cancellationToken);
        }

        public ValueTask ShowDashboardAsync(
            CancellationToken cancellationToken)
        {
            return NavigateFromDiagnosticsAsync(
                _dashboard.ShowOrActivate,
                cancellationToken);
        }

        public ValueTask ShowSettingsAsync(
            CancellationToken cancellationToken)
        {
            return NavigateFromDiagnosticsAsync(
                _settings.ShowOrActivate,
                cancellationToken);
        }

        public ValueTask ShowDiagnosticsAsync(
            CancellationToken cancellationToken)
        {
            return _dispatcher.InvokeAsync(
                _diagnostics.ShowOrActivate,
                cancellationToken);
        }

        public ValueTask ShowPlaceholderAsync(
            PlaceholderSurface surface,
            CancellationToken cancellationToken)
        {
            return NavigateFromDiagnosticsAsync(
                () =>
                {
                    (LocalizedString titleKey, LocalizedString bodyKey) =
                        surface switch
                        {
                            PlaceholderSurface.Onboarding =>
                                (LocalizedString.PlaceholderOnboardingTitle,
                                    LocalizedString.PlaceholderOnboardingBody),
                            PlaceholderSurface.Updates =>
                                (LocalizedString.PlaceholderUpdateTitle,
                                    LocalizedString.PlaceholderUpdateBody),
                            _ => throw new ArgumentOutOfRangeException(
                                nameof(surface),
                                surface,
                                "Undefined placeholder surface."),
                        };
                    AppInterfaceLanguage language =
                        _localization.CurrentLanguage;
                    _placeholder.ShowMessage(
                        _localization.Get(titleKey, language),
                        _localization.Get(bodyKey, language),
                        _localization.Get(
                            LocalizedString.PlaceholderClose,
                            language));
                },
                cancellationToken);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            return _diagnosticsViewModel.StopAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _diagnosticsViewModel.StopAsync(CancellationToken.None)
                .ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    _placeholder.CloseForApplicationExit();
                    _diagnostics.CloseForApplicationExit();
                    _settings.CloseForApplicationExit();
                    _floating.CloseForApplicationExit();
                    _dashboard.CloseForApplicationExit();
                }).ConfigureAwait(false);
            await _diagnosticsViewModel.DisposeAsync().ConfigureAwait(false);
            await _audioDiagnostics.DisposeAsync().ConfigureAwait(false);
        }

        private async ValueTask NavigateFromDiagnosticsAsync(
            Action showTarget,
            CancellationToken cancellationToken)
        {
            await _diagnosticsViewModel.StopAsync(cancellationToken)
                .ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    _diagnostics.Hide();
                    showTarget();
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class PlaceholderSurfaceWindow : Window
    {
        private readonly TextBlock _body;
        private readonly Button _close;
        private bool _applicationExit;

        public PlaceholderSurfaceWindow()
        {
            Width = 440;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            _body = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            };
            _close = new Button
            {
                MinWidth = 88,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            _close.Click += (_, _) => Hide();
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    _body,
                    _close,
                },
            };
        }

        public void ShowMessage(
            string title,
            string body,
            string closeLabel)
        {
            Title = title;
            _body.Text = body;
            _close.Content = closeLabel;
            if (!IsVisible)
            {
                Show();
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
                e.Cancel = true;
                Hide();
            }

            base.OnClosing(e);
        }
    }
}

internal sealed class ProductionAppAdapterFactory : IAppAdapterFactory
{
    private readonly Func<
        CancellationToken,
        ValueTask<AppCoreAdapterBundle>> _createCore;
    private readonly Func<
        AppUiCompositionContext,
        CancellationToken,
        ValueTask<AppUiAdapterBundle>> _createUi;

    internal ProductionAppAdapterFactory(
        Func<CancellationToken, ValueTask<AppCoreAdapterBundle>> createCore,
        Func<
            AppUiCompositionContext,
            CancellationToken,
            ValueTask<AppUiAdapterBundle>> createUi)
    {
        _createCore =
            createCore ?? throw new ArgumentNullException(nameof(createCore));
        _createUi =
            createUi ?? throw new ArgumentNullException(nameof(createUi));
    }

    public ValueTask<AppCoreAdapterBundle> CreateCoreAsync(
        CancellationToken cancellationToken)
    {
        return _createCore(cancellationToken);
    }

    public ValueTask<AppUiAdapterBundle> CreateUiAsync(
        AppUiCompositionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _createUi(context, cancellationToken);
    }
}
