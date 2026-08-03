using System.Security.Cryptography;
using EMKE.Setup;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
[TestCategory("WindowsSetupSignedPayload")]
public sealed class WindowsSignedPayloadFixtureTests
{
    private const string SignedMsixFixtureVariable =
        "EMKE_SETUP_SIGNED_MSIX_FIXTURE";
    private const string SigningCertificateFixtureVariable =
        "EMKE_SETUP_SIGNING_CER_FIXTURE";
    private const string ExpectedPublisher = "CN=EMKE Internal Test";

    [TestMethod]
    public void HeldSignedFixturesExposeMatchingMsixAndCertificateEvidence()
    {
        string signedMsixPath = RequireFixture(SignedMsixFixtureVariable);
        string signingCertificatePath = RequireFixture(
            SigningCertificateFixtureVariable);
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path,
            new Version(0, 2, 0, 0));
        VerifiedSetupPayload msix = CopyFixture(
            extraction,
            signedMsixPath,
            "application-msix",
            "EMKE-Translation-Windows-0.2.0-internal-x64.msix",
            SetupPayloadKind.Msix);
        VerifiedSetupPayload certificate = CopyFixture(
            extraction,
            signingCertificatePath,
            "application-certificate",
            "EMKE-Translation-Windows-0.2.0-internal-x64.cer",
            SetupPayloadKind.Certificate);

        SetupMsixSignatureEvidence msixEvidence =
            WindowsSetupSignatureProbe.Instance.VerifyMsix(msix);
        SetupCertificateEvidence certificateEvidence =
            WindowsSetupSignatureProbe.Instance.ReadCertificate(certificate);

        Assert.IsTrue(msixEvidence.SignatureValid);
        Assert.AreEqual(certificate.Sha256, msixEvidence.SignerSha256);
        Assert.AreEqual(certificate.Sha256, certificateEvidence.Sha256Thumbprint);
        Assert.AreEqual(ExpectedPublisher, certificateEvidence.Subject);
        Assert.IsTrue(certificateEvidence.ValidityValid);
        Assert.AreEqual(ExpectedPublisher, msixEvidence.IdentityPublisher);
    }

    private static string RequireFixture(string variableName)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            Assert.Fail($"{variableName} must name a real signed fixture.");
        }

        string fullPath = Path.GetFullPath(configuredPath);
        Assert.IsTrue(
            File.Exists(fullPath),
            $"The signed fixture does not exist: {fullPath}");
        return fullPath;
    }

    private static VerifiedSetupPayload CopyFixture(
        SetupExtractionDirectory extraction,
        string sourcePath,
        string logicalName,
        string fileName,
        SetupPayloadKind kind)
    {
        using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        long length = source.Length;
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        source.Position = 0;
        SetupPayload expected = new(
            logicalName,
            fileName,
            length,
            sha256,
            kind);

        SetupExtractionResult result = extraction.CopyVerified(source, expected);

        Assert.IsTrue(result.Succeeded, result.FailureCode);
        return result.Payload ?? throw new InvalidOperationException(
            $"Fixture '{logicalName}' did not produce a verified lease.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "EMKE.Setup.Tests",
                Guid.NewGuid().ToString("N"));
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
