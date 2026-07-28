using System.Text.Json;
using System.Text.Json.Serialization;
using EMKE.Core;

namespace EMKE.Platform.Settings;

internal sealed record WindowsSettingsDocument(
    int SchemaVersion,
    string BaseUrl,
    string ModelId,
    string NativeLanguage,
    string MeetingLanguage,
    string InterfaceLanguage,
    string? InputEndpointId,
    string? OutputEndpointId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InboundBypass { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OutboundBypass { get; init; }

    public static WindowsSettingsDocument SafeDefaults { get; } = new(
        1,
        "https://api.302.ai",
        "gpt-realtime-translate",
        "zh",
        "en",
        "system",
        null,
        null);
}

internal sealed record WindowsSettingsMigrationResult(
    string Outcome,
    bool Overwrite,
    bool Quarantine,
    WindowsSettingsDocument Settings);

internal static class WindowsSettingsMigrationPolicy
{
    public static WindowsSettingsMigrationResult Migrate(string persistedJson)
    {
        ArgumentNullException.ThrowIfNull(persistedJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(persistedJson);
        }
        catch (JsonException)
        {
            return new WindowsSettingsMigrationResult(
                "quarantined",
                Overwrite: false,
                Quarantine: true,
                WindowsSettingsDocument.SafeDefaults);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new WindowsSettingsMigrationResult(
                    "quarantined",
                    Overwrite: false,
                    Quarantine: true,
                    WindowsSettingsDocument.SafeDefaults);
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion))
            {
                return new WindowsSettingsMigrationResult(
                    "migrated",
                    Overwrite: true,
                    Quarantine: false,
                    WindowsSettingsDocument.SafeDefaults);
            }

            if (schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out int version)
                || version != WindowsSettingsDocument.SafeDefaults.SchemaVersion)
            {
                return new WindowsSettingsMigrationResult(
                    "unsupported",
                    Overwrite: false,
                    Quarantine: false,
                    WindowsSettingsDocument.SafeDefaults);
            }

            try
            {
                return new WindowsSettingsMigrationResult(
                    "identity",
                    Overwrite: false,
                    Quarantine: false,
                    ParseVersionOne(root));
            }
            catch (InvalidDataException)
            {
                return new WindowsSettingsMigrationResult(
                    "quarantined",
                    Overwrite: false,
                    Quarantine: true,
                    WindowsSettingsDocument.SafeDefaults);
            }
        }
    }

    private static WindowsSettingsDocument ParseVersionOne(JsonElement root)
    {
        return new WindowsSettingsDocument(
            ReadRequiredInt32(root, "schemaVersion"),
            ReadRequiredString(root, "baseUrl"),
            ReadRequiredString(root, "modelId"),
            ReadRequiredString(root, "nativeLanguage"),
            ReadRequiredString(root, "meetingLanguage"),
            ReadRequiredString(root, "interfaceLanguage"),
            ReadOptionalString(root, "inputEndpointId"),
            ReadOptionalString(root, "outputEndpointId"))
        {
            InboundBypass = ReadOptionalBoolean(root, "inboundBypass"),
            OutboundBypass = ReadOptionalBoolean(root, "outboundBypass"),
        };
    }

    private static int ReadRequiredInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result))
        {
            throw new InvalidDataException($"Settings field {name} must be an integer.");
        }

        return result;
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Settings field {name} must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidDataException($"Settings field {name} is required.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Settings field {name} must be null or a non-empty string.");
        }

        return value.GetString();
    }

    private static bool ReadOptionalBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Settings field {name} must be a boolean.");
        }

        return value.GetBoolean();
    }
}

public interface IWindowsSettingsPersistence
{
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken);

    ValueTask OverwriteAsync(
        string canonicalJson,
        CancellationToken cancellationToken);

    ValueTask QuarantineAsync(
        string invalidJson,
        CancellationToken cancellationToken);
}

internal sealed record WindowsSettingsMigrationObservation(
    string Outcome,
    bool Overwrite,
    bool Quarantine,
    WindowsSettingsDocument Settings);

internal interface IWindowsSettingsMigrationDiagnostics
{
    void Record(WindowsSettingsMigrationObservation observation);
}

internal sealed class NullWindowsSettingsMigrationDiagnostics
    : IWindowsSettingsMigrationDiagnostics
{
    public static NullWindowsSettingsMigrationDiagnostics Instance { get; } = new();

    private NullWindowsSettingsMigrationDiagnostics()
    {
    }

    public void Record(WindowsSettingsMigrationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
    }
}

public sealed class WindowsSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IWindowsSettingsPersistence _persistence;
    private readonly IWindowsSettingsMigrationDiagnostics _diagnostics;
    private WindowsSettingsDocument _currentDocument =
        WindowsSettingsDocument.SafeDefaults;

    public WindowsSettingsStore(IWindowsSettingsPersistence persistence)
        : this(persistence, NullWindowsSettingsMigrationDiagnostics.Instance)
    {
    }

    internal WindowsSettingsStore(
        IWindowsSettingsPersistence persistence,
        IWindowsSettingsMigrationDiagnostics diagnostics)
    {
        _persistence =
            persistence ?? throw new ArgumentNullException(nameof(persistence));
        _diagnostics =
            diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public async ValueTask<RuntimeSettings?> LoadAsync(
        CancellationToken cancellationToken)
    {
        string? persistedJson =
            await _persistence.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (persistedJson is null)
        {
            return null;
        }

        WindowsSettingsMigrationResult migration =
            WindowsSettingsMigrationPolicy.Migrate(persistedJson);
        if (migration.Quarantine)
        {
            await _persistence.QuarantineAsync(
                persistedJson,
                cancellationToken).ConfigureAwait(false);
        }
        if (migration.Overwrite)
        {
            await _persistence.OverwriteAsync(
                Serialize(migration.Settings),
                cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref _currentDocument, migration.Settings);
        _diagnostics.Record(new WindowsSettingsMigrationObservation(
            migration.Outcome,
            migration.Overwrite,
            migration.Quarantine,
            migration.Settings));
        return ToRuntimeSettings(migration.Settings);
    }

    public async ValueTask SaveAsync(
        RuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        WindowsSettingsDocument current = Volatile.Read(ref _currentDocument);
        WindowsSettingsDocument updated = current with
        {
            ModelId = settings.Model,
            NativeLanguage = SerializeLanguage(settings.SourceLanguage),
            MeetingLanguage = SerializeLanguage(settings.TargetLanguage),
            InboundBypass = settings.InboundBypass,
            OutboundBypass = settings.OutboundBypass,
        };
        await _persistence.OverwriteAsync(
            Serialize(updated),
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _currentDocument, updated);
    }

    private static string Serialize(WindowsSettingsDocument settings)
    {
        return JsonSerializer.Serialize(settings, SerializerOptions);
    }

    private static RuntimeSettings ToRuntimeSettings(WindowsSettingsDocument settings)
    {
        return new RuntimeSettings(
            ParseLanguage(settings.NativeLanguage),
            ParseLanguage(settings.MeetingLanguage),
            settings.ModelId,
            settings.InboundBypass,
            settings.OutboundBypass);
    }

    private static LanguageCode ParseLanguage(string stableValue)
    {
        return stableValue switch
        {
            "zh" => LanguageCode.Zh,
            "en" => LanguageCode.En,
            "de" => LanguageCode.De,
            _ => throw new InvalidDataException(
                $"Unsupported settings language: {stableValue}"),
        };
    }

    private static string SerializeLanguage(LanguageCode language)
    {
        return language switch
        {
            LanguageCode.Zh => "zh",
            LanguageCode.En => "en",
            LanguageCode.De => "de",
            _ => throw new ArgumentOutOfRangeException(
                nameof(language),
                language,
                "Undefined language."),
        };
    }
}

internal sealed record InstalledDriverSnapshot(
    bool Present,
    bool SignatureValid,
    int Abi,
    Version Version,
    int EndpointCount);

internal sealed record DriverCompatibilityRequirements(
    int RequiredAbi,
    Version MinimumVersion,
    Version RecommendedVersion,
    int RequiredEndpointCount);

internal sealed record DriverCompatibilityDecision(
    bool Allowed,
    string Reason,
    bool UpdateRecommended);

internal static class WindowsDriverCompatibilityPolicy
{
    public static DriverCompatibilityDecision Evaluate(
        InstalledDriverSnapshot installed,
        DriverCompatibilityRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(requirements);

        if (!installed.Present)
        {
            return Deny("driverMissing");
        }

        if (!installed.SignatureValid)
        {
            return Deny("driverSignatureInvalid");
        }

        if (installed.Abi != requirements.RequiredAbi)
        {
            return Deny("driverAbiMismatch");
        }

        if (installed.EndpointCount < requirements.RequiredEndpointCount)
        {
            return Deny("virtualEndpointsIncomplete");
        }

        if (installed.Version < requirements.MinimumVersion)
        {
            return Deny("driverVersionUnsupported");
        }

        if (installed.Version < requirements.RecommendedVersion)
        {
            return new DriverCompatibilityDecision(
                Allowed: true,
                "compatibleUpdateRecommended",
                UpdateRecommended: true);
        }

        return new DriverCompatibilityDecision(
            Allowed: true,
            "compatible",
            UpdateRecommended: false);
    }

    private static DriverCompatibilityDecision Deny(string reason)
    {
        return new DriverCompatibilityDecision(
            Allowed: false,
            reason,
            UpdateRecommended: true);
    }
}

public sealed record WindowsInstalledDriverSnapshot
{
    public WindowsInstalledDriverSnapshot(
        bool present,
        bool signatureValid,
        int abi,
        Version version,
        int endpointCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(abi);
        ArgumentOutOfRangeException.ThrowIfNegative(endpointCount);

        Present = present;
        SignatureValid = signatureValid;
        Abi = abi;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        EndpointCount = endpointCount;
    }

    public bool Present { get; }

    public bool SignatureValid { get; }

    public int Abi { get; }

    public Version Version { get; }

    public int EndpointCount { get; }
}

public interface IWindowsDriverSnapshotSource
{
    ValueTask<WindowsInstalledDriverSnapshot> ReadAsync(
        CancellationToken cancellationToken);
}

public sealed record WindowsDriverCompatibilityOptions
{
    public WindowsDriverCompatibilityOptions(
        int requiredAbi,
        Version minimumVersion,
        Version recommendedVersion,
        int requiredEndpointCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredAbi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredEndpointCount);

        RequiredAbi = requiredAbi;
        MinimumVersion =
            minimumVersion ?? throw new ArgumentNullException(nameof(minimumVersion));
        RecommendedVersion =
            recommendedVersion ?? throw new ArgumentNullException(nameof(recommendedVersion));
        if (RecommendedVersion < MinimumVersion)
        {
            throw new ArgumentException(
                "Recommended driver version must not precede the minimum version.",
                nameof(recommendedVersion));
        }

        RequiredEndpointCount = requiredEndpointCount;
    }

    public int RequiredAbi { get; }

    public Version MinimumVersion { get; }

    public Version RecommendedVersion { get; }

    public int RequiredEndpointCount { get; }
}

internal sealed record WindowsDriverCompatibilityObservation(
    bool Allowed,
    string Reason,
    bool UpdateRecommended);

internal interface IWindowsDriverCompatibilityDiagnostics
{
    void Record(WindowsDriverCompatibilityObservation observation);
}

internal sealed class NullWindowsDriverCompatibilityDiagnostics
    : IWindowsDriverCompatibilityDiagnostics
{
    public static NullWindowsDriverCompatibilityDiagnostics Instance { get; } = new();

    private NullWindowsDriverCompatibilityDiagnostics()
    {
    }

    public void Record(WindowsDriverCompatibilityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
    }
}

public sealed class WindowsDriverManager : IDriverManager
{
    private readonly IWindowsDriverSnapshotSource _snapshotSource;
    private readonly WindowsDriverCompatibilityOptions _options;
    private readonly IWindowsDriverCompatibilityDiagnostics _diagnostics;

    public WindowsDriverManager(
        IWindowsDriverSnapshotSource snapshotSource,
        WindowsDriverCompatibilityOptions options)
        : this(
            snapshotSource,
            options,
            NullWindowsDriverCompatibilityDiagnostics.Instance)
    {
    }

    internal WindowsDriverManager(
        IWindowsDriverSnapshotSource snapshotSource,
        WindowsDriverCompatibilityOptions options,
        IWindowsDriverCompatibilityDiagnostics diagnostics)
    {
        _snapshotSource =
            snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _diagnostics =
            diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public async Task<DriverCompatibility> CheckCompatibilityAsync(
        CancellationToken cancellationToken)
    {
        WindowsInstalledDriverSnapshot snapshot =
            await _snapshotSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        DriverCompatibilityDecision decision =
            WindowsDriverCompatibilityPolicy.Evaluate(
                new InstalledDriverSnapshot(
                    snapshot.Present,
                    snapshot.SignatureValid,
                    snapshot.Abi,
                    snapshot.Version,
                    snapshot.EndpointCount),
                new DriverCompatibilityRequirements(
                    _options.RequiredAbi,
                    _options.MinimumVersion,
                    _options.RecommendedVersion,
                    _options.RequiredEndpointCount));
        _diagnostics.Record(new WindowsDriverCompatibilityObservation(
            decision.Allowed,
            decision.Reason,
            decision.UpdateRecommended));
        return new DriverCompatibility(decision.Allowed, decision.Reason);
    }
}
