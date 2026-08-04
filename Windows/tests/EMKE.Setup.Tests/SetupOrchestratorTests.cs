using EMKE.Setup.Elevated;
using EMKE.Setup.Platform;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class SetupOrchestratorTests
{
    [TestMethod]
    public async Task SuccessfulSetupLaunchesOnlyInControlledModeAfterReadiness()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        MemoryPackageDeploymentApi packageApi = PackageTestData.Api();
        PackageInstaller packageInstaller = new(
            packageApi,
            new RecordingRecoveryWriter());
        RecordingMachineCoordinator machine = new(
            SetupMachineChangeResult.Succeeded(new SetupMachineChangeReceipt(
                new SetupMachineCreatedState(true, true, true))));
        FixedEndpointReadinessVerifier endpoints = new(
            EndpointVerificationResult.Succeeded(
                [
                    "meetingSpeakerRender",
                    "appSpeakerCapture",
                    "appMicrophoneRender",
                    "meetingMicrophoneCapture",
                ]));
        RecordingSetupApplicationLauncher launcher = new(machine);
        MemorySetupResumeRecordStore recovery = new();
        SetupOrchestrator orchestrator = new(
            machine,
            packageInstaller,
            endpoints,
            launcher,
            recovery);

        SetupResult result = await orchestrator.ExecuteAsync(
            Request(payload.Payload),
            CancellationToken.None);

        Assert.AreEqual(SetupOutcome.Succeeded, result.Outcome);
        Assert.HasCount(1, packageApi.InstalledPackages);
        Assert.AreEqual(SetupApplicationLaunchMode.ControlledNoTranslationConnect,
            launcher.LastMode);
        Assert.IsTrue(endpoints.VerificationCompletedBeforeLaunch);
        Assert.IsTrue(machine.CommitCompleted);
        Assert.IsEmpty(recovery.Records);
    }

    [TestMethod]
    public async Task EndpointFailureRollsBackUserAndMachineStateWithoutLaunch()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        MemoryPackageDeploymentApi packageApi = PackageTestData.Api();
        PackageInstaller packageInstaller = new(
            packageApi,
            new RecordingRecoveryWriter());
        RecordingMachineCoordinator machine = new(
            SetupMachineChangeResult.Succeeded(new SetupMachineChangeReceipt(
                new SetupMachineCreatedState(true, true, true))));
        FixedEndpointReadinessVerifier endpoints = new(
            EndpointVerificationResult.Rejected("virtualEndpointsIncomplete"));
        RecordingSetupApplicationLauncher launcher = new();
        SetupOrchestrator orchestrator = new(
            machine,
            packageInstaller,
            endpoints,
            launcher,
            new MemorySetupResumeRecordStore());

        SetupResult result = await orchestrator.ExecuteAsync(
            Request(payload.Payload),
            CancellationToken.None);

        Assert.AreEqual(SetupOutcome.Failed, result.Outcome);
        Assert.AreEqual("virtualEndpointsIncomplete", result.Detail);
        Assert.IsEmpty(packageApi.InstalledPackages);
        Assert.IsTrue(machine.RollbackCompleted);
        Assert.IsNull(launcher.LastMode);
    }

    [TestMethod]
    public async Task RebootPersistsAuthenticatedExactRecoveryWithoutRequestMacKey()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        SetupMachineCreatedState created = new(
            CertificateCreated: true,
            DriverPackageCreated: true,
            DriverDeviceCreated: true);
        RecordingMachineCoordinator machine = new(
            SetupMachineChangeResult.RebootRequired(
                new SetupMachineChangeReceipt(created)));
        MemoryPackageDeploymentApi packageApi = PackageTestData.Api();
        RecordingSetupApplicationLauncher launcher = new();
        MemorySetupResumeRecordStore recovery = new();
        SetupOrchestrator orchestrator = new(
            machine,
            new PackageInstaller(packageApi, new RecordingRecoveryWriter()),
            new FixedEndpointReadinessVerifier(
                EndpointVerificationResult.Rejected("notReached")),
            launcher,
            recovery);
        SetupOrchestrationRequest request = Request(payload.Payload);

        SetupResult result = await orchestrator.ExecuteAsync(
            request,
            CancellationToken.None);

        Assert.AreEqual(SetupOutcome.RebootRequired, result.Outcome);
        Assert.IsEmpty(packageApi.InstalledPackages);
        Assert.IsNull(launcher.LastMode);
        Assert.HasCount(1, recovery.Records);
        SetupResumeRecord record = recovery.Records[0];
        Assert.AreEqual(request.ElevationRequest.TransactionId, record.TransactionId);
        Assert.AreEqual(created, record.MachineCreatedState);
        Assert.AreEqual(
            request.ElevationRequest.PayloadHashes,
            record.PayloadHashes);
        Assert.AreEqual("verifyMachineAndInstallUserPackage", record.NextStep);
        Assert.IsFalse(record.CanonicalPayload.Contains(
            "macKey",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsGreaterThan(0, record.Authenticator.Length);
    }

    [TestMethod]
    public void RecoveryRecordRejectsCanonicalPayloadTamper()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        SetupOrchestrationRequest request = Request(payload.Payload);
        SetupResumeRecord record = SetupResumeRecord.Create(
            request.ElevationRequest.TransactionId,
            new SetupMachineCreatedState(true, true, true),
            request.ElevationRequest.PayloadHashes);
        string tampered = record.CanonicalPayload.Replace(
            "verifyMachineAndInstallUserPackage",
            "skipVerificationAndLaunch",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            SetupResumeRecord.ParseAndVerify(tampered, record.Authenticator));
    }

    [TestMethod]
    public async Task ResumeReverifiesRecordMachineAndPayloadBeforePackage()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        SetupOrchestrationRequest request = Request(payload.Payload);
        SetupMachineCreatedState created = new(true, true, true);
        SetupResumeRecord record = SetupResumeRecord.Create(
            request.ElevationRequest.TransactionId,
            created,
            request.ElevationRequest.PayloadHashes);
        RecordingMachineCoordinator machine = new(
            SetupMachineChangeResult.Failed("applyMustNotRun"))
        {
            ResumeVerificationResult = true,
        };
        MemoryPackageDeploymentApi packageApi = PackageTestData.Api();
        RecordingSetupApplicationLauncher launcher = new();
        MemorySetupResumeRecordStore recovery = new(record);
        SetupOrchestrator orchestrator = new(
            machine,
            new PackageInstaller(packageApi, new RecordingRecoveryWriter()),
            new FixedEndpointReadinessVerifier(
                EndpointVerificationResult.Succeeded(
                [
                    "meetingSpeakerRender",
                    "appSpeakerCapture",
                    "appMicrophoneRender",
                    "meetingMicrophoneCapture",
                ])),
            launcher,
            recovery);

        SetupResult result = await orchestrator.ResumeAsync(
            request,
            CancellationToken.None);

        Assert.AreEqual(SetupOutcome.Succeeded, result.Outcome);
        Assert.IsTrue(machine.ResumeVerificationCompleted);
        Assert.HasCount(1, packageApi.InstalledPackages);
        Assert.AreEqual(
            SetupApplicationLaunchMode.ControlledNoTranslationConnect,
            launcher.LastMode);
        Assert.IsEmpty(recovery.Records);
    }

    private static SetupOrchestrationRequest Request(VerifiedSetupPayload msix)
    {
        DateTimeOffset expires = new(
            2026,
            8,
            4,
            12,
            0,
            0,
            TimeSpan.Zero);
        SetupElevationPayloadHashes hashes = new(
            msix.Sha256,
            new string('2', 64),
            new string('3', 64),
            new string('4', 64),
            new string('5', 64));
        SetupElevationRequest elevation = new(
            new string('6', 64),
            Guid.Parse("e9464ca5-910e-4baf-8ea6-57ca26c0e8f1"),
            new SetupExtractionRootIdentity(
                @"C:\ProgramData\EMKE\Setup\0.2.0\fixture",
                volumeSerialNumber: 1,
                fileIndexHigh: 2,
                fileIndexLow: 3,
                fileAttributes: (uint)FileAttributes.Directory),
            expires,
            new string('7', 64),
            "33E9992B08919BA6522F8A16B95CC2AA5DA6BB98",
            @"ROOT\EMKEVIRTUALAUDIO",
            new Version(1, 0, 0, 2),
            hashes);
        return new SetupOrchestrationRequest(
            elevation,
            msix,
            PackageTestData.Contract(),
            PackageTestData.Sid);
    }
}

internal sealed class RecordingMachineCoordinator(SetupMachineChangeResult result)
    : ISetupMachineChangeCoordinator
{
    public bool CommitCompleted { get; private set; }

    public bool RollbackCompleted { get; private set; }

    public bool ResumeVerificationResult { get; set; }

    public bool ResumeVerificationCompleted { get; private set; }

    public Task<SetupMachineChangeResult> ApplyAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    public Task<bool> RollbackAsync(
        SetupMachineChangeReceipt receipt,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RollbackCompleted = true;
        return Task.FromResult(true);
    }

    public Task<bool> CommitAsync(
        SetupMachineChangeReceipt receipt,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitCompleted = true;
        return Task.FromResult(true);
    }

    public Task<bool> VerifyResumeAsync(
        SetupMachineChangeReceipt receipt,
        SetupElevationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResumeVerificationCompleted = true;
        return Task.FromResult(ResumeVerificationResult);
    }
}

internal sealed class FixedEndpointReadinessVerifier(EndpointVerificationResult result)
    : IEndpointReadinessVerifier
{
    public bool VerificationCompletedBeforeLaunch { get; private set; }

    public Task<EndpointVerificationResult> VerifyAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerificationCompletedBeforeLaunch = true;
        return Task.FromResult(result);
    }
}

internal sealed class RecordingSetupApplicationLauncher(
    RecordingMachineCoordinator? machine = null)
    : ISetupApplicationLauncher
{
    public SetupApplicationLaunchMode? LastMode { get; private set; }

    public Task LaunchAsync(
        SetupApplicationLaunchMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (machine is not null && !machine.CommitCompleted)
        {
            throw new InvalidOperationException(
                "Machine changes must be committed before launch.");
        }
        LastMode = mode;
        return Task.CompletedTask;
    }
}

internal sealed class MemorySetupResumeRecordStore : ISetupResumeRecordStore
{
    public MemorySetupResumeRecordStore()
    {
    }

    public MemorySetupResumeRecordStore(SetupResumeRecord record)
    {
        Records.Add(record);
    }

    public List<SetupResumeRecord> Records { get; } = [];

    public void Write(SetupResumeRecord record) => Records.Add(record);

    public SetupResumeRecord ReadVerified(Guid transactionId) => Records.Single(
        record => record.TransactionId == transactionId);

    public void Delete(Guid transactionId) => Records.RemoveAll(
        record => record.TransactionId == transactionId);
}

#pragma warning restore CA1515
#pragma warning restore CA2007
