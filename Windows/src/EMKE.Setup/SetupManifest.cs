using System.Runtime.InteropServices;

namespace EMKE.Setup;

public enum SetupPayloadKind
{
    Msix,
    Certificate,
    DriverInf,
    DriverSys,
    DriverCatalog,
}

public sealed record SetupPayload
{
    public SetupPayload(
        string logicalName,
        string fileName,
        long length,
        string sha256,
        SetupPayloadKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (!IsSafeLeafName(logicalName))
        {
            throw new ArgumentException(
                "The payload logical name must be a safe leaf name.",
                nameof(logicalName));
        }
        if (!IsSafeLeafName(fileName))
        {
            throw new ArgumentException(
                "The payload file name must be a safe leaf name.",
                nameof(fileName));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!IsLowercaseSha256(sha256))
        {
            throw new ArgumentException(
                "The payload SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(sha256));
        }
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        LogicalName = logicalName;
        FileName = fileName;
        Length = length;
        Sha256 = sha256;
        Kind = kind;
    }

    public string LogicalName { get; }

    public string FileName { get; }

    public long Length { get; }

    public string Sha256 { get; }

    public SetupPayloadKind Kind { get; }

    private static bool IsSafeLeafName(string value)
    {
        return value is not "." and not ".."
            && value.AsSpan().Trim().SequenceEqual(value.AsSpan())
            && value.IndexOfAny(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) < 0
            && !value.Any(char.IsControl);
    }

    private static bool IsLowercaseSha256(string value)
    {
        return value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');
    }
}

public sealed record SetupManifest
{
    private const string InternalChannel = "internal";
    private const string InternalPackagePrefix =
        "EMKE.Translation.Internal_";
    private const string InternalPublisher = "CN=EMKE Internal Test";
    private const string DriverHardwareIdentity =
        "ROOT\\EMKEVIRTUALAUDIO";
    private static readonly Version FrozenProductVersion = new(0, 2, 0, 0);
    private static readonly Version FrozenDriverVersion = new(1, 0, 0, 2);

    public SetupManifest(
        string channel,
        Version productVersion,
        string packageFamilyName,
        string publisher,
        int minimumWindowsBuild,
        Architecture architecture,
        string driverHardwareId,
        Version driverVersion,
        IReadOnlyList<SetupPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(productVersion);
        ArgumentNullException.ThrowIfNull(driverVersion);
        ArgumentNullException.ThrowIfNull(payloads);
        if (!string.Equals(channel, InternalChannel, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Setup supports only the frozen internal channel.",
                nameof(channel));
        }
        if (productVersion != FrozenProductVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(productVersion));
        }
        if (!IsInternalPackageFamilyName(packageFamilyName))
        {
            throw new ArgumentException(
                "The package family name does not match the internal package identity.",
                nameof(packageFamilyName));
        }
        if (!string.Equals(publisher, InternalPublisher, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The publisher does not match the pinned internal certificate.",
                nameof(publisher));
        }
        if (minimumWindowsBuild != 19045)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumWindowsBuild));
        }
        if (architecture != Architecture.X64)
        {
            throw new ArgumentOutOfRangeException(nameof(architecture));
        }
        if (!string.Equals(
                driverHardwareId,
                DriverHardwareIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The driver hardware ID does not match the frozen driver contract.",
                nameof(driverHardwareId));
        }
        if (driverVersion != FrozenDriverVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(driverVersion));
        }

        SetupPayload[] copiedPayloads = payloads.ToArray();
        ValidatePayloadInventory(copiedPayloads);

        Channel = channel;
        ProductVersion = productVersion;
        PackageFamilyName = packageFamilyName;
        Publisher = publisher;
        MinimumWindowsBuild = minimumWindowsBuild;
        Architecture = architecture;
        DriverHardwareId = driverHardwareId;
        DriverVersion = driverVersion;
        Payloads = Array.AsReadOnly(copiedPayloads);
    }

    public string Channel { get; }

    public Version ProductVersion { get; }

    public string PackageFamilyName { get; }

    public string Publisher { get; }

    public int MinimumWindowsBuild { get; }

    public Architecture Architecture { get; }

    public string DriverHardwareId { get; }

    public Version DriverVersion { get; }

    public IReadOnlyList<SetupPayload> Payloads { get; }

    private static bool IsInternalPackageFamilyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(
                InternalPackagePrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> publisherId =
            value.AsSpan(InternalPackagePrefix.Length);
        if (publisherId.Length != 13)
        {
            return false;
        }

        foreach (char character in publisherId)
        {
            bool digit = character is >= '0' and <= '9';
            bool lowercaseLetter = character is >= 'a' and <= 'z';
            if (!digit && !lowercaseLetter)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidatePayloadInventory(SetupPayload[] payloads)
    {
        if (payloads.Length != Enum.GetValues<SetupPayloadKind>().Length
            || payloads.Any(static payload => payload is null))
        {
            throw new ArgumentException(
                "Setup requires exactly one canonical payload of each kind.",
                nameof(payloads));
        }
        if (payloads.Select(static payload => payload.LogicalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != payloads.Length
            || payloads.Select(static payload => payload.FileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != payloads.Length)
        {
            throw new ArgumentException(
                "Setup payload logical names and file names must be unique.",
                nameof(payloads));
        }

        foreach (SetupPayloadKind kind in Enum.GetValues<SetupPayloadKind>())
        {
            SetupPayload[] matches = payloads
                .Where(payload => payload.Kind == kind)
                .ToArray();
            if (matches.Length != 1 || !HasCanonicalFileName(matches[0]))
            {
                throw new ArgumentException(
                    $"Setup requires one canonical '{kind}' payload.",
                    nameof(payloads));
            }
        }
    }

    private static bool HasCanonicalFileName(SetupPayload payload)
    {
        return payload.Kind switch
        {
            SetupPayloadKind.Msix => string.Equals(
                payload.FileName,
                "EMKE-Translation-Windows-0.2.0-internal-x64.msix",
                StringComparison.Ordinal),
            SetupPayloadKind.Certificate => string.Equals(
                payload.FileName,
                "EMKE-Translation-Windows-0.2.0-internal-x64.cer",
                StringComparison.Ordinal),
            SetupPayloadKind.DriverInf => string.Equals(
                payload.FileName,
                "EMKE.VirtualAudio.inf",
                StringComparison.Ordinal),
            SetupPayloadKind.DriverSys => string.Equals(
                payload.FileName,
                "EMKE.VirtualAudio.sys",
                StringComparison.Ordinal),
            SetupPayloadKind.DriverCatalog => string.Equals(
                payload.FileName,
                "EMKE.VirtualAudio.cat",
                StringComparison.Ordinal),
            _ => false,
        };
    }
}
