using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EMKE.Setup;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class SetupPayloadVerifierTests
{
    [TestMethod]
    [DataRow("application-msix", "tamperedPayloadHash")]
    [DataRow("driver-inf", "tamperedPayloadHash")]
    public void ChangedEmbeddedBytesAreRejectedAgainstManifestHash(
        string logicalName,
        string expectedFailure)
    {
        SetupPayloadVerifier verifier = new(TrustedSignatures());
        byte[] changedBytes = Encoding.UTF8.GetBytes(logicalName);
        changedBytes[0] ^= 1;
        IReadOnlyList<SetupEmbeddedPayload> embedded = EmbeddedPayloads(
            logicalName,
            changedBytes);

        SetupPayloadVerificationResult result = verifier.Verify(Manifest(), embedded);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(expectedFailure, result.FailureCode);
    }

    [TestMethod]
    public void TamperedManifestHashCannotAuthorizeTheEmbeddedPayload()
    {
        SetupPayloadVerifier verifier = new(TrustedSignatures());
        List<SetupPayload> payloads = Payloads().ToList();
        payloads[0] = new SetupPayload(
            "application-msix",
            "EMKE-Translation-Windows-0.2.0-internal-x64.msix",
            16,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            SetupPayloadKind.Msix);
        SetupManifest tamperedManifest = new(
            "internal", new Version(0, 2, 0, 0),
            "EMKE.Translation.Internal_kvab4te83cr7p", "CN=EMKE Internal Test",
            19045, Architecture.X64, "ROOT\\EMKEVIRTUALAUDIO",
            new Version(1, 0, 0, 2), payloads);

        SetupPayloadVerificationResult result = verifier.Verify(
            tamperedManifest,
            EmbeddedPayloads());

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("tamperedPayloadHash", result.FailureCode);
    }

    [TestMethod]
    public void ChangedDeclaredLengthIsRejectedBeforeSignatureVerification()
    {
        SetupPayloadVerifier verifier = new(TrustedSignatures());
        IReadOnlyList<SetupEmbeddedPayload> embedded = EmbeddedPayloads();
        embedded = embedded.Select(payload => payload.LogicalName == "driver-sys"
            ? new SetupEmbeddedPayload(payload.LogicalName, payload.OpenRead, 9)
            : payload).ToArray();

        SetupPayloadVerificationResult result = verifier.Verify(Manifest(), embedded);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("tamperedPayloadLength", result.FailureCode);
    }

    [TestMethod]
    public void VerifyStopsReadingWhenAnEmbeddedStreamExceedsManifestLength()
    {
        SetupPayloadVerifier verifier = new(TrustedSignatures());
        List<SetupEmbeddedPayload> embedded = EmbeddedPayloads(
            "application-msix",
            Encoding.UTF8.GetBytes("application-msix!"));
        embedded[0] = new SetupEmbeddedPayload(
            embedded[0].LogicalName,
            embedded[0].OpenRead,
            declaredLength: 16);

        SetupPayloadVerificationResult result = verifier.Verify(Manifest(), embedded);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("tamperedPayloadLength", result.FailureCode);
    }

    [TestMethod]
    public void DuplicateEmbeddedPayloadNameIsRejected()
    {
        SetupPayloadVerifier verifier = new(TrustedSignatures());
        List<SetupEmbeddedPayload> embedded = EmbeddedPayloads().ToList();
        embedded[1] = embedded[0];

        SetupPayloadVerificationResult result = verifier.Verify(Manifest(), embedded);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("duplicateEmbeddedPayload", result.FailureCode);
    }

    [TestMethod]
    [DataRow("msixSignatureInvalid")]
    [DataRow("msixPublisherMismatch")]
    [DataRow("certificateEvidenceMismatch")]
    [DataRow("driverInfSubmissionHashMismatch")]
    [DataRow("driverSysSubmissionHashMismatch")]
    [DataRow("driverCatalogKernelTrustInvalid")]
    [DataRow("driverCatalogInfMemberMissing")]
    [DataRow("driverCatalogSysMemberMissing")]
    public void IncompleteOrMismatchedSignatureEvidenceIsRejected(
        string failureCode)
    {
        SetupPayloadVerifier verifier = new(new StaticSignatureVerifier(
            SetupPayloadSignatureEvidence.Rejected(failureCode)));

        SetupPayloadVerificationResult result = verifier.Verify(
            Manifest(),
            EmbeddedPayloads());

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(failureCode, result.FailureCode);
    }

    [TestMethod]
    public void CompletePinnedSignatureEvidenceIsAccepted()
    {
        SetupPayloadVerifier verifier = new(TrustedSignatures());

        SetupPayloadVerificationResult result = verifier.Verify(
            Manifest(),
            EmbeddedPayloads());

        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.FailureCode);
    }

    [TestMethod]
    public void VerificationFailureDoesNotExposeThePayloadPathOrCertificateSecret()
    {
        string privatePath = Path.Combine(
            Path.GetTempPath(),
            "setup-private", "certificate-secret.cer");
        SetupPayloadVerifier verifier = new(new StaticSignatureVerifier(
            SetupPayloadSignatureEvidence.Rejected(
                "certificateEvidenceMismatch",
                privatePath,
                "certificate-secret")));

        SetupPayloadVerificationResult result = verifier.Verify(
            Manifest(),
            EmbeddedPayloads());

        Assert.AreEqual("certificateEvidenceMismatch", result.FailureCode);
        Assert.IsFalse(result.DisplayDetail.Contains(privatePath, StringComparison.Ordinal));
        Assert.IsFalse(result.DisplayDetail.Contains("certificate-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(
        false,
        CertificateHash,
        "CN=EMKE Internal Test",
        true,
        CertificateHash,
        "msixSignatureInvalid")]
    [DataRow(
        true,
        DifferentCertificateHash,
        "CN=EMKE Internal Test",
        true,
        CertificateHash,
        "msixSignerMismatch")]
    [DataRow(
        true,
        CertificateHash,
        "CN=Other Publisher",
        true,
        CertificateHash,
        "certificateEvidenceMismatch")]
    [DataRow(
        true,
        CertificateHash,
        "CN=EMKE Internal Test",
        false,
        CertificateHash,
        "certificateEvidenceMismatch")]
    [DataRow(
        true,
        CertificateHash,
        "CN=EMKE Internal Test",
        true,
        DifferentCertificateHash,
        "certificateEvidenceMismatch")]
    public void ProductionSignatureVerifierUsesStableMsixAndCertificateFailures(
        bool signatureValid,
        string signerSha256,
        string certificateSubject,
        bool certificateValidityValid,
        string certificateSha256,
        string expectedFailure)
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        RecordingSignatureProbe probe = new(
            new SetupMsixSignatureEvidence(
                signatureValid,
                signerSha256,
                "CN=EMKE Internal Test"),
            new SetupCertificateEvidence(
                certificateSubject,
                certificateValidityValid,
                certificateSha256),
            new SetupDriverCatalogEvidence(trusted: true));
        WindowsSetupPayloadSignatureVerifier verifier = new(probe);

        SetupPayloadSignatureEvidence result = verifier.Verify(
            Manifest(),
            ExtractPayloads(extraction));

        Assert.IsFalse(result.Trusted);
        Assert.AreEqual(expectedFailure, result.FailureCode);
    }

    [TestMethod]
    public void ProductionSignatureVerifierRejectsMsixPublisherMismatchBeforeDriverTrust()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        RecordingSignatureProbe probe = new(
            new SetupMsixSignatureEvidence(
                signatureValid: true,
                signerSha256: CertificateHash,
                identityPublisher: "CN=Other Publisher"),
            new SetupCertificateEvidence(
                "CN=EMKE Internal Test",
                validityValid: true,
                sha256Thumbprint: CertificateHash),
            new SetupDriverCatalogEvidence(trusted: true));
        WindowsSetupPayloadSignatureVerifier verifier = new(probe);
        VerifiedSetupPayload[] payloads = ExtractPayloads(extraction);

        SetupPayloadSignatureEvidence result = verifier.Verify(
            Manifest(),
            payloads);

        Assert.IsFalse(result.Trusted);
        Assert.AreEqual("msixPublisherMismatch", result.FailureCode);
        Assert.AreSame(
            PayloadOfKind(payloads, SetupPayloadKind.Msix),
            probe.MsixPayload);
        Assert.AreSame(
            PayloadOfKind(payloads, SetupPayloadKind.Certificate),
            probe.CertificatePayload);
        Assert.IsNull(probe.CatalogPayload);
        Assert.IsNull(probe.InfPayload);
        Assert.IsNull(probe.SysPayload);
    }

    [TestMethod]
    public void ProductionSignatureVerifierAcceptsPinnedSignerPublisherAndCatalogEvidence()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        RecordingSignatureProbe probe = new(
            new SetupMsixSignatureEvidence(
                signatureValid: true,
                signerSha256: CertificateHash,
                identityPublisher: "CN=EMKE Internal Test"),
            new SetupCertificateEvidence(
                "CN=EMKE Internal Test",
                validityValid: true,
                sha256Thumbprint: CertificateHash),
            new SetupDriverCatalogEvidence(trusted: true));
        WindowsSetupPayloadSignatureVerifier verifier = new(probe);
        VerifiedSetupPayload[] payloads = ExtractPayloads(extraction);

        SetupPayloadSignatureEvidence result = verifier.Verify(
            Manifest(),
            payloads);

        Assert.IsTrue(result.Trusted);
        Assert.AreSame(
            PayloadOfKind(payloads, SetupPayloadKind.Msix),
            probe.MsixPayload);
        Assert.AreSame(
            PayloadOfKind(payloads, SetupPayloadKind.Certificate),
            probe.CertificatePayload);
        Assert.AreSame(
            PayloadOfKind(payloads, SetupPayloadKind.DriverCatalog),
            probe.CatalogPayload);
        Assert.AreSame(
            PayloadOfKind(payloads, SetupPayloadKind.DriverInf),
            probe.InfPayload);
        Assert.AreSame(
            PayloadOfKind(payloads, SetupPayloadKind.DriverSys),
            probe.SysPayload);
    }

    [TestMethod]
    public void VerifyAndExtractKeepsPayloadLeasesForProductionSignatureVerification()
    {
        using TemporaryDirectory temporary = new();
        RecordingSignatureProbe probe = new(
            new SetupMsixSignatureEvidence(true, CertificateHash, "CN=EMKE Internal Test"),
            new SetupCertificateEvidence("CN=EMKE Internal Test", true, CertificateHash),
            new SetupDriverCatalogEvidence(trusted: true));
        SetupPayloadVerifier verifier = new(
            new WindowsSetupPayloadSignatureVerifier(probe),
            version => SetupExtractionDirectory.Create(temporary.Path, version));

        using SetupPayloadVerificationResult result = verifier.VerifyAndExtract(
            Manifest(),
            EmbeddedPayloads());

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(probe.MsixPayload);
        Assert.IsNotNull(probe.CertificatePayload);
        Assert.IsNotNull(probe.CatalogPayload);
        Assert.IsNotNull(probe.InfPayload);
        Assert.IsNotNull(probe.SysPayload);
    }

    [TestMethod]
    public void SignatureRejectionAfterAllExtractionsExposesCompletedCleanup()
    {
        using TemporaryDirectory temporary = new();
        CountingSignatureVerifier signatures = new(
            SetupPayloadSignatureEvidence.Rejected("msixSignatureInvalid"));
        string? extractionRoot = null;
        SetupExtractionDirectory? capturedExtraction = null;
        SetupPayloadVerifier verifier = new(
            signatures,
            version =>
            {
                SetupExtractionDirectory extraction =
                    SetupExtractionDirectory.Create(temporary.Path, version);
                extractionRoot = extraction.RootPath;
                capturedExtraction = extraction;
                return extraction;
            });

        using SetupPayloadVerificationResult result = verifier.VerifyAndExtract(
            Manifest(),
            EmbeddedPayloads());

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("msixSignatureInvalid", result.FailureCode);
        Assert.AreEqual(5, signatures.ObservedPayloadCount);
        Assert.IsNotNull(extractionRoot);
        Assert.IsFalse(Directory.Exists(extractionRoot));
        SetupExtractionDirectory extraction = capturedExtraction
            ?? throw new InvalidOperationException("Extraction was not created.");
        Assert.AreSame(
            extraction.CleanupState,
            result.LastCleanupOutcome);
        Assert.AreSame(result.LastCleanupOutcome, result.Cleanup());
        Assert.IsTrue(result.LastCleanupOutcome.Completed);
        Assert.IsFalse(result.LastCleanupOutcome.ResidualRetained);
        Assert.IsEmpty(result.LastCleanupOutcome.RetainedLogicalNames);
    }

    [TestMethod]
    public void EmbeddedOpenFailurePreservesExactCatchCleanupOutcome()
    {
        using TemporaryDirectory temporary = new();
        SetupExtractionDirectory? capturedExtraction = null;
        SetupPayloadVerifier verifier = new(
            TrustedSignatures(),
            version =>
            {
                SetupExtractionDirectory extraction =
                    SetupExtractionDirectory.Create(temporary.Path, version);
                capturedExtraction = extraction;
                return extraction;
            });
        List<SetupEmbeddedPayload> embedded = EmbeddedPayloads();
        SetupEmbeddedPayload first = embedded[0];
        embedded[0] = new SetupEmbeddedPayload(
            first.LogicalName,
            () => throw new IOException("Injected embedded stream failure."),
            first.DeclaredLength);

        using SetupPayloadVerificationResult result = verifier.VerifyAndExtract(
            Manifest(),
            embedded);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("embeddedPayloadUnreadable", result.FailureCode);
        SetupExtractionDirectory extraction = capturedExtraction
            ?? throw new InvalidOperationException("Extraction was not created.");
        SetupCleanupOutcome expected = extraction.CleanupState;
        Assert.AreSame(expected, result.LastCleanupOutcome);
        Assert.AreSame(expected, result.Cleanup());
        Assert.IsTrue(expected.Completed);
        Assert.IsFalse(expected.ResidualRetained);
        Assert.IsEmpty(expected.RetainedLogicalNames);
        Assert.IsFalse(Directory.Exists(extraction.RootPath));
    }

    [TestMethod]
    public void SuccessfulAttemptCleanupIsTheResultCleanupOutcomeInstance()
    {
        using TemporaryDirectory temporary = new();
        string? extractionRoot = null;
        SetupPayloadVerifier verifier = new(
            TrustedSignatures(),
            version =>
            {
                SetupExtractionDirectory extraction =
                    SetupExtractionDirectory.Create(temporary.Path, version);
                extractionRoot = extraction.RootPath;
                return extraction;
            });
        using SetupPayloadVerificationResult result = verifier.VerifyAndExtract(
            Manifest(),
            EmbeddedPayloads());
        Assert.IsTrue(result.IsValid, result.FailureCode);

        SetupCleanupOutcome outcome = result.Attempt!.Cleanup();

        Assert.AreSame(outcome, result.LastCleanupOutcome);
        Assert.IsTrue(outcome.Completed);
        Assert.IsFalse(outcome.ResidualRetained);
        Assert.IsEmpty(outcome.RetainedLogicalNames);
        Assert.IsNotNull(extractionRoot);
        Assert.IsFalse(Directory.Exists(extractionRoot));
    }

    [TestMethod]
    public void MsixPublisherIsReadFromTheNamespacedIdentityElement()
    {
        using MemoryStream msix = new();
        using (ZipArchive archive = new(msix, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("AppxManifest.xml");
            using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
            writer.Write(
                "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">"
                + "<Identity Name=\"EMKE.Translation.Internal\" Publisher=\"CN=EMKE Internal Test\" "
                + "Version=\"0.2.0.0\" ProcessorArchitecture=\"x64\" />"
                + "</Package>");
        }
        msix.Position = 0;

        string publisher = WindowsSetupSignatureProbe.ReadMsixPublisher(msix);

        Assert.AreEqual("CN=EMKE Internal Test", publisher);
    }

    [TestMethod]
    public void MsixPublisherCanBeReadWhileExtractionOwnerLeaseIsHeld()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        byte[] msix = CreateMinimalMsix();
        SetupPayload payload = PayloadForBytes(
            "application-msix",
            "fixture.msix",
            msix,
            SetupPayloadKind.Msix);
        using MemoryStream msixSource = new(msix);
        SetupExtractionResult extracted = extraction.CopyVerified(
            msixSource,
            payload);

        using Stream heldReadView = extracted.Payload!.Lease.OpenReadView();
        string publisher = WindowsSetupSignatureProbe.ReadMsixPublisher(
            heldReadView);

        Assert.AreEqual("CN=EMKE Internal Test", publisher);
    }

    [TestMethod]
    public void TrustedMsixWithUnreadablePublisherPreservesSignatureEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        byte[] msix = CreateMsixWithoutManifest();
        SetupPayload expected = PayloadForBytes(
            "application-msix",
            "fixture.msix",
            msix,
            SetupPayloadKind.Msix);
        using MemoryStream source = new(msix);
        SetupExtractionResult extracted = extraction.CopyVerified(source, expected);
        WindowsSetupSignatureProbe probe = new(
            _ => new SetupMsixSignatureEvidence(
                signatureValid: true,
                CertificateHash,
                identityPublisher: null),
            WindowsSetupSignatureProbe.ReadMsixPublisher);

        SetupMsixSignatureEvidence evidence = probe.VerifyMsix(
            extracted.Payload!);

        Assert.IsTrue(evidence.SignatureValid);
        Assert.AreEqual(CertificateHash, evidence.SignerSha256);
        Assert.IsNull(evidence.IdentityPublisher);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProductionProbeRejectsUnsignedOrMalformedMsix(bool malformed)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        byte[] msix = malformed
            ? "not-an-msix"u8.ToArray()
            : CreateMinimalMsix();
        SetupPayload expected = PayloadForBytes(
            "application-msix",
            "fixture.msix",
            msix,
            SetupPayloadKind.Msix);
        using MemoryStream source = new(msix);
        SetupExtractionResult extracted = extraction.CopyVerified(source, expected);

        SetupMsixSignatureEvidence evidence = WindowsSetupSignatureProbe.Instance
            .VerifyMsix(extracted.Payload!);

        Assert.IsFalse(evidence.SignatureValid);
        Assert.IsNull(evidence.SignerSha256);
        Assert.IsNull(evidence.IdentityPublisher);
    }

    [TestMethod]
    public void CertificateCanBeReadWhileExtractionOwnerLeaseIsHeld()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=EMKE Internal Test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
        byte[] certificateBytes = certificate.Export(X509ContentType.Cert);
        SetupPayload payload = PayloadForBytes(
            "application-certificate",
            "fixture.cer",
            certificateBytes,
            SetupPayloadKind.Certificate);
        using MemoryStream certificateSource = new(certificateBytes);
        SetupExtractionResult extracted = extraction.CopyVerified(
            certificateSource,
            payload);

        SetupCertificateEvidence evidence = WindowsSetupSignatureProbe.Instance
            .ReadCertificate(extracted.Payload!);

        Assert.AreEqual("CN=EMKE Internal Test", evidence.Subject);
        Assert.IsTrue(evidence.ValidityValid);
    }

    private static byte[] CreateMinimalMsix()
    {
        using MemoryStream bytes = new();
        using (ZipArchive archive = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("AppxManifest.xml");
            using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
            writer.Write(
                "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">"
                + "<Identity Name=\"EMKE.Translation.Internal\" "
                + "Publisher=\"CN=EMKE Internal Test\" Version=\"0.2.0.0\" "
                + "ProcessorArchitecture=\"x64\" />"
                + "</Package>");
        }
        return bytes.ToArray();
    }

    private static byte[] CreateMsixWithoutManifest()
    {
        using MemoryStream bytes = new();
        using (ZipArchive archive = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("not-manifest.txt");
            using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
            writer.Write("missing AppxManifest.xml");
        }
        return bytes.ToArray();
    }

    private static SetupPayload PayloadForBytes(
        string logicalName,
        string fileName,
        byte[] bytes,
        SetupPayloadKind kind)
    {
        return new SetupPayload(
            logicalName,
            fileName,
            bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            kind);
    }

    private static StaticSignatureVerifier TrustedSignatures()
    {
        return new StaticSignatureVerifier(
            SetupPayloadSignatureEvidence.TrustedEvidence);
    }

    private static SetupManifest Manifest()
    {
        return new SetupManifest(
            "internal", new Version(0, 2, 0, 0),
            "EMKE.Translation.Internal_kvab4te83cr7p", "CN=EMKE Internal Test",
            19045, Architecture.X64, "ROOT\\EMKEVIRTUALAUDIO",
            new Version(1, 0, 0, 2), Payloads());
    }

    private static IReadOnlyList<SetupPayload> Payloads() =>
    [
        new("application-msix", "EMKE-Translation-Windows-0.2.0-internal-x64.msix", 16, "3519c43beb231dcbab153b916b232a2daf913552ef1cfeca4ca83bdbfb05b78e", SetupPayloadKind.Msix),
        new("application-certificate", "EMKE-Translation-Windows-0.2.0-internal-x64.cer", 23, "de6d47c98e8cc925adb5c33c64ce76321978ba7b1ba2ded1eef0d9417d01ef85", SetupPayloadKind.Certificate),
        new("driver-inf", "EMKE.VirtualAudio.inf", 10, "4dedc6dd0667ef467bcbdd8316b47e2619d0fa65a2f894a0428c87074a7eaa2d", SetupPayloadKind.DriverInf),
        new("driver-sys", "EMKE.VirtualAudio.sys", 10, "3ef98b837865f6da47d5e75f0261b11e2eaf9eade9d2347b15ba9d5429a1b0ae", SetupPayloadKind.DriverSys),
        new("driver-catalog", "EMKE.VirtualAudio.cat", 14, "24be15e1705c3debf4d6515daea506d70327452c6769585235e89b614cd1e68a", SetupPayloadKind.DriverCatalog),
    ];

    private const string CertificateHash =
        "de6d47c98e8cc925adb5c33c64ce76321978ba7b1ba2ded1eef0d9417d01ef85";
    private const string DifferentCertificateHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static VerifiedSetupPayload[] ExtractPayloads(
        SetupExtractionDirectory extraction)
    {
        return Payloads().Select(payload =>
        {
            using MemoryStream source = new(
                Encoding.UTF8.GetBytes(payload.LogicalName));
            SetupExtractionResult result = extraction.CopyVerified(source, payload);
            return result.Payload ?? throw new InvalidOperationException(
                $"Failed to extract test payload '{payload.LogicalName}'.");
        }).ToArray();
    }

    private static VerifiedSetupPayload PayloadOfKind(
        IReadOnlyList<VerifiedSetupPayload> payloads,
        SetupPayloadKind kind) => payloads.Single(
            payload => payload.ManifestPayload.Kind == kind);

    private static List<SetupEmbeddedPayload> EmbeddedPayloads(
        string? changedLogicalName = null,
        byte[]? changedBytes = null)
    {
        return Payloads().Select(payload =>
        {
            byte[] bytes = payload.LogicalName == changedLogicalName
                ? changedBytes!
                : Encoding.UTF8.GetBytes(payload.LogicalName);
            return new SetupEmbeddedPayload(
                payload.LogicalName,
                () => new MemoryStream(bytes, writable: false),
                bytes.Length);
        }).ToList();
    }

    private sealed class StaticSignatureVerifier(
        SetupPayloadSignatureEvidence evidence) : ISetupPayloadSignatureVerifier
    {
        public SetupPayloadSignatureEvidence Verify(
            SetupManifest manifest,
            IReadOnlyList<VerifiedSetupPayload> payloads) => evidence;
    }

    private sealed class CountingSignatureVerifier(
        SetupPayloadSignatureEvidence evidence) : ISetupPayloadSignatureVerifier
    {
        public int ObservedPayloadCount { get; private set; }

        public SetupPayloadSignatureEvidence Verify(
            SetupManifest manifest,
            IReadOnlyList<VerifiedSetupPayload> payloads)
        {
            ObservedPayloadCount = payloads.Count;
            return evidence;
        }
    }

    private sealed class RecordingSignatureProbe(
        SetupMsixSignatureEvidence msix,
        SetupCertificateEvidence certificate,
        SetupDriverCatalogEvidence catalog) : ISetupSignatureProbe
    {
        public VerifiedSetupPayload? MsixPayload { get; private set; }

        public VerifiedSetupPayload? CertificatePayload { get; private set; }

        public VerifiedSetupPayload? CatalogPayload { get; private set; }

        public VerifiedSetupPayload? InfPayload { get; private set; }

        public VerifiedSetupPayload? SysPayload { get; private set; }

        public SetupMsixSignatureEvidence VerifyMsix(VerifiedSetupPayload payload)
        {
            MsixPayload = payload;
            return msix;
        }

        public SetupCertificateEvidence ReadCertificate(
            VerifiedSetupPayload payload)
        {
            CertificatePayload = payload;
            return certificate;
        }

        public SetupDriverCatalogEvidence VerifyDriverCatalog(
            VerifiedSetupPayload catalogPayload,
            VerifiedSetupPayload infPayload,
            VerifiedSetupPayload sysPayload)
        {
            CatalogPayload = catalogPayload;
            InfPayload = infPayload;
            SysPayload = sysPayload;
            return catalog;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "EMKE.Setup.Tests", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

#pragma warning restore CA1515
