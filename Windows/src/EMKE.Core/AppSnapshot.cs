using System.Text.Json;
using System.Text.Json.Serialization;

namespace EMKE.Core;

[JsonConverter(typeof(RuntimeStateJsonConverter))]
public enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Degraded,
    Failed,
}

[JsonConverter(typeof(ChannelStateJsonConverter))]
public enum ChannelState
{
    Inactive,
    Connecting,
    Connected,
    Reconnecting,
    Bypassed,
    Degraded,
    Failed,
}

[JsonConverter(typeof(InboundRouteJsonConverter))]
public enum InboundRoute
{
    Stopped,
    Translated,
    OriginalFailOpen,
    OriginalBypass,
}

[JsonConverter(typeof(OutboundRouteJsonConverter))]
public enum OutboundRoute
{
    Stopped,
    Translated,
    MutedFailClosed,
    OriginalBypass,
}

public sealed record AudioSelection
{
    public AudioSelection(string inboundLabel, string outboundLabel)
    {
        InboundLabel = inboundLabel ?? throw new ArgumentNullException(nameof(inboundLabel));
        OutboundLabel = outboundLabel ?? throw new ArgumentNullException(nameof(outboundLabel));
    }

    public string InboundLabel { get; }

    public string OutboundLabel { get; }
}

public sealed record DriverCompatibility
{
    public DriverCompatibility(bool isCompatible, string statusLabel)
        : this(
            isCompatible,
            statusLabel,
            updateRecommended: false,
            repairAvailable: !isCompatible)
    {
    }

    public DriverCompatibility(
        bool isCompatible,
        string statusLabel,
        bool updateRecommended,
        bool repairAvailable)
    {
        IsCompatible = isCompatible;
        StatusLabel = statusLabel ?? throw new ArgumentNullException(nameof(statusLabel));
        UpdateRecommended = updateRecommended;
        RepairAvailable = repairAvailable;
    }

    public bool IsCompatible { get; }

    public string StatusLabel { get; }

    public bool UpdateRecommended { get; }

    public bool RepairAvailable { get; }
}

public sealed class TranslationCompatibilityReport : IEquatable<TranslationCompatibilityReport>
{
    public static readonly IReadOnlyList<string> StableStageNames =
        Array.AsReadOnly(
        [
            "authentication",
            "translationWebSocketHandshake",
            "targetLanguageUpdate",
            "dualSessionConcurrency",
            "sourceTranscript",
            "translatedAudio",
            "safeClose",
        ]);

    public TranslationCompatibilityReport(bool isCompatible, IEnumerable<string> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        string[] copy = findings.ToArray();
        if (copy.Any(static finding => finding is null))
        {
            throw new ArgumentException("Findings cannot contain null values.", nameof(findings));
        }

        IsCompatible = isCompatible;
        Findings = Array.AsReadOnly(copy);
        Stages = Array.Empty<TranslationCompatibilityStageResult>();
        Overall = isCompatible
            ? TranslationCompatibilityOverall.Compatible
            : TranslationCompatibilityOverall.Incompatible;
    }

    public TranslationCompatibilityReport(
        IEnumerable<TranslationCompatibilityStageResult> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        TranslationCompatibilityStageResult[] copy = [.. stages];
        if (copy.Length != StableStageNames.Count)
        {
            throw new ArgumentException(
                "A compatibility report must contain all seven stages.",
                nameof(stages));
        }

        for (int index = 0; index < copy.Length; index++)
        {
            if (!string.Equals(
                    copy[index].StableName,
                    StableStageNames[index],
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Compatibility stages must use the stable order.",
                    nameof(stages));
            }
        }

        Stages = Array.AsReadOnly(copy);
        Overall = DetermineOverall(copy);
        IsCompatible = Overall == TranslationCompatibilityOverall.Compatible;
        Findings = Array.AsReadOnly(
            copy.Where(static stage =>
                    stage.Outcome == TranslationCapabilityOutcome.Failed)
                .Select(static stage =>
                    stage.FailureCode ?? stage.StableName)
                .ToArray());
    }

    public bool IsCompatible { get; }

    public IReadOnlyList<string> Findings { get; }

    public IReadOnlyList<TranslationCompatibilityStageResult> Stages { get; }

    public TranslationCompatibilityOverall Overall { get; }

    public TranslationCompatibilityStageResult Stage(string stableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableName);
        return Stages.First(
            stage => string.Equals(
                stage.StableName,
                stableName,
                StringComparison.Ordinal));
    }

    public bool Equals(TranslationCompatibilityReport? other)
    {
        return other is not null
            && IsCompatible == other.IsCompatible
            && Overall == other.Overall
            && Findings.SequenceEqual(other.Findings, StringComparer.Ordinal)
            && Stages.SequenceEqual(other.Stages);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as TranslationCompatibilityReport);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(IsCompatible);
        hash.Add(Overall);
        foreach (string finding in Findings)
        {
            hash.Add(finding, StringComparer.Ordinal);
        }

        foreach (TranslationCompatibilityStageResult stage in Stages)
        {
            hash.Add(stage);
        }

        return hash.ToHashCode();
    }

    private static TranslationCompatibilityOverall DetermineOverall(
        TranslationCompatibilityStageResult[] stages)
    {
        bool protocolPassed = stages
            .Take(4)
            .All(static stage =>
                stage.Outcome == TranslationCapabilityOutcome.Passed);
        bool audioRequiresInteraction = stages[4].Outcome
                == TranslationCapabilityOutcome.RequiresInteractiveAudio
            && stages[5].Outcome
                == TranslationCapabilityOutcome.RequiresInteractiveAudio;
        bool closed = stages[6].Outcome
            == TranslationCapabilityOutcome.Passed;
        return protocolPassed && audioRequiresInteraction && closed
            ? TranslationCompatibilityOverall.ProtocolCompatibleRequiresAudio
            : TranslationCompatibilityOverall.Incompatible;
    }
}

public sealed record AudioDiagnostics
{
    public AudioDiagnostics(bool isHealthy, ulong droppedFrameCount)
    {
        IsHealthy = isHealthy;
        DroppedFrameCount = droppedFrameCount;
    }

    public bool IsHealthy { get; }

    public ulong DroppedFrameCount { get; }
}

public sealed record UpdateAvailability
{
    public UpdateAvailability(bool isAvailable, string versionLabel)
    {
        IsAvailable = isAvailable;
        VersionLabel = versionLabel ?? throw new ArgumentNullException(nameof(versionLabel));
    }

    public bool IsAvailable { get; }

    public string VersionLabel { get; }
}

public sealed record AppSnapshot
{
    public AppSnapshot(
        int contractVersion,
        ulong version,
        RuntimeState runtimeState,
        ChannelState inboundChannelState,
        ChannelState outboundChannelState,
        InboundRoute inboundRoute,
        OutboundRoute outboundRoute,
        double inboundLevel,
        double outboundLevel,
        string sourceCaption,
        string translatedCaption,
        AudioSelection audioSelection,
        DriverCompatibility driverCompatibility,
        TranslationCompatibilityReport? connectionReport,
        AudioDiagnostics audioDiagnostics,
        UpdateAvailability updateAvailability,
        RuntimeError? error)
    {
        if (contractVersion != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(contractVersion), contractVersion, "Only contract version 1 is supported.");
        }

        DomainEnum.ThrowIfUndefined(runtimeState, nameof(runtimeState));
        DomainEnum.ThrowIfUndefined(inboundChannelState, nameof(inboundChannelState));
        DomainEnum.ThrowIfUndefined(outboundChannelState, nameof(outboundChannelState));
        DomainEnum.ThrowIfUndefined(inboundRoute, nameof(inboundRoute));
        DomainEnum.ThrowIfUndefined(outboundRoute, nameof(outboundRoute));

        ContractVersion = contractVersion;
        Version = version;
        RuntimeState = runtimeState;
        InboundChannelState = inboundChannelState;
        OutboundChannelState = outboundChannelState;
        InboundRoute = inboundRoute;
        OutboundRoute = outboundRoute;
        InboundLevel = ClampLevel(inboundLevel, nameof(inboundLevel));
        OutboundLevel = ClampLevel(outboundLevel, nameof(outboundLevel));
        SourceCaption = sourceCaption ?? throw new ArgumentNullException(nameof(sourceCaption));
        TranslatedCaption = translatedCaption ?? throw new ArgumentNullException(nameof(translatedCaption));
        AudioSelection = audioSelection ?? throw new ArgumentNullException(nameof(audioSelection));
        DriverCompatibility = driverCompatibility ?? throw new ArgumentNullException(nameof(driverCompatibility));
        ConnectionReport = connectionReport;
        AudioDiagnostics = audioDiagnostics ?? throw new ArgumentNullException(nameof(audioDiagnostics));
        UpdateAvailability = updateAvailability ?? throw new ArgumentNullException(nameof(updateAvailability));
        Error = error;
    }

    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; }

    [JsonPropertyName("version")]
    public ulong Version { get; }

    [JsonPropertyName("runtimeState")]
    public RuntimeState RuntimeState { get; }

    [JsonPropertyName("inboundChannelState")]
    public ChannelState InboundChannelState { get; }

    [JsonPropertyName("outboundChannelState")]
    public ChannelState OutboundChannelState { get; }

    [JsonPropertyName("inboundRoute")]
    public InboundRoute InboundRoute { get; }

    [JsonPropertyName("outboundRoute")]
    public OutboundRoute OutboundRoute { get; }

    [JsonPropertyName("inboundLevel")]
    public double InboundLevel { get; }

    [JsonPropertyName("outboundLevel")]
    public double OutboundLevel { get; }

    [JsonPropertyName("sourceCaption")]
    public string SourceCaption { get; }

    [JsonPropertyName("translatedCaption")]
    public string TranslatedCaption { get; }

    [JsonIgnore]
    public AudioSelection AudioSelection { get; }

    [JsonIgnore]
    public DriverCompatibility DriverCompatibility { get; }

    [JsonIgnore]
    public TranslationCompatibilityReport? ConnectionReport { get; }

    [JsonIgnore]
    public AudioDiagnostics AudioDiagnostics { get; }

    [JsonIgnore]
    public UpdateAvailability UpdateAvailability { get; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeError? Error { get; }

    public AppSnapshot WithNextVersion()
    {
        return Next(
            RuntimeState,
            InboundChannelState,
            OutboundChannelState,
            InboundRoute,
            OutboundRoute,
            InboundLevel,
            OutboundLevel,
            SourceCaption,
            TranslatedCaption,
            AudioSelection,
            DriverCompatibility,
            ConnectionReport,
            AudioDiagnostics,
            UpdateAvailability,
            Error);
    }

    public AppSnapshot Next(
        RuntimeState runtimeState,
        ChannelState inboundChannelState,
        ChannelState outboundChannelState,
        InboundRoute inboundRoute,
        OutboundRoute outboundRoute,
        double inboundLevel,
        double outboundLevel,
        string sourceCaption,
        string translatedCaption,
        AudioSelection audioSelection,
        DriverCompatibility driverCompatibility,
        TranslationCompatibilityReport? connectionReport,
        AudioDiagnostics audioDiagnostics,
        UpdateAvailability updateAvailability,
        RuntimeError? error)
    {
        ulong nextVersion = checked(Version + 1UL);
        return new AppSnapshot(
            ContractVersion,
            nextVersion,
            runtimeState,
            inboundChannelState,
            outboundChannelState,
            inboundRoute,
            outboundRoute,
            inboundLevel,
            outboundLevel,
            sourceCaption,
            translatedCaption,
            audioSelection,
            driverCompatibility,
            connectionReport,
            audioDiagnostics,
            updateAvailability,
            error);
    }

    private static double ClampLevel(double level, string parameterName)
    {
        if (!double.IsFinite(level))
        {
            throw new ArgumentOutOfRangeException(parameterName, level, "Audio levels must be finite.");
        }

        return Math.Clamp(level, 0, 1);
    }
}

public sealed class RuntimeStateJsonConverter : JsonConverter<RuntimeState>
{
    public override RuntimeState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("RuntimeState must be a string.");
        }

        return reader.GetString() switch
        {
            "stopped" => RuntimeState.Stopped,
            "starting" => RuntimeState.Starting,
            "running" => RuntimeState.Running,
            "stopping" => RuntimeState.Stopping,
            "degraded" => RuntimeState.Degraded,
            "failed" => RuntimeState.Failed,
            _ => throw new JsonException("Unknown RuntimeState value."),
        };
    }

    public override void Write(Utf8JsonWriter writer, RuntimeState value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string stableValue = value switch
        {
            RuntimeState.Stopped => "stopped",
            RuntimeState.Starting => "starting",
            RuntimeState.Running => "running",
            RuntimeState.Stopping => "stopping",
            RuntimeState.Degraded => "degraded",
            RuntimeState.Failed => "failed",
            _ => throw new JsonException("Undefined RuntimeState value."),
        };
        writer.WriteStringValue(stableValue);
    }
}

public sealed class ChannelStateJsonConverter : JsonConverter<ChannelState>
{
    public override ChannelState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("ChannelState must be a string.");
        }

        return reader.GetString() switch
        {
            "inactive" => ChannelState.Inactive,
            "connecting" => ChannelState.Connecting,
            "connected" => ChannelState.Connected,
            "reconnecting" => ChannelState.Reconnecting,
            "bypassed" => ChannelState.Bypassed,
            "degraded" => ChannelState.Degraded,
            "failed" => ChannelState.Failed,
            _ => throw new JsonException("Unknown ChannelState value."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ChannelState value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string stableValue = value switch
        {
            ChannelState.Inactive => "inactive",
            ChannelState.Connecting => "connecting",
            ChannelState.Connected => "connected",
            ChannelState.Reconnecting => "reconnecting",
            ChannelState.Bypassed => "bypassed",
            ChannelState.Degraded => "degraded",
            ChannelState.Failed => "failed",
            _ => throw new JsonException("Undefined ChannelState value."),
        };
        writer.WriteStringValue(stableValue);
    }
}

public sealed class InboundRouteJsonConverter : JsonConverter<InboundRoute>
{
    public override InboundRoute Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("InboundRoute must be a string.");
        }

        return reader.GetString() switch
        {
            "stopped" => InboundRoute.Stopped,
            "translated" => InboundRoute.Translated,
            "originalFailOpen" => InboundRoute.OriginalFailOpen,
            "originalBypass" => InboundRoute.OriginalBypass,
            _ => throw new JsonException("Unknown InboundRoute value."),
        };
    }

    public override void Write(Utf8JsonWriter writer, InboundRoute value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string stableValue = value switch
        {
            InboundRoute.Stopped => "stopped",
            InboundRoute.Translated => "translated",
            InboundRoute.OriginalFailOpen => "originalFailOpen",
            InboundRoute.OriginalBypass => "originalBypass",
            _ => throw new JsonException("Undefined InboundRoute value."),
        };
        writer.WriteStringValue(stableValue);
    }
}

public sealed class OutboundRouteJsonConverter : JsonConverter<OutboundRoute>
{
    public override OutboundRoute Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("OutboundRoute must be a string.");
        }

        return reader.GetString() switch
        {
            "stopped" => OutboundRoute.Stopped,
            "translated" => OutboundRoute.Translated,
            "mutedFailClosed" => OutboundRoute.MutedFailClosed,
            "originalBypass" => OutboundRoute.OriginalBypass,
            _ => throw new JsonException("Unknown OutboundRoute value."),
        };
    }

    public override void Write(Utf8JsonWriter writer, OutboundRoute value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string stableValue = value switch
        {
            OutboundRoute.Stopped => "stopped",
            OutboundRoute.Translated => "translated",
            OutboundRoute.MutedFailClosed => "mutedFailClosed",
            OutboundRoute.OriginalBypass => "originalBypass",
            _ => throw new JsonException("Undefined OutboundRoute value."),
        };
        writer.WriteStringValue(stableValue);
    }
}
