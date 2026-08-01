namespace EMKE.Platform.Diagnostics;

public enum WindowsAudioDiagnosticKind
{
    InputLevel,
    LocalOutputTone,
    VirtualEndpoints,
}

public enum WindowsAudioEndpointRole
{
    PhysicalInput,
    PhysicalOutput,
    MeetingSpeakerRender,
    AppSpeakerCapture,
    AppMicrophoneRender,
    MeetingMicrophoneCapture,
}

public sealed record WindowsAudioEndpointDiagnostic(
    string EndpointId,
    string FriendlyName,
    WindowsAudioEndpointRole Role,
    string CurrentFormat,
    bool IsAvailable);

public sealed record WindowsAudioDiagnosticCounters(
    string LastHResultCategory,
    ulong Underruns,
    ulong Overflows,
    ulong DroppedFrames);

public sealed record WindowsAudioDiagnosticSnapshot
{
    public static WindowsAudioDiagnosticSnapshot Empty { get; } =
        new([], new WindowsAudioDiagnosticCounters("none", 0, 0, 0));

    public WindowsAudioDiagnosticSnapshot(
        IEnumerable<WindowsAudioEndpointDiagnostic> endpoints,
        WindowsAudioDiagnosticCounters counters)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Endpoints = Array.AsReadOnly(endpoints.ToArray());
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    public IReadOnlyList<WindowsAudioEndpointDiagnostic> Endpoints { get; }

    public WindowsAudioDiagnosticCounters Counters { get; }
}

public sealed record WindowsAudioDiagnosticResult(
    WindowsAudioDiagnosticKind Kind,
    bool IsSuccessful,
    double? Level,
    WindowsAudioDiagnosticSnapshot? Snapshot,
    ReadOnlyMemory<short>? Pcm16 = null);

public interface IWindowsAudioDiagnosticBackend
{
    Task<double> MeasureInputLevelAsync(
        string endpointId,
        CancellationToken cancellationToken);

    Task PlayLocalPcm16Async(
        string endpointId,
        ReadOnlyMemory<short> pcm16,
        int sampleRate,
        int channelCount,
        CancellationToken cancellationToken);

    Task<WindowsAudioDiagnosticSnapshot> InspectAsync(
        CancellationToken cancellationToken);
}

public interface IWindowsAudioDiagnostics
{
    bool IsRunning { get; }

    string? LastErrorCode { get; }

    Task<WindowsAudioDiagnosticResult> RunInputTestAsync(
        string endpointId,
        CancellationToken cancellationToken);

    Task<WindowsAudioDiagnosticResult> RunOutputTestAsync(
        string endpointId,
        CancellationToken cancellationToken);

    Task<WindowsAudioDiagnosticResult> InspectVirtualEndpointsAsync(
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

#pragma warning disable CA1001 // DisposeAsync owns and releases lifecycle resources.

public sealed class WindowsAudioDiagnostics :
    IWindowsAudioDiagnostics,
    IAsyncDisposable
{
    public const string TranslationActiveCode =
        "windowsAudioDiagnostics.translationActive";
    public const string ProviderCleanupFailureCode =
        "windowsAudioDiagnostics.providerCleanupFailed";

    private const int ToneSampleRate = 48_000;
    private const int ToneFrequency = 440;
    private const int ToneDurationMilliseconds = 250;
    private const double ToneAmplitude = 0.25;

    private readonly IWindowsAudioDiagnosticBackend _backend;
    private readonly Func<bool> _isTranslationActive;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _activeCancellation;
    private Task<WindowsAudioDiagnosticResult>? _activeOperation;
    private string? _lastErrorCode;
    private int _disposed;

    public WindowsAudioDiagnostics(
        IWindowsAudioDiagnosticBackend backend,
        Func<bool> isTranslationActive)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _isTranslationActive = isTranslationActive
            ?? throw new ArgumentNullException(nameof(isTranslationActive));
    }

    public bool IsRunning =>
        Volatile.Read(ref _activeOperation) is { IsCompleted: false };

    public string? LastErrorCode => Volatile.Read(ref _lastErrorCode);

    public Task<WindowsAudioDiagnosticResult> RunInputTestAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        return RunExclusiveAsync(
            async token =>
            {
                double level =
                    await _backend.MeasureInputLevelAsync(endpointId, token)
                        .ConfigureAwait(false);
                double bounded = double.IsFinite(level)
                    ? Math.Clamp(level, 0d, 1d)
                    : 0d;
                return new WindowsAudioDiagnosticResult(
                    WindowsAudioDiagnosticKind.InputLevel,
                    IsSuccessful: true,
                    bounded,
                    Snapshot: null);
            },
            cancellationToken);
    }

    public Task<WindowsAudioDiagnosticResult> RunOutputTestAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        return RunExclusiveAsync(
            async token =>
            {
                short[] tone = GenerateTone();
                await _backend.PlayLocalPcm16Async(
                    endpointId,
                    tone,
                    ToneSampleRate,
                    channelCount: 1,
                    token).ConfigureAwait(false);
                return new WindowsAudioDiagnosticResult(
                    WindowsAudioDiagnosticKind.LocalOutputTone,
                    IsSuccessful: true,
                    Level: null,
                    Snapshot: null);
            },
            cancellationToken);
    }

    public Task<WindowsAudioDiagnosticResult> InspectVirtualEndpointsAsync(
        CancellationToken cancellationToken)
    {
        return RunExclusiveAsync(
            async token =>
            {
                WindowsAudioDiagnosticSnapshot snapshot =
                    await _backend.InspectAsync(token).ConfigureAwait(false);
                bool rolesReady = RequiredVirtualRoles.All(
                    role => snapshot.Endpoints.Any(
                        endpoint =>
                            endpoint.Role == role && endpoint.IsAvailable));
                return new WindowsAudioDiagnosticResult(
                    WindowsAudioDiagnosticKind.VirtualEndpoints,
                    rolesReady,
                    Level: null,
                    snapshot);
            },
            cancellationToken);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task<WindowsAudioDiagnosticResult>? operation = _activeOperation;
            if (_activeCancellation is not null)
            {
                await _activeCancellation.CancelAsync().ConfigureAwait(false);
            }
            if (operation is not null)
            {
                await ObserveExpectedCancellationAsync(operation)
                    .ConfigureAwait(false);
            }

            ReleaseActive();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private static readonly WindowsAudioEndpointRole[] RequiredVirtualRoles =
    [
        WindowsAudioEndpointRole.MeetingSpeakerRender,
        WindowsAudioEndpointRole.AppSpeakerCapture,
        WindowsAudioEndpointRole.AppMicrophoneRender,
        WindowsAudioEndpointRole.MeetingMicrophoneCapture,
    ];

    private async Task<WindowsAudioDiagnosticResult> RunExclusiveAsync(
        Func<CancellationToken, Task<WindowsAudioDiagnosticResult>> operation,
        CancellationToken cancellationToken)
    {
        Task<WindowsAudioDiagnosticResult> current;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_activeCancellation is not null)
            {
                await _activeCancellation.CancelAsync().ConfigureAwait(false);
            }
            if (_activeOperation is not null)
            {
                await ObserveExpectedCancellationAsync(_activeOperation)
                    .ConfigureAwait(false);
                ReleaseActive();
            }

            if (_isTranslationActive())
            {
                throw new InvalidOperationException(TranslationActiveCode);
            }

            _activeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            current = operation(_activeCancellation.Token);
            Volatile.Write(ref _activeOperation, current);
        }
        finally
        {
            _lifecycle.Release();
        }

        try
        {
            return await current.ConfigureAwait(false);
        }
        finally
        {
            await ClearIfCurrentAsync(current).ConfigureAwait(false);
        }
    }

    private async Task ClearIfCurrentAsync(
        Task<WindowsAudioDiagnosticResult> operation)
    {
        await _lifecycle.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                ReleaseActive();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ReleaseActive()
    {
        Volatile.Write(ref _activeOperation, null);
        _activeCancellation?.Dispose();
        _activeCancellation = null;
    }

    private async Task ObserveExpectedCancellationAsync(
        Task<WindowsAudioDiagnosticResult> operation)
    {
        try
        {
            _ = await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Provider cleanup details are reduced to a stable, non-sensitive code.
        catch (Exception)
        {
            Volatile.Write(
                ref _lastErrorCode,
                ProviderCleanupFailureCode);
        }
#pragma warning restore CA1031
    }

    private static short[] GenerateTone()
    {
        int sampleCount =
            ToneSampleRate * ToneDurationMilliseconds / 1_000;
        short[] samples = GC.AllocateUninitializedArray<short>(sampleCount);
        double scale = short.MaxValue * ToneAmplitude;
        double radiansPerSample =
            2d * Math.PI * ToneFrequency / ToneSampleRate;
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = checked(
                (short)Math.Round(
                    Math.Sin(index * radiansPerSample) * scale,
                    MidpointRounding.ToEven));
        }

        return samples;
    }
}

#pragma warning restore CA1001
