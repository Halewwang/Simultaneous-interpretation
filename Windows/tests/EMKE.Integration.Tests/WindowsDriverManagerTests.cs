using EMKE.Core;
using EMKE.Platform.Driver;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class WindowsDriverManagerTests
{
    private static readonly string[] CompleteEvidenceCalls =
        ["root", "catalog", "endpoints"];
    private static readonly string[] RootEvidenceCalls = ["root"];

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

    [TestMethod]
    public async Task WindowsSnapshotSourceMapsEveryReadOnlyWin32EvidenceField()
    {
        RecordingWindowsEvidenceApi api = new()
        {
            Root = new WindowsRootDriverEvidence(
                Present: true,
                HardwareId: @"ROOT\EMKEVIRTUALAUDIO",
                DriverFileVersion: new Version(1, 0, 0, 1),
                DriverAbi: 1,
                CatalogPath: @"C:\Windows\System32\DriverStore\emke.cat"),
            Catalog = new WindowsCatalogEvidence(
                "CN=EMKE Internal Test",
                ChainValid: true),
            Endpoints =
            [
                new("meetingSpeakerRender", "active"),
                new("appSpeakerCapture", "active"),
                new("appMicrophoneRender", "active"),
                new("meetingMicrophoneCapture", "disabled"),
            ],
        };
        WindowsDriverSnapshotSource source = new(api);

        WindowsInstalledDriverSnapshot snapshot =
            await source.ReadAsync(CancellationToken.None)
                .ConfigureAwait(false);

        Assert.IsTrue(snapshot.Present);
        Assert.AreEqual(@"ROOT\EMKEVIRTUALAUDIO", snapshot.RootDevnodeHardwareId);
        Assert.AreEqual(new Version(1, 0, 0, 1), snapshot.DriverFileVersion);
        Assert.AreEqual(1, snapshot.DriverAbiProperty);
        Assert.AreEqual("CN=EMKE Internal Test", snapshot.CatalogSigner);
        Assert.IsTrue(snapshot.CatalogChainValid);
        Assert.HasCount(4, snapshot.EndpointStates);
        Assert.AreEqual("disabled", snapshot.EndpointStates[3].State);
        CollectionAssert.AreEqual(
            CompleteEvidenceCalls,
            api.Calls);
    }

    [TestMethod]
    public async Task WindowsSnapshotSourceSkipsLaterReadsWhenRootDriverIsMissing()
    {
        RecordingWindowsEvidenceApi api = new()
        {
            Root = WindowsRootDriverEvidence.Missing,
        };
        WindowsDriverSnapshotSource source = new(api);

        WindowsInstalledDriverSnapshot snapshot =
            await source.ReadAsync(CancellationToken.None)
                .ConfigureAwait(false);

        Assert.IsFalse(snapshot.Present);
        CollectionAssert.AreEqual(RootEvidenceCalls, api.Calls);
    }

    [TestMethod]
    public async Task WindowsSnapshotSourceFailsClosedWhenWin32EvidenceReadFails()
    {
        RecordingWindowsEvidenceApi api = new()
        {
            RootFailure = new InvalidDataException("synthetic Win32 failure"),
        };
        WindowsDriverSnapshotSource source = new(api);

        WindowsInstalledDriverSnapshot snapshot =
            await source.ReadAsync(CancellationToken.None)
                .ConfigureAwait(false);

        Assert.IsFalse(snapshot.Present);
        Assert.IsFalse(snapshot.CatalogChainValid);
        Assert.IsEmpty(snapshot.EndpointStates);
        CollectionAssert.AreEqual(RootEvidenceCalls, api.Calls);
    }

    [TestMethod]
    public void ProductionWin32EvidenceApiNeverRunsOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "The off-Windows P/Invoke guard is covered only on non-Windows hosts.");
        }

        Assert.ThrowsExactly<PlatformNotSupportedException>(
            WindowsDriverEvidenceApi.Instance.ReadRootDriver);
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

    private sealed class RecordingWindowsEvidenceApi
        : IWindowsDriverEvidenceApi
    {
        public List<string> Calls { get; } = [];

        public WindowsRootDriverEvidence Root { get; init; } =
            WindowsRootDriverEvidence.Missing;

        public WindowsCatalogEvidence Catalog { get; init; } =
            new(null, ChainValid: false);

        public IReadOnlyList<WindowsInstalledDriverEndpointState> Endpoints
        {
            get;
            init;
        } = [];

        public Exception? RootFailure { get; init; }

        public WindowsRootDriverEvidence ReadRootDriver()
        {
            Calls.Add("root");
            if (RootFailure is not null)
            {
                throw RootFailure;
            }

            return Root;
        }

        public WindowsCatalogEvidence ReadCatalog(string catalogPath)
        {
            Calls.Add("catalog");
            return Catalog;
        }

        public IReadOnlyList<WindowsInstalledDriverEndpointState>
            ReadEndpointStates()
        {
            Calls.Add("endpoints");
            return Endpoints;
        }
    }
}
