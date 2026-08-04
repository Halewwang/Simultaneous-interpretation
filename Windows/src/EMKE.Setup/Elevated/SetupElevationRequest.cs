namespace EMKE.Setup.Elevated;

internal sealed record SetupExtractionRootIdentity
{
    public SetupExtractionRootIdentity(
        string fullPath,
        uint volumeSerialNumber,
        uint fileIndexHigh,
        uint fileIndexLow,
        uint fileAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        string normalized = Path.GetFullPath(fullPath);
        if (!Path.IsPathFullyQualified(fullPath)
            || fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            || !string.Equals(normalized, fullPath, StringComparison.Ordinal)
            || fullPath.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The extraction root must be one canonical absolute path.",
                nameof(fullPath));
        }
        if ((fileAttributes & (uint)System.IO.FileAttributes.Directory) == 0
            || (fileAttributes & (uint)System.IO.FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                "The extraction root identity must describe a non-reparse directory.",
                nameof(fileAttributes));
        }

        FullPath = fullPath;
        VolumeSerialNumber = volumeSerialNumber;
        FileIndexHigh = fileIndexHigh;
        FileIndexLow = fileIndexLow;
        FileAttributes = fileAttributes;
    }

    public string FullPath { get; }

    public uint VolumeSerialNumber { get; }

    public uint FileIndexHigh { get; }

    public uint FileIndexLow { get; }

    public uint FileAttributes { get; }
}

internal sealed record SetupElevationPayloadHashes
{
    public SetupElevationPayloadHashes(
        string msixSha256,
        string certificateSha256,
        string driverInfSha256,
        string driverSysSha256,
        string driverCatalogSha256)
    {
        ValidateSha256(msixSha256, nameof(msixSha256));
        ValidateSha256(certificateSha256, nameof(certificateSha256));
        ValidateSha256(driverInfSha256, nameof(driverInfSha256));
        ValidateSha256(driverSysSha256, nameof(driverSysSha256));
        ValidateSha256(driverCatalogSha256, nameof(driverCatalogSha256));

        MsixSha256 = msixSha256;
        CertificateSha256 = certificateSha256;
        DriverInfSha256 = driverInfSha256;
        DriverSysSha256 = driverSysSha256;
        DriverCatalogSha256 = driverCatalogSha256;
    }

    public string MsixSha256 { get; }

    public string CertificateSha256 { get; }

    public string DriverInfSha256 { get; }

    public string DriverSysSha256 { get; }

    public string DriverCatalogSha256 { get; }

    internal static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64
            || value.Any(static character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A SHA-256 value must use 64 lowercase hexadecimal characters.",
                parameterName);
        }
    }
}

internal sealed record SetupElevationRequest
{
    public const uint CurrentVersion = 1;
    internal const string FrozenDriverHardwareId = "ROOT\\EMKEVIRTUALAUDIO";
    internal static readonly Version FrozenDriverVersion = new(1, 0, 0, 2);

    public SetupElevationRequest(
        string manifestSha256,
        Guid transactionId,
        SetupExtractionRootIdentity extractionRoot,
        DateTimeOffset expiresAtUtc,
        string nonce,
        string allowedCertificateThumbprint,
        string allowedDriverHardwareId,
        Version allowedDriverVersion,
        SetupElevationPayloadHashes payloadHashes)
    {
        SetupElevationPayloadHashes.ValidateSha256(
            manifestSha256,
            nameof(manifestSha256));
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The transaction ID must not be empty.",
                nameof(transactionId));
        }
        ArgumentNullException.ThrowIfNull(extractionRoot);
        if (expiresAtUtc.Offset != TimeSpan.Zero
            || expiresAtUtc.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException(
                "The expiry must be UTC with whole-second precision.",
                nameof(expiresAtUtc));
        }
        ValidateLowercaseHex(nonce, 64, nameof(nonce));
        ValidateUppercaseHex(
            allowedCertificateThumbprint,
            40,
            nameof(allowedCertificateThumbprint));
        if (!string.Equals(
                allowedDriverHardwareId,
                FrozenDriverHardwareId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The driver hardware ID does not match the frozen contract.",
                nameof(allowedDriverHardwareId));
        }
        ArgumentNullException.ThrowIfNull(allowedDriverVersion);
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            allowedDriverVersion,
            FrozenDriverVersion);
        ArgumentNullException.ThrowIfNull(payloadHashes);

        ManifestSha256 = manifestSha256;
        TransactionId = transactionId;
        ExtractionRoot = extractionRoot;
        ExpiresAtUtc = expiresAtUtc;
        Nonce = nonce;
        AllowedCertificateThumbprint = allowedCertificateThumbprint;
        AllowedDriverHardwareId = allowedDriverHardwareId;
        AllowedDriverVersion = allowedDriverVersion;
        PayloadHashes = payloadHashes;
    }

#pragma warning disable CA1822 // The wire contract exposes the version on each request.
    public uint Version => CurrentVersion;
#pragma warning restore CA1822

    public string ManifestSha256 { get; }

    public Guid TransactionId { get; }

    public SetupExtractionRootIdentity ExtractionRoot { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public string Nonce { get; }

    public string AllowedCertificateThumbprint { get; }

    public string AllowedDriverHardwareId { get; }

    public Version AllowedDriverVersion { get; }

    public SetupElevationPayloadHashes PayloadHashes { get; }

    private static void ValidateLowercaseHex(
        string value,
        int length,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != length
            || value.Any(static character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The value is not canonical lowercase hexadecimal.",
                parameterName);
        }
    }

    private static void ValidateUppercaseHex(
        string value,
        int length,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != length
            || value.Any(static character => character is not (
                >= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "The value is not canonical uppercase hexadecimal.",
                parameterName);
        }
    }
}

internal sealed class SetupElevationReplayGuard
{
    private readonly object _gate = new();
    private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);

    public bool TryAccept(Guid transactionId, string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        string key = string.Concat(transactionId.ToString("N"), ":", nonce);
        lock (_gate)
        {
            return _accepted.Add(key);
        }
    }
}

internal enum SetupElevatedHelperOutcome
{
    Succeeded = 1,
    RebootRequired = 2,
    Failed = 3,
}

internal sealed record SetupElevatedHelperResult
{
    public SetupElevatedHelperResult(
        Guid transactionId,
        string nonce,
        SetupElevatedHelperOutcome outcome)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The transaction ID must not be empty.",
                nameof(transactionId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        if (nonce.Length != 64
            || nonce.Any(static character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The result nonce must be canonical lowercase hexadecimal.",
                nameof(nonce));
        }
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        TransactionId = transactionId;
        Nonce = nonce;
        Outcome = outcome;
    }

    public Guid TransactionId { get; }

    public string Nonce { get; }

    public SetupElevatedHelperOutcome Outcome { get; }
}

internal sealed class SetupElevationProtocolException : Exception
{
    public SetupElevationProtocolException()
        : this("elevationProtocolRejected")
    {
    }

    public SetupElevationProtocolException(string failureCode)
        : base("The Setup elevation protocol rejected the message.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        FailureCode = failureCode;
    }

    public SetupElevationProtocolException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(innerException);
        FailureCode = "elevationProtocolRejected";
    }

    public string FailureCode { get; }
}
