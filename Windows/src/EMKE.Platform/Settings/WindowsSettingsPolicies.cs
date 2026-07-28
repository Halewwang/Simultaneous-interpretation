using System.Text.Json;

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
            ReadOptionalString(root, "outputEndpointId"));
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
