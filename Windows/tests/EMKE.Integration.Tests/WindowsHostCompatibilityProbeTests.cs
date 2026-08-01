using System.Reflection;
using System.Runtime.InteropServices;
using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Driver;

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
        RuntimeError? error = await CheckAsync(
            isWindows,
            build,
            architecture,
            productType).ConfigureAwait(false);

        Assert.AreEqual(expectedCode, error?.Code);
    }

    private static async Task<RuntimeError?> CheckAsync(
        bool isWindows,
        int build,
        Architecture architecture,
        byte productType)
    {
        Assembly platformAssembly = typeof(WindowsDriverManager).Assembly;
        Type evidenceType = RequireType(
            platformAssembly,
            "EMKE.Platform.Compatibility.WindowsHostEvidence");
        Type sourceType = RequireType(
            platformAssembly,
            "EMKE.Platform.Compatibility.IWindowsHostEvidenceSource");
        Type gateType = RequireType(
            platformAssembly,
            "EMKE.Platform.Compatibility.WindowsHostBuildGate");
        object evidence = Activator.CreateInstance(
            evidenceType,
            isWindows,
            build,
            architecture,
            productType)
            ?? throw new AssertFailedException("Could not create host evidence.");
        object source = CreateEvidenceSource(sourceType, evidence);
        object gate = Activator.CreateInstance(
            gateType,
            CreateManifest(),
            source)
            ?? throw new AssertFailedException("Could not create host gate.");
        MethodInfo checkAsync = gateType.GetMethod(
            "CheckAsync",
            [typeof(CancellationToken)])
            ?? throw new AssertFailedException("Host gate does not expose CheckAsync.");

        object valueTask = checkAsync.Invoke(
            gate,
            [CancellationToken.None])
            ?? throw new AssertFailedException("Host gate did not return a result.");
        return await ((ValueTask<RuntimeError?>)valueTask).ConfigureAwait(false);
    }

    private static object CreateEvidenceSource(Type sourceType, object evidence)
    {
        MethodInfo create = typeof(DispatchProxy).GetMethod(
            nameof(DispatchProxy.Create),
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new AssertFailedException("DispatchProxy.Create is unavailable.");
        object source = create.MakeGenericMethod(
            sourceType,
            typeof(WindowsHostEvidenceDispatchProxy)).Invoke(null, null)
            ?? throw new AssertFailedException("Could not create host evidence source.");
        ((WindowsHostEvidenceDispatchProxy)source).Evidence = evidence;
        return source;
    }

    private static Type RequireType(Assembly assembly, string fullName)
    {
        return assembly.GetType(fullName)
            ?? throw new AssertFailedException(
                $"Required host compatibility type {fullName} is missing.");
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
}

public sealed class WindowsHostEvidenceDispatchProxy : DispatchProxy
{
    public object? Evidence { get; set; }

    protected override object? Invoke(
        MethodInfo? targetMethod,
        object?[]? args)
    {
        Assert.IsNotNull(targetMethod);
        Assert.AreEqual("Read", targetMethod.Name);
        return Evidence;
    }
}

#pragma warning restore CA2007
