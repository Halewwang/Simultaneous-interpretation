using System.Reflection;
using System.Text.Json;

namespace EMKE.Core;

public sealed record CompatibilityManifest
{
    private const int InternalMinimumWindowsBuild = 26200;
    private const int InternalRequiredEndpointRoleCount = 4;

    public CompatibilityManifest(
        Version appVersion,
        int contractVersion,
        int settingsSchemaVersion,
        int driverAbiVersion,
        Version minimumDriverVersion,
        Version recommendedDriverVersion,
        bool driverPackageAvailable,
        string channel,
        int minimumWindowsBuild,
        int requiredEndpointRoleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contractVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settingsSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(driverAbiVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumWindowsBuild);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            requiredEndpointRoleCount);

        AppVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        ContractVersion = contractVersion;
        SettingsSchemaVersion = settingsSchemaVersion;
        DriverAbiVersion = driverAbiVersion;
        MinimumDriverVersion = minimumDriverVersion
            ?? throw new ArgumentNullException(nameof(minimumDriverVersion));
        RecommendedDriverVersion = recommendedDriverVersion
            ?? throw new ArgumentNullException(nameof(recommendedDriverVersion));
        if (RecommendedDriverVersion < MinimumDriverVersion)
        {
            throw new ArgumentException(
                "Recommended driver version must not precede the minimum version.",
                nameof(recommendedDriverVersion));
        }

        DriverPackageAvailable = driverPackageAvailable;
        Channel = string.IsNullOrWhiteSpace(channel)
            ? throw new ArgumentException(
                "The compatibility channel is required.",
                nameof(channel))
            : channel;
        MinimumWindowsBuild = minimumWindowsBuild;
        RequiredEndpointRoleCount = requiredEndpointRoleCount;
    }

    public Version AppVersion { get; }

    public int ContractVersion { get; }

    public int SettingsSchemaVersion { get; }

    public int DriverAbiVersion { get; }

    public Version MinimumDriverVersion { get; }

    public Version RecommendedDriverVersion { get; }

    public bool DriverPackageAvailable { get; }

    public string Channel { get; }

    public int MinimumWindowsBuild { get; }

    public int RequiredEndpointRoleCount { get; }

    public static CompatibilityManifest ParseInternalJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Compatibility metadata must be a JSON object.");
            }

            bool driverPackageAvailable =
                ReadRequiredBoolean(root, "driverPackageAvailable");
            if (!driverPackageAvailable
                && (root.TryGetProperty("driverPackageSha256", out _)
                    || root.TryGetProperty("driverPackageUrl", out _)))
            {
                throw new InvalidDataException(
                    "Unavailable driver packages cannot declare a hash or URL.");
            }

            string channel = ReadRequiredString(root, "channel");
            if (!string.Equals(channel, "internal", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Internal compatibility metadata must use the internal channel.");
            }

            return new CompatibilityManifest(
                ParseVersion(root, "appVersion"),
                ReadRequiredPositiveInt32(root, "contractVersion"),
                ReadRequiredPositiveInt32(root, "settingsSchemaVersion"),
                ReadRequiredPositiveInt32(root, "driverAbiVersion"),
                ParseVersion(root, "minimumDriverVersion"),
                ParseVersion(root, "recommendedDriverVersion"),
                driverPackageAvailable,
                channel,
                InternalMinimumWindowsBuild,
                InternalRequiredEndpointRoleCount);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Compatibility metadata is not valid JSON.",
                exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Compatibility metadata contains an invalid version.",
                exception);
        }
    }

    public static CompatibilityManifest LoadEmbedded(
        Assembly assembly,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new ArgumentException(
                "The compatibility resource name is required.",
                nameof(resourceName));
        }

        using Stream resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException(
                $"Required compatibility resource {resourceName} is missing.");
        using StreamReader reader = new(resource);
        return ParseInternalJson(reader.ReadToEnd());
    }

    private static string ReadRequiredString(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Compatibility field {propertyName} must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static bool ReadRequiredBoolean(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Compatibility field {propertyName} must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static int ReadRequiredPositiveInt32(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result)
            || result <= 0)
        {
            throw new InvalidDataException(
                $"Compatibility field {propertyName} must be a positive integer.");
        }

        return result;
    }

    private static Version ParseVersion(
        JsonElement root,
        string propertyName)
    {
        return Version.Parse(ReadRequiredString(root, propertyName));
    }
}

public sealed record InstalledDriverEndpointEvidence
{
    public InstalledDriverEndpointEvidence(string role, string state)
    {
        Role = string.IsNullOrWhiteSpace(role)
            ? throw new ArgumentException(
                "The endpoint role is required.",
                nameof(role))
            : role;
        State = string.IsNullOrWhiteSpace(state)
            ? throw new ArgumentException(
                "The endpoint state is required.",
                nameof(state))
            : state;
    }

    public string Role { get; }

    public string State { get; }
}

public sealed record InstalledDriverEvidence
{
    public InstalledDriverEvidence(
        bool present,
        string? rootDevnodeHardwareId,
        Version driverFileVersion,
        int driverAbiProperty,
        string? catalogSigner,
        bool catalogChainValid,
        IEnumerable<InstalledDriverEndpointEvidence> endpointStates)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(driverAbiProperty);
        ArgumentNullException.ThrowIfNull(endpointStates);

        InstalledDriverEndpointEvidence[] endpoints = endpointStates.ToArray();
        if (endpoints.Any(static endpoint => endpoint is null))
        {
            throw new ArgumentException(
                "Endpoint evidence cannot contain null values.",
                nameof(endpointStates));
        }

        Present = present;
        RootDevnodeHardwareId = rootDevnodeHardwareId;
        DriverFileVersion = driverFileVersion
            ?? throw new ArgumentNullException(nameof(driverFileVersion));
        DriverAbiProperty = driverAbiProperty;
        CatalogSigner = catalogSigner;
        CatalogChainValid = catalogChainValid;
        EndpointStates = Array.AsReadOnly(endpoints);
    }

    public bool Present { get; }

    public string? RootDevnodeHardwareId { get; }

    public Version DriverFileVersion { get; }

    public int DriverAbiProperty { get; }

    public string? CatalogSigner { get; }

    public bool CatalogChainValid { get; }

    public IReadOnlyList<InstalledDriverEndpointEvidence> EndpointStates { get; }
}
