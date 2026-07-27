using System.Buffers;
using System.Runtime.InteropServices;
using EMKE.Core;
using EMKE.Platform.Native;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
[TestCategory("NativeAudioManagedSeam")]
public sealed class NativeAudioAbiTests
{
    private static readonly AudioEngineConfiguration ValidConfiguration =
        new("physical-input", "physical-output", 24_000, 1);

    [TestMethod]
    public void ManagedInteropStructsHaveAbiOneLiteralSizes()
    {
        Assert.AreEqual(6_152, Marshal.SizeOf<NativeAudioConfiguration>());
        Assert.AreEqual(32, Marshal.SizeOf<NativeAudioEvent>());
        Assert.AreEqual(104, Marshal.SizeOf<NativeAudioDiagnostics>());
        Assert.AreEqual(1_048, Marshal.SizeOf<NativeAudioDiscoveredEndpoint>());
        Assert.AreEqual(6_272, Marshal.SizeOf<NativeAudioEndpointSnapshot>());
    }

    [TestMethod]
    public async Task AbiVersionOneStartsAndMapsDiscoveredVirtualEndpoints()
    {
        FakeNativeAudioApi native = new();
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());

        await engine.StartAsync(ValidConfiguration, CancellationToken.None);

        Assert.AreEqual(1, native.CreateCount);
        Assert.AreEqual("physical-input", ReadEndpointId(native.LastConfiguration, 8));
        Assert.AreEqual("physical-output", ReadEndpointId(native.LastConfiguration, 1_032));
        Assert.AreEqual("virtual-speaker-render", ReadEndpointId(native.LastConfiguration, 2_056));
        Assert.AreEqual("virtual-speaker-capture", ReadEndpointId(native.LastConfiguration, 3_080));
        Assert.AreEqual("virtual-microphone-render", ReadEndpointId(native.LastConfiguration, 4_104));
        Assert.AreEqual("virtual-microphone-capture", ReadEndpointId(native.LastConfiguration, 5_128));
    }

    [TestMethod]
    public async Task EmptyPhysicalSelectionUsesDiscoveredPhysicalDefaults()
    {
        FakeNativeAudioApi native = new();
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());

        await engine.StartAsync(
            new AudioEngineConfiguration(null, null, 24_000, 1),
            CancellationToken.None);

        Assert.AreEqual("default-input", ReadEndpointId(native.LastConfiguration, 8));
        Assert.AreEqual("default-output", ReadEndpointId(native.LastConfiguration, 1_032));
    }

    [TestMethod]
    public async Task DriverMissingDiscoveryFailsClosedBeforeCreate()
    {
        FakeNativeAudioApi native = new()
        {
            DiscoveryStatus = NativeAudioEndpointDiscoveryStatus.DriverMissing,
        };
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());

        NativeAudioException exception =
            await Assert.ThrowsExactlyAsync<NativeAudioException>(
                () => engine.StartAsync(ValidConfiguration, CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.DeviceMissing, exception.Status);
        StringAssert.Contains(
            exception.Message,
            "DriverMissing",
            StringComparison.Ordinal);
        Assert.AreEqual(0, native.CreateCount);
    }

    [TestMethod]
    public async Task AbiVersionTwoReportsExplicitIncompatibilityBeforeCreate()
    {
        FakeNativeAudioApi native = new()
        {
            AbiVersion = 2,
        };
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());

        NativeAudioException exception =
            await Assert.ThrowsExactlyAsync<NativeAudioException>(
                () => engine.StartAsync(ValidConfiguration, CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.AbiMismatch, exception.Status);
        StringAssert.Contains(exception.Message, "ABI 2", StringComparison.Ordinal);
        Assert.AreEqual(0, native.CreateCount);
    }

    [TestMethod]
    public async Task ManagedAbiSizeMismatchReportsIncompatibilityBeforeCreate()
    {
        FakeNativeAudioApi native = new()
        {
            EventSize = 31,
        };
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());

        NativeAudioException exception =
            await Assert.ThrowsExactlyAsync<NativeAudioException>(
                () => engine.StartAsync(ValidConfiguration, CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.AbiMismatch, exception.Status);
        StringAssert.Contains(exception.Message, "event", StringComparison.Ordinal);
        Assert.AreEqual(0, native.CreateCount);
    }

    [TestMethod]
    public async Task UnsupportedNetworkFormatIsRejectedWithoutCreatingNativeHandle()
    {
        FakeNativeAudioApi native = new();
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());
        AudioEngineConfiguration unsupported = new(null, null, 48_000, 2);

        NativeAudioException exception =
            await Assert.ThrowsExactlyAsync<NativeAudioException>(
                () => engine.StartAsync(unsupported, CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.FormatUnsupported, exception.Status);
        Assert.AreEqual(0, native.CreateCount);
    }

    private static string ReadEndpointId(
        NativeAudioConfiguration configuration,
        int byteOffset)
    {
        int structSize = Marshal.SizeOf<NativeAudioConfiguration>();
        nint memory = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.StructureToPtr(configuration, memory, fDeleteOld: false);
            byte[] bytes = new byte[NativeAudioConstants.EndpointIdCapacity * sizeof(char)];
            Marshal.Copy(memory + byteOffset, bytes, 0, bytes.Length);
            int terminator = 0;
            while (terminator + 1 < bytes.Length
                && (bytes[terminator] != 0 || bytes[terminator + 1] != 0))
            {
                terminator += sizeof(char);
            }

            return System.Text.Encoding.Unicode.GetString(bytes, 0, terminator);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}

#pragma warning restore CA2007
#pragma warning restore CA1515
