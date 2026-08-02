using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Xml.Linq;
using EMKE.Platform.Driver;

namespace EMKE.Setup;

internal sealed class SetupEmbeddedPayload
{
    public SetupEmbeddedPayload(
        string logicalName,
        Func<Stream> openRead,
        long declaredLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentNullException.ThrowIfNull(openRead);
        ArgumentOutOfRangeException.ThrowIfNegative(declaredLength);
        LogicalName = logicalName;
        OpenRead = openRead;
        DeclaredLength = declaredLength;
    }

    public string LogicalName { get; }

    public Func<Stream> OpenRead { get; }

    public long DeclaredLength { get; }
}

internal sealed class VerifiedSetupPayload
{
    public VerifiedSetupPayload(
        SetupPayload manifestPayload,
        long length,
        string sha256,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(manifestPayload);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ManifestPayload = manifestPayload;
        Length = length;
        Sha256 = sha256;
        Path = path;
    }

    public SetupPayload ManifestPayload { get; }

    public long Length { get; }

    public string Sha256 { get; }

    public string? Path { get; }
}

internal sealed class SetupPayloadSignatureEvidence
{
    public SetupPayloadSignatureEvidence(bool trusted, string? failureCode)
    {
        if (trusted && failureCode is not null)
        {
            throw new ArgumentException(
                "Trusted signature evidence cannot carry a failure code.",
                nameof(failureCode));
        }
        if (!trusted && string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException(
                "Rejected signature evidence requires a failure code.",
                nameof(failureCode));
        }

        Trusted = trusted;
        FailureCode = failureCode;
    }

    public bool Trusted { get; }

    public string? FailureCode { get; }

    public static SetupPayloadSignatureEvidence TrustedEvidence { get; } = new(true, null);

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

internal sealed class SetupMsixSignatureEvidence
{
    public SetupMsixSignatureEvidence(
        bool signatureValid,
        string? signerSha256,
        string? identityPublisher)
    {
        SignatureValid = signatureValid;
        SignerSha256 = signerSha256;
        IdentityPublisher = identityPublisher;
    }

    public bool SignatureValid { get; }

    public string? SignerSha256 { get; }

    public string? IdentityPublisher { get; }
}

internal sealed class SetupCertificateEvidence
{
    public SetupCertificateEvidence(
        string? subject,
        bool validityValid,
        string? sha256Thumbprint)
    {
        Subject = subject;
        ValidityValid = validityValid;
        Sha256Thumbprint = sha256Thumbprint;
    }

    public string? Subject { get; }

    public bool ValidityValid { get; }

    public string? Sha256Thumbprint { get; }
}

internal sealed class SetupDriverCatalogEvidence
{
    public SetupDriverCatalogEvidence(bool trusted)
    {
        Trusted = trusted;
    }

    public bool Trusted { get; }
}

internal interface ISetupSignatureProbe
{
    SetupMsixSignatureEvidence VerifyMsix(string msixPath);

    SetupCertificateEvidence ReadCertificate(string certificatePath);

    SetupDriverCatalogEvidence VerifyDriverCatalog(
        string catalogPath,
        string infPath,
        string sysPath);
}

internal sealed class SetupPayloadVerificationResult : IDisposable
{
    private readonly IDisposable? _attemptLifetime;

    private SetupPayloadVerificationResult(
        bool isValid,
        string? failureCode,
        string displayDetail,
        IDisposable? attemptLifetime = null)
    {
        IsValid = isValid;
        FailureCode = failureCode;
        DisplayDetail = displayDetail;
        _attemptLifetime = attemptLifetime;
    }

    public bool IsValid { get; }

    public string? FailureCode { get; }

    public string DisplayDetail { get; }

    public static SetupPayloadVerificationResult Valid { get; } = new(
        true, null, "Setup payload verification succeeded.");

    public static SetupPayloadVerificationResult Rejected(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new SetupPayloadVerificationResult(
            false,
            failureCode,
            "Setup payload verification failed.");
    }

    internal static SetupPayloadVerificationResult VerifiedAttempt(
        SetupPayloadVerificationAttempt attempt) => new(
            true,
            null,
            "Setup payload verification succeeded.",
            attempt);

    public void Dispose()
    {
        _attemptLifetime?.Dispose();
    }
}

internal sealed class SetupPayloadVerificationAttempt : IDisposable
{
    private readonly SetupExtractionDirectory _extractionDirectory;

    public SetupPayloadVerificationAttempt(SetupExtractionDirectory extractionDirectory)
    {
        _extractionDirectory = extractionDirectory
            ?? throw new ArgumentNullException(nameof(extractionDirectory));
    }

    public void Dispose()
    {
        _extractionDirectory.Dispose();
    }
}

internal sealed class SetupPayloadVerifier
{
    private readonly ISetupPayloadSignatureVerifier _signatureVerifier;
    private readonly Func<Version, SetupExtractionDirectory>
        _createExtractionDirectory;

    public SetupPayloadVerifier()
        : this(
            WindowsSetupPayloadSignatureVerifier.Instance,
            SetupExtractionDirectory.CreateForCurrentUser)
    {
    }

    public SetupPayloadVerifier(ISetupPayloadSignatureVerifier signatureVerifier)
        : this(signatureVerifier, SetupExtractionDirectory.CreateForCurrentUser)
    {
    }

    internal SetupPayloadVerifier(
        ISetupPayloadSignatureVerifier signatureVerifier,
        Func<Version, SetupExtractionDirectory> createExtractionDirectory)
    {
        _signatureVerifier = signatureVerifier
            ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _createExtractionDirectory = createExtractionDirectory
            ?? throw new ArgumentNullException(nameof(createExtractionDirectory));
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

            (long length, string hash, bool exceededLength)? observed = ReadAndHash(
                embedded.OpenRead,
                expected.Length);
            if (observed is null)
            {
                return SetupPayloadVerificationResult.Rejected("embeddedPayloadUnreadable");
            }
            if (observed.Value.exceededLength
                || observed.Value.length != expected.Length)
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

#pragma warning disable CA1031 // Payload and signature faults must fail closed and clean up.
    public SetupPayloadVerificationResult VerifyAndExtract(
        SetupManifest manifest,
        IReadOnlyList<SetupEmbeddedPayload> embeddedPayloads)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(embeddedPayloads);

        if (embeddedPayloads.Count != manifest.Payloads.Count
            || embeddedPayloads.Any(static payload => payload is null)
            || embeddedPayloads.Select(static payload => payload.LogicalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != embeddedPayloads.Count)
        {
            return SetupPayloadVerificationResult.Rejected(
                "embeddedPayloadInventoryMismatch");
        }

        SetupExtractionDirectory? extraction =
            _createExtractionDirectory(manifest.ProductVersion);
        try
        {
            List<VerifiedSetupPayload> verified = [];
            foreach (SetupPayload expected in manifest.Payloads)
            {
                SetupEmbeddedPayload? embedded = embeddedPayloads.SingleOrDefault(
                    payload => string.Equals(
                        payload.LogicalName,
                        expected.LogicalName,
                        StringComparison.OrdinalIgnoreCase));
                if (embedded is null || embedded.DeclaredLength != expected.Length)
                {
                    return SetupPayloadVerificationResult.Rejected(
                        "tamperedPayloadLength");
                }

                using Stream source = embedded.OpenRead();
                SetupExtractionResult extracted = extraction.CopyVerified(
                    expected.FileName,
                    source,
                    expected);
                if (!extracted.Succeeded)
                {
                    return SetupPayloadVerificationResult.Rejected(
                        extracted.FailureCode!);
                }

                verified.Add(new VerifiedSetupPayload(
                    expected,
                    expected.Length,
                    expected.Sha256,
                    extracted.OutputPath));
            }

            SetupPayloadSignatureEvidence signature = _signatureVerifier.Verify(
                manifest,
                verified.AsReadOnly());
            if (!signature.Trusted)
            {
                return SetupPayloadVerificationResult.Rejected(signature.FailureCode!);
            }

            SetupPayloadVerificationAttempt? attempt = null;
            try
            {
                attempt = new SetupPayloadVerificationAttempt(extraction);
                extraction = null;
                SetupPayloadVerificationResult result =
                    SetupPayloadVerificationResult.VerifiedAttempt(attempt);
                attempt = null;
                return result;
            }
            finally
            {
                attempt?.Dispose();
            }
        }
        catch (Exception)
        {
            return SetupPayloadVerificationResult.Rejected("embeddedPayloadUnreadable");
        }
        finally
        {
            extraction?.Dispose();
        }
    }
#pragma warning restore CA1031

#pragma warning disable CA1031 // Corrupt or unavailable embedded streams fail closed.
    private static (long length, string hash, bool exceededLength)? ReadAndHash(
        Func<Stream> openRead,
        long maximumLength)
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
                if (read > maximumLength - length)
                {
                    return (length, string.Empty, exceededLength: true);
                }

                length += read;
                hash.AppendData(buffer, 0, read);
            }

            return (
                length,
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                exceededLength: false);
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
    private readonly ISetupSignatureProbe _probe;

    public static WindowsSetupPayloadSignatureVerifier Instance { get; } = new(
        WindowsSetupSignatureProbe.Instance);

    internal WindowsSetupPayloadSignatureVerifier(ISetupSignatureProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
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

        SetupMsixSignatureEvidence msixEvidence = _probe.VerifyMsix(msix.Path);
        if (!msixEvidence.SignatureValid)
        {
            return SetupPayloadSignatureEvidence.Rejected("msixAuthenticodeInvalid");
        }
        SetupCertificateEvidence certificateEvidence = _probe.ReadCertificate(
            certificate.Path);
        SetupPayloadSignatureEvidence? certificateFailure = VerifyCertificate(
            certificateEvidence,
            certificate.ManifestPayload,
            manifest);
        if (certificateFailure is not null)
        {
            return certificateFailure;
        }
        if (!string.Equals(
                msixEvidence.SignerSha256,
                certificateEvidence.Sha256Thumbprint,
                StringComparison.Ordinal))
        {
            return SetupPayloadSignatureEvidence.Rejected("msixSignerMismatch");
        }
        if (!string.Equals(
                msixEvidence.IdentityPublisher,
                manifest.Publisher,
                StringComparison.Ordinal))
        {
            return SetupPayloadSignatureEvidence.Rejected("msixPublisherMismatch");
        }

        SetupDriverCatalogEvidence driverEvidence = _probe.VerifyDriverCatalog(
            catalog.Path,
            inf.Path,
            sys.Path);
        return driverEvidence.Trusted
            ? SetupPayloadSignatureEvidence.TrustedEvidence
            : SetupPayloadSignatureEvidence.Rejected("driverCatalogKernelTrustInvalid");
    }

    private static VerifiedSetupPayload? Find(
        IReadOnlyList<VerifiedSetupPayload> payloads,
        SetupPayloadKind kind) => payloads.SingleOrDefault(
            payload => payload.ManifestPayload.Kind == kind);

#pragma warning disable CA1031 // Invalid certificate input must fail closed.
    private static SetupPayloadSignatureEvidence? VerifyCertificate(
        SetupCertificateEvidence certificate,
        SetupPayload certificatePayload,
        SetupManifest manifest)
    {
        if (!string.Equals(
                certificate.Subject,
                manifest.Publisher,
                StringComparison.Ordinal))
        {
            return SetupPayloadSignatureEvidence.Rejected(
                "certificateSubjectMismatch");
        }
        if (!certificate.ValidityValid)
        {
            return SetupPayloadSignatureEvidence.Rejected(
                "certificateValidityInvalid");
        }
        return string.Equals(
                certificate.Sha256Thumbprint,
                certificatePayload.Sha256,
                StringComparison.Ordinal)
            ? null
            : SetupPayloadSignatureEvidence.Rejected(
                "certificateThumbprintMismatch");
    }
#pragma warning restore CA1031
}

internal sealed class WindowsSetupSignatureProbe : ISetupSignatureProbe
{
    public static WindowsSetupSignatureProbe Instance { get; } = new();

    private WindowsSetupSignatureProbe()
    {
    }

#pragma warning disable CA1031 // Malformed signed containers and certificates fail closed.
    public SetupMsixSignatureEvidence VerifyMsix(string msixPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(msixPath);
        try
        {
            string signerSha256 = WindowsAuthenticodeVerifier.ReadSignerSha256(msixPath);
            string publisher = ReadMsixPublisher(msixPath);
            return new SetupMsixSignatureEvidence(
                WindowsAuthenticodeVerifier.IsSignatureIntact(msixPath),
                signerSha256,
                publisher);
        }
        catch (Exception)
        {
            return new SetupMsixSignatureEvidence(false, null, null);
        }
    }

    public SetupCertificateEvidence ReadCertificate(string certificatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);
        try
        {
            using X509Certificate2 certificate = X509CertificateLoader
                .LoadCertificateFromFile(certificatePath);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new SetupCertificateEvidence(
                certificate.Subject,
                certificate.NotBefore <= now && certificate.NotAfter >= now,
                Convert.ToHexStringLower(
                    certificate.GetCertHash(HashAlgorithmName.SHA256)));
        }
        catch (Exception)
        {
            return new SetupCertificateEvidence(null, false, null);
        }
    }

    public SetupDriverCatalogEvidence VerifyDriverCatalog(
        string catalogPath,
        string infPath,
        string sysPath)
    {
        WindowsCatalogEvidence evidence = WindowsCatalogTrustVerifier.Instance.Verify(
            catalogPath,
            infPath,
            sysPath);
        return new SetupDriverCatalogEvidence(evidence.ChainValid);
    }
#pragma warning restore CA1031

    internal static string ReadMsixPublisher(string msixPath)
    {
        using FileStream file = File.OpenRead(msixPath);
        using ZipArchive archive = new(file, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry entry = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidDataException("MSIX identity manifest is missing.");
        using Stream manifestStream = entry.Open();
        XDocument document = XDocument.Load(manifestStream, LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidDataException("MSIX identity manifest is missing.");
        XElement? identity = root.Element(root.Name.Namespace + "Identity");
        return identity?.Attribute("Publisher")?.Value
            ?? throw new InvalidDataException("MSIX publisher is missing.");
    }
}

internal static class WindowsAuthenticodeVerifier
{
    private const int TrustSuccess = 0;
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const uint WtdUiNone = 2;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly Guid GenericVerifyV2 = new(
        "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

#pragma warning disable CA1031, CA1416 // This is reached only by the Windows Setup executable.
    public static bool IsSignatureIntact(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            int result = VerifyFileTrust(Path.GetFullPath(filePath));
            return result is TrustSuccess or CertEUntrustedRoot or CertEChaining;
        }
        catch (Exception)
        {
            return false;
        }
    }
#pragma warning restore CA1031, CA1416

#pragma warning disable SYSLIB0057 // Signed-file extraction has no loader replacement.
    public static string ReadSignerSha256(string filePath)
    {
        using X509Certificate signer = X509Certificate.CreateFromSignedFile(filePath);
        using X509Certificate2 signerCertificate = X509CertificateLoader
            .LoadCertificate(signer.GetRawCertData());
        return Convert.ToHexStringLower(
            signerCertificate.GetCertHash(HashAlgorithmName.SHA256));
    }
#pragma warning restore SYSLIB0057

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
