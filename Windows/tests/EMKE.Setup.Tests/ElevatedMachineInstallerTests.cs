using EMKE.Setup.Elevated;
using EMKE.Setup.Platform;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class ElevatedMachineInstallerTests
{
    private static readonly string[] ExpectedRollbackOrder =
        ["driver", "certificate"];

    [TestMethod]
    public async Task ExactMachineInstallReturnsSuccessWithoutRollback()
    {
        using MachinePayloadFixture payloads = new();
        FakeCertificateMachineInstaller certificate = new(payloads.CertificateReceipt);
        FakeDriverMachineInstaller driver = new(payloads.DriverReceipt);
        ElevatedMachineInstaller installer = new(
            new FixedMachinePayloadSource(payloads.Payloads),
            certificate,
            driver);

        SetupElevatedHelperOutcome outcome = await installer.HandleAsync(
            payloads.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevatedHelperOutcome.Succeeded, outcome);
        Assert.AreEqual(1, certificate.InstallCalls);
        Assert.AreEqual(1, driver.InstallCalls);
        Assert.AreEqual(0, certificate.RollbackCalls);
        Assert.AreEqual(0, driver.RollbackCalls);
    }

    [TestMethod]
    public async Task DriverRebootRequirementIsReturnedWithoutRollback()
    {
        using MachinePayloadFixture payloads = new();
        FakeCertificateMachineInstaller certificate = new(payloads.CertificateReceipt);
        FakeDriverMachineInstaller driver = new(payloads.DriverReceipt)
        {
            InstallOutcome = DriverInstallOutcome.RebootRequired,
        };
        ElevatedMachineInstaller installer = new(
            new FixedMachinePayloadSource(payloads.Payloads),
            certificate,
            driver);

        SetupElevatedHelperOutcome outcome = await installer.HandleAsync(
            payloads.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevatedHelperOutcome.RebootRequired, outcome);
        Assert.AreEqual(0, certificate.RollbackCalls);
        Assert.AreEqual(0, driver.RollbackCalls);
    }

    [TestMethod]
    public async Task PreparedMachineChangeReportsCreatedStateAndRollsBackOnFinalization()
    {
        using MachinePayloadFixture payloads = new();
        List<string> order = [];
        FakeCertificateMachineInstaller certificate = new(
            payloads.CertificateReceipt,
            order);
        FakeDriverMachineInstaller driver = new(payloads.DriverReceipt, order);
        ElevatedMachineInstaller installer = new(
            new FixedMachinePayloadSource(payloads.Payloads),
            certificate,
            driver);

        SetupElevatedPreparedChange prepared = await installer.PrepareAsync(
            payloads.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevatedHelperOutcome.Succeeded, prepared.Outcome);
        Assert.AreEqual(
            new SetupMachineCreatedState(true, true, true),
            prepared.CreatedState);
        Assert.AreEqual(0, certificate.RollbackCalls);
        Assert.AreEqual(0, driver.RollbackCalls);
        Assert.IsTrue(await installer.FinalizeAsync(
            prepared,
            SetupElevationFinalizationAction.Rollback,
            MachinePayloadFixture.TransactionId,
            CancellationToken.None));
        CollectionAssert.AreEqual(ExpectedRollbackOrder, order.ToArray());
    }

    [TestMethod]
    public async Task CertificateBlockStopsBeforeDriverMutation()
    {
        using MachinePayloadFixture payloads = new();
        FakeCertificateMachineInstaller certificate = new(payloads.CertificateReceipt)
        {
            InstallOutcome = CertificateInstallOutcome.Blocked,
        };
        FakeDriverMachineInstaller driver = new(payloads.DriverReceipt);
        ElevatedMachineInstaller installer = new(
            new FixedMachinePayloadSource(payloads.Payloads),
            certificate,
            driver);

        SetupElevatedHelperOutcome outcome = await installer.HandleAsync(
            payloads.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevatedHelperOutcome.Failed, outcome);
        Assert.AreEqual(0, driver.InstallCalls);
        Assert.AreEqual(0, certificate.RollbackCalls);
    }

    [TestMethod]
    public async Task PartialCertificateFailureRollsBackBeforeDriverMutation()
    {
        using MachinePayloadFixture payloads = new();
        FakeCertificateMachineInstaller certificate = new(payloads.CertificateReceipt)
        {
            InstallOutcome = CertificateInstallOutcome.Failed,
            ReturnReceiptOnFailure = true,
        };
        FakeDriverMachineInstaller driver = new(payloads.DriverReceipt);
        ElevatedMachineInstaller installer = new(
            new FixedMachinePayloadSource(payloads.Payloads),
            certificate,
            driver);

        SetupElevatedHelperOutcome outcome = await installer.HandleAsync(
            payloads.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevatedHelperOutcome.Failed, outcome);
        Assert.AreEqual(0, driver.InstallCalls);
        Assert.AreEqual(1, certificate.RollbackCalls);
    }

    [TestMethod]
    public async Task PartialDriverFailureRollsBackDriverThenCertificate()
    {
        using MachinePayloadFixture payloads = new();
        List<string> order = [];
        FakeCertificateMachineInstaller certificate = new(
            payloads.CertificateReceipt,
            order);
        FakeDriverMachineInstaller driver = new(payloads.DriverReceipt, order)
        {
            InstallOutcome = DriverInstallOutcome.Failed,
        };
        ElevatedMachineInstaller installer = new(
            new FixedMachinePayloadSource(payloads.Payloads),
            certificate,
            driver);

        SetupElevatedHelperOutcome outcome = await installer.HandleAsync(
            payloads.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevatedHelperOutcome.Failed, outcome);
        CollectionAssert.AreEqual(
            ExpectedRollbackOrder,
            order.ToArray());
    }

    [TestMethod]
    public async Task RollbackFailureNeverChangesFailedOutcomeOrSkipsRemainingRollback()
    {
        using MachinePayloadFixture payloads = new();
        FakeCertificateMachineInstaller certificate = new(payloads.CertificateReceipt)
        {
            RollbackSucceeds = false,
        };
        FakeDriverMachineInstaller driver = new(payloads.DriverReceipt)
        {
            InstallOutcome = DriverInstallOutcome.Failed,
            RollbackSucceeds = false,
        };
        ElevatedMachineInstaller installer = new(
            new FixedMachinePayloadSource(payloads.Payloads),
            certificate,
            driver);

        SetupElevatedHelperOutcome outcome = await installer.HandleAsync(
            payloads.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevatedHelperOutcome.Failed, outcome);
        Assert.AreEqual(1, driver.RollbackCalls);
        Assert.AreEqual(1, certificate.RollbackCalls);
    }

    private sealed class FixedMachinePayloadSource(ElevatedMachinePayloadSet payloads)
        : IElevatedMachinePayloadSource
    {
        public ElevatedMachinePayloadSet Open(SetupElevationRequest request)
        {
            Assert.IsNotNull(request);
            return payloads;
        }
    }

    private sealed class FakeCertificateMachineInstaller(
        CertificateInstallReceipt receipt,
        List<string>? rollbackOrder = null) : ICertificateMachineInstaller
    {
        public CertificateInstallOutcome InstallOutcome { get; set; } =
            CertificateInstallOutcome.Succeeded;

        public bool RollbackSucceeds { get; set; } = true;

        public bool ReturnReceiptOnFailure { get; set; }

        public int InstallCalls { get; private set; }

        public int RollbackCalls { get; private set; }

        public CertificateInstallResult Install(
            VerifiedSetupPayload certificate,
            CertificateInstallContract contract,
            Guid transactionId)
        {
            Assert.IsNotNull(certificate);
            Assert.AreEqual(MachinePayloadFixture.TransactionId, transactionId);
            InstallCalls++;
            return new CertificateInstallResult(
                InstallOutcome,
                InstallOutcome == CertificateInstallOutcome.Succeeded
                    || ReturnReceiptOnFailure
                    ? receipt
                    : null,
                InstallOutcome == CertificateInstallOutcome.Succeeded
                    ? null
                    : "certificateBlocked");
        }

        public CertificateRollbackResult Rollback(
            CertificateInstallReceipt installReceipt,
            Guid transactionId)
        {
            Assert.AreEqual(receipt, installReceipt);
            Assert.AreEqual(MachinePayloadFixture.TransactionId, transactionId);
            RollbackCalls++;
            rollbackOrder?.Add("certificate");
            return new CertificateRollbackResult(
                RollbackSucceeds,
                Removed: RollbackSucceeds,
                RollbackSucceeds ? null : "certificateRollbackFailed");
        }
    }

    private sealed class FakeDriverMachineInstaller(
        DriverInstallReceipt receipt,
        List<string>? rollbackOrder = null) : IDriverMachineInstaller
    {
        public DriverInstallOutcome InstallOutcome { get; set; } =
            DriverInstallOutcome.Succeeded;

        public bool RollbackSucceeds { get; set; } = true;

        public int InstallCalls { get; private set; }

        public int RollbackCalls { get; private set; }

        public DriverInstallResult Install(
            VerifiedSetupPayload inf,
            VerifiedSetupPayload sys,
            VerifiedSetupPayload catalog,
            DriverInstallContract contract,
            Guid transactionId)
        {
            Assert.IsNotNull(inf);
            Assert.IsNotNull(sys);
            Assert.IsNotNull(catalog);
            Assert.AreEqual(MachinePayloadFixture.TransactionId, transactionId);
            InstallCalls++;
            return new DriverInstallResult(
                InstallOutcome,
                receipt,
                InstallOutcome is DriverInstallOutcome.Succeeded
                    or DriverInstallOutcome.RebootRequired
                    ? null
                    : "driverFailed");
        }

        public DriverRollbackResult Rollback(
            DriverInstallReceipt installReceipt,
            Guid transactionId)
        {
            Assert.AreEqual(receipt, installReceipt);
            Assert.AreEqual(MachinePayloadFixture.TransactionId, transactionId);
            RollbackCalls++;
            rollbackOrder?.Add("driver");
            return new DriverRollbackResult(
                RollbackSucceeds,
                RollbackSucceeds ? null : "driverRollbackFailed");
        }
    }

    private sealed class MachinePayloadFixture : IDisposable
    {
        public static readonly Guid TransactionId =
            new("00112233-4455-6677-8899-aabbccddeeff");
        private const string CertificateThumbprint =
            "33E9992B08919BA6522F8A16B95CC2AA5DA6BB98";
        private const string HardwareId = "ROOT\\EMKEVIRTUALAUDIO";
        private static readonly Version DriverVersion = new(1, 0, 0, 2);

        public MachinePayloadFixture()
        {
            Certificate = Task4PayloadFixture.Create(
                SetupPayloadKind.Certificate,
                "application-certificate",
                "EMKE-Translation-Windows-0.2.0-internal-x64.cer",
                0x20);
            Inf = Task4PayloadFixture.Create(
                SetupPayloadKind.DriverInf,
                "driver-inf",
                "EMKE.VirtualAudio.inf",
                0x21);
            Sys = Task4PayloadFixture.Create(
                SetupPayloadKind.DriverSys,
                "driver-sys",
                "EMKE.VirtualAudio.sys",
                0x22);
            Catalog = Task4PayloadFixture.Create(
                SetupPayloadKind.DriverCatalog,
                "driver-catalog",
                "EMKE.VirtualAudio.cat",
                0x23);
            Payloads = new ElevatedMachinePayloadSet(
                Certificate.Payload,
                Inf.Payload,
                Sys.Payload,
                Catalog.Payload,
                ownsPayloads: false);
            Request = new SetupElevationRequest(
                new string('0', 64),
                TransactionId,
                new SetupExtractionRootIdentity(
                    "C:\\ProgramData\\EMKE\\Setup\\0.2.0-test",
                    1,
                    2,
                    3,
                    0x10),
                new DateTimeOffset(2026, 8, 4, 1, 3, 0, TimeSpan.Zero),
                new string('1', 64),
                CertificateThumbprint,
                HardwareId,
                DriverVersion,
                new SetupElevationPayloadHashes(
                    new string('2', 64),
                    Certificate.Payload.Sha256,
                    Inf.Payload.Sha256,
                    Sys.Payload.Sha256,
                    Catalog.Payload.Sha256));
            CertificateReceipt = new CertificateInstallReceipt(
                new InstalledCertificateIdentity(
                    "CN=EMKE Internal Test",
                    CertificateThumbprint),
                CreatedByAttempt: true);
            DriverPackageState package = new(
                Present: true,
                "oem42.inf",
                HardwareId,
                DriverVersion,
                Catalog.Payload.Sha256,
                "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation",
                KernelTrustValid: true);
            DriverDeviceState device = new(
                Present: true,
                "ROOT\\MEDIA\\0000",
                HardwareId,
                "oem42.inf",
                DriverVersion,
                Catalog.Payload.Sha256);
            DriverReceipt = new DriverInstallReceipt(
                package,
                device,
                PackageCreatedByAttempt: true,
                DeviceCreatedByAttempt: true);
        }

        public Task4PayloadFixture Certificate { get; }

        public Task4PayloadFixture Inf { get; }

        public Task4PayloadFixture Sys { get; }

        public Task4PayloadFixture Catalog { get; }

        public ElevatedMachinePayloadSet Payloads { get; }

        public SetupElevationRequest Request { get; }

        public CertificateInstallReceipt CertificateReceipt { get; }

        public DriverInstallReceipt DriverReceipt { get; }

        public void Dispose()
        {
            Payloads.Dispose();
            Catalog.Dispose();
            Sys.Dispose();
            Inf.Dispose();
            Certificate.Dispose();
        }
    }
}

#pragma warning restore CA1515
#pragma warning restore CA2007
