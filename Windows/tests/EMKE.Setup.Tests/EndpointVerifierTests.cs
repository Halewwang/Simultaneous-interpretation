using EMKE.Core;
using EMKE.Platform.Driver;
using EMKE.Setup.Platform;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class EndpointVerifierTests
{
    private const string MicrosoftSigner =
        "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation";

    [TestMethod]
    public async Task ExactTrustedDriverAndFourActiveRolesPermitLaunch()
    {
        EndpointVerifier verifier = CreateVerifier(Snapshot());

        EndpointVerificationResult result = await verifier.VerifyAsync(
            CancellationToken.None);

        Assert.IsTrue(result.Ready);
        Assert.IsTrue(result.LaunchAllowed);
        Assert.IsNull(result.FailureCode);
        CollectionAssert.AreEquivalent(
            RequiredRoles,
            result.ActiveRoles.ToArray());
    }

    [TestMethod]
    public async Task NonMicrosoftCatalogSignerIsRejected()
    {
        EndpointVerifier verifier = CreateVerifier(Snapshot(
            signer: "CN=Other Hardware Publisher, O=Other Corporation"));

        EndpointVerificationResult result = await verifier.VerifyAsync(
            CancellationToken.None);

        Assert.IsFalse(result.Ready);
        Assert.IsFalse(result.LaunchAllowed);
        Assert.AreEqual("driverCatalogSignerRejected", result.FailureCode);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("inactive")]
    [DataRow("duplicate")]
    [DataRow("extra")]
    public async Task InexactEndpointInventoryBlocksLaunch(string mutation)
    {
        List<WindowsInstalledDriverEndpointState> endpoints = ActiveEndpoints();
        switch (mutation)
        {
            case "missing":
                endpoints.RemoveAt(0);
                break;
            case "inactive":
                endpoints[0] = new(endpoints[0].Role, "disabled");
                break;
            case "duplicate":
                endpoints[0] = new(endpoints[1].Role, "active");
                break;
            case "extra":
                endpoints.Add(new("unexpectedRole", "active"));
                break;
        }
        EndpointVerifier verifier = CreateVerifier(Snapshot(endpoints));

        EndpointVerificationResult result = await verifier.VerifyAsync(
            CancellationToken.None);

        Assert.IsFalse(result.Ready);
        Assert.IsFalse(result.LaunchAllowed);
        Assert.AreEqual("virtualEndpointsIncomplete", result.FailureCode);
    }

    [TestMethod]
    public async Task DriverEvidenceUsesProductionCompatibilityGate()
    {
        EndpointVerifier verifier = CreateVerifier(Snapshot(
            hardwareId: @"ROOT\OTHER"));

        EndpointVerificationResult result = await verifier.VerifyAsync(
            CancellationToken.None);

        Assert.IsFalse(result.Ready);
        Assert.AreEqual("driverMissing", result.FailureCode);
    }

    internal static EndpointVerifier CreateVerifier(
        WindowsInstalledDriverSnapshot snapshot) => new(
            new FixedDriverSnapshotSource(snapshot),
            Manifest(),
            new FixedWindowsBuildSource(19045),
            MicrosoftDriverCatalogTrustPolicy.Instance);

    internal static CompatibilityManifest Manifest() => new(
        new Version(0, 2, 0),
        contractVersion: 1,
        settingsSchemaVersion: 1,
        driverAbiVersion: 1,
        new Version(1, 0, 0, 2),
        new Version(1, 0, 0, 2),
        driverPackageAvailable: true,
        "internal",
        minimumWindowsBuild: 19045,
        requiredEndpointRoleCount: 4);

    internal static WindowsInstalledDriverSnapshot Snapshot(
        IEnumerable<WindowsInstalledDriverEndpointState>? endpoints = null,
        string hardwareId = @"ROOT\EMKEVIRTUALAUDIO",
        string signer = MicrosoftSigner) => new(
            present: true,
            hardwareId,
            new Version(1, 0, 0, 2),
            driverAbiProperty: 1,
            signer,
            catalogChainValid: true,
            endpoints ?? ActiveEndpoints());

    private static List<WindowsInstalledDriverEndpointState> ActiveEndpoints() =>
    [
        new("meetingSpeakerRender", "active"),
        new("appSpeakerCapture", "active"),
        new("appMicrophoneRender", "active"),
        new("meetingMicrophoneCapture", "active"),
    ];

    private static string[] RequiredRoles =>
    [
        "meetingSpeakerRender",
        "appSpeakerCapture",
        "appMicrophoneRender",
        "meetingMicrophoneCapture",
    ];
}

internal sealed class FixedDriverSnapshotSource(
    WindowsInstalledDriverSnapshot snapshot) : IWindowsDriverSnapshotSource
{
    public ValueTask<WindowsInstalledDriverSnapshot> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(snapshot);
    }
}

internal sealed class FixedWindowsBuildSource(int build)
    : IWindowsHostCompatibilitySource
{
    public int GetCurrentWindowsBuild() => build;
}

#pragma warning restore CA1515
#pragma warning restore CA2007
