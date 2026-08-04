using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace EMKE.Setup.Platform;

internal sealed record SetupRecoveryRecord(
    Guid TransactionId,
    string Component,
    string FailureCode,
    string ExpectedIdentity,
    DateTimeOffset RecordedAtUtc);

internal interface ISetupRecoveryRecordWriter
{
    void Write(SetupRecoveryRecord record);
}

internal sealed class WindowsSetupRecoveryRecordWriter
    : ISetupRecoveryRecordWriter
{
    private readonly string _recoveryRoot;

    public static WindowsSetupRecoveryRecordWriter Instance { get; } = new();

    private WindowsSetupRecoveryRecordWriter()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EMKE",
            "Setup",
            "recovery"))
    {
    }

    internal WindowsSetupRecoveryRecordWriter(string recoveryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryRoot);
        _recoveryRoot = Path.GetFullPath(recoveryRoot);
    }

    public void Write(SetupRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = Directory.CreateDirectory(_recoveryRoot);
        string leafName = string.Concat(
            record.TransactionId.ToString("N"),
            "-",
            SafeComponent(record.Component),
            ".json");
        string destination = Path.Combine(_recoveryRoot, leafName);
        string temporary = Path.Combine(
            _recoveryRoot,
            string.Concat(".", leafName, ".", Guid.NewGuid().ToString("N"), ".tmp"));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record);

        try
        {
            using (FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                output.Write(json);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string SafeComponent(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        if (component.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "The recovery component is not a safe leaf token.",
                nameof(component));
        }
        return component;
    }
}

internal sealed record CertificatePayloadIdentity(
    string Subject,
    string Sha1Thumbprint,
    string Sha256,
    bool ValidityValid,
    bool HasPrivateKey);

internal sealed record InstalledCertificateIdentity(
    string Subject,
    string Sha1Thumbprint);

internal sealed record CertificateInstallContract
{
    public CertificateInstallContract(
        string subject,
        string sha1Thumbprint,
        string sha256,
        DateTimeOffset verificationTimeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ValidateHex(sha1Thumbprint, 40, uppercase: true, nameof(sha1Thumbprint));
        ValidateHex(sha256, 64, uppercase: false, nameof(sha256));
        if (verificationTimeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Certificate verification time must be UTC.",
                nameof(verificationTimeUtc));
        }
        Subject = subject;
        Sha1Thumbprint = sha1Thumbprint;
        Sha256 = sha256;
        VerificationTimeUtc = verificationTimeUtc;
    }

    public string Subject { get; }

    public string Sha1Thumbprint { get; }

    public string Sha256 { get; }

    public DateTimeOffset VerificationTimeUtc { get; }

    private static void ValidateHex(
        string value,
        int length,
        bool uppercase,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        bool valid = value.Length == length && value.All(character =>
            character is >= '0' and <= '9'
            || (uppercase
                ? character is >= 'A' and <= 'F'
                : character is >= 'a' and <= 'f'));
        if (!valid)
        {
            throw new ArgumentException(
                "The certificate identity is not canonical hexadecimal.",
                parameterName);
        }
    }
}

internal enum CertificateInstallOutcome
{
    Succeeded,
    Blocked,
    Failed,
}

internal sealed record CertificateInstallReceipt(
    InstalledCertificateIdentity Identity,
    bool CreatedByAttempt);

internal sealed record CertificateInstallResult(
    CertificateInstallOutcome Outcome,
    CertificateInstallReceipt? Receipt,
    string? FailureCode);

internal sealed record CertificateRollbackResult(
    bool Succeeded,
    bool Removed,
    string? FailureCode);

internal interface ICertificatePlatform
{
    CertificatePayloadIdentity InspectPayload(VerifiedSetupPayload payload);

    IReadOnlyList<InstalledCertificateIdentity> ReadTrustedPeople();

    void AddTrustedPeople(VerifiedSetupPayload payload);

    bool RemoveTrustedPeople(string sha1Thumbprint);
}

internal interface ICertificateMachineInstaller
{
    CertificateInstallResult Install(
        VerifiedSetupPayload certificate,
        CertificateInstallContract contract,
        Guid transactionId);

    CertificateRollbackResult Rollback(
        CertificateInstallReceipt receipt,
        Guid transactionId);
}

internal sealed class CertificateInstaller : ICertificateMachineInstaller
{
    private readonly ICertificatePlatform _platform;
    private readonly ISetupRecoveryRecordWriter _recoveryWriter;

    public CertificateInstaller()
        : this(
            WindowsCertificatePlatform.Instance,
            WindowsSetupRecoveryRecordWriter.Instance)
    {
    }

    internal CertificateInstaller(
        ICertificatePlatform platform,
        ISetupRecoveryRecordWriter recoveryWriter)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _recoveryWriter = recoveryWriter
            ?? throw new ArgumentNullException(nameof(recoveryWriter));
    }

#pragma warning disable CA1031 // Machine mutation failures are returned as closed outcomes.
    public CertificateInstallResult Install(
        VerifiedSetupPayload certificate,
        CertificateInstallContract contract,
        Guid transactionId)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(contract);
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The transaction ID must not be empty.",
                nameof(transactionId));
        }

        CertificateInstallReceipt? mutationReceipt = null;
        try
        {
            CertificatePayloadIdentity payload = _platform.InspectPayload(certificate);
            if (!PayloadMatches(payload, contract))
            {
                return Rejected("certificatePayloadMismatch");
            }

            IReadOnlyList<InstalledCertificateIdentity> existing =
                _platform.ReadTrustedPeople();
            InstalledCertificateIdentity? exact = existing.SingleOrDefault(item =>
                string.Equals(
                    item.Sha1Thumbprint,
                    contract.Sha1Thumbprint,
                    StringComparison.Ordinal));
            if (exact is not null)
            {
                return string.Equals(
                        exact.Subject,
                        contract.Subject,
                        StringComparison.Ordinal)
                    ? Succeeded(exact, createdByAttempt: false)
                    : Rejected("certificateConflict");
            }
            if (existing.Any(item => string.Equals(
                    item.Subject,
                    contract.Subject,
                    StringComparison.Ordinal)))
            {
                return Rejected("certificateConflict");
            }

            CertificatePayloadIdentity beforeAdd = _platform.InspectPayload(certificate);
            if (!PayloadMatches(beforeAdd, contract))
            {
                return Rejected("certificatePayloadMismatch");
            }
            mutationReceipt = new CertificateInstallReceipt(
                new InstalledCertificateIdentity(
                    contract.Subject,
                    contract.Sha1Thumbprint),
                CreatedByAttempt: true);
            _platform.AddTrustedPeople(certificate);
            InstalledCertificateIdentity? installed = _platform
                .ReadTrustedPeople()
                .SingleOrDefault(item =>
                    string.Equals(
                        item.Sha1Thumbprint,
                        contract.Sha1Thumbprint,
                        StringComparison.Ordinal)
                    && string.Equals(
                        item.Subject,
                        contract.Subject,
                        StringComparison.Ordinal));
            return installed is null
                ? Failed("certificatePostInstallMismatch", mutationReceipt)
                : Succeeded(installed, createdByAttempt: true);
        }
        catch (Exception)
        {
            return Failed("certificateInstallFailed", mutationReceipt);
        }
    }

    public CertificateRollbackResult Rollback(
        CertificateInstallReceipt receipt,
        Guid transactionId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.CreatedByAttempt)
        {
            return new CertificateRollbackResult(
                Succeeded: true,
                Removed: false,
                FailureCode: null);
        }

        try
        {
            IReadOnlyList<InstalledCertificateIdentity> existing =
                _platform.ReadTrustedPeople();
            InstalledCertificateIdentity? exact = existing.SingleOrDefault(item =>
                string.Equals(
                    item.Sha1Thumbprint,
                    receipt.Identity.Sha1Thumbprint,
                    StringComparison.Ordinal));
            if (exact is null)
            {
                if (existing.Any(item => string.Equals(
                        item.Subject,
                        receipt.Identity.Subject,
                        StringComparison.Ordinal)))
                {
                    return RecoveryRequired(
                        transactionId,
                        "certificateRollbackIdentityChanged",
                        receipt.Identity.Sha1Thumbprint);
                }
                return new CertificateRollbackResult(
                    Succeeded: true,
                    Removed: false,
                    FailureCode: null);
            }
            if (!string.Equals(
                    exact.Subject,
                    receipt.Identity.Subject,
                    StringComparison.Ordinal))
            {
                return RecoveryRequired(
                    transactionId,
                    "certificateRollbackIdentityChanged",
                    receipt.Identity.Sha1Thumbprint);
            }

            if (!_platform.RemoveTrustedPeople(receipt.Identity.Sha1Thumbprint)
                || _platform.ReadTrustedPeople().Any(item => string.Equals(
                    item.Sha1Thumbprint,
                    receipt.Identity.Sha1Thumbprint,
                    StringComparison.Ordinal)))
            {
                return RecoveryRequired(
                    transactionId,
                    "certificateRollbackFailed",
                    receipt.Identity.Sha1Thumbprint);
            }
            return new CertificateRollbackResult(
                Succeeded: true,
                Removed: true,
                FailureCode: null);
        }
        catch (Exception)
        {
            return RecoveryRequired(
                transactionId,
                "certificateRollbackFailed",
                receipt.Identity.Sha1Thumbprint);
        }
    }
#pragma warning restore CA1031

    private static bool PayloadMatches(
        CertificatePayloadIdentity payload,
        CertificateInstallContract contract)
    {
        return string.Equals(payload.Subject, contract.Subject, StringComparison.Ordinal)
            && string.Equals(
                payload.Sha1Thumbprint,
                contract.Sha1Thumbprint,
                StringComparison.Ordinal)
            && string.Equals(payload.Sha256, contract.Sha256, StringComparison.Ordinal)
            && payload.ValidityValid
            && !payload.HasPrivateKey;
    }

    private static CertificateInstallResult Succeeded(
        InstalledCertificateIdentity identity,
        bool createdByAttempt) => new(
            CertificateInstallOutcome.Succeeded,
            new CertificateInstallReceipt(identity, createdByAttempt),
            FailureCode: null);

    private static CertificateInstallResult Rejected(string failureCode) => new(
        CertificateInstallOutcome.Blocked,
        Receipt: null,
        failureCode);

    private static CertificateInstallResult Failed(
        string failureCode,
        CertificateInstallReceipt? receipt = null) => new(
        CertificateInstallOutcome.Failed,
        receipt,
        failureCode);

#pragma warning disable CA1031 // A failed recovery writer must remain a closed rollback result.
    private CertificateRollbackResult RecoveryRequired(
        Guid transactionId,
        string failureCode,
        string expectedIdentity)
    {
        try
        {
            _recoveryWriter.Write(new SetupRecoveryRecord(
                transactionId,
                "certificate",
                failureCode,
                expectedIdentity,
                DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
            return new CertificateRollbackResult(
                Succeeded: false,
                Removed: false,
                FailureCode: "certificateRecoveryRecordFailed");
        }
        return new CertificateRollbackResult(
            Succeeded: false,
            Removed: false,
            failureCode);
    }
#pragma warning restore CA1031
}

internal sealed class WindowsCertificatePlatform : ICertificatePlatform
{
    private const int MaximumCertificateBytes = 1024 * 1024;

    public static WindowsCertificatePlatform Instance { get; } = new();

    private WindowsCertificatePlatform()
    {
    }

    public CertificatePayloadIdentity InspectPayload(VerifiedSetupPayload payload)
    {
        byte[] bytes = ReadPayloadBytes(payload);
        using X509Certificate2 certificate = X509CertificateLoader
            .LoadCertificate(bytes);
        DateTime utcNow = DateTime.UtcNow;
        return new CertificatePayloadIdentity(
            certificate.Subject,
            Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA1)),
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            certificate.NotBefore.ToUniversalTime() <= utcNow
                && certificate.NotAfter.ToUniversalTime() >= utcNow,
            certificate.HasPrivateKey);
    }

    public IReadOnlyList<InstalledCertificateIdentity> ReadTrustedPeople()
    {
        EnsureWindows();
        using X509Store store = OpenStore(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates
            .Select(static certificate => new InstalledCertificateIdentity(
                certificate.Subject,
                certificate.Thumbprint))
            .ToArray();
    }

    public void AddTrustedPeople(VerifiedSetupPayload payload)
    {
        EnsureWindows();
        byte[] bytes = ReadPayloadBytes(payload);
        using X509Certificate2 certificate = X509CertificateLoader
            .LoadCertificate(bytes);
        if (certificate.HasPrivateKey)
        {
            throw new InvalidDataException(
                "Setup certificate payload must not contain a private key.");
        }
        using X509Store store = OpenStore(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    public bool RemoveTrustedPeople(string sha1Thumbprint)
    {
        EnsureWindows();
        using X509Store store = OpenStore(OpenFlags.ReadWrite);
        X509Certificate2[] matches = store.Certificates
            .Find(X509FindType.FindByThumbprint, sha1Thumbprint, validOnly: false)
            .Cast<X509Certificate2>()
            .ToArray();
        foreach (X509Certificate2 certificate in matches)
        {
            store.Remove(certificate);
        }
        return matches.Length > 0;
    }

    private static byte[] ReadPayloadBytes(VerifiedSetupPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is <= 0 or > MaximumCertificateBytes)
        {
            throw new InvalidDataException(
                "Certificate payload has an invalid length.");
        }
        byte[] bytes = new byte[checked((int)payload.Length)];
        using Stream input = payload.Lease.OpenReadView();
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "Certificate payload exceeds its verified length.");
        }
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, payload.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Certificate payload changed after verification.");
        }
        return bytes;
    }

    private static X509Store OpenStore(OpenFlags flags)
    {
        X509Store store = new(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        try
        {
            store.Open(flags);
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Machine certificate installation requires Windows.");
        }
    }
}
