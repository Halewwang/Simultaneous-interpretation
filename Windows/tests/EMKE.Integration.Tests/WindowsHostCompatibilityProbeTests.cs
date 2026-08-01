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
