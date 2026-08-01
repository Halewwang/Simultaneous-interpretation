using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Diagnostics;
using EMKE.Windows.App.Localization;

namespace EMKE.Windows.App.Diagnostics;

internal sealed record DiagnosticEndpointRow(
    string FriendlyName,
    string EndpointHash,
    WindowsAudioEndpointRole Role,
    string CurrentFormat,
    bool IsAvailable,
    string RoleLabel = "",
    string AvailabilityLabel = "")
{
    public string DisplayName => $"{FriendlyName} #{EndpointHash}";
}

internal sealed record DiagnosticCapabilityStageRow(
    string StableName,
    string OutcomeLabel);

#pragma warning disable CA1001 // DisposeAsync closes the owned cancellation lifecycle.

internal sealed class DiagnosticsViewModel :
    INotifyPropertyChanged,
    IAsyncDisposable
{
    private readonly IWindowsAudioDiagnostics _audio;
    private readonly ITranslationConnectionProbe _connectionProbe;
    private readonly TranslationSessionConfiguration _inbound;
    private readonly TranslationSessionConfiguration _outbound;
    private readonly LocalizationService? _localization;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<DiagnosticEndpointRow> _endpoints = [];
    private TranslationCompatibilityReport? _connectionReport;
    private IReadOnlyList<DiagnosticCapabilityStageRow> _connectionStages = [];
    private WindowsAudioDiagnosticResult? _lastDiagnosticResult;
    private string _lastHResultCategory = "none";
    private ulong _underruns;
    private ulong _overflows;
    private ulong _droppedFrames;
    private int _closed;
    private int _disposed;

    public DiagnosticsViewModel(
        IWindowsAudioDiagnostics audio,
        ITranslationConnectionProbe connectionProbe,
        TranslationSessionConfiguration inbound,
        TranslationSessionConfiguration outbound,
        LocalizationService? localization = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _connectionProbe = connectionProbe
            ?? throw new ArgumentNullException(nameof(connectionProbe));
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _localization = localization;
        if (_localization is not null)
        {
            _localization.LanguageChanged += OnLanguageChanged;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<DiagnosticEndpointRow> Endpoints => _endpoints;

    public TranslationCompatibilityReport? ConnectionReport =>
        _connectionReport;

    public IReadOnlyList<DiagnosticCapabilityStageRow> ConnectionStages =>
        _connectionStages;

    public WindowsAudioDiagnosticResult? LastDiagnosticResult =>
        _lastDiagnosticResult;

    public string LastHResultCategory => _lastHResultCategory;

    public ulong Underruns => _underruns;

    public ulong Overflows => _overflows;

    public ulong DroppedFrames => _droppedFrames;

    public bool IsDiagnosticRunning => _audio.IsRunning;

    public string WindowTitle => Text(LocalizedString.DiagnosticsTitle);

    public string RefreshLabel => Text(LocalizedString.DiagnosticsRefresh);

    public string TestConnectionLabel =>
        Text(LocalizedString.DiagnosticsTestConnection);

    public string CloseLabel => Text(LocalizedString.DiagnosticsClose);

    public string EndpointsLabel => Text(LocalizedString.DiagnosticsEndpoints);

    public string EndpointNameLabel =>
        Text(LocalizedString.DiagnosticsEndpointName);

    public string EndpointIdentifierLabel =>
        Text(LocalizedString.DiagnosticsEndpointIdentifier);

    public string EndpointRoleLabel =>
        Text(LocalizedString.DiagnosticsEndpointRole);

    public string EndpointFormatLabel =>
        Text(LocalizedString.DiagnosticsEndpointFormat);

    public string EndpointAvailableLabel =>
        Text(LocalizedString.DiagnosticsAvailable);

    public string LastHResultLabel =>
        Text(LocalizedString.DiagnosticsLastHResult);

    public string UnderrunsLabel =>
        Text(LocalizedString.DiagnosticsUnderruns);

    public string OverflowsLabel =>
        Text(LocalizedString.DiagnosticsOverflows);

    public string DroppedFramesLabel =>
        Text(LocalizedString.DiagnosticsDroppedFrames);

    public string ConnectionStagesLabel =>
        Text(LocalizedString.DiagnosticsConnectionStages);

    public string StageLabel => Text(LocalizedString.DiagnosticsStage);

    public string OutcomeLabel =>
        Text(LocalizedString.DiagnosticsOutcome);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        WindowsAudioDiagnosticResult result =
            await RunWithLifetimeAsync(
                token => _audio.InspectVirtualEndpointsAsync(token),
                cancellationToken).ConfigureAwait(true);
        ApplyDiagnostic(result);
    }

    public async Task TestConnectionAsync(
        CancellationToken cancellationToken)
    {
        TranslationCompatibilityReport report =
            await RunWithLifetimeAsync(
                token => _connectionProbe.RunAsync(
                    _inbound,
                    _outbound,
                    token),
                cancellationToken).ConfigureAwait(true);
        _connectionReport = report;
        RebuildConnectionStages();
        OnPropertyChanged(nameof(ConnectionReport));
        OnPropertyChanged(nameof(ConnectionStages));
    }

    public async Task RunInputTestAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        WindowsAudioDiagnosticResult result =
            await RunWithLifetimeAsync(
                token => _audio.RunInputTestAsync(endpointId, token),
                cancellationToken).ConfigureAwait(true);
        ApplyDiagnostic(result);
    }

    public async Task RunOutputTestAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        WindowsAudioDiagnosticResult result =
            await RunWithLifetimeAsync(
                token => _audio.RunOutputTestAsync(endpointId, token),
                cancellationToken).ConfigureAwait(true);
        ApplyDiagnostic(result);
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _audio.StopAsync(cancellationToken).ConfigureAwait(true);
        OnPropertyChanged(nameof(IsDiagnosticRunning));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (_localization is not null)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
        }
        _lifetime.Dispose();
    }

    private async Task<T> RunWithLifetimeAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _closed) != 0,
            this);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
        OnPropertyChanged(nameof(IsDiagnosticRunning));
        try
        {
            return await action(linked.Token).ConfigureAwait(true);
        }
        finally
        {
            OnPropertyChanged(nameof(IsDiagnosticRunning));
        }
    }

    private void ApplyDiagnostic(WindowsAudioDiagnosticResult result)
    {
        _lastDiagnosticResult = result;
        OnPropertyChanged(nameof(LastDiagnosticResult));
        if (result.Snapshot is not WindowsAudioDiagnosticSnapshot snapshot)
        {
            return;
        }

        _endpoints = Array.AsReadOnly(
            snapshot.Endpoints.Select(
                endpoint => new DiagnosticEndpointRow(
                    endpoint.FriendlyName,
                    HashEndpointId(endpoint.EndpointId),
                    endpoint.Role,
                    endpoint.CurrentFormat,
                    endpoint.IsAvailable,
                    RoleLabel(endpoint.Role),
                    endpoint.IsAvailable
                        ? Text(LocalizedString.DiagnosticsAvailable)
                        : Text(LocalizedString.DiagnosticsUnavailable)))
                .ToArray());
        _lastHResultCategory = snapshot.Counters.LastHResultCategory;
        _underruns = snapshot.Counters.Underruns;
        _overflows = snapshot.Counters.Overflows;
        _droppedFrames = snapshot.Counters.DroppedFrames;
        OnPropertyChanged(nameof(Endpoints));
        OnPropertyChanged(nameof(LastHResultCategory));
        OnPropertyChanged(nameof(Underruns));
        OnPropertyChanged(nameof(Overflows));
        OnPropertyChanged(nameof(DroppedFrames));
    }

    private static string HashEndpointId(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(endpointId));
        return Convert.ToHexString(digest.AsSpan(0, 4));
    }

    private void RebuildConnectionStages()
    {
        _connectionStages = _connectionReport is null
            ? []
            : Array.AsReadOnly(
                _connectionReport.Stages.Select(
                    stage => new DiagnosticCapabilityStageRow(
                        stage.StableName,
                        OutcomeLabelFor(stage.Outcome)))
                    .ToArray());
    }

    private string RoleLabel(WindowsAudioEndpointRole role)
    {
        LocalizedString key = role switch
        {
            WindowsAudioEndpointRole.PhysicalInput =>
                LocalizedString.DiagnosticsRolePhysicalInput,
            WindowsAudioEndpointRole.PhysicalOutput =>
                LocalizedString.DiagnosticsRolePhysicalOutput,
            WindowsAudioEndpointRole.MeetingSpeakerRender =>
                LocalizedString.DiagnosticsRoleMeetingSpeakerRender,
            WindowsAudioEndpointRole.AppSpeakerCapture =>
                LocalizedString.DiagnosticsRoleAppSpeakerCapture,
            WindowsAudioEndpointRole.AppMicrophoneRender =>
                LocalizedString.DiagnosticsRoleAppMicrophoneRender,
            WindowsAudioEndpointRole.MeetingMicrophoneCapture =>
                LocalizedString.DiagnosticsRoleMeetingMicrophoneCapture,
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Undefined diagnostic endpoint role."),
        };
        return Text(key);
    }

    private string OutcomeLabelFor(TranslationCapabilityOutcome outcome)
    {
        LocalizedString key = outcome switch
        {
            TranslationCapabilityOutcome.Passed =>
                LocalizedString.DiagnosticsOutcomePassed,
            TranslationCapabilityOutcome.Failed =>
                LocalizedString.DiagnosticsOutcomeFailed,
            TranslationCapabilityOutcome.RequiresInteractiveAudio =>
                LocalizedString.DiagnosticsOutcomeRequiresAudio,
            TranslationCapabilityOutcome.NotRun =>
                LocalizedString.DiagnosticsOutcomeNotRun,
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Undefined Translation capability outcome."),
        };
        return Text(key);
    }

    private void OnLanguageChanged(
        object? sender,
        AppInterfaceLanguageChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_lastDiagnosticResult?.Snapshot is { } snapshot)
        {
            ApplyDiagnostic(_lastDiagnosticResult with { Snapshot = snapshot });
        }

        RebuildConnectionStages();
        OnPropertyChanged(string.Empty);
    }

    private string Text(LocalizedString key)
    {
        if (_localization is null)
        {
            return key.ToString();
        }

        return _localization.Get(key, _localization.CurrentLanguage);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(name));
    }
}

#pragma warning restore CA1001
