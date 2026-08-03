using System.Runtime.CompilerServices;
using EMKE.Platform.Native;

namespace EMKE.SafeHandle.FinalizerProbe;

#pragma warning disable CA1303 // Probe output is a machine-readable invariant.
#pragma warning disable CA2000 // The unrooted SafeHandle must be released by its critical finalizer.

internal static class Program
{
    private const string SuccessEvidence =
        "destroyAttempts=1;handleAlive=false";

    public static int Main()
    {
        using CountdownEvent destroyAttempted = new(1);
        ThrowingDestroyNativeAudioApi owner = new(destroyAttempted);
        WeakReference handleReference = CreateUnrootedHandle(owner);

        for (int attempt = 0; attempt < 10 && !destroyAttempted.IsSet; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (!destroyAttempted.Wait(TimeSpan.FromSeconds(5)))
        {
            return 2;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (handleReference.IsAlive)
        {
            return 3;
        }
        if (owner.DestroyAttempts != 1)
        {
            return 4;
        }

        Console.WriteLine(SuccessEvidence);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnrootedHandle(
        ThrowingDestroyNativeAudioApi owner)
    {
        SafeNativeAudioHandle handle = new(owner, new nint(73));
        return new WeakReference(handle, trackResurrection: false);
    }
}

internal sealed class ThrowingDestroyNativeAudioApi : INativeAudioApi
{
    private readonly CountdownEvent _destroyAttempted;
    private int _destroyAttempts;

    public ThrowingDestroyNativeAudioApi(CountdownEvent destroyAttempted)
    {
        _destroyAttempted = destroyAttempted;
    }

    public int DestroyAttempts => Volatile.Read(ref _destroyAttempts);

    public uint GetAbiVersion() => throw new NotSupportedException();

    public uint GetConfigurationSize() => throw new NotSupportedException();

    public uint GetEventSize() => throw new NotSupportedException();

    public uint GetDiagnosticsSize() => throw new NotSupportedException();

    public uint GetDiscoveredEndpointSize() => throw new NotSupportedException();

    public uint GetEndpointSnapshotSize() => throw new NotSupportedException();

    public uint GetEndpointDescriptorV1Size() => throw new NotSupportedException();

    public NativeAudioStatus DiscoverEndpoints(
        ref NativeAudioEndpointSnapshot snapshot) =>
        throw new NotSupportedException();

    public NativeAudioStatus EnumerateEndpointsV1(
        Span<NativeAudioEndpointDescriptorV1> items,
        out uint requiredCount) =>
        throw new NotSupportedException();

    public NativeAudioStatus Create(
        in NativeAudioConfiguration configuration,
        out SafeNativeAudioHandle? handle) =>
        throw new NotSupportedException();

    public void Destroy(nint handle)
    {
        _ = handle;
        int attempts = Interlocked.Increment(ref _destroyAttempts);
        if (attempts == 1)
        {
            _destroyAttempted.Signal();
        }

        throw new InvalidOperationException("Synthetic finalizer destroy failure.");
    }

    public NativeAudioStatus Start(SafeNativeAudioHandle handle) =>
        throw new NotSupportedException();

    public NativeAudioStatus Stop(SafeNativeAudioHandle handle) =>
        throw new NotSupportedException();

    public NativeAudioStatus SetInboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route) =>
        throw new NotSupportedException();

    public NativeAudioStatus SetOutboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route) =>
        throw new NotSupportedException();

    public NativeAudioStatus EnqueueInboundTranslation(
        SafeNativeAudioHandle handle,
        ReadOnlySpan<byte> pcm16) =>
        throw new NotSupportedException();

    public NativeAudioStatus EnqueueOutboundTranslation(
        SafeNativeAudioHandle handle,
        ReadOnlySpan<byte> pcm16) =>
        throw new NotSupportedException();

    public NativeAudioStatus Poll(
        SafeNativeAudioHandle handle,
        ref NativeAudioEvent nativeEvent,
        Span<byte> pcm16) =>
        throw new NotSupportedException();
}

#pragma warning restore CA2000
#pragma warning restore CA1303
