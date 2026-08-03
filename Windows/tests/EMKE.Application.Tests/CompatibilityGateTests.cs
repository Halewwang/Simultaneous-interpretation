using EMKE.Application;
using EMKE.Core;

namespace EMKE.Application.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // Runtime ownership transfers to await using.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class CompatibilityGateTests
{
    private static readonly string[] CompleteEndpointRoles =
    [
        "meetingSpeakerRender",
        "appSpeakerCapture",
        "appMicrophoneRender",
        "meetingMicrophoneCapture",
    ];

    private static readonly string[] HostOnlyTrace = ["os"];

    [TestMethod]
    [DataRow(26199, true, true, 1, "0.1.0", 4, "unsupportedWindowsBuild", false)]
    [DataRow(26200, false, false, 0, "0.0.0", 0, "driverMissing", false)]
    [DataRow(26200, true, false, 1, "0.1.0", 4, "driverSignatureInvalid", false)]
    [DataRow(26200, true, true, 2, "0.1.0", 4, "driverAbiMismatch", false)]
    [DataRow(26200, true, true, 1, "0.0.9", 4, "driverBelowMinimum", false)]
    [DataRow(26200, true, true, 1, "0.1.0", 3, "virtualEndpointsIncomplete", false)]
    [DataRow(26200, true, true, 1, "0.1.0", 4, "compatible", true)]
    public void EvaluationUsesStableReasonsInFailClosedOrder(
        int windowsBuild,
        bool present,
        bool signatureValid,
        int abi,
        string version,
        int endpointCount,
        string expectedReason,
        bool expectedAllowed)
    {
        CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
            CreateManifest(),
            windowsBuild,
            CreateEvidence(
                present,
                signatureValid,
                abi,
                Version.Parse(version),
                endpointCount));

        Assert.AreEqual(expectedAllowed, decision.Allowed);
        Assert.AreEqual(expectedReason, decision.Reason);
        Assert.AreEqual(expectedAllowed, decision.CanStart);
    }

    [TestMethod]
    public void CompatibleDriverBelowRecommendedIsAllowedWithWarning()
    {
        CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
            CreateManifest(recommendedDriverVersion: new Version(0, 2, 0)),
            currentWindowsBuild: 26200,
            CreateEvidence(
                present: true,
                signatureValid: true,
                abi: 1,
                version: new Version(0, 1, 0),
                endpointCount: 4));

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual("compatibleUpdateRecommended", decision.Reason);
        Assert.IsTrue(decision.UpdateRecommended);
    }

    [TestMethod]
    [DataRow(0, "disabled")]
    [DataRow(0, "inactive")]
    [DataRow(0, "missing")]
    [DataRow(1, "disabled")]
    [DataRow(1, "inactive")]
    [DataRow(1, "missing")]
    [DataRow(2, "disabled")]
    [DataRow(2, "inactive")]
    [DataRow(2, "missing")]
    [DataRow(3, "disabled")]
    [DataRow(3, "inactive")]
    [DataRow(3, "missing")]
    public void EveryRequiredEndpointRoleMustBeActive(
        int endpointIndex,
        string state)
    {
        InstalledDriverEndpointEvidence[] endpoints =
            CompleteEndpointRoles
                .Select(static role =>
                    new InstalledDriverEndpointEvidence(role, "active"))
                .ToArray();
        endpoints[endpointIndex] = new InstalledDriverEndpointEvidence(
            endpoints[endpointIndex].Role,
            state);

        CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
            CreateManifest(),
            currentWindowsBuild: 26200,
            CreateEvidence(endpoints));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("virtualEndpointsIncomplete", decision.Reason);
    }

    [TestMethod]
    public void DuplicateRequiredEndpointRoleFailsClosed()
    {
        InstalledDriverEndpointEvidence[] endpoints =
        [
            new("meetingSpeakerRender", "active"),
            new("appSpeakerCapture", "active"),
            new("appMicrophoneRender", "active"),
            new("appMicrophoneRender", "active"),
        ];

        CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
            CreateManifest(),
            currentWindowsBuild: 26200,
            CreateEvidence(endpoints));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("virtualEndpointsIncomplete", decision.Reason);
    }

    [TestMethod]
    public void UnavailableDriverPackageDisablesRepairAction()
    {
        CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
            CreateManifest(driverPackageAvailable: false),
            currentWindowsBuild: 26200,
            CreateEvidence(
                present: false,
                signatureValid: false,
                abi: 0,
                version: new Version(0, 0, 0),
                endpointCount: 0));

        Assert.IsFalse(decision.RepairAvailable);
        Assert.AreEqual("driverMissing", decision.Reason);
    }

    [TestMethod]
    public async Task DriverMissingBlocksStartBeforeNetworkOrNativeAudio()
    {
        CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
            CreateManifest(driverPackageAvailable: false),
            currentWindowsBuild: 26200,
            CreateEvidence(
                present: false,
                signatureValid: false,
                abi: 0,
                version: new Version(0, 0, 0),
                endpointCount: 0));
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.DriverCompatibility = decision.ToDriverCompatibility();
        await using TranslationRuntime runtime = harness.CreateRuntime();

        RuntimeError? error = await runtime.StartAsync().ConfigureAwait(false);

        Assert.IsNotNull(error);
        Assert.AreEqual(ErrorCategory.Driver, error.Category);
        Assert.AreEqual("translationRuntime.driverIncompatible", error.Code);
        Assert.AreEqual(RecoveryAction.ReportCompatibility, error.RecoveryAction);
        Assert.HasCount(0, error.Parameters);
        Assert.IsFalse(decision.CanStart);
        Assert.AreEqual(0, harness.SessionCreateCount);
        Assert.AreEqual(0, harness.AudioStartCount);
        Assert.AreEqual(InboundRoute.Stopped, runtime.CurrentSnapshot.InboundRoute);
        Assert.AreEqual(OutboundRoute.Stopped, runtime.CurrentSnapshot.OutboundRoute);
        Assert.AreEqual(
            "driverMissing",
            runtime.CurrentSnapshot.DriverCompatibility.StatusLabel);
        Assert.IsFalse(
            runtime.CurrentSnapshot.DriverCompatibility.RepairAvailable);
    }

    [TestMethod]
    public void EmbeddedInternalJsonIsParsedStrictlyWithoutAllowAnyFallback()
    {
        const string json = """
            {
              "appVersion": "0.1.0",
              "contractVersion": 1,
              "settingsSchemaVersion": 1,
              "driverAbiVersion": 1,
              "minimumDriverVersion": "0.1.0",
              "recommendedDriverVersion": "0.1.0",
              "driverPackageAvailable": false,
              "channel": "internal",
              "minimumWindowsBuild": 19045
            }
            """;

        CompatibilityManifest manifest =
            CompatibilityManifest.ParseInternalJson(json);

        Assert.AreEqual(new Version(0, 1, 0), manifest.AppVersion);
        Assert.AreEqual(19045, manifest.MinimumWindowsBuild);
        Assert.AreEqual(4, manifest.RequiredEndpointRoleCount);
        Assert.IsFalse(manifest.DriverPackageAvailable);
        Assert.ThrowsExactly<InvalidDataException>(
            () => CompatibilityManifest.ParseInternalJson("{}"));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"minimumWindowsBuild\":\"19045\"}")]
    [DataRow("{\"minimumWindowsBuild\":19045.5}")]
    [DataRow("{\"minimumWindowsBuild\":0}")]
    [DataRow("{\"minimumWindowsBuild\":-1}")]
    public void EmbeddedInternalJsonRequiresPositiveIntegerMinimumWindowsBuild(
        string minimumWindowsBuild)
    {
        const string prefix = """
            {
              "appVersion": "0.1.0",
              "contractVersion": 1,
              "settingsSchemaVersion": 1,
              "driverAbiVersion": 1,
              "minimumDriverVersion": "0.1.0",
              "recommendedDriverVersion": "0.1.0",
              "driverPackageAvailable": false,
              "channel": "internal"
            }
            """;
        string json = minimumWindowsBuild == "{}"
            ? prefix
            : prefix[..^1] + ",\n" + minimumWindowsBuild[1..];

        Assert.ThrowsExactly<InvalidDataException>(
            () => CompatibilityManifest.ParseInternalJson(json));
    }

    [TestMethod]
    public async Task HostGateFailureStopsBeforeDriverSecretAudioOrNetwork()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.OsError = new RuntimeError(
            ErrorCategory.Configuration,
            "unsupportedWindowsBuild",
            new Dictionary<string, string>(),
            RecoveryAction.ReportCompatibility);
        await using TranslationRuntime runtime = harness.CreateRuntime();

        RuntimeError? error = await runtime.StartAsync().ConfigureAwait(false);

        Assert.AreEqual("unsupportedWindowsBuild", error?.Code);
        Assert.AreEqual(RecoveryAction.ReportCompatibility, error?.RecoveryAction);
        Assert.HasCount(0, error!.Parameters);
        CollectionAssert.AreEqual(HostOnlyTrace, harness.Trace.ToArray());
        Assert.AreEqual(0, harness.AudioStartCount);
        Assert.AreEqual(0, harness.SessionCreateCount);
        Assert.AreEqual(InboundRoute.Stopped, runtime.CurrentSnapshot.InboundRoute);
        Assert.AreEqual(OutboundRoute.Stopped, runtime.CurrentSnapshot.OutboundRoute);
    }

    private static CompatibilityManifest CreateManifest(
        Version? recommendedDriverVersion = null,
        bool driverPackageAvailable = false)
    {
        return new CompatibilityManifest(
            appVersion: new Version(0, 1, 0),
            contractVersion: 1,
            settingsSchemaVersion: 1,
            driverAbiVersion: 1,
            minimumDriverVersion: new Version(0, 1, 0),
            recommendedDriverVersion:
                recommendedDriverVersion ?? new Version(0, 1, 0),
            driverPackageAvailable,
            channel: "internal",
            minimumWindowsBuild: 26200,
            requiredEndpointRoleCount: 4);
    }

    private static InstalledDriverEvidence CreateEvidence(
        bool present,
        bool signatureValid,
        int abi,
        Version version,
        int endpointCount)
    {
        return new InstalledDriverEvidence(
            present,
            rootDevnodeHardwareId:
                present ? @"ROOT\EMKEVIRTUALAUDIO" : null,
            driverFileVersion: version,
            driverAbiProperty: abi,
            catalogSigner: signatureValid ? "EMKE test signer" : null,
            catalogChainValid: signatureValid,
            CompleteEndpointRoles
                .Take(endpointCount)
                .Select(static role =>
                    new InstalledDriverEndpointEvidence(role, "active")));
    }

    private static InstalledDriverEvidence CreateEvidence(
        IEnumerable<InstalledDriverEndpointEvidence> endpoints)
    {
        return new InstalledDriverEvidence(
            present: true,
            rootDevnodeHardwareId: @"ROOT\EMKEVIRTUALAUDIO",
            driverFileVersion: new Version(0, 1, 0),
            driverAbiProperty: 1,
            catalogSigner: "EMKE test signer",
            catalogChainValid: true,
            endpoints);
    }
}
