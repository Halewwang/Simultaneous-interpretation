using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EMKE.Core;
using EMKE.Platform.Native;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
[TestCategory("NativeAudioNativeFake")]
[DoNotParallelize]
public sealed class NativeAudioNativeFakeTests
{
    private const string TestModeVariable = "EMKE_NATIVE_AUDIO_TEST_MODE";
    private const string FakeLibraryVariable = "EMKE_NATIVE_AUDIO_FAKE_LIBRARY";
    private static readonly AudioEngineConfiguration ValidConfiguration =
        new(null, null, 24_000, 1);
    private static readonly object ResolverGate = new();
    private static string? _fakeLibraryPath;
    private static bool _resolverConfigured;

    [TestInitialize]
    public void Initialize()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(TestModeVariable),
            "native-fake",
            StringComparison.Ordinal))
        {
            Assert.Fail(
                "The native-fake tests require an isolated native-fake test process.");
        }

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64
            || (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()))
        {
            Assert.Fail(
                "The native-fake P/Invoke tests require a Windows or macOS x64 process.");
        }

        ConfigureResolvers();
        Assert.AreEqual(0U, NativeAudioManagedFakeMethods.Reset());
    }

    [TestMethod]
    public async Task DeviceCatalogReadsFixedEndpointsThroughProductionPInvokePath()
    {
        WindowsAudioDeviceCatalog catalog = new();

        AudioDeviceSnapshot snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(6, snapshot.Devices.Count);
        Assert.AreEqual(
            "Fake microphone",
            snapshot.Devices.Single(device => device.Id == "fake-physical-input").Label);
        Assert.IsTrue(snapshot.Devices.Single(
            device => device.Id == "fake-physical-input").IsDefault);
        Assert.AreEqual(
            "Meeting speaker render",
            snapshot.Devices.Single(
                device => device.Id == "fake-virtual-speaker-render").Label);
    }

    [TestMethod]
    public async Task AbiOneStartsThroughProductionPInvokePath()
    {
        await using NativeAudioEngine engine = new();

        await engine.StartAsync(ValidConfiguration, CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetCreateCount());
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetStartCount());
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetStopCount());
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetDestroyCount());
    }

    [TestMethod]
    public async Task AbiTwoReturnsExplicitIncompatibilityThroughProductionPInvokePath()
    {
        NativeAudioManagedFakeMethods.SetAbiVersion(2);
        await using NativeAudioEngine engine = new();

        NativeAudioException exception =
            await Assert.ThrowsExactlyAsync<NativeAudioException>(
                () => engine.StartAsync(ValidConfiguration, CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.AbiMismatch, exception.Status);
        Assert.AreEqual(0U, NativeAudioManagedFakeMethods.GetCreateCount());
        Assert.AreEqual(0U, NativeAudioManagedFakeMethods.GetLiveHandleCount());
    }

    [TestMethod]
    public async Task SafeHandleReleasesExactlyOnceThroughProductionPInvokePath()
    {
        NativeAudioEngine engine = new();
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);

        await engine.DisposeAsync();
        await engine.DisposeAsync();

        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetDestroyCount());
        Assert.AreEqual(0U, NativeAudioManagedFakeMethods.GetLiveHandleCount());
    }

    [TestMethod]
    public async Task FailedCreateDoesNotLeakThroughProductionPInvokePath()
    {
        NativeAudioManagedFakeMethods.SetCreateBehavior(
            (int)NativeAudioStatus.InternalError,
            returnHandle: 1);
        await using NativeAudioEngine engine = new();

        NativeAudioException exception =
            await Assert.ThrowsExactlyAsync<NativeAudioException>(
                () => engine.StartAsync(ValidConfiguration, CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.InternalError, exception.Status);
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetDestroyCount());
        Assert.AreEqual(0U, NativeAudioManagedFakeMethods.GetLiveHandleCount());
    }

    [TestMethod]
    public async Task StopIsIdempotentThroughProductionPInvokePath()
    {
        await using NativeAudioEngine engine = new();
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);

        await Task.WhenAll(
            engine.StopAsync(CancellationToken.None),
            engine.StopAsync(CancellationToken.None),
            engine.StopAsync(CancellationToken.None));

        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetStopCount());
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetDestroyCount());
    }

    [TestMethod]
    public async Task DisposeCancelsAndJoinsPollingThroughProductionPInvokePath()
    {
        NativeAudioEngine engine = new();
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);
        await WaitUntilAsync(
            () => NativeAudioManagedFakeMethods.GetPollCount() >= 1);

        await engine.DisposeAsync();
        uint pollCountAfterDispose = NativeAudioManagedFakeMethods.GetPollCount();
        await Task.Delay(TimeSpan.FromMilliseconds(30));

        Assert.AreEqual(
            pollCountAfterDispose,
            NativeAudioManagedFakeMethods.GetPollCount());
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetStopCount());
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetDestroyCount());
        Assert.AreEqual(0U, NativeAudioManagedFakeMethods.GetLiveHandleCount());
    }

    [TestMethod]
    public async Task PcmPollProbesThenCopiesThroughProductionPInvokePath()
    {
        NativeAudioManagedFakeMethods.QueueTwoFramePcm(
            (uint)NativeAudioEventKind.InboundPcm16,
            (uint)NativeAudioRoute.OriginalFailOpen,
            sequence: 73,
            sample0: 513,
            sample1: -3);
        await using NativeAudioEngine engine = new();
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);

        using AudioEngineEvent? result =
            await engine.PollEventAsync(CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(AudioEngineEventKind.InboundPcm16, result.Kind);
        Assert.AreEqual(AudioEngineRoute.OriginalFailOpen, result.Route);
        Assert.AreEqual(2U, result.FrameCount);
        Assert.AreEqual(73UL, result.Sequence);
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 253, 255 },
            result.Pcm16.ToArray());
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetPcmProbeCount());
        Assert.AreEqual(1U, NativeAudioManagedFakeMethods.GetPcmCopyCount());
    }

    private static void ConfigureResolvers()
    {
        lock (ResolverGate)
        {
            if (_resolverConfigured)
            {
                return;
            }

            string? configuredPath =
                Environment.GetEnvironmentVariable(FakeLibraryVariable);
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                Assert.Fail($"{FakeLibraryVariable} must name the native fake library.");
            }

            string fullPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(fullPath))
            {
                Assert.Fail($"The native fake library does not exist: {fullPath}");
            }

            _fakeLibraryPath = fullPath;
            NativeLibrary.SetDllImportResolver(
                typeof(PInvokeNativeAudioApi).Assembly,
                ResolveLibrary);
            NativeLibrary.SetDllImportResolver(
                typeof(NativeAudioNativeFakeTests).Assembly,
                ResolveLibrary);
            _resolverConfigured = true;
        }
    }

    private static nint ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        _ = assembly;
        _ = searchPath;
        if (libraryName is "EMKE.NativeAudio" or "EMKE.NativeAudio.ManagedFake")
        {
            return NativeLibrary.Load(_fakeLibraryPath!);
        }

        return nint.Zero;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
    }
}

internal static partial class NativeAudioManagedFakeMethods
{
    private const string LibraryName = "EMKE.NativeAudio.ManagedFake";

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Reset();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_set_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetAbiVersion(uint version);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_set_create_behavior")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetCreateBehavior(int status, int returnHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_queue_two_frame_pcm")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void QueueTwoFramePcm(
        uint kind,
        uint route,
        ulong sequence,
        short sample0,
        short sample1);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_get_create_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetCreateCount();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_get_start_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetStartCount();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_get_stop_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetStopCount();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_get_destroy_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetDestroyCount();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_get_poll_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetPollCount();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_get_pcm_probe_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetPcmProbeCount();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_get_pcm_copy_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetPcmCopyCount();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_managed_fake_get_live_handle_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetLiveHandleCount();
}

#pragma warning restore CA2007
#pragma warning restore CA1515
