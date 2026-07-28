using System.ComponentModel;
using System.Runtime.CompilerServices;
using EMKE.Core;
using EMKE.Platform.Settings;
using EMKE.Windows.App.Commands;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Presentation;

namespace EMKE.Windows.App.Settings;

internal sealed record SettingsChoice<T>(T Value, string Label);

internal enum SettingsOperationResult
{
    None,
    Saved,
    ConnectionSucceeded,
    StartRequested,
    SaveFailed,
    ConnectionFailed,
    StartFailed,
}

internal interface ISettingsCapabilityTester
{
    Task TestConnectionAsync(CancellationToken cancellationToken);
}

internal interface ISettingsSystemActions
{
    ValueTask RunLocalDiagnosticsAsync(CancellationToken cancellationToken);

    ValueTask CheckForUpdatesAsync(CancellationToken cancellationToken);

    ValueTask ReopenOnboardingAsync(CancellationToken cancellationToken);

    ValueTask ExportDiagnosticsAsync(CancellationToken cancellationToken);
}

internal sealed class SettingsViewModel :
    INotifyPropertyChanged,
    IDisposable
{
    private const string ApiKeyName = "translationApiKey";

    private readonly IWindowsProductSettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IRuntimeCommandSink _runtime;
    private readonly ISettingsCapabilityTester _capabilityTester;
    private readonly FloatingStatusVisibilityController
        _floatingStatusVisibility;
    private readonly LocalizationService _localization;
    private readonly string[] _onboardingPreferenceIdentifiers;
    private char[] _apiKeyDraft = [];
    private string _baseAddress;
    private string _modelId;
    private LanguageCode _nativeLanguage;
    private LanguageCode _meetingLanguage;
    private string? _inputEndpointId;
    private string? _outputEndpointId;
    private bool _followDefaultInput;
    private bool _followDefaultOutput;
    private AppInterfaceLanguage _interfaceLanguage;
    private bool _floatingStatusEnabled = true;
    private SettingsOperationResult _operationResult;
    private int _disposed;

    public SettingsViewModel(
        WindowsProductSettings initialSettings,
        IWindowsProductSettingsStore settings,
        ISecretStore secrets,
        IRuntimeCommandSink runtime,
        ISettingsCapabilityTester capabilityTester,
        ISettingsSystemActions systemActions,
        FloatingStatusVisibilityController floatingStatusVisibility,
        LocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        _settings = settings
            ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets
            ?? throw new ArgumentNullException(nameof(secrets));
        _runtime = runtime
            ?? throw new ArgumentNullException(nameof(runtime));
        _capabilityTester = capabilityTester
            ?? throw new ArgumentNullException(nameof(capabilityTester));
        _floatingStatusVisibility = floatingStatusVisibility
            ?? throw new ArgumentNullException(
                nameof(floatingStatusVisibility));
        ArgumentNullException.ThrowIfNull(systemActions);
        _localization = localization
            ?? throw new ArgumentNullException(nameof(localization));

        _baseAddress = initialSettings.BaseUri.OriginalString;
        _modelId = initialSettings.ModelId;
        _nativeLanguage = initialSettings.NativeLanguage;
        _meetingLanguage = initialSettings.MeetingLanguage;
        _inputEndpointId = initialSettings.InputEndpointId;
        _outputEndpointId = initialSettings.OutputEndpointId;
        _followDefaultInput = initialSettings.FollowDefaultInput;
        _followDefaultOutput = initialSettings.FollowDefaultOutput;
        _interfaceLanguage =
            AppInterfaceLanguageExtensions.ParseStableValue(
                initialSettings.InterfaceLanguage);
        _floatingStatusEnabled = initialSettings.FloatingStatusEnabled;
        _floatingStatusVisibility.SetEnabled(_floatingStatusEnabled);
        _onboardingPreferenceIdentifiers =
            [.. initialSettings.OnboardingPreferenceIdentifiers];
        InputEndpointOptions = initialSettings.InputEndpointId is null
            ? []
            :
            [
                new SettingsChoice<string>(
                    initialSettings.InputEndpointId,
                    initialSettings.InputEndpointId),
            ];
        OutputEndpointOptions = initialSettings.OutputEndpointId is null
            ? []
            :
            [
                new SettingsChoice<string>(
                    initialSettings.OutputEndpointId,
                    initialSettings.OutputEndpointId),
            ];

        AsyncRuntimeCommandGroup group = new();
        SaveCommand = new AsyncRuntimeCommand(SaveAsync, group: group);
        TestConnectionCommand = new AsyncRuntimeCommand(
            TestConnectionAsync,
            group: group);
        StartCommand = new AsyncRuntimeCommand(StartAsync, group: group);
        SaveCommand.ExecutionFailed += OnSaveExecutionFailed;
        TestConnectionCommand.ExecutionFailed +=
            OnTestConnectionExecutionFailed;
        StartCommand.ExecutionFailed += OnStartExecutionFailed;
        RunLocalDiagnosticsCommand = new AsyncRuntimeCommand(
            cancellationToken =>
                systemActions.RunLocalDiagnosticsAsync(cancellationToken)
                    .AsTask(),
            group: group);
        CheckForUpdatesCommand = new AsyncRuntimeCommand(
            cancellationToken =>
                systemActions.CheckForUpdatesAsync(cancellationToken)
                    .AsTask(),
            group: group);
        ReopenOnboardingCommand = new AsyncRuntimeCommand(
            cancellationToken =>
                systemActions.ReopenOnboardingAsync(cancellationToken)
                    .AsTask(),
            group: group);
        ExportDiagnosticsCommand = new AsyncRuntimeCommand(
            cancellationToken =>
                systemActions.ExportDiagnosticsAsync(cancellationToken)
                    .AsTask(),
            group: group);
        RebuildOptions();
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ApiKeyClearRequested;

    public event EventHandler? CloseRequested;

    public AsyncRuntimeCommand SaveCommand { get; }

    public AsyncRuntimeCommand TestConnectionCommand { get; }

    public AsyncRuntimeCommand StartCommand { get; }

    public AsyncRuntimeCommand RunLocalDiagnosticsCommand { get; }

    public AsyncRuntimeCommand CheckForUpdatesCommand { get; }

    public AsyncRuntimeCommand ReopenOnboardingCommand { get; }

    public AsyncRuntimeCommand ExportDiagnosticsCommand { get; }

    public SettingsOperationResult OperationResult =>
        _operationResult;

    public string? ResultMessage => OperationResult switch
    {
        SettingsOperationResult.None => null,
        SettingsOperationResult.Saved =>
            Text(LocalizedString.SettingsSavedResult),
        SettingsOperationResult.ConnectionSucceeded =>
            Text(LocalizedString.SettingsConnectionSucceededResult),
        SettingsOperationResult.StartRequested =>
            Text(LocalizedString.SettingsStartRequestedResult),
        SettingsOperationResult.SaveFailed =>
            Text(LocalizedString.SettingsSaveFailedError),
        SettingsOperationResult.ConnectionFailed =>
            Text(LocalizedString.SettingsConnectionFailedError),
        SettingsOperationResult.StartFailed =>
            Text(LocalizedString.SettingsStartFailedError),
        _ => throw new InvalidOperationException(
            "Undefined settings operation result."),
    };

    public string? ErrorMessage => OperationResult switch
    {
        SettingsOperationResult.SaveFailed
            or SettingsOperationResult.ConnectionFailed
            or SettingsOperationResult.StartFailed
            => ResultMessage,
        _ => null,
    };

    public string? ResultAutomationDescription => ResultMessage;

    public string BaseAddress
    {
        get => _baseAddress;
        set => SetField(ref _baseAddress, value);
    }

    public string ModelId
    {
        get => _modelId;
        set => SetField(ref _modelId, value);
    }

    public LanguageCode NativeLanguage
    {
        get => _nativeLanguage;
        set => SetField(ref _nativeLanguage, value);
    }

    public LanguageCode MeetingLanguage
    {
        get => _meetingLanguage;
        set => SetField(ref _meetingLanguage, value);
    }

    public string? InputEndpointId
    {
        get => _inputEndpointId;
        set => SetField(ref _inputEndpointId, value);
    }

    public string? OutputEndpointId
    {
        get => _outputEndpointId;
        set => SetField(ref _outputEndpointId, value);
    }

    public bool FollowDefaultInput
    {
        get => _followDefaultInput;
        set => SetField(ref _followDefaultInput, value);
    }

    public bool FollowDefaultOutput
    {
        get => _followDefaultOutput;
        set => SetField(ref _followDefaultOutput, value);
    }

    public AppInterfaceLanguage InterfaceLanguage
    {
        get => _interfaceLanguage;
        set
        {
            if (SetField(ref _interfaceLanguage, value))
            {
                _localization.ChangeLanguage(value);
            }
        }
    }

    public bool FloatingStatusEnabled
    {
        get => _floatingStatusEnabled;
        set
        {
            if (SetField(ref _floatingStatusEnabled, value))
            {
                _floatingStatusVisibility.SetEnabled(value);
            }
        }
    }

    public bool HasApiKeyDraft => _apiKeyDraft.Length > 0;

    public IReadOnlyList<SettingsChoice<LanguageCode>>
        TranslationLanguageOptions
    { get; private set; } = [];

    public IReadOnlyList<SettingsChoice<AppInterfaceLanguage>>
        InterfaceLanguageOptions
    { get; private set; } = [];

    public IReadOnlyList<SettingsChoice<string>> InputEndpointOptions
    {
        get;
    }

    public IReadOnlyList<SettingsChoice<string>> OutputEndpointOptions
    {
        get;
    }

    public string WindowTitle => Text(LocalizedString.SettingsTitle);

    public string ServiceSectionLabel =>
        Text(LocalizedString.SettingsServiceSection);

    public string TranslationSectionLabel =>
        Text(LocalizedString.SettingsTranslationSection);

    public string AudioSectionLabel =>
        Text(LocalizedString.SettingsAudioSection);

    public string AppearanceSectionLabel =>
        Text(LocalizedString.SettingsAppearanceSection);

    public string SystemSectionLabel =>
        Text(LocalizedString.SettingsSystemSection);

    public string BaseAddressLabel =>
        Text(LocalizedString.SettingsBaseUrl);

    public string ModelLabel => Text(LocalizedString.SettingsModel);

    public string ApiKeyLabel => Text(LocalizedString.SettingsApiKey);

    public string TestConnectionLabel =>
        Text(LocalizedString.SettingsTestConnection);

    public string NativeLanguageLabel =>
        Text(LocalizedString.DashboardNativeLanguage);

    public string MeetingLanguageLabel =>
        Text(LocalizedString.DashboardMeetingLanguage);

    public string InputDeviceLabel =>
        Text(LocalizedString.SettingsInputDevice);

    public string OutputDeviceLabel =>
        Text(LocalizedString.SettingsOutputDevice);

    public string FollowDefaultInputLabel =>
        Text(LocalizedString.SettingsFollowDefaultInput);

    public string FollowDefaultOutputLabel =>
        Text(LocalizedString.SettingsFollowDefaultOutput);

    public string LocalDiagnosticsLabel =>
        Text(LocalizedString.SettingsLocalDiagnostics);

    public string InterfaceLanguageLabel =>
        Text(LocalizedString.SettingsInterfaceLanguage);

    public string FloatingStatusLabel =>
        Text(LocalizedString.SettingsFloatingStatus);

    public string DriverStatusLabel =>
        Text(LocalizedString.SettingsDriverStatus);

    public string CheckForUpdatesLabel =>
        Text(LocalizedString.TrayCheckForUpdates);

    public string ReopenOnboardingLabel =>
        Text(LocalizedString.SettingsReopenOnboarding);

    public string ExportDiagnosticsLabel =>
        Text(LocalizedString.SettingsExportDiagnostics);

    public string SaveLabel => Text(LocalizedString.SettingsSave);

    public string StartLabel => Text(LocalizedString.ActionStart);

    public string CloseLabel => Text(LocalizedString.PlaceholderClose);

    public void ReplaceApiKeyDraft(ReadOnlySpan<char> secret)
    {
        ClearApiKeyDraft(notifyPasswordBox: false);
        _apiKeyDraft = secret.ToArray();
        OnPropertyChanged(nameof(HasApiKeyDraft));
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        SetOperationResult(SettingsOperationResult.None);
        await PersistAndClearAsync(cancellationToken).ConfigureAwait(true);
        SetOperationResult(SettingsOperationResult.Saved);
    }

    public async Task TestConnectionAsync(
        CancellationToken cancellationToken)
    {
        SetOperationResult(SettingsOperationResult.None);
        await PersistAndClearAsync(cancellationToken).ConfigureAwait(true);
        await _capabilityTester.TestConnectionAsync(cancellationToken)
            .ConfigureAwait(true);
        SetOperationResult(SettingsOperationResult.ConnectionSucceeded);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SetOperationResult(SettingsOperationResult.None);
        await PersistAndClearAsync(cancellationToken).ConfigureAwait(true);
        _ = await _runtime.SubmitAsync(
                new RuntimeCommand.Start(),
                cancellationToken)
            .ConfigureAwait(true);
        SetOperationResult(SettingsOperationResult.StartRequested);
    }

    public void Close()
    {
        ClearApiKeyDraft(notifyPasswordBox: true);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _localization.LanguageChanged -= OnLanguageChanged;
        SaveCommand.ExecutionFailed -= OnSaveExecutionFailed;
        TestConnectionCommand.ExecutionFailed -=
            OnTestConnectionExecutionFailed;
        StartCommand.ExecutionFailed -= OnStartExecutionFailed;
        ClearApiKeyDraft(notifyPasswordBox: true);
        SaveCommand.Dispose();
        TestConnectionCommand.Dispose();
        StartCommand.Dispose();
        RunLocalDiagnosticsCommand.Dispose();
        CheckForUpdatesCommand.Dispose();
        ReopenOnboardingCommand.Dispose();
        ExportDiagnosticsCommand.Dispose();
    }

    private async Task PersistAndClearAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            WindowsProductSettings updated = new(
                new Uri(_baseAddress, UriKind.Absolute),
                _modelId,
                _nativeLanguage,
                _meetingLanguage,
                _inputEndpointId,
                _outputEndpointId,
                _followDefaultInput,
                _followDefaultOutput,
                _interfaceLanguage.ToStableValue(),
                _onboardingPreferenceIdentifiers,
                _floatingStatusEnabled);
            if (_apiKeyDraft.Length > 0)
            {
                await _secrets.SaveAsync(
                        ApiKeyName,
                        _apiKeyDraft,
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            await _settings.SaveProductSettingsAsync(
                    updated,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            ClearApiKeyDraft(notifyPasswordBox: true);
        }
    }

    private void ClearApiKeyDraft(bool notifyPasswordBox)
    {
        if (_apiKeyDraft.Length > 0)
        {
            Array.Clear(_apiKeyDraft);
            _apiKeyDraft = [];
            OnPropertyChanged(nameof(HasApiKeyDraft));
        }

        if (notifyPasswordBox)
        {
            ApiKeyClearRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnLanguageChanged(
        object? sender,
        AppInterfaceLanguageChangedEventArgs e)
    {
        _interfaceLanguage = e.Language;
        RebuildOptions();
        OnPropertyChanged(string.Empty);
    }

    private void OnSaveExecutionFailed(
        object? sender,
        CommandExecutionFailedEventArgs e)
    {
        _ = sender;
        _ = e;
        SetOperationResult(SettingsOperationResult.SaveFailed);
    }

    private void OnTestConnectionExecutionFailed(
        object? sender,
        CommandExecutionFailedEventArgs e)
    {
        _ = sender;
        _ = e;
        SetOperationResult(SettingsOperationResult.ConnectionFailed);
    }

    private void OnStartExecutionFailed(
        object? sender,
        CommandExecutionFailedEventArgs e)
    {
        _ = sender;
        _ = e;
        SetOperationResult(SettingsOperationResult.StartFailed);
    }

    private void SetOperationResult(SettingsOperationResult value)
    {
        if (_operationResult == value)
        {
            return;
        }

        _operationResult = value;
        OnPropertyChanged(nameof(OperationResult));
        OnPropertyChanged(nameof(ResultMessage));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(ResultAutomationDescription));
    }

    private void RebuildOptions()
    {
        TranslationLanguageOptions =
        [
            new SettingsChoice<LanguageCode>(
                LanguageCode.Zh,
                Text(LocalizedString.LanguageSimplifiedChinese)),
            new SettingsChoice<LanguageCode>(
                LanguageCode.En,
                Text(LocalizedString.LanguageEnglish)),
            new SettingsChoice<LanguageCode>(
                LanguageCode.De,
                Text(LocalizedString.LanguageGerman)),
        ];
        InterfaceLanguageOptions =
        [
            new SettingsChoice<AppInterfaceLanguage>(
                AppInterfaceLanguage.System,
                Text(LocalizedString.SettingsFollowSystem)),
            new SettingsChoice<AppInterfaceLanguage>(
                AppInterfaceLanguage.ZhHans,
                Text(LocalizedString.LanguageSimplifiedChinese)),
            new SettingsChoice<AppInterfaceLanguage>(
                AppInterfaceLanguage.English,
                Text(LocalizedString.LanguageEnglish)),
        ];
    }

    private string Text(LocalizedString key)
    {
        return _localization.Get(key, _localization.CurrentLanguage);
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
