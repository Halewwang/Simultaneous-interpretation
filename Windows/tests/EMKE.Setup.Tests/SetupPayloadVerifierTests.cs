using System.Runtime.InteropServices;
using System.Text;
using EMKE.Setup;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class SetupPayloadVerifierTests
{
    [DataTestMethod]
    [DataRow("application-msix", "tamperedPayloadHash")]
    [DataRow("driver-inf", "tamperedPayloadHash")]
    public void ChangedEmbeddedBytesAreRejectedAgainstManifestHash(
        string logicalName,
        string expectedFailure)
    {
        SetupPayloadVerifier verifier = new(TrustedSignatures());
        IReadOnlyList<SetupEmbeddedPayload> embedded = EmbeddedPayloads(
            logicalName,
            Encoding.UTF8.GetBytes("substituted"));

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
    public void DuplicateEmbeddedPayloadNameIsRejected()
    {
        SetupPayloadVerifier verifier = new(TrustedSignatures());
        List<SetupEmbeddedPayload> embedded = EmbeddedPayloads().ToList();
        embedded.Add(embedded[0]);

        SetupPayloadVerificationResult result = verifier.Verify(Manifest(), embedded);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("duplicateEmbeddedPayload", result.FailureCode);
    }

    [DataTestMethod]
    [DataRow("msixAuthenticodeInvalid")]
    [DataRow("msixPublisherMismatch")]
    [DataRow("certificateHashMismatch")]
    [DataRow("certificateSubjectMismatch")]
    [DataRow("certificateValidityInvalid")]
    [DataRow("certificateThumbprintMismatch")]
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
                "certificateThumbprintMismatch",
                privatePath,
                "certificate-secret")));

        SetupPayloadVerificationResult result = verifier.Verify(
            Manifest(),
            EmbeddedPayloads());

        Assert.AreEqual("certificateThumbprintMismatch", result.FailureCode);
        StringAssert.DoesNotContain(result.DisplayDetail, privatePath);
        StringAssert.DoesNotContain(result.DisplayDetail, "certificate-secret");
    }

    private static ISetupPayloadSignatureVerifier TrustedSignatures()
    {
        return new StaticSignatureVerifier(SetupPayloadSignatureEvidence.Trusted);
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
}

#pragma warning restore CA1515
