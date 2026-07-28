using EMKE.Windows.App.Bootstrap;
using EMKE.Windows.App.Localization;

namespace EMKE.Windows.App.Tray;

internal enum TrayInteraction
{
    PrimaryActivate,
    OpenDashboard,
    OpenSettings,
    OpenOnboarding,
    CheckForUpdates,
    Exit,
    TaskbarCreated,
}

internal sealed record TrayMenuLabels(
    string ToolTip,
    string OpenDashboard,
    string OpenSettings,
    string OpenOnboarding,
    string CheckForUpdates,
    string Exit);

internal interface ITrayIconTransport : IAsyncDisposable
{
    ValueTask StartAsync(
        Func<TrayInteraction, ValueTask> interaction,
        CancellationToken cancellationToken);

    void AddIcon(TrayMenuLabels labels);

    void UpdateIcon(TrayMenuLabels labels);

    void DeleteIcon();
}

internal interface ITrayActions
{
    ValueTask ShowDashboardAsync(CancellationToken cancellationToken);

    ValueTask ShowSettingsAsync(CancellationToken cancellationToken);

    ValueTask ShowOnboardingAsync(CancellationToken cancellationToken);

    ValueTask CheckForUpdatesAsync(CancellationToken cancellationToken);

    ValueTask ExitAsync(CancellationToken cancellationToken);
}

internal sealed class TrayHost : IAppTrayLifetime
{
    private readonly object _sync = new();
    private readonly ITrayIconTransport _transport;
    private readonly ITrayActions _actions;
    private readonly LocalizationService _localization;
    private Task? _startTask;
    private int _removed;

    public TrayHost(
        ITrayIconTransport transport,
        ITrayActions actions,
        LocalizationService localization)
    {
        _transport =
            transport ?? throw new ArgumentNullException(nameof(transport));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _localization = localization
            ?? throw new ArgumentNullException(nameof(localization));
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _startTask ??= StartCoreAsync(cancellationToken);
            return new ValueTask(_startTask);
        }
    }

    public async ValueTask RemoveAsync()
    {
        if (Interlocked.Exchange(ref _removed, 1) != 0)
        {
            return;
        }

        _localization.LanguageChanged -= OnLanguageChanged;
        _transport.DeleteIcon();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _removed) != 0,
            this);
        await _transport.StartAsync(
                HandleInteractionAsync,
                cancellationToken)
            .ConfigureAwait(false);
        _localization.LanguageChanged += OnLanguageChanged;
        _transport.AddIcon(CurrentLabels());
    }

    private ValueTask HandleInteractionAsync(TrayInteraction interaction)
    {
        if (Volatile.Read(ref _removed) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return interaction switch
        {
            TrayInteraction.PrimaryActivate
                or TrayInteraction.OpenDashboard =>
                _actions.ShowDashboardAsync(CancellationToken.None),
            TrayInteraction.OpenSettings =>
                _actions.ShowSettingsAsync(CancellationToken.None),
            TrayInteraction.OpenOnboarding =>
                _actions.ShowOnboardingAsync(CancellationToken.None),
            TrayInteraction.CheckForUpdates =>
                _actions.CheckForUpdatesAsync(CancellationToken.None),
            TrayInteraction.Exit =>
                _actions.ExitAsync(CancellationToken.None),
            TrayInteraction.TaskbarCreated =>
                RecreateIconAsync(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(interaction),
                interaction,
                "Undefined tray interaction."),
        };
    }

    private ValueTask RecreateIconAsync()
    {
        _transport.AddIcon(CurrentLabels());
        return ValueTask.CompletedTask;
    }

    private void OnLanguageChanged(
        object? sender,
        AppInterfaceLanguageChangedEventArgs e)
    {
        if (Volatile.Read(ref _removed) == 0)
        {
            _transport.UpdateIcon(CurrentLabels());
        }
    }

    private TrayMenuLabels CurrentLabels()
    {
        AppInterfaceLanguage language = _localization.CurrentLanguage;
        return new TrayMenuLabels(
            _localization.Get(LocalizedString.AppName, language),
            _localization.Get(
                LocalizedString.TrayOpenDashboard,
                language),
            _localization.Get(
                LocalizedString.TrayOpenSettings,
                language),
            _localization.Get(
                LocalizedString.TrayOpenOnboarding,
                language),
            _localization.Get(
                LocalizedString.TrayCheckForUpdates,
                language),
            _localization.Get(LocalizedString.TrayExit, language));
    }
}
