using System.Runtime.InteropServices;
using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Compatibility;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest provides no synchronization context.

[TestClass]
public sealed class WindowsHostCompatibilityProbeTests
{
    [TestMethod]
    public void ProductionProbeReadsCurrentWindowsHostEvidence()
    {
        Type versionInfoType = typeof(WindowsHostCompatibilityProbe).GetNestedType(
            "OsVersionInfoEx",
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Production version info layout is missing.");
        Assert.AreEqual(284, Marshal.SizeOf(versionInfoType));

        WindowsHostEvidence evidence = new WindowsHostCompatibilityProbe().Read();

        Assert.IsTrue(evidence.IsWindows);
        Assert.IsGreaterThan(0, evidence.Build);
        Assert.AreEqual(RuntimeInformation.OSArchitecture, evidence.Architecture);
        Assert.AreEqual(Architecture.X64, evidence.Architecture);
        Assert.IsTrue(evidence.ProductType is 1 or 2 or 3);
    }

    [TestMethod]
    [DataRow(true, 19044, Architecture.X64, (byte)1, "unsupportedWindowsBuild")]
    [DataRow(true, 19045, Architecture.X64, (byte)1, null)]
    [DataRow(true, 19045, Architecture.X86, (byte)1, "unsupportedWindowsArchitecture")]
    [DataRow(true, 19045, Architecture.Arm64, (byte)1, "unsupportedWindowsArchitecture")]
    [DataRow(true, 19045, Architecture.X64, (byte)2, "unsupportedWindowsProductType")]
    [DataRow(true, 19045, Architecture.X64, (byte)3, "unsupportedWindowsProductType")]
    [DataRow(false, 19045, Architecture.X64, (byte)1, "unsupportedWindowsPlatform")]
    public async Task MetadataBackedHostGateAdmitsOnlySupportedWorkstations(
        bool isWindows,
        int build,
        Architecture architecture,
        byte productType,
        string? expectedCode)
    {
        WindowsHostBuildGate gate = new(
            CreateManifest(),
            new FixedWindowsHostEvidenceSource(
                new WindowsHostEvidence(
                    isWindows,
                    build,
                    architecture,
                    productType)));

        RuntimeError? error = await gate.CheckAsync(CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(expectedCode, error?.Code);
        if (expectedCode is not null)
        {
            RuntimeError observed = error
                ?? throw new AssertFailedException("Unsupported host must have a stable error.");
            Assert.AreEqual(ErrorCategory.Configuration, observed.Category);
            Assert.AreEqual(RecoveryAction.ReportCompatibility, observed.RecoveryAction);
            Assert.HasCount(0, observed.Parameters);
        }
    }

    private static CompatibilityManifest CreateManifest()
    {
        return CompatibilityManifest.ParseInternalJson("""
            {
              "appVersion": "0.2.0",
              "contractVersion": 1,
              "settingsSchemaVersion": 1,
              "driverAbiVersion": 1,
              "minimumDriverVersion": "1.0.0.2",
              "recommendedDriverVersion": "1.0.0.2",
              "driverPackageAvailable": false,
              "channel": "internal",
              "minimumWindowsBuild": 19045
            }
            """);
    }

    private sealed class FixedWindowsHostEvidenceSource(
        WindowsHostEvidence evidence) : IWindowsHostEvidenceSource
    {
        public WindowsHostEvidence Read()
        {
            return evidence;
        }
    }
}

#pragma warning restore CA2007
