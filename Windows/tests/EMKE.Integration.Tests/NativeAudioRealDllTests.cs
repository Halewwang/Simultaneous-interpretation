using System.Runtime.InteropServices;
using EMKE.Platform.Native;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
[TestCategory("NativeAudioRealDll")]
public sealed class NativeAudioRealDllTests
{
    [TestMethod]
    public void WindowsX64RealDllExportsMatchingAbiAndStructSizes()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("EMKE_NATIVE_AUDIO_TEST_MODE"),
            "real-dll",
            StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "The real-DLL test requires an isolated real-dll test process.");
        }

        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Assert.Inconclusive("The real EMKE.NativeAudio DLL ABI check runs only on Windows x64.");
        }

        PInvokeNativeAudioApi native = PInvokeNativeAudioApi.Instance;

        Assert.AreEqual(NativeAudioConstants.AbiVersion, native.GetAbiVersion());
        Assert.AreEqual(
            checked((uint)Marshal.SizeOf<NativeAudioConfiguration>()),
            native.GetConfigurationSize());
        Assert.AreEqual(
            checked((uint)Marshal.SizeOf<NativeAudioEvent>()),
            native.GetEventSize());
        Assert.AreEqual(
            checked((uint)Marshal.SizeOf<NativeAudioDiagnostics>()),
            native.GetDiagnosticsSize());
        Assert.AreEqual(
            checked((uint)Marshal.SizeOf<NativeAudioDiscoveredEndpoint>()),
            native.GetDiscoveredEndpointSize());
        Assert.AreEqual(
            checked((uint)Marshal.SizeOf<NativeAudioEndpointSnapshot>()),
            native.GetEndpointSnapshotSize());
    }
}

#pragma warning restore CA1515
