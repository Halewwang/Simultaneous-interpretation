using System.ComponentModel;
using System.Runtime.CompilerServices;
using EMKE.Core;
using EMKE.Windows.App.Commands;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Presentation;

namespace EMKE.Windows.App.Dashboard;

internal sealed class DashboardWindowLifetime
{
    private readonly Action _hide;

    public DashboardWindowLifetime(Action hide)
    {
        _hide = hide ?? throw new ArgumentNullException(nameof(hide));
    }

    public bool HandleClosing()
    {
        _hide();
        return true;
    }
}

internal sealed class DashboardViewModel :
    INotifyPropertyChanged,
    IDisposable
{
    private const int CaptionTextElementLimit = 320;

    private readonly LocalizationService _localization;
    private readonly IRuntimeCommandSink _runtimeCommands;
    private readonly IDisposable _presentationSubscription;
    private AppPresentation? _presentation;
    private IReadOnlyList<string> _translationLanguageOptions = [];
    private int _selectedNativeLanguageIndex;
    private int _selectedMeetingLanguageIndex = 1;
    private int _disposed;

    public DashboardViewModel(
        PresentationCoordinator presentationCoordinator,
        LocalizationService localization,
        IRuntimeCommandSink runtimeCommands,
        IAppSurfaceActions surfaceActions)
    {
        ArgumentNullException.ThrowIfNull(presentationCoordinator);
        _localization = localization
            ?? throw new ArgumentNullException(nameof(localization));
        _runtimeCommands = runtimeCommands
            ?? throw new ArgumentNullException(nameof(runtimeCommands));
        ArgumentNullException.ThrowIfNull(surfaceActions);

        AsyncRuntimeCommandGroup runtimeGroup = new();
        StartCommand = new AsyncRuntimeCommand(
            cancellationToken => SubmitAsync(
                new RuntimeCommand.Start(),
                cancellationToken),
            () => _presentation?.StartAction.IsEnabled == true,
            runtimeGroup);
        StopCommand = new AsyncRuntimeCommand(
            cancellationToken => SubmitAsync(
                new RuntimeCommand.Stop(),
                cancellationToken),
            () => _presentation?.StopAction.IsEnabled == true,
            runtimeGroup,
            isPriority: true);
        InboundBypassCommand = new AsyncRuntimeCommand(
            cancellationToken => SubmitAsync(
                new RuntimeCommand.SetInboundBypass(
                    !(_presentation?.InboundChannel.IsBypassActive ?? false)),
                cancellationToken),
            CanChangeBypass,
            runtimeGroup);
        OutboundBypassCommand = new AsyncRuntimeCommand(
            cancellationToken => SubmitAsync(
                new RuntimeCommand.SetOutboundBypass(
                    !(_presentation?.OutboundChannel.IsBypassActive ?? false)),
                cancellationToken),
            CanChangeBypass,
            runtimeGroup);
        OpenSettingsCommand = new AsyncRuntimeCommand(
            cancellationToken =>
                surfaceActions.OpenSettingsAsync(cancellationToken).AsTask());
        OpenDiagnosticsCommand = new AsyncRuntimeCommand(
            cancellationToken =>
                surfaceActions.OpenDiagnosticsAsync(cancellationToken).AsTask());

        RemapStaticText();
        _presentationSubscription =
            presentationCoordinator.Subscribe(ApplyPresentation);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRuntimeCommand StartCommand { get; }

    public AsyncRuntimeCommand StopCommand { get; }

    public AsyncRuntimeCommand InboundBypassCommand { get; }

    public AsyncRuntimeCommand OutboundBypassCommand { get; }

    public AsyncRuntimeCommand OpenSettingsCommand { get; }

    public AsyncRuntimeCommand OpenDiagnosticsCommand { get; }

    public string WindowTitle => Text(LocalizedString.DashboardTitle);

    public string RuntimeStatus => _presentation?.RuntimeLabel ?? string.Empty;

    public bool IsProgressVisible =>
        _presentation?.IsProgressVisible == true;

    public bool IsStartVisible =>
        _presentation?.StartAction.IsVisible == true;

    public bool IsStopVisible =>
        _presentation?.StopAction.IsVisible == true;

    public string StartLabel =>
        _presentation?.StartAction.Label
        ?? Text(LocalizedString.ActionStart);

    public string StopLabel =>
        _presentation?.StopAction.Label
        ?? Text(LocalizedString.ActionStop);

    public bool AreLanguageSelectorsEnabled =>
        _presentation is not null
        && !_presentation.StopAction.IsVisible;

    public IReadOnlyList<string> TranslationLanguageOptions =>
        _translationLanguageOptions;

    public int SelectedNativeLanguageIndex
    {
        get => _selectedNativeLanguageIndex;
        set
        {
            if (_selectedNativeLanguageIndex != value)
            {
                _selectedNativeLanguageIndex = value;
                OnPropertyChanged();
            }
        }
    }

    public int SelectedMeetingLanguageIndex
    {
        get => _selectedMeetingLanguageIndex;
        set
        {
            if (_selectedMeetingLanguageIndex != value)
            {
                _selectedMeetingLanguageIndex = value;
                OnPropertyChanged();
            }
        }
    }

    public string NativeLanguageLabel =>
        Text(LocalizedString.DashboardNativeLanguage);

    public string MeetingLanguageLabel =>
        Text(LocalizedString.DashboardMeetingLanguage);

    public string InboundHeading => Text(LocalizedString.DashboardInbound);

    public string OutboundHeading => Text(LocalizedString.DashboardOutbound);

    public string InboundStatus =>
        _presentation?.InboundChannel.StatusLabel ?? string.Empty;

    public string OutboundStatus =>
        _presentation?.OutboundChannel.StatusLabel ?? string.Empty;

    public string InboundRoute =>
        _presentation?.InboundChannel.RouteLabel ?? string.Empty;

    public string OutboundRoute =>
        _presentation?.OutboundChannel.RouteLabel ?? string.Empty;

    public string? InboundSafetyMessage =>
        _presentation?.InboundChannel.SafetyMessage;

    public string? OutboundSafetyMessage =>
        _presentation?.OutboundChannel.SafetyMessage;

    public bool IsInboundBypassActive =>
        _presentation?.InboundChannel.IsBypassActive == true;

    public bool IsOutboundBypassActive =>
        _presentation?.OutboundChannel.IsBypassActive == true;

    public string InboundBypassLabel =>
        Text(LocalizedString.DashboardInboundBypass);

    public string OutboundBypassLabel =>
        Text(LocalizedString.DashboardOutboundBypass);

    public double InboundLevel => _presentation?.InboundLevel ?? 0;

    public double OutboundLevel => _presentation?.OutboundLevel ?? 0;

    public string LevelLabel => Text(LocalizedString.DashboardLevel);

    public string SourceCaptionLabel =>
        Text(LocalizedString.DashboardSourceCaption);

    public string TranslatedCaptionLabel =>
        Text(LocalizedString.DashboardTranslatedCaption);

    public string SourceCaption => BoundedPresentationText.Caption(
        _presentation?.SourceCaption ?? string.Empty,
        CaptionTextElementLimit);

    public string TranslatedCaption => BoundedPresentationText.Caption(
        _presentation?.TranslatedCaption ?? string.Empty,
        CaptionTextElementLimit);

    public string? ErrorMessage => _presentation?.Error?.Message;

    public string SettingsLabel => Text(LocalizedString.DashboardSettings);

    public string DiagnosticsLabel =>
        Text(LocalizedString.DashboardDiagnostics);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _presentationSubscription.Dispose();
        StartCommand.Dispose();
        StopCommand.Dispose();
        InboundBypassCommand.Dispose();
        OutboundBypassCommand.Dispose();
        OpenSettingsCommand.Dispose();
        OpenDiagnosticsCommand.Dispose();
    }

    private bool CanChangeBypass()
    {
        return _presentation is
        {
            IsProgressVisible: false,
            StopAction.IsVisible: true,
        };
    }

    private async Task SubmitAsync(
        RuntimeCommand command,
        CancellationToken cancellationToken)
    {
        _ = await _runtimeCommands.SubmitAsync(command, cancellationToken)
            .ConfigureAwait(true);
    }

    private void ApplyPresentation(AppPresentation presentation)
    {
        _presentation = presentation
            ?? throw new ArgumentNullException(nameof(presentation));
        RemapStaticText();
        OnPropertyChanged(string.Empty);
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        InboundBypassCommand.NotifyCanExecuteChanged();
        OutboundBypassCommand.NotifyCanExecuteChanged();
    }

    private void RemapStaticText()
    {
        _translationLanguageOptions =
        [
            Text(LocalizedString.LanguageSimplifiedChinese),
            Text(LocalizedString.LanguageEnglish),
        ];
    }

    private string Text(LocalizedString key)
    {
        return _localization.Get(key, _localization.CurrentLanguage);
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
