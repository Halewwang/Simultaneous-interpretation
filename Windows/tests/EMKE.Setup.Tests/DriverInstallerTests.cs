using EMKE.Platform.Driver;
using EMKE.Setup.Platform;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class DriverInstallerTests
{
    private static readonly Guid TransactionId =
        new("22222222-3333-4444-5555-666666666666");
    private static readonly Version DriverVersion = new(1, 0, 0, 2);
    private const string HardwareId = "ROOT\\EMKEVIRTUALAUDIO";
    private const string PublishedInf = "oem42.inf";
    private const string DeviceInstanceId = "ROOT\\MEDIA\\0000";
    private const string MicrosoftSigner =
        "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [TestMethod]
    public void AbsentDriverInstallsAndRecordsExactPackageAndDevice()
    {
        using DriverPayloadFixture fixture = new();
        FakeDriverSetupApi api = new(DriverMachineState.Missing)
        {
            StateAfterInstall = ExactState(fixture.Catalog.Payload.Sha256),
        };
        DriverInstaller installer = CreateInstaller(api, fixture);

        DriverInstallResult result = installer.Install(
            fixture.Inf.Payload,
            fixture.Sys.Payload,
            fixture.Catalog.Payload,
            fixture.Contract,
            TransactionId);

        Assert.AreEqual(DriverInstallOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.Receipt);
        Assert.IsTrue(result.Receipt.PackageCreatedByAttempt);
        Assert.IsTrue(result.Receipt.DeviceCreatedByAttempt);
        Assert.AreEqual(1, api.InstallCalls);
        Assert.AreEqual(HardwareId, api.LastInstallHardwareId);
    }

    [TestMethod]
    public void ExactDriverAndDeviceArePreservedWithoutMutation()
    {
        using DriverPayloadFixture fixture = new();
        FakeDriverSetupApi api = new(ExactState(fixture.Catalog.Payload.Sha256));
        DriverInstaller installer = CreateInstaller(api, fixture);

        DriverInstallResult result = installer.Install(
            fixture.Inf.Payload,
            fixture.Sys.Payload,
            fixture.Catalog.Payload,
            fixture.Contract,
            TransactionId);

        Assert.AreEqual(DriverInstallOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.Receipt);
        Assert.IsFalse(result.Receipt.PackageCreatedByAttempt);
        Assert.IsFalse(result.Receipt.DeviceCreatedByAttempt);
        Assert.AreEqual(0, api.InstallCalls);
    }

    [TestMethod]
    public void ExactPackageWithAbsentDeviceCreatesOnlyTheDevice()
    {
        using DriverPayloadFixture fixture = new();
        DriverMachineState before = new(
            ExactState(fixture.Catalog.Payload.Sha256).Package,
            DriverDeviceState.Missing);
        FakeDriverSetupApi api = new(before)
        {
            StateAfterInstall = ExactState(fixture.Catalog.Payload.Sha256),
        };
        DriverInstaller installer = CreateInstaller(api, fixture);

        DriverInstallResult result = installer.Install(
            fixture.Inf.Payload,
            fixture.Sys.Payload,
            fixture.Catalog.Payload,
            fixture.Contract,
            TransactionId);

        Assert.AreEqual(DriverInstallOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.Receipt);
        Assert.IsFalse(result.Receipt.PackageCreatedByAttempt);
        Assert.IsTrue(result.Receipt.DeviceCreatedByAttempt);
    }

    [TestMethod]
    public void OlderNewerWrongSignerWrongHardwareIdAndUnrelatedDeviceAllBlock()
    {
        using DriverPayloadFixture fixture = new();
        DriverMachineState exact = ExactState(fixture.Catalog.Payload.Sha256);
        DriverMachineState[] incompatible =
        [
            exact with { Package = exact.Package with { Version = new Version(1, 0, 0, 1) } },
            exact with { Package = exact.Package with { Version = new Version(1, 0, 0, 3) } },
            exact with { Package = exact.Package with { SignerSubject = "CN=Other" } },
            exact with { Package = exact.Package with { HardwareId = "ROOT\\OTHER" } },
            exact with { Device = exact.Device with { HardwareId = "ROOT\\OTHER" } },
        ];

        foreach (DriverMachineState state in incompatible)
        {
            FakeDriverSetupApi api = new(state);
            DriverInstallResult result = CreateInstaller(api, fixture).Install(
                fixture.Inf.Payload,
                fixture.Sys.Payload,
                fixture.Catalog.Payload,
                fixture.Contract,
                TransactionId);

            Assert.AreEqual(DriverInstallOutcome.Blocked, result.Outcome);
            Assert.AreEqual("incompatibleDriverPresent", result.FailureCode);
            Assert.AreEqual(0, api.InstallCalls);
            Assert.AreEqual(0, api.RemoveDeviceCalls);
            Assert.AreEqual(0, api.RemovePackageCalls);
        }
    }

    [TestMethod]
    public void ElevatedCatalogReverificationRejectsBeforeSetupApiMutation()
    {
        using DriverPayloadFixture fixture = new();
        FakeDriverSetupApi api = new(DriverMachineState.Missing);
        FakeDriverPayloadTrustVerifier trust = new()
        {
            Evidence = new DriverPayloadTrustEvidence(
                KernelPolicyValid: false,
                CatalogEntriesMatch: true,
                MemberTrustValid: false,
                Allowed: false),
        };
        DriverInstaller installer = new(
            api,
            trust,
            MicrosoftDriverCatalogTrustPolicy.Instance,
            new RecordingRecoveryWriter());

        DriverInstallResult result = installer.Install(
            fixture.Inf.Payload,
            fixture.Sys.Payload,
            fixture.Catalog.Payload,
            fixture.Contract,
            TransactionId);

        Assert.AreEqual(DriverInstallOutcome.Blocked, result.Outcome);
        Assert.AreEqual("driverCatalogRejected", result.FailureCode);
        Assert.AreEqual(0, api.InstallCalls);
    }

    [TestMethod]
    public void RebootRequiredIsExplicitAndKeepsCreatedReceipt()
    {
        using DriverPayloadFixture fixture = new();
        FakeDriverSetupApi api = new(DriverMachineState.Missing)
        {
            StateAfterInstall = ExactState(fixture.Catalog.Payload.Sha256),
            InstallResult = new DriverNativeInstallResult(
                Succeeded: true,
                RebootRequired: true,
                PublishedInf,
                DeviceInstanceId,
                FailureCode: null),
        };

        DriverInstallResult result = CreateInstaller(api, fixture).Install(
            fixture.Inf.Payload,
            fixture.Sys.Payload,
            fixture.Catalog.Payload,
            fixture.Contract,
            TransactionId);

        Assert.AreEqual(DriverInstallOutcome.RebootRequired, result.Outcome);
        Assert.IsNotNull(result.Receipt);
        Assert.IsTrue(result.Receipt.PackageCreatedByAttempt);
        Assert.IsTrue(result.Receipt.DeviceCreatedByAttempt);
    }

    [TestMethod]
    public void PostInstallIdentityMismatchFailsWithRollbackReceipt()
    {
        using DriverPayloadFixture fixture = new();
        DriverMachineState changed = ExactState(fixture.Catalog.Payload.Sha256);
        changed = changed with
        {
            Package = changed.Package with { CatalogSha256 = new string('f', 64) },
        };
        FakeDriverSetupApi api = new(DriverMachineState.Missing)
        {
            StateAfterInstall = changed,
        };

        DriverInstallResult result = CreateInstaller(api, fixture).Install(
            fixture.Inf.Payload,
            fixture.Sys.Payload,
            fixture.Catalog.Payload,
            fixture.Contract,
            TransactionId);

        Assert.AreEqual(DriverInstallOutcome.Failed, result.Outcome);
        Assert.AreEqual("driverPostInstallMismatch", result.FailureCode);
        Assert.IsNotNull(result.Receipt);
        Assert.IsTrue(result.Receipt.PackageCreatedByAttempt);
        Assert.IsTrue(result.Receipt.DeviceCreatedByAttempt);
    }

    [TestMethod]
    public void RollbackRemovesStillMatchingCreatedDeviceBeforePackage()
    {
        using DriverPayloadFixture fixture = new();
        DriverMachineState exact = ExactState(fixture.Catalog.Payload.Sha256);
        FakeDriverSetupApi api = new(exact);
        RecordingRecoveryWriter recovery = new();
        DriverInstaller installer = CreateInstaller(api, fixture, recovery);
        DriverInstallReceipt receipt = new(
            exact.Package,
            exact.Device,
            PackageCreatedByAttempt: true,
            DeviceCreatedByAttempt: true);

        DriverRollbackResult result = installer.Rollback(receipt, TransactionId);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "device:" + DeviceInstanceId, "package:" + PublishedInf },
            api.RemovalOrder.ToArray());
        Assert.IsEmpty(recovery.Records);
    }

    [TestMethod]
    public void RollbackPreservesChangedDriverAndWritesRecoveryRecord()
    {
        using DriverPayloadFixture fixture = new();
        DriverMachineState exact = ExactState(fixture.Catalog.Payload.Sha256);
        DriverMachineState drifted = exact with
        {
            Device = exact.Device with { Version = new Version(9, 9, 9, 9) },
        };
        FakeDriverSetupApi api = new(drifted);
        RecordingRecoveryWriter recovery = new();
        DriverInstaller installer = CreateInstaller(api, fixture, recovery);
        DriverInstallReceipt receipt = new(
            exact.Package,
            exact.Device,
            PackageCreatedByAttempt: true,
            DeviceCreatedByAttempt: true);

        DriverRollbackResult result = installer.Rollback(receipt, TransactionId);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("driverRollbackIdentityChanged", result.FailureCode);
        Assert.AreEqual(0, api.RemoveDeviceCalls);
        Assert.AreEqual(0, api.RemovePackageCalls);
        Assert.HasCount(1, recovery.Records);
        Assert.AreEqual("driver", recovery.Records[0].Component);
    }

    private static DriverInstaller CreateInstaller(
        FakeDriverSetupApi api,
        DriverPayloadFixture fixture,
        RecordingRecoveryWriter? recovery = null) => new(
            api,
            new FakeDriverPayloadTrustVerifier(),
            MicrosoftDriverCatalogTrustPolicy.Instance,
            recovery ?? new RecordingRecoveryWriter());

    private static DriverMachineState ExactState(string catalogSha256) => new(
        new DriverPackageState(
            Present: true,
            PublishedInf,
            HardwareId,
            DriverVersion,
            catalogSha256,
            MicrosoftSigner,
            KernelTrustValid: true),
        new DriverDeviceState(
            Present: true,
            DeviceInstanceId,
            HardwareId,
            PublishedInf,
            DriverVersion,
            catalogSha256));

    private sealed class FakeDriverPayloadTrustVerifier
        : IDriverPayloadTrustVerifier
    {
        public DriverPayloadTrustEvidence Evidence { get; set; } = new(
            KernelPolicyValid: true,
            CatalogEntriesMatch: true,
            MemberTrustValid: true,
            Allowed: true);

        public DriverPayloadTrustEvidence Verify(
            VerifiedSetupPayload catalog,
            VerifiedSetupPayload inf,
            VerifiedSetupPayload sys)
        {
            Assert.IsNotNull(catalog);
            Assert.IsNotNull(inf);
            Assert.IsNotNull(sys);
            return Evidence;
        }
    }

    private sealed class FakeDriverSetupApi(DriverMachineState state)
        : IDriverSetupApi
    {
        private DriverMachineState _state = state;

        public DriverMachineState? StateAfterInstall { get; set; }

        public DriverNativeInstallResult InstallResult { get; set; } = new(
            Succeeded: true,
            RebootRequired: false,
            PublishedInf,
            DeviceInstanceId,
            FailureCode: null);

        public int InstallCalls { get; private set; }

        public int RemoveDeviceCalls { get; private set; }

        public int RemovePackageCalls { get; private set; }

        public string? LastInstallHardwareId { get; private set; }

        public List<string> RemovalOrder { get; } = [];

        public DriverMachineState ReadState(string hardwareId)
        {
            Assert.AreEqual(HardwareId, hardwareId);
            return _state;
        }

        public DriverNativeInstallResult Install(
            VerifiedSetupPayload inf,
            string hardwareId)
        {
            Assert.AreEqual(SetupPayloadKind.DriverInf, inf.ManifestPayload.Kind);
            InstallCalls++;
            LastInstallHardwareId = hardwareId;
            if (StateAfterInstall is not null)
            {
                _state = StateAfterInstall;
            }
            return InstallResult;
        }

        public bool RemoveDevice(string deviceInstanceId)
        {
            RemoveDeviceCalls++;
            RemovalOrder.Add("device:" + deviceInstanceId);
            _state = _state with { Device = DriverDeviceState.Missing };
            return true;
        }

        public bool RemovePackage(string publishedInfName)
        {
            RemovePackageCalls++;
            RemovalOrder.Add("package:" + publishedInfName);
            _state = DriverMachineState.Missing;
            return true;
        }
    }

    private sealed class DriverPayloadFixture : IDisposable
    {
        public DriverPayloadFixture()
        {
            Inf = Task4PayloadFixture.Create(
                SetupPayloadKind.DriverInf,
                "driver-inf",
                "EMKE.VirtualAudio.inf",
                0x31);
            Sys = Task4PayloadFixture.Create(
                SetupPayloadKind.DriverSys,
                "driver-sys",
                "EMKE.VirtualAudio.sys",
                0x32);
            Catalog = Task4PayloadFixture.Create(
                SetupPayloadKind.DriverCatalog,
                "driver-catalog",
                "EMKE.VirtualAudio.cat",
                0x33);
            Contract = new DriverInstallContract(
                HardwareId,
                DriverVersion,
                Inf.Payload.Sha256,
                Sys.Payload.Sha256,
                Catalog.Payload.Sha256);
        }

        public Task4PayloadFixture Inf { get; }

        public Task4PayloadFixture Sys { get; }

        public Task4PayloadFixture Catalog { get; }

        public DriverInstallContract Contract { get; }

        public void Dispose()
        {
            Catalog.Dispose();
            Sys.Dispose();
            Inf.Dispose();
        }
    }
}

#pragma warning restore CA1515
