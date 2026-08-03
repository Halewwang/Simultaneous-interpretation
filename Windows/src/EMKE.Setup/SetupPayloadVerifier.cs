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
        VerifiedPayloadLease lease)
    {
        ArgumentNullException.ThrowIfNull(manifestPayload);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentNullException.ThrowIfNull(lease);
        ManifestPayload = manifestPayload;
        Length = length;
        Sha256 = sha256;
        Lease = lease;
    }

    public SetupPayload ManifestPayload { get; }

    public long Length { get; }

    public string Sha256 { get; }

    public string DisplayPath => Lease.DisplayPath;

    public VerifiedPayloadLease Lease { get; }
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
    public SetupDriverCatalogEvidence(
        bool kernelPolicyValid,
        bool catalogEntriesMatch,
        bool memberTrustValid,
        bool allowed)
    {
        KernelPolicyValid = kernelPolicyValid;
        CatalogEntriesMatch = catalogEntriesMatch;
        MemberTrustValid = memberTrustValid;
        Allowed = allowed;
    }

    public bool KernelPolicyValid { get; }

    public bool CatalogEntriesMatch { get; }

    public bool MemberTrustValid { get; }

    public bool Allowed { get; }
}

internal interface ISetupSignatureProbe
{
    SetupMsixSignatureEvidence VerifyMsix(VerifiedSetupPayload msix);

    SetupCertificateEvidence ReadCertificate(VerifiedSetupPayload certificate);

    SetupDriverCatalogEvidence VerifyDriverCatalog(
        VerifiedSetupPayload catalog,
        VerifiedSetupPayload inf,
        VerifiedSetupPayload sys);
}

internal sealed class SetupPayloadVerificationResult : IDisposable
{
    private SetupCleanupOutcome _lastCleanupOutcome;

    private SetupPayloadVerificationResult(
        bool isValid,
        string? failureCode,
        string displayDetail,
        SetupPayloadVerificationAttempt? attempt = null,
        SetupCleanupOutcome? cleanupOutcome = null)
    {
        IsValid = isValid;
        FailureCode = failureCode;
        DisplayDetail = displayDetail;
        Attempt = attempt;
        _lastCleanupOutcome = cleanupOutcome ?? SetupCleanupOutcome.NotAttempted;
    }

    public bool IsValid { get; }

    public string? FailureCode { get; }

    public string DisplayDetail { get; }

    public SetupPayloadVerificationAttempt? Attempt { get; }

    public SetupCleanupOutcome LastCleanupOutcome
    {
        get => Attempt?.LastCleanupOutcome ?? _lastCleanupOutcome;
        private set => _lastCleanupOutcome = value;
    }

    public static SetupPayloadVerificationResult Valid { get; } = new(
        true, null, "Setup payload verification succeeded.");

    public static SetupPayloadVerificationResult Rejected(
        string failureCode,
        SetupCleanupOutcome? cleanupOutcome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new SetupPayloadVerificationResult(
            false,
            failureCode,
            "Setup payload verification failed.",
            cleanupOutcome: cleanupOutcome);
    }

    internal static SetupPayloadVerificationResult VerifiedAttempt(
        SetupPayloadVerificationAttempt attempt) => new(
            true,
            null,
            "Setup payload verification succeeded.",
            attempt);

    public SetupCleanupOutcome Cleanup()
    {
        LastCleanupOutcome = Attempt?.Cleanup() ?? LastCleanupOutcome;
        return LastCleanupOutcome;
    }

    public void Dispose() => _ = Cleanup();
}

internal sealed class SetupPayloadVerificationAttempt : IDisposable
{
    private readonly SetupExtractionDirectory _extractionDirectory;

    public SetupPayloadVerificationAttempt(SetupExtractionDirectory extractionDirectory)
    {
        _extractionDirectory = extractionDirectory
            ?? throw new ArgumentNullException(nameof(extractionDirectory));
    }

    public SetupCleanupOutcome LastCleanupOutcome { get; private set; } =
        SetupCleanupOutcome.NotAttempted;

    public SetupCleanupOutcome Cleanup()
    {
        LastCleanupOutcome = _extractionDirectory.Cleanup();
        return LastCleanupOutcome;
    }

    public void Dispose() => _ = Cleanup();
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
        }

        using SetupPayloadVerificationResult extracted = VerifyAndExtract(
            manifest,
            embeddedPayloads);
        return extracted.IsValid
            ? SetupPayloadVerificationResult.Valid
            : SetupPayloadVerificationResult.Rejected(
                extracted.FailureCode!,
                extracted.LastCleanupOutcome);
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
                    return RejectAfterCleanup(
                        "tamperedPayloadLength",
                        extraction);
                }

                using Stream source = embedded.OpenRead();
                SetupExtractionResult extracted = extraction.CopyVerified(
                    source,
                    expected);
                if (!extracted.Succeeded)
                {
                    return RejectAfterCleanup(
                        extracted.FailureCode!,
                        extraction);
                }

                verified.Add(extracted.Payload!);
            }

            SetupPayloadSignatureEvidence signature = _signatureVerifier.Verify(
                manifest,
                verified.AsReadOnly());
            if (!signature.Trusted)
            {
                return RejectAfterCleanup(signature.FailureCode!, extraction);
            }

            SetupPayloadVerificationAttempt? attempt = null;
            try
            {
                attempt = new SetupPayloadVerificationAttempt(extraction);
                SetupPayloadVerificationResult result =
                    SetupPayloadVerificationResult.VerifiedAttempt(attempt);
                extraction = null;
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
            return RejectAfterCleanup("embeddedPayloadUnreadable", extraction);
        }
        finally
        {
            extraction?.Dispose();
        }
    }
#pragma warning restore CA1031

    private static SetupPayloadVerificationResult RejectAfterCleanup(
        string failureCode,
        SetupExtractionDirectory? extraction)
    {
        SetupCleanupOutcome outcome = extraction?.Cleanup()
            ?? SetupCleanupOutcome.NotAttempted;
        return SetupPayloadVerificationResult.Rejected(failureCode, outcome);
    }

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
        if (certificate is null || msix is null || inf is null
            || sys is null || catalog is null)
        {
            return SetupPayloadSignatureEvidence.Rejected("signatureEvidenceUnavailable");
        }

        SetupMsixSignatureEvidence msixEvidence = _probe.VerifyMsix(msix);
        if (!msixEvidence.SignatureValid)
        {
            return SetupPayloadSignatureEvidence.Rejected("msixSignatureInvalid");
        }
        SetupCertificateEvidence certificateEvidence = _probe.ReadCertificate(
            certificate);
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
            catalog,
            inf,
            sys);
        if (!driverEvidence.CatalogEntriesMatch)
        {
            return SetupPayloadSignatureEvidence.Rejected(
                "catalogMemberMismatch");
        }
        if (!driverEvidence.KernelPolicyValid
            || !driverEvidence.MemberTrustValid
            || !driverEvidence.Allowed)
        {
            return SetupPayloadSignatureEvidence.Rejected(
                "catalogKernelTrustInvalid");
        }

        return SetupPayloadSignatureEvidence.TrustedEvidence;
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
                "certificateEvidenceMismatch");
        }
        if (!certificate.ValidityValid)
        {
            return SetupPayloadSignatureEvidence.Rejected(
                "certificateEvidenceMismatch");
        }
        return string.Equals(
                certificate.Sha256Thumbprint,
                certificatePayload.Sha256,
                StringComparison.Ordinal)
            ? null
            : SetupPayloadSignatureEvidence.Rejected(
                "certificateEvidenceMismatch");
    }
#pragma warning restore CA1031
}
