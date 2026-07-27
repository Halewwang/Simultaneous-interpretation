namespace EMKE.Core;

public enum TranslationSessionEventKind
{
    SourceCaption,
    TranslatedCaption,
    Completed,
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

public sealed record TranslationSessionEvent
{
    public TranslationSessionEvent(
        TranslationSessionEventKind kind,
        string text,
        LanguageCode? detectedLanguage,
        bool isFinal)
    {
        Kind = kind;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        DetectedLanguage = detectedLanguage;
        IsFinal = isFinal;
    }

    public TranslationSessionEventKind Kind { get; }

    public string Text { get; }

    public LanguageCode? DetectedLanguage { get; }

    public bool IsFinal { get; }
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

    ValueTask<ReadOnlyMemory<byte>> PollInboundPcmAsync(CancellationToken cancellationToken);

    ValueTask WriteOutboundPcmAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken);
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
    ValueTask<LanguageCode> ClassifyAsync(string text, CancellationToken cancellationToken);
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
