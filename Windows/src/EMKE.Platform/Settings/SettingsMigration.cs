using System.Text.Json;
using EMKE.Core;

namespace EMKE.Platform.Settings;

internal sealed record WindowsSettingsDocument(
    int SchemaVersion,
    string BaseUrl,
    string ModelId,
    string NativeLanguage,
    string MeetingLanguage,
    string? InputEndpointId,
    string? OutputEndpointId,
    bool FollowDefaultInput,
    bool FollowDefaultOutput,
    string InterfaceLanguage,
    string[] OnboardingPreferenceIdentifiers)
{
    public static WindowsSettingsDocument SafeDefaults { get; } = new(
        1,
        "https://api.302.ai",
        "gpt-realtime-translate",
        "zh",
        "en",
        null,
        null,
        FollowDefaultInput: true,
        FollowDefaultOutput: true,
        "system",
        []);
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
            return Quarantined();
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Quarantined();
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
                return Quarantined();
            }
        }
    }

    private static WindowsSettingsMigrationResult Quarantined()
    {
        return new WindowsSettingsMigrationResult(
            "quarantined",
            Overwrite: false,
            Quarantine: true,
            WindowsSettingsDocument.SafeDefaults);
    }

    private static WindowsSettingsDocument ParseVersionOne(JsonElement root)
    {
        return new WindowsSettingsDocument(
            ReadRequiredInt32(root, "schemaVersion"),
            ReadRequiredString(root, "baseUrl"),
            ReadRequiredString(root, "modelId"),
            ReadRequiredString(root, "nativeLanguage"),
            ReadRequiredString(root, "meetingLanguage"),
            ReadOptionalString(root, "inputEndpointId"),
            ReadOptionalString(root, "outputEndpointId"),
            ReadOptionalBoolean(root, "followDefaultInput", defaultValue: true),
            ReadOptionalBoolean(root, "followDefaultOutput", defaultValue: true),
            ReadRequiredString(root, "interfaceLanguage"),
            ReadOptionalStringArray(root, "onboardingPreferenceIdentifiers"));
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
            throw new InvalidDataException(
                $"Settings field {name} must be a non-empty string.");
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

    private static bool ReadOptionalBoolean(
        JsonElement root,
        string name,
        bool defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Settings field {name} must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static string[] ReadOptionalStringArray(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Settings field {name} must be an array.");
        }

        List<string> values = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException(
                    $"Settings field {name} must contain non-empty strings.");
            }

            values.Add(item.GetString()!);
        }

        return [.. values.Distinct(StringComparer.Ordinal)];
    }
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
