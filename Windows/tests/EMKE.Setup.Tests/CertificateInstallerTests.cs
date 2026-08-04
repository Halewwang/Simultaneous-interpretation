using System.Security.Cryptography;
using EMKE.Setup.Platform;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class CertificateInstallerTests
{
    private static readonly Guid TransactionId =
        new("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 1, 2, 3, TimeSpan.Zero);
    private const string Subject = "CN=EMKE Internal Test";
    private const string Thumbprint =
        "33E9992B08919BA6522F8A16B95CC2AA5DA6BB98";
    private const string OtherThumbprint =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public void AbsentExactCertificateIsAddedAndRecordedAsCreated()
    {
        using Task4PayloadFixture fixture = Task4PayloadFixture.Create(
            SetupPayloadKind.Certificate,
            "application-certificate",
            "EMKE-Translation-Windows-0.2.0-internal-x64.cer");
        FakeCertificatePlatform platform = ExactPlatform(fixture.Payload.Sha256);
        RecordingRecoveryWriter recovery = new();
        CertificateInstaller installer = new(platform, recovery);

        CertificateInstallResult result = installer.Install(
            fixture.Payload,
            Contract(fixture.Payload.Sha256),
            TransactionId);

        Assert.AreEqual(CertificateInstallOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.Receipt);
        Assert.IsTrue(result.Receipt.CreatedByAttempt);
        Assert.AreEqual(1, platform.AddCalls);
        Assert.AreEqual(Thumbprint, platform.Certificates.Single().Sha1Thumbprint);
        Assert.IsEmpty(recovery.Records);
    }

    [TestMethod]
    public void ExactPreExistingCertificateIsPreservedWithoutMutation()
    {
        using Task4PayloadFixture fixture = Task4PayloadFixture.Create(
            SetupPayloadKind.Certificate,
            "application-certificate",
            "EMKE-Translation-Windows-0.2.0-internal-x64.cer");
        FakeCertificatePlatform platform = ExactPlatform(fixture.Payload.Sha256);
        platform.Certificates.Add(new InstalledCertificateIdentity(
            Subject,
            Thumbprint));
        CertificateInstaller installer = new(platform, new RecordingRecoveryWriter());

        CertificateInstallResult result = installer.Install(
            fixture.Payload,
            Contract(fixture.Payload.Sha256),
            TransactionId);

        Assert.AreEqual(CertificateInstallOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.Receipt);
        Assert.IsFalse(result.Receipt.CreatedByAttempt);
        Assert.AreEqual(0, platform.AddCalls);
    }

    [TestMethod]
    public void DifferentCertificateForPinnedSubjectBlocksBeforeMutation()
    {
        using Task4PayloadFixture fixture = Task4PayloadFixture.Create(
            SetupPayloadKind.Certificate,
            "application-certificate",
            "EMKE-Translation-Windows-0.2.0-internal-x64.cer");
        FakeCertificatePlatform platform = ExactPlatform(fixture.Payload.Sha256);
        platform.Certificates.Add(new InstalledCertificateIdentity(
            Subject,
            OtherThumbprint));
        CertificateInstaller installer = new(platform, new RecordingRecoveryWriter());

        CertificateInstallResult result = installer.Install(
            fixture.Payload,
            Contract(fixture.Payload.Sha256),
            TransactionId);

        Assert.AreEqual(CertificateInstallOutcome.Blocked, result.Outcome);
        Assert.AreEqual("certificateConflict", result.FailureCode);
        Assert.AreEqual(0, platform.AddCalls);
        Assert.AreEqual(0, platform.RemoveCalls);
    }

    [TestMethod]
    public void PayloadWithChangedHashSubjectThumbprintValidityOrPrivateKeyIsRejected()
    {
        using Task4PayloadFixture fixture = Task4PayloadFixture.Create(
            SetupPayloadKind.Certificate,
            "application-certificate",
            "EMKE-Translation-Windows-0.2.0-internal-x64.cer");
        CertificatePayloadIdentity[] rejected =
        [
            new(Subject, Thumbprint, new string('f', 64), true, false),
            new("CN=Other", Thumbprint, fixture.Payload.Sha256, true, false),
            new(Subject, OtherThumbprint, fixture.Payload.Sha256, true, false),
            new(Subject, Thumbprint, fixture.Payload.Sha256, false, false),
            new(Subject, Thumbprint, fixture.Payload.Sha256, true, true),
        ];

        foreach (CertificatePayloadIdentity identity in rejected)
        {
            FakeCertificatePlatform platform = new(identity);
            CertificateInstallResult result = new CertificateInstaller(
                platform,
                new RecordingRecoveryWriter()).Install(
                    fixture.Payload,
                    Contract(fixture.Payload.Sha256),
                    TransactionId);

            Assert.AreEqual(CertificateInstallOutcome.Blocked, result.Outcome);
            Assert.AreEqual("certificatePayloadMismatch", result.FailureCode);
            Assert.AreEqual(0, platform.AddCalls);
        }
    }

    [TestMethod]
    public void RollbackRemovesOnlyACreatedCertificateThatStillMatches()
    {
        FakeCertificatePlatform platform = new(new CertificatePayloadIdentity(
            Subject,
            Thumbprint,
            new string('a', 64),
            true,
            false));
        platform.Certificates.Add(new InstalledCertificateIdentity(
            Subject,
            Thumbprint));
        RecordingRecoveryWriter recovery = new();
        CertificateInstaller installer = new(platform, recovery);
        CertificateInstallReceipt receipt = new(
            new InstalledCertificateIdentity(Subject, Thumbprint),
            CreatedByAttempt: true);

        CertificateRollbackResult result = installer.Rollback(
            receipt,
            TransactionId);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Removed);
        Assert.AreEqual(1, platform.RemoveCalls);
        Assert.IsEmpty(platform.Certificates);
        Assert.IsEmpty(recovery.Records);
    }

    [TestMethod]
    public void RollbackPreservesIdentityDriftAndWritesRecoveryRecord()
    {
        FakeCertificatePlatform platform = new(new CertificatePayloadIdentity(
            Subject,
            Thumbprint,
            new string('a', 64),
            true,
            false));
        platform.Certificates.Add(new InstalledCertificateIdentity(
            Subject,
            OtherThumbprint));
        RecordingRecoveryWriter recovery = new();
        CertificateInstaller installer = new(platform, recovery);
        CertificateInstallReceipt receipt = new(
            new InstalledCertificateIdentity(Subject, Thumbprint),
            CreatedByAttempt: true);

        CertificateRollbackResult result = installer.Rollback(
            receipt,
            TransactionId);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("certificateRollbackIdentityChanged", result.FailureCode);
        Assert.AreEqual(0, platform.RemoveCalls);
        Assert.HasCount(1, recovery.Records);
        Assert.AreEqual("certificate", recovery.Records[0].Component);
    }

    private static CertificateInstallContract Contract(string sha256) => new(
        Subject,
        Thumbprint,
        sha256,
        Now);

    private static FakeCertificatePlatform ExactPlatform(string sha256) => new(
        new CertificatePayloadIdentity(
            Subject,
            Thumbprint,
            sha256,
            ValidityValid: true,
            HasPrivateKey: false));

    private sealed class FakeCertificatePlatform(
        CertificatePayloadIdentity payloadIdentity) : ICertificatePlatform
    {
        public List<InstalledCertificateIdentity> Certificates { get; } = [];

        public int AddCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public CertificatePayloadIdentity InspectPayload(
            VerifiedSetupPayload payload)
        {
            Assert.IsNotNull(payload);
            return payloadIdentity;
        }

        public IReadOnlyList<InstalledCertificateIdentity> ReadTrustedPeople() =>
            Certificates.ToArray();

        public void AddTrustedPeople(VerifiedSetupPayload payload)
        {
            Assert.IsNotNull(payload);
            AddCalls++;
            Certificates.Add(new InstalledCertificateIdentity(
                payloadIdentity.Subject,
                payloadIdentity.Sha1Thumbprint));
        }

        public bool RemoveTrustedPeople(string sha1Thumbprint)
        {
            RemoveCalls++;
            int removed = Certificates.RemoveAll(certificate => string.Equals(
                certificate.Sha1Thumbprint,
                sha1Thumbprint,
                StringComparison.Ordinal));
            return removed == 1;
        }
    }
}

internal sealed class RecordingRecoveryWriter : ISetupRecoveryRecordWriter
{
    public List<SetupRecoveryRecord> Records { get; } = [];

    public void Write(SetupRecoveryRecord record) => Records.Add(record);
}

#pragma warning disable CA2000 // The fixture transfers handle ownership to the lease and owns the lease.
internal sealed class Task4PayloadFixture : IDisposable
{
    private readonly string _directory;

    private Task4PayloadFixture(string directory, VerifiedSetupPayload payload)
    {
        _directory = directory;
        Payload = payload;
    }

    public VerifiedSetupPayload Payload { get; }

    public static Task4PayloadFixture Create(
        SetupPayloadKind kind,
        string logicalName,
        string fileName,
        byte fill = 0x5a)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "EMKE.Setup.Task4.Tests",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        byte[] bytes = Enumerable.Repeat(fill, 128).ToArray();
        File.WriteAllBytes(path, bytes);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        VerifiedPayloadLease lease = new(handle, logicalName, path);
        SetupPayload manifest = new(
            logicalName,
            fileName,
            bytes.Length,
            sha256,
            kind);
        return new Task4PayloadFixture(
            directory,
            new VerifiedSetupPayload(manifest, bytes.Length, sha256, lease));
    }

    public void Dispose()
    {
        Payload.Lease.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
#pragma warning restore CA2000

#pragma warning restore CA1515
