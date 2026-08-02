using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EMKE.Core;

[JsonConverter(typeof(ErrorCategoryJsonConverter))]
public enum ErrorCategory
{
    Configuration,
    Permission,
    Driver,
    Device,
    Authentication,
    EndpointModel,
    Protocol,
    Network,
    Backpressure,
    CloseTimeout,
}

[JsonConverter(typeof(RecoveryActionJsonConverter))]
public enum RecoveryAction
{
    None,
    EditSettings,
    OpenPrivacySettings,
    InstallDriver,
    SelectDevice,
    UpdateApiKey,
    Retry,
    ReportCompatibility,
}

public sealed class RuntimeError : IEquatable<RuntimeError>
{
    private static readonly HashSet<string> ReservedParameterKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "authorization",
            "apiKey",
            "token",
        };

    private static readonly HashSet<string> EndpointRoles =
        new(StringComparer.Ordinal)
        {
            "physicalInput",
            "physicalOutput",
            "meetingSpeakerRender",
            "appSpeakerCapture",
            "appMicrophoneRender",
            "meetingMicrophoneCapture",
        };

    private static readonly Regex ApiKeyPattern =
        new(
            @"sk-[A-Za-z0-9_-]{16,}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RuntimeError(
        ErrorCategory category,
        string code,
        IReadOnlyDictionary<string, string> parameters,
        RecoveryAction recoveryAction)
    {
        DomainEnum.ThrowIfUndefined(category, nameof(category));
        DomainEnum.ThrowIfUndefined(recoveryAction, nameof(recoveryAction));
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Error code must not be empty.", nameof(code));
        }

        if (ApiKeyPattern.IsMatch(code))
        {
            throw new ArgumentException("Error code must not contain an API key.", nameof(code));
        }

        ArgumentNullException.ThrowIfNull(parameters);
        Dictionary<string, string> copy = new(StringComparer.Ordinal);
        foreach ((string key, string value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Parameter keys must not be empty.", nameof(parameters));
            }

            if (value is null)
            {
                throw new ArgumentNullException(nameof(parameters), "Parameter values must not be null.");
            }

            if (ReservedParameterKeys.Contains(key)
                || !IsStructuredDiagnosticParameter(key, value))
            {
                throw new ArgumentException(
                    "Only structured diagnostic parameters are allowed.",
                    nameof(parameters));
            }

            if (ApiKeyPattern.IsMatch(value))
            {
                throw new ArgumentException("Parameter values must not contain an API key.", nameof(parameters));
            }

            copy.Add(key, value);
        }

        Category = category;
        Code = code;
        Parameters = new ReadOnlyDictionary<string, string>(copy);
        RecoveryAction = recoveryAction;
    }

    private static bool IsStructuredDiagnosticParameter(
        string key,
        string value)
    {
        return key switch
        {
            "build" or "retryCount" or "duration" =>
                ulong.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _),
            "driverVersion" => Version.TryParse(value, out _),
            "endpointRole" => EndpointRoles.Contains(value),
            _ => false,
        };
    }

    [JsonPropertyName("category")]
    public ErrorCategory Category { get; }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string> Parameters { get; }

    [JsonPropertyName("recoveryAction")]
    public RecoveryAction RecoveryAction { get; }

    public bool Equals(RuntimeError? other)
    {
        if (other is null
            || Category != other.Category
            || RecoveryAction != other.RecoveryAction
            || !string.Equals(Code, other.Code, StringComparison.Ordinal)
            || Parameters.Count != other.Parameters.Count)
        {
            return false;
        }

        foreach ((string key, string value) in Parameters)
        {
            if (!other.Parameters.TryGetValue(key, out string? otherValue)
                || !string.Equals(value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as RuntimeError);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Category);
        hash.Add(Code, StringComparer.Ordinal);
        hash.Add(RecoveryAction);
        foreach ((string key, string value) in Parameters.OrderBy(
                     static pair => pair.Key,
                     StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

public sealed class ErrorCategoryJsonConverter : JsonConverter<ErrorCategory>
{
    public override ErrorCategory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("ErrorCategory must be a string.");
        }

        return reader.GetString() switch
        {
            "configuration" => ErrorCategory.Configuration,
            "permission" => ErrorCategory.Permission,
            "driver" => ErrorCategory.Driver,
            "device" => ErrorCategory.Device,
            "authentication" => ErrorCategory.Authentication,
            "endpointModel" => ErrorCategory.EndpointModel,
            "protocol" => ErrorCategory.Protocol,
            "network" => ErrorCategory.Network,
            "backpressure" => ErrorCategory.Backpressure,
            "closeTimeout" => ErrorCategory.CloseTimeout,
            _ => throw new JsonException("Unknown ErrorCategory value."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ErrorCategory value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string stableValue = value switch
        {
            ErrorCategory.Configuration => "configuration",
            ErrorCategory.Permission => "permission",
            ErrorCategory.Driver => "driver",
            ErrorCategory.Device => "device",
            ErrorCategory.Authentication => "authentication",
            ErrorCategory.EndpointModel => "endpointModel",
            ErrorCategory.Protocol => "protocol",
            ErrorCategory.Network => "network",
            ErrorCategory.Backpressure => "backpressure",
            ErrorCategory.CloseTimeout => "closeTimeout",
            _ => throw new JsonException("Undefined ErrorCategory value."),
        };
        writer.WriteStringValue(stableValue);
    }
}

public sealed class RecoveryActionJsonConverter : JsonConverter<RecoveryAction>
{
    public override RecoveryAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("RecoveryAction must be a string.");
        }

        return reader.GetString() switch
        {
            "none" => RecoveryAction.None,
            "editSettings" => RecoveryAction.EditSettings,
            "openPrivacySettings" => RecoveryAction.OpenPrivacySettings,
            "installDriver" => RecoveryAction.InstallDriver,
            "selectDevice" => RecoveryAction.SelectDevice,
            "updateApiKey" => RecoveryAction.UpdateApiKey,
            "retry" => RecoveryAction.Retry,
            "reportCompatibility" => RecoveryAction.ReportCompatibility,
            _ => throw new JsonException("Unknown RecoveryAction value."),
        };
    }

    public override void Write(Utf8JsonWriter writer, RecoveryAction value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string stableValue = value switch
        {
            RecoveryAction.None => "none",
            RecoveryAction.EditSettings => "editSettings",
            RecoveryAction.OpenPrivacySettings => "openPrivacySettings",
            RecoveryAction.InstallDriver => "installDriver",
            RecoveryAction.SelectDevice => "selectDevice",
            RecoveryAction.UpdateApiKey => "updateApiKey",
            RecoveryAction.Retry => "retry",
            RecoveryAction.ReportCompatibility => "reportCompatibility",
            _ => throw new JsonException("Undefined RecoveryAction value."),
        };
        writer.WriteStringValue(stableValue);
    }
}
