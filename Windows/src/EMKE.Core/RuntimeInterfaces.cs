namespace EMKE.Core;

public enum TranslationSessionEventKind
{
    SourceCaption,
    TranslatedCaption,
    AudioDelta,
    Completed,
}

public enum AudioEngineEventKind
{
    None,
    InboundPcm16,
    OutboundPcm16,
    DeviceChanged,
    StreamError,
    Backpressure,
}

public enum AudioDirection
{
    Inbound,
    Outbound,
}

public enum AudioEngineStatus
{
    Ok,
    InvalidArgument,
    AbiMismatch,
    DeviceMissing,
    FormatUnsupported,
    QueueFull,
    NotRunning,
    InternalError,
}

public enum AudioEngineRoute
{
    Stopped,
    Translated,
    OriginalFailOpen,
    OriginalBypass,
    MutedFailClosed,
}

public enum AudioDeviceDirection
{
    Input,
    Output,
}

public enum RuntimeLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public sealed record TranslationSessionConfiguration
{
    public TranslationSessionConfiguration(LanguageCode sourceLanguage, LanguageCode targetLanguage, string model)
    {
        DomainEnum.ThrowIfUndefined(sourceLanguage, nameof(sourceLanguage));
        DomainEnum.ThrowIfUndefined(targetLanguage, nameof(targetLanguage));
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model must not be empty.", nameof(model));
        }

        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        Model = model;
    }

    public LanguageCode SourceLanguage { get; }

    public LanguageCode TargetLanguage { get; }

    public string Model { get; }
}

public interface IPcmBufferLease : IDisposable
{
    /// <summary>
    /// Gets PCM16 bytes owned by this lease.
    /// </summary>
    /// <remarks>
    /// Consumers must call <see cref="IDisposable.Dispose"/> after processing.
    /// Dispose returns the underlying buffer to its owner; the memory must not
    /// be read afterward.
    /// </remarks>
    ReadOnlyMemory<byte> Memory { get; }
}

#pragma warning disable CA1034 // Nesting closes the public translation-event hierarchy.

public abstract class TranslationSessionEvent
{
    private TranslationSessionEvent(TranslationSessionEventKind kind)
    {
        DomainEnum.ThrowIfUndefined(kind, nameof(kind));
        Kind = kind;
    }

    public TranslationSessionEventKind Kind { get; }

    public sealed class SourceCaption : TranslationSessionEvent
    {
        public SourceCaption(string text, LanguageCode? detectedLanguage, bool isFinal)
            : base(TranslationSessionEventKind.SourceCaption)
        {
            ValidateDetectedLanguage(detectedLanguage);
            Text = text ?? throw new ArgumentNullException(nameof(text));
            DetectedLanguage = detectedLanguage;
            IsFinal = isFinal;
        }

        public string Text { get; }

        public LanguageCode? DetectedLanguage { get; }

        public bool IsFinal { get; }
    }

    public sealed class TranslatedCaption : TranslationSessionEvent
    {
        public TranslatedCaption(string text, LanguageCode? detectedLanguage, bool isFinal)
            : base(TranslationSessionEventKind.TranslatedCaption)
        {
            ValidateDetectedLanguage(detectedLanguage);
            Text = text ?? throw new ArgumentNullException(nameof(text));
            DetectedLanguage = detectedLanguage;
            IsFinal = isFinal;
        }

        public string Text { get; }

        public LanguageCode? DetectedLanguage { get; }

        public bool IsFinal { get; }
    }

    public sealed class AudioDelta : TranslationSessionEvent, IDisposable
    {
        private readonly ReadOnlyMemory<byte> _pcm16;
        private IPcmBufferLease? _lease;

        public AudioDelta(IPcmBufferLease lease)
            : base(TranslationSessionEventKind.AudioDelta)
        {
            ArgumentNullException.ThrowIfNull(lease);

            ReadOnlyMemory<byte> pcm16 = lease.Memory;
            ValidatePcm16(pcm16, nameof(lease));
            _pcm16 = pcm16;
            _lease = lease;
        }

        public ReadOnlyMemory<byte> Pcm16
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _lease) is null, this);
                return _pcm16;
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }

    public sealed class Completed : TranslationSessionEvent
    {
        public Completed()
            : base(TranslationSessionEventKind.Completed)
        {
        }
    }

    private static void ValidateDetectedLanguage(LanguageCode? detectedLanguage)
    {
        if (detectedLanguage is LanguageCode language)
        {
            DomainEnum.ThrowIfUndefined(language, nameof(detectedLanguage));
        }
    }

    private static void ValidatePcm16(ReadOnlyMemory<byte> pcm16, string parameterName)
    {
        if (pcm16.IsEmpty || (pcm16.Length & 1) != 0)
        {
            throw new ArgumentException("PCM16 buffers must contain a non-empty, even number of bytes.", parameterName);
        }
    }
}

#pragma warning restore CA1034

public sealed class AudioEngineEvent : IDisposable
{
    private readonly ReadOnlyMemory<byte> _pcm16;
    private IPcmBufferLease? _lease;

    private AudioEngineEvent(
        AudioEngineEventKind kind,
        AudioDirection? direction,
        AudioEngineRoute route,
        AudioEngineStatus status,
        uint frameCount,
        ulong sequence,
        IPcmBufferLease? lease,
        ReadOnlyMemory<byte> pcm16)
    {
        Kind = kind;
        Direction = direction;
        Route = route;
        Status = status;
        FrameCount = frameCount;
        Sequence = sequence;
        _lease = lease;
        _pcm16 = pcm16;
    }

    public AudioEngineEventKind Kind { get; }

    public AudioDirection? Direction { get; }

    public AudioEngineRoute Route { get; }

    public AudioEngineStatus Status { get; }

    public uint FrameCount { get; }

    public ulong Sequence { get; }

    public ReadOnlyMemory<byte> Pcm16
    {
        get
        {
            if (!IsPcmKind(Kind))
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _lease) is null, this);
            return _pcm16;
        }
    }

    public static AudioEngineEvent CreatePcm(
        IPcmBufferLease lease,
        AudioDirection direction,
        AudioEngineRoute route,
        AudioEngineStatus status,
        uint frameCount,
        ulong sequence)
    {
        ArgumentNullException.ThrowIfNull(lease);
        DomainEnum.ThrowIfUndefined(direction, nameof(direction));
        DomainEnum.ThrowIfUndefined(route, nameof(route));
        DomainEnum.ThrowIfUndefined(status, nameof(status));

        ReadOnlyMemory<byte> pcm16 = lease.Memory;
        if (pcm16.IsEmpty || (pcm16.Length & 1) != 0)
        {
            throw new ArgumentException("PCM16 buffers must contain a non-empty, even number of bytes.", nameof(lease));
        }

        if (frameCount != (uint)(pcm16.Length / sizeof(short)))
        {
            throw new ArgumentException("Frame count must match the mono PCM16 byte count.", nameof(frameCount));
        }

        if (!IsRouteValidForDirection(direction, route))
        {
            throw new ArgumentException("The route is not valid for the PCM direction.", nameof(route));
        }

        AudioEngineEventKind kind = direction switch
        {
            AudioDirection.Inbound => AudioEngineEventKind.InboundPcm16,
            AudioDirection.Outbound => AudioEngineEventKind.OutboundPcm16,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        return new AudioEngineEvent(kind, direction, route, status, frameCount, sequence, lease, pcm16);
    }

    public static AudioEngineEvent CreateControl(
        AudioEngineEventKind kind,
        AudioEngineStatus status,
        AudioEngineRoute route,
        ulong sequence)
    {
        DomainEnum.ThrowIfUndefined(kind, nameof(kind));
        DomainEnum.ThrowIfUndefined(status, nameof(status));
        DomainEnum.ThrowIfUndefined(route, nameof(route));
        if (kind is not AudioEngineEventKind.DeviceChanged
            and not AudioEngineEventKind.StreamError
            and not AudioEngineEventKind.Backpressure)
        {
            throw new ArgumentException("Control events cannot use none or PCM event kinds.", nameof(kind));
        }

        return new AudioEngineEvent(kind, null, route, status, 0, sequence, null, ReadOnlyMemory<byte>.Empty);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    public override string ToString()
    {
        return $"{nameof(AudioEngineEvent)} {{ Kind = {Kind}, Direction = {Direction}, Route = {Route}, Status = {Status}, FrameCount = {FrameCount}, Sequence = {Sequence} }}";
    }

    private static bool IsPcmKind(AudioEngineEventKind kind)
    {
        return kind is AudioEngineEventKind.InboundPcm16 or AudioEngineEventKind.OutboundPcm16;
    }

    private static bool IsRouteValidForDirection(AudioDirection direction, AudioEngineRoute route)
    {
        return direction switch
        {
            AudioDirection.Inbound => route is AudioEngineRoute.Stopped
                or AudioEngineRoute.Translated
                or AudioEngineRoute.OriginalFailOpen
                or AudioEngineRoute.OriginalBypass,
            AudioDirection.Outbound => route is AudioEngineRoute.Stopped
                or AudioEngineRoute.Translated
                or AudioEngineRoute.MutedFailClosed
                or AudioEngineRoute.OriginalBypass,
            _ => false,
        };
    }
}

public sealed record AudioEngineConfiguration
{
    public AudioEngineConfiguration(
        string? inputDeviceId,
        string? outputDeviceId,
        int sampleRate,
        int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);

        InputDeviceId = inputDeviceId;
        OutputDeviceId = outputDeviceId;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }

    public string? InputDeviceId { get; }

    public string? OutputDeviceId { get; }

    public int SampleRate { get; }

    public int ChannelCount { get; }
}

public sealed record AudioDeviceDescriptor
{
    public AudioDeviceDescriptor(
        string id,
        string label,
        AudioDeviceDirection direction,
        bool isDefault,
        bool isAvailable)
    {
        DomainEnum.ThrowIfUndefined(direction, nameof(direction));
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Device ID must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Device label must not be empty.", nameof(label));
        }

        Id = id;
        Label = label;
        Direction = direction;
        IsDefault = isDefault;
        IsAvailable = isAvailable;
    }

    public string Id { get; }

    public string Label { get; }

    public AudioDeviceDirection Direction { get; }

    public bool IsDefault { get; }

    public bool IsAvailable { get; }
}

public sealed record AudioDeviceSnapshot
{
    public AudioDeviceSnapshot(IEnumerable<AudioDeviceDescriptor> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        AudioDeviceDescriptor[] copy = devices.ToArray();
        if (copy.Any(static device => device is null))
        {
            throw new ArgumentException("Devices cannot contain null values.", nameof(devices));
        }

        Devices = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<AudioDeviceDescriptor> Devices { get; }
}

public sealed record RuntimeSettings
{
    public RuntimeSettings(
        LanguageCode sourceLanguage,
        LanguageCode targetLanguage,
        string model,
        bool inboundBypass,
        bool outboundBypass)
    {
        DomainEnum.ThrowIfUndefined(sourceLanguage, nameof(sourceLanguage));
        DomainEnum.ThrowIfUndefined(targetLanguage, nameof(targetLanguage));
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model must not be empty.", nameof(model));
        }

        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        Model = model;
        InboundBypass = inboundBypass;
        OutboundBypass = outboundBypass;
    }

    public LanguageCode SourceLanguage { get; }

    public LanguageCode TargetLanguage { get; }

    public string Model { get; }

    public bool InboundBypass { get; }

    public bool OutboundBypass { get; }
}

public sealed record OnboardingProgress
{
    public OnboardingProgress(bool isCompleted)
    {
        IsCompleted = isCompleted;
    }

    public bool IsCompleted { get; }
}

public interface ITranslationSession
{
    Task ConnectAsync(CancellationToken cancellationToken);

    ValueTask SendPcmAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken);

    IAsyncEnumerable<TranslationSessionEvent> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

public interface ITranslationSessionFactory
{
    ValueTask<ITranslationSession> CreateAsync(
        TranslationSessionConfiguration configuration,
        CancellationToken cancellationToken);
}

public interface ITranslationAudioEngine
{
    Task StartAsync(AudioEngineConfiguration configuration, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    ValueTask<AudioEngineEvent?> PollEventAsync(CancellationToken cancellationToken);

    /// <remarks>
    /// Implementations synchronously copy borrowed PCM16 bytes and never retain
    /// the supplied memory after this call returns.
    /// </remarks>
    ValueTask EnqueueInboundTranslationAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken);

    /// <remarks>
    /// Implementations synchronously copy borrowed PCM16 bytes and never retain
    /// the supplied memory after this call returns.
    /// </remarks>
    ValueTask EnqueueOutboundTranslationAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken);

    ValueTask SetInboundRouteAsync(InboundRoute route, CancellationToken cancellationToken);

    ValueTask SetOutboundRouteAsync(OutboundRoute route, CancellationToken cancellationToken);
}

public interface IAudioDeviceCatalog
{
    Task<AudioDeviceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IAudioDiagnostics
{
    Task<AudioDiagnostics> InspectAsync(CancellationToken cancellationToken);
}

public interface ILanguageClassifier
{
    ValueTask<LanguageProbabilities> ClassifyAsync(string text, CancellationToken cancellationToken);
}

public interface ISecretBuffer : IDisposable
{
    /// <summary>
    /// Gets the secret without converting it to an immutable <see cref="string"/>.
    /// Implementations must zero their owned buffer during <see cref="IDisposable.Dispose"/>.
    /// </summary>
    ReadOnlyMemory<char> Memory { get; }
}

public interface ISecretStore
{
    /// <remarks>
    /// Implementations must not include the secret or secret name in exceptions or logs.
    /// </remarks>
    ValueTask<ISecretBuffer?> LoadAsync(string name, CancellationToken cancellationToken);

    ValueTask SaveAsync(string name, ReadOnlyMemory<char> secret, CancellationToken cancellationToken);

    ValueTask DeleteAsync(string name, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    ValueTask<RuntimeSettings?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken);
}

public interface IOnboardingProgressStore
{
    ValueTask<OnboardingProgress?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(OnboardingProgress progress, CancellationToken cancellationToken);
}

public interface IDriverManager
{
    Task<DriverCompatibility> CheckCompatibilityAsync(CancellationToken cancellationToken);
}

public interface IUpdateService
{
    Task<UpdateAvailability> CheckForUpdatesAsync(CancellationToken cancellationToken);
}

public interface IClock
{
    TimeSpan MonotonicNow { get; }

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IRuntimeLog
{
    /// <summary>
    /// Writes structured, user-safe fields. Fields must not contain secrets, device IDs, or local paths.
    /// </summary>
    void Write(
        RuntimeLogLevel level,
        string eventName,
        IReadOnlyDictionary<string, string> safeFields);
}
