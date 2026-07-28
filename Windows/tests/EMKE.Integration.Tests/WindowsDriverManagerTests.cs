using EMKE.Core;
using EMKE.Platform.Driver;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class WindowsDriverManagerTests
{
    [TestMethod]
    public async Task WindowsDriverManagerUsesReadOnlyInstalledEvidence()
    {
        RecordingSnapshotSource source = new(
            new WindowsInstalledDriverSnapshot(
                present: true,
                rootDevnodeHardwareId: @"ROOT\EMKEVIRTUALAUDIO",
                driverFileVersion: new Version(0, 1, 0),
                driverAbiProperty: 1,
                catalogSigner: "EMKE Internal Test",
                catalogChainValid: true,
                endpointStates:
                [
                    new("meetingSpeakerRender", "active"),
                    new("appSpeakerCapture", "active"),
                    new("appMicrophoneRender", "active"),
                    new("meetingMicrophoneCapture", "active"),
                ]));
        RecordingHostSource host = new(26200);
        WindowsDriverManager manager = new(
            source,
            CreateManifest(),
            host);

        DriverCompatibility compatibility =
            await manager.CheckCompatibilityAsync(CancellationToken.None)
                .ConfigureAwait(false);

        Assert.IsTrue(compatibility.IsCompatible);
        Assert.AreEqual("compatible", compatibility.StatusLabel);
        Assert.AreEqual(1, source.ReadCount);
        Assert.AreEqual(1, host.ReadCount);
        Assert.AreEqual(0, source.MutationCount);
    }

    [TestMethod]
    public async Task WindowsDriverManagerFailsClosedForIncompleteOrWrongRoleEvidence()
    {
        RecordingSnapshotSource source = new(
            new WindowsInstalledDriverSnapshot(
                present: true,
                rootDevnodeHardwareId: @"ROOT\EMKEVIRTUALAUDIO",
                driverFileVersion: new Version(0, 1, 0),
                driverAbiProperty: 1,
                catalogSigner: "EMKE Internal Test",
                catalogChainValid: true,
                endpointStates:
                [
                    new("meetingSpeakerRender", "active"),
                    new("appSpeakerCapture", "active"),
                    new("appMicrophoneRender", "active"),
                    new("unexpectedRole", "active"),
                ]));
        WindowsDriverManager manager = new(
            source,
            CreateManifest(),
            new RecordingHostSource(26200));

        DriverCompatibility compatibility =
            await manager.CheckCompatibilityAsync(CancellationToken.None)
                .ConfigureAwait(false);

        Assert.IsFalse(compatibility.IsCompatible);
        Assert.AreEqual(
            "virtualEndpointsIncomplete",
            compatibility.StatusLabel);
        Assert.IsFalse(compatibility.RepairAvailable);
        Assert.AreEqual(0, source.MutationCount);
    }

    [TestMethod]
    public async Task WindowsDriverManagerReportsMissingWithoutMutatingDriver()
    {
        RecordingSnapshotSource source = new(
            new WindowsInstalledDriverSnapshot(
                present: false,
                rootDevnodeHardwareId: null,
                driverFileVersion: new Version(0, 0, 0),
                driverAbiProperty: 0,
                catalogSigner: null,
                catalogChainValid: false,
                endpointStates: []));
        WindowsDriverManager manager = new(
            source,
            CreateManifest(),
            new RecordingHostSource(26200));

        DriverCompatibility compatibility =
            await manager.CheckCompatibilityAsync(CancellationToken.None)
                .ConfigureAwait(false);

        Assert.IsFalse(compatibility.IsCompatible);
        Assert.AreEqual("driverMissing", compatibility.StatusLabel);
        Assert.IsFalse(compatibility.RepairAvailable);
        Assert.AreEqual(0, source.MutationCount);
    }

    private static CompatibilityManifest CreateManifest()
    {
        return new CompatibilityManifest(
            appVersion: new Version(0, 1, 0),
            contractVersion: 1,
            settingsSchemaVersion: 1,
            driverAbiVersion: 1,
            minimumDriverVersion: new Version(0, 1, 0),
            recommendedDriverVersion: new Version(0, 1, 0),
            driverPackageAvailable: false,
            channel: "internal",
            minimumWindowsBuild: 26200,
            requiredEndpointRoleCount: 4);
    }

    private sealed class RecordingSnapshotSource(
        WindowsInstalledDriverSnapshot snapshot)
        : IWindowsDriverSnapshotSource
    {
        public int ReadCount { get; private set; }

        public int MutationCount { get; private set; }

        public ValueTask<WindowsInstalledDriverSnapshot> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class RecordingHostSource(int windowsBuild)
        : IWindowsHostCompatibilitySource
    {
        public int ReadCount { get; private set; }

        public int GetCurrentWindowsBuild()
        {
            ReadCount++;
            return windowsBuild;
        }
    }
}
