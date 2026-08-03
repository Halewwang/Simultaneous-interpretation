using EMKE.Core;
using EMKE.Platform.Driver;
using System.Security.Cryptography.X509Certificates;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class WindowsDriverManagerTests
{
    private const string CatalogPath =
        @"C:\Windows\System32\DriverStore\FileRepository\emke\EMKE.VirtualAudio.cat";
    private const string InfPath =
        @"C:\Windows\INF\oem42.inf";
    private const string DriverBinaryPath =
        @"C:\Windows\System32\DriverStore\FileRepository\emke\EMKE.VirtualAudio.sys";
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
                CatalogPath,
                InfPath,
                DriverBinaryPath),
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
        Assert.AreEqual(CatalogPath, api.ReadCatalogPath);
        Assert.AreEqual(InfPath, api.ReadInfPath);
        Assert.AreEqual(DriverBinaryPath, api.ReadDriverBinaryPath);
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

    [TestMethod]
    public void CatalogTrustRequiresExactSignerLocalChainIntegrityAndBothMembers()
    {
        RecordingCatalogTrustNativeApi native = new();
        WindowsCatalogTrustVerifier verifier = new(native);

        WindowsCatalogEvidence evidence = verifier.Verify(
            CatalogPath,
            InfPath,
            DriverBinaryPath);

        Assert.AreEqual(
            RecordingCatalogTrustNativeApi.MicrosoftSignerSubject,
            evidence.Signer);
        Assert.IsTrue(evidence.ChainValid);
        Assert.AreEqual(CatalogPath, native.SignerCatalogPath);
        Assert.AreEqual(CatalogPath, native.SignatureCatalogPath);
        CollectionAssert.AreEqual(
            new[] { InfPath, DriverBinaryPath },
            native.MemberPaths);
    }

    [TestMethod]
    [DataRow(
        "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, C=US",
        true,
        true,
        true)]
    [DataRow(
        "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, C=US",
        false,
        true,
        false)]
    [DataRow(
        "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, C=US",
        true,
        false,
        false)]
    [DataRow("CN=EMKE Internal Test", true, true, false)]
    [DataRow(
        "CN=Microsoft Windows Hardware Compatibility Publisher, O=Other Publisher, C=US",
        true,
        true,
        false)]
    public void MicrosoftCatalogPolicyRequiresPublisherKernelAndMembers(
        string signerSubject,
        bool kernelPolicyValid,
        bool catalogMembersValid,
        bool expectedAllowed)
    {
        DriverCatalogTrustDecision decision =
            MicrosoftDriverCatalogTrustPolicy.Instance.Evaluate(
                signerSubject,
                kernelPolicyValid,
                catalogMembersValid);

        Assert.AreEqual(expectedAllowed, decision.Allowed);
    }

    [TestMethod]
    public void CatalogVerifierDelegatesFinalDecisionToInjectedPolicy()
    {
        RecordingCatalogTrustNativeApi native = new();
        RecordingDriverCatalogTrustPolicy policy = new(
            new DriverCatalogTrustDecision(
                Allowed: true,
                Reason: "synthetic-test-policy"));
        WindowsCatalogTrustVerifier verifier = new(native, policy);

        WindowsCatalogEvidence evidence = verifier.Verify(
            CatalogPath,
            InfPath,
            DriverBinaryPath);

        Assert.IsTrue(evidence.ChainValid);
        Assert.AreEqual(native.Signer.Subject, policy.SignerSubject);
        Assert.IsTrue(policy.KernelPolicyValid);
        Assert.IsTrue(policy.CatalogMembersValid);
        Assert.AreEqual(1, policy.EvaluationCount);
    }

    [TestMethod]
    public void ProductionCatalogVerifierCannotComposeATestPolicy()
    {
        Assert.IsInstanceOfType<MicrosoftDriverCatalogTrustPolicy>(
            WindowsCatalogTrustVerifier.Instance.TrustPolicy);
    }

    [TestMethod]
    public void ProductionCatalogTrustChecksOnlineRevocationForWholeChain()
    {
        WindowsCatalogRevocationConfiguration configuration =
            WindowsCatalogTrustNativeApi.RevocationConfiguration;

        Assert.AreEqual(
            X509RevocationMode.Online,
            configuration.ChainRevocationMode);
        Assert.AreEqual(
            X509RevocationFlag.EntireChain,
            configuration.ChainRevocationFlag);
        Assert.AreEqual(1U, configuration.WinTrustRevocationChecks);
        Assert.AreEqual(0x00000040U, configuration.WinTrustProviderFlags);
        Assert.IsTrue(configuration.CertificateDownloadsEnabled);
    }

    [TestMethod]
    [DataRow("missingSigner")]
    [DataRow("wrongSigner")]
    [DataRow("localChainInvalid")]
    [DataRow("tamperedCatalog")]
    [DataRow("unrelatedCatalog")]
    [DataRow("missingInfMember")]
    [DataRow("missingDriverMember")]
    [DataRow("winVerifyTrustError")]
    public void CatalogTrustFailsClosedWhenAnyProofIsMissing(
        string failure)
    {
        RecordingCatalogTrustNativeApi native =
            RecordingCatalogTrustNativeApi.ForFailure(failure);
        WindowsCatalogTrustVerifier verifier = new(native);

        WindowsCatalogEvidence evidence = verifier.Verify(
            CatalogPath,
            InfPath,
            DriverBinaryPath);

        Assert.IsFalse(evidence.ChainValid);
    }

    [TestMethod]
    [TestCategory("WindowsCatalogTrust")]
    public void ProductionCatalogTrustRejectsTamperedCatalogOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Production WinTrust catalog verification runs in Windows CI.");
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"emke-catalog-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string catalogPath = Path.Combine(
                temporaryDirectory,
                "EMKE.VirtualAudio.cat");
            string infPath = Path.Combine(
                temporaryDirectory,
                "EMKE.VirtualAudio.inf");
            string driverPath = Path.Combine(
                temporaryDirectory,
                "EMKE.VirtualAudio.sys");
            File.WriteAllBytes(catalogPath, [0x43, 0x41, 0x54]);
            File.WriteAllBytes(infPath, [0x49, 0x4E, 0x46]);
            File.WriteAllBytes(driverPath, [0x53, 0x59, 0x53]);

            WindowsCatalogEvidence evidence =
                WindowsCatalogTrustVerifier.Instance.Verify(
                    catalogPath,
                    infPath,
                    driverPath);

            Assert.IsFalse(evidence.ChainValid);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
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

        public string? ReadCatalogPath { get; private set; }

        public string? ReadInfPath { get; private set; }

        public string? ReadDriverBinaryPath { get; private set; }

        public WindowsRootDriverEvidence ReadRootDriver()
        {
            Calls.Add("root");
            if (RootFailure is not null)
            {
                throw RootFailure;
            }

            return Root;
        }

        public WindowsCatalogEvidence ReadCatalog(
            string catalogPath,
            string infPath,
            string driverBinaryPath)
        {
            Calls.Add("catalog");
            ReadCatalogPath = catalogPath;
            ReadInfPath = infPath;
            ReadDriverBinaryPath = driverBinaryPath;
            return Catalog;
        }

        public IReadOnlyList<WindowsInstalledDriverEndpointState>
            ReadEndpointStates()
        {
            Calls.Add("endpoints");
            return Endpoints;
        }
    }

    private sealed class RecordingCatalogTrustNativeApi
        : IWindowsCatalogTrustNativeApi
    {
        public const string MicrosoftSignerSubject =
            "CN=Microsoft Windows Hardware Compatibility Publisher, " +
            "O=Microsoft Corporation, C=US";
        private const int TrustSuccess = 0;
        private const int TrustFailure = unchecked((int)0x800B0100);
        private const int WinTrustError = unchecked((int)0x80092003);

        public WindowsCatalogSignerEvidence Signer { get; init; } =
            new(MicrosoftSignerSubject, LocalChainValid: true);

        public int SignatureStatus { get; init; } = TrustSuccess;

        public int InfMemberStatus { get; init; } = TrustSuccess;

        public int DriverMemberStatus { get; init; } = TrustSuccess;

        public string? SignerCatalogPath { get; private set; }

        public string? SignatureCatalogPath { get; private set; }

        public List<string> MemberPaths { get; } = [];

        public static RecordingCatalogTrustNativeApi ForFailure(
            string failure)
        {
            return failure switch
            {
                "missingSigner" => new()
                {
                    Signer = new(null, LocalChainValid: true),
                },
                "wrongSigner" => new()
                {
                    Signer = new(
                        "CN=EMKE Internal Test",
                        LocalChainValid: true),
                },
                "localChainInvalid" => new()
                {
                    Signer = new(
                        MicrosoftSignerSubject,
                        LocalChainValid: false),
                },
                "tamperedCatalog" => new()
                {
                    SignatureStatus = TrustFailure,
                },
                "unrelatedCatalog" => new()
                {
                    InfMemberStatus = TrustFailure,
                    DriverMemberStatus = TrustFailure,
                },
                "missingInfMember" => new()
                {
                    InfMemberStatus = TrustFailure,
                },
                "missingDriverMember" => new()
                {
                    DriverMemberStatus = TrustFailure,
                },
                "winVerifyTrustError" => new()
                {
                    SignatureStatus = WinTrustError,
                },
                _ => throw new ArgumentOutOfRangeException(
                    nameof(failure),
                    failure,
                    "Undefined catalog trust failure."),
            };
        }

        public WindowsCatalogSignerEvidence ReadCatalogSigner(
            string catalogPath)
        {
            SignerCatalogPath = catalogPath;
            return Signer;
        }

        public int VerifyCatalogSignature(string catalogPath)
        {
            SignatureCatalogPath = catalogPath;
            return SignatureStatus;
        }

        public int VerifyCatalogMember(
            string catalogPath,
            string memberPath)
        {
            Assert.AreEqual(CatalogPath, catalogPath);
            MemberPaths.Add(memberPath);
            return string.Equals(
                    memberPath,
                    InfPath,
                    StringComparison.Ordinal)
                ? InfMemberStatus
                : DriverMemberStatus;
        }
    }

    private sealed class RecordingDriverCatalogTrustPolicy(
        DriverCatalogTrustDecision decision)
        : IDriverCatalogTrustPolicy
    {
        public int EvaluationCount { get; private set; }

        public string? SignerSubject { get; private set; }

        public bool KernelPolicyValid { get; private set; }

        public bool CatalogMembersValid { get; private set; }

        public DriverCatalogTrustDecision Evaluate(
            string signerSubject,
            bool kernelPolicyValid,
            bool catalogMembersValid)
        {
            EvaluationCount++;
            SignerSubject = signerSubject;
            KernelPolicyValid = kernelPolicyValid;
            CatalogMembersValid = catalogMembersValid;
            return decision;
        }
    }
}
