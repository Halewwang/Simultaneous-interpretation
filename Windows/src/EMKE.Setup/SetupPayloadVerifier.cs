using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using EMKE.Platform.Driver;

namespace EMKE.Setup;

internal sealed record SetupEmbeddedPayload(
    string LogicalName,
    Func<Stream> OpenRead,
    long DeclaredLength)
{
    public SetupEmbeddedPayload
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LogicalName);
        ArgumentNullException.ThrowIfNull(OpenRead);
        ArgumentOutOfRangeException.ThrowIfNegative(DeclaredLength);
    }
}

internal sealed record VerifiedSetupPayload(
    SetupPayload ManifestPayload,
    long Length,
    string Sha256,
    string? Path = null)
{
    public VerifiedSetupPayload
    {
        ArgumentNullException.ThrowIfNull(ManifestPayload);
        ArgumentOutOfRangeException.ThrowIfNegative(Length);
        ArgumentException.ThrowIfNullOrWhiteSpace(Sha256);
    }
}

internal sealed record SetupPayloadSignatureEvidence(
    bool Trusted,
    string? FailureCode)
{
    public SetupPayloadSignatureEvidence
    {
        if (Trusted && FailureCode is not null)
        {
            throw new ArgumentException(
                "Trusted signature evidence cannot carry a failure code.",
                nameof(FailureCode));
        }
        if (!Trusted && string.IsNullOrWhiteSpace(FailureCode))
        {
            throw new ArgumentException(
                "Rejected signature evidence requires a failure code.",
                nameof(FailureCode));
        }
    }

    public static SetupPayloadSignatureEvidence Trusted { get; } = new(true, null);

    public static SetupPayloadSignatureEvidence Rejected(
        string failureCode,
        string? untrustedDiagnosticPath = null,
        string? untrustedSecret = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        _ = untrustedDiagnosticPath;
        _ = untrustedSecret;
        return new SetupPayloadSignatureEvidence(false, failureCode);
    }
}

internal interface ISetupPayloadSignatureVerifier
{
    SetupPayloadSignatureEvidence Verify(
        SetupManifest manifest,
        IReadOnlyList<VerifiedSetupPayload> payloads);
}

internal sealed record SetupPayloadVerificationResult(
    bool IsValid,
    string? FailureCode,
    string DisplayDetail)
{
    public static SetupPayloadVerificationResult Valid { get; } = new(
        true,
        null,
        "Setup payload verification succeeded.");

    public static SetupPayloadVerificationResult Rejected(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new SetupPayloadVerificationResult(
            false,
            failureCode,
            "Setup payload verification failed.");
    }
}

internal sealed class SetupPayloadVerifier
{
    private readonly ISetupPayloadSignatureVerifier _signatureVerifier;

    public SetupPayloadVerifier()
        : this(WindowsSetupPayloadSignatureVerifier.Instance)
    {
    }

    public SetupPayloadVerifier(ISetupPayloadSignatureVerifier signatureVerifier)
    {
        _signatureVerifier = signatureVerifier
            ?? throw new ArgumentNullException(nameof(signatureVerifier));
    }

    public SetupPayloadVerificationResult Verify(
        SetupManifest manifest,
        IReadOnlyList<SetupEmbeddedPayload> embeddedPayloads)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(embeddedPayloads);

        if (embeddedPayloads.Count != manifest.Payloads.Count
            || embeddedPayloads.Any(static payload => payload is null))
        {
            return SetupPayloadVerificationResult.Rejected("embeddedPayloadInventoryMismatch");
        }
        if (embeddedPayloads.Select(static payload => payload.LogicalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != embeddedPayloads.Count)
        {
            return SetupPayloadVerificationResult.Rejected("duplicateEmbeddedPayload");
        }

        List<VerifiedSetupPayload> verified = [];
        foreach (SetupPayload expected in manifest.Payloads)
        {
            SetupEmbeddedPayload? embedded = embeddedPayloads.SingleOrDefault(
                payload => string.Equals(
                    payload.LogicalName,
                    expected.LogicalName,
                    StringComparison.OrdinalIgnoreCase));
            if (embedded is null)
            {
                return SetupPayloadVerificationResult.Rejected("embeddedPayloadMissing");
            }
            if (embedded.DeclaredLength != expected.Length)
            {
                return SetupPayloadVerificationResult.Rejected("tamperedPayloadLength");
            }

            (long length, string hash)? observed = ReadAndHash(embedded.OpenRead);
            if (observed is null)
            {
                return SetupPayloadVerificationResult.Rejected("embeddedPayloadUnreadable");
            }
            if (observed.Value.length != expected.Length)
            {
                return SetupPayloadVerificationResult.Rejected("tamperedPayloadLength");
            }
            if (!string.Equals(
                    observed.Value.hash,
                    expected.Sha256,
                    StringComparison.Ordinal))
            {
                return SetupPayloadVerificationResult.Rejected("tamperedPayloadHash");
            }

            verified.Add(new VerifiedSetupPayload(
                expected,
                observed.Value.length,
                observed.Value.hash));
        }

        SetupPayloadSignatureEvidence signature = _signatureVerifier.Verify(
            manifest,
            verified.AsReadOnly());
        return signature.Trusted
            ? SetupPayloadVerificationResult.Valid
            : SetupPayloadVerificationResult.Rejected(signature.FailureCode!);
    }

#pragma warning disable CA1031 // Corrupt or unavailable embedded streams fail closed.
    private static (long length, string hash)? ReadAndHash(Func<Stream> openRead)
    {
        try
        {
            using Stream stream = openRead();
            if (stream is null || !stream.CanRead)
            {
                return null;
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long length = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                length = checked(length + read);
                hash.AppendData(buffer, 0, read);
            }

            return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch (Exception)
        {
            return null;
        }
    }
#pragma warning restore CA1031
}

internal sealed class WindowsSetupPayloadSignatureVerifier
    : ISetupPayloadSignatureVerifier
{
    public static WindowsSetupPayloadSignatureVerifier Instance { get; } = new();

    private WindowsSetupPayloadSignatureVerifier()
    {
    }

    public SetupPayloadSignatureEvidence Verify(
        SetupManifest manifest,
        IReadOnlyList<VerifiedSetupPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloads);

        VerifiedSetupPayload? certificate = Find(payloads, SetupPayloadKind.Certificate);
        VerifiedSetupPayload? msix = Find(payloads, SetupPayloadKind.Msix);
        VerifiedSetupPayload? inf = Find(payloads, SetupPayloadKind.DriverInf);
        VerifiedSetupPayload? sys = Find(payloads, SetupPayloadKind.DriverSys);
        VerifiedSetupPayload? catalog = Find(payloads, SetupPayloadKind.DriverCatalog);
        if (certificate?.Path is null || msix?.Path is null || inf?.Path is null
            || sys?.Path is null || catalog?.Path is null)
        {
            return SetupPayloadSignatureEvidence.Rejected("signatureEvidenceUnavailable");
        }

        if (!WindowsAuthenticodeVerifier.IsTrusted(msix.Path))
        {
            return SetupPayloadSignatureEvidence.Rejected("msixAuthenticodeInvalid");
        }
        SetupPayloadSignatureEvidence? certificateEvidence =
            VerifyCertificate(certificate, manifest);
        if (certificateEvidence is not null)
        {
            return certificateEvidence;
        }

        WindowsCatalogEvidence driverEvidence = WindowsCatalogTrustVerifier.Instance.Verify(
            catalog.Path,
            inf.Path,
            sys.Path);
        return driverEvidence.ChainValid
            ? SetupPayloadSignatureEvidence.Trusted
            : SetupPayloadSignatureEvidence.Rejected("driverCatalogKernelTrustInvalid");
    }

    private static VerifiedSetupPayload? Find(
        IReadOnlyList<VerifiedSetupPayload> payloads,
        SetupPayloadKind kind) => payloads.SingleOrDefault(
            payload => payload.ManifestPayload.Kind == kind);

#pragma warning disable CA1031 // Invalid certificate input must fail closed.
    private static SetupPayloadSignatureEvidence? VerifyCertificate(
        VerifiedSetupPayload certificatePayload,
        SetupManifest manifest)
    {
        try
        {
            using X509Certificate2 certificate = X509CertificateLoader
                .LoadCertificateFromFile(certificatePayload.Path!);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!string.Equals(
                    certificate.Subject,
                    manifest.Publisher,
                    StringComparison.Ordinal))
            {
                return SetupPayloadSignatureEvidence.Rejected(
                    "certificateSubjectMismatch");
            }
            if (certificate.NotBefore > now || certificate.NotAfter < now)
            {
                return SetupPayloadSignatureEvidence.Rejected(
                    "certificateValidityInvalid");
            }

            string certificateThumbprint = Convert.ToHexStringLower(
                certificate.GetCertHash(HashAlgorithmName.SHA256));
            SetupPayload expected = manifest.Payloads.Single(
                payload => payload.Kind == SetupPayloadKind.Certificate);
            return string.Equals(
                    certificateThumbprint,
                    expected.Sha256,
                    StringComparison.Ordinal)
                ? null
                : SetupPayloadSignatureEvidence.Rejected(
                    "certificateThumbprintMismatch");
        }
        catch (Exception)
        {
            return SetupPayloadSignatureEvidence.Rejected(
                "certificateHashMismatch");
        }
    }
#pragma warning restore CA1031
}

internal static class WindowsAuthenticodeVerifier
{
    private const int TrustSuccess = 0;
    private const uint WtdUiNone = 2;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly Guid GenericVerifyV2 = new(
        "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

#pragma warning disable CA1416 // This is reached only by the Windows Setup executable.
    public static bool IsTrusted(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            return VerifyFileTrust(Path.GetFullPath(filePath)) == TrustSuccess;
        }
        catch (Exception)
        {
            return false;
        }
    }
#pragma warning restore CA1416

    private static int VerifyFileTrust(string fullPath)
    {
        nint path = Marshal.StringToCoTaskMemUni(fullPath);
        try
        {
            WindowsCatalogNativeMethods.WinTrustFileInfo fileInfo = new()
            {
                Size = checked((uint)Marshal.SizeOf<
                    WindowsCatalogNativeMethods.WinTrustFileInfo>()),
                FilePath = path,
            };
            WindowsCatalogRevocationConfiguration configuration =
                WindowsCatalogTrustNativeApi.RevocationConfiguration;
            nint fileInfoPointer = Marshal.AllocCoTaskMem(
                Marshal.SizeOf<WindowsCatalogNativeMethods.WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
                WindowsCatalogNativeMethods.WinTrustData trustData = new()
                {
                    Size = checked((uint)Marshal.SizeOf<
                        WindowsCatalogNativeMethods.WinTrustData>()),
                    UiChoice = WtdUiNone,
                    RevocationChecks = configuration.WinTrustRevocationChecks,
                    UnionChoice = WtdChoiceFile,
                    UnionInfo = fileInfoPointer,
                    StateAction = WtdStateActionIgnore,
                    ProviderFlags = configuration.WinTrustProviderFlags,
                };
                Guid action = GenericVerifyV2;
                return WindowsCatalogNativeMethods.WinVerifyTrust(
                    InvalidHandleValue,
                    ref action,
                    ref trustData);
            }
            finally
            {
                Marshal.DestroyStructure<
                    WindowsCatalogNativeMethods.WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }
}
