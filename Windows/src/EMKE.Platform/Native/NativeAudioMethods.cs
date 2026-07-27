using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EMKE.Platform.Native;

internal static partial class NativeAudioMethods
{
    private const string LibraryName = "EMKE.NativeAudio";

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbiVersion();

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_sizeof_config")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetConfigurationSize();

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_sizeof_event")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetEventSize();

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_sizeof_diagnostics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetDiagnosticsSize();

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_sizeof_discovered_endpoint")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetDiscoveredEndpointSize();

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_sizeof_endpoint_snapshot")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetEndpointSnapshotSize();

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_discover_endpoints")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeAudioStatus DiscoverEndpoints(
        ref NativeAudioEndpointSnapshot snapshot);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeAudioStatus Create(
        in NativeAudioConfiguration configuration,
        out nint handle);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint handle);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_start")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeAudioStatus Start(SafeNativeAudioHandle handle);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeAudioStatus Stop(SafeNativeAudioHandle handle);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_set_inbound_route")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeAudioStatus SetInboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_set_outbound_route")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeAudioStatus SetOutboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_enqueue_inbound_translation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeAudioStatus EnqueueInboundTranslation(
        SafeNativeAudioHandle handle,
        short* pcm16,
        uint frameCount);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_enqueue_outbound_translation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeAudioStatus EnqueueOutboundTranslation(
        SafeNativeAudioHandle handle,
        short* pcm16,
        uint frameCount);

    [DefaultDllImportSearchPaths(
        DllImportSearchPath.SafeDirectories)]
    [LibraryImport(LibraryName, EntryPoint = "emke_audio_poll_event")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeAudioStatus PollEvent(
        SafeNativeAudioHandle handle,
        ref NativeAudioEvent nativeEvent,
        short* pcm16,
        uint pcmCapacityFrames);
}

internal sealed class PInvokeNativeAudioApi : INativeAudioApi
{
    public static PInvokeNativeAudioApi Instance { get; } = new();

    private PInvokeNativeAudioApi()
    {
    }

    public uint GetAbiVersion() => NativeAudioMethods.GetAbiVersion();

    public uint GetConfigurationSize() => NativeAudioMethods.GetConfigurationSize();

    public uint GetEventSize() => NativeAudioMethods.GetEventSize();

    public uint GetDiagnosticsSize() => NativeAudioMethods.GetDiagnosticsSize();

    public uint GetDiscoveredEndpointSize() =>
        NativeAudioMethods.GetDiscoveredEndpointSize();

    public uint GetEndpointSnapshotSize() =>
        NativeAudioMethods.GetEndpointSnapshotSize();

    public NativeAudioStatus DiscoverEndpoints(
        ref NativeAudioEndpointSnapshot snapshot) =>
        NativeAudioMethods.DiscoverEndpoints(ref snapshot);

    public NativeAudioStatus Create(
        in NativeAudioConfiguration configuration,
        out SafeNativeAudioHandle? handle)
    {
        NativeAudioStatus status =
            NativeAudioMethods.Create(in configuration, out nint rawHandle);
        handle = rawHandle == nint.Zero
            ? null
            : new SafeNativeAudioHandle(this, rawHandle);
        return status;
    }

    public void Destroy(nint handle)
    {
        NativeAudioMethods.Destroy(handle);
    }

    public NativeAudioStatus Start(SafeNativeAudioHandle handle) =>
        NativeAudioMethods.Start(handle);

    public NativeAudioStatus Stop(SafeNativeAudioHandle handle) =>
        NativeAudioMethods.Stop(handle);

    public NativeAudioStatus SetInboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route) =>
        NativeAudioMethods.SetInboundRoute(handle, route);

    public NativeAudioStatus SetOutboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route) =>
        NativeAudioMethods.SetOutboundRoute(handle, route);

    public unsafe NativeAudioStatus EnqueueInboundTranslation(
        SafeNativeAudioHandle handle,
        ReadOnlySpan<byte> pcm16)
    {
        fixed (byte* bytes = pcm16)
        {
            return NativeAudioMethods.EnqueueInboundTranslation(
                handle,
                (short*)bytes,
                checked((uint)(pcm16.Length / sizeof(short))));
        }
    }

    public unsafe NativeAudioStatus EnqueueOutboundTranslation(
        SafeNativeAudioHandle handle,
        ReadOnlySpan<byte> pcm16)
    {
        fixed (byte* bytes = pcm16)
        {
            return NativeAudioMethods.EnqueueOutboundTranslation(
                handle,
                (short*)bytes,
                checked((uint)(pcm16.Length / sizeof(short))));
        }
    }

    public unsafe NativeAudioStatus Poll(
        SafeNativeAudioHandle handle,
        ref NativeAudioEvent nativeEvent,
        Span<byte> pcm16)
    {
        fixed (byte* bytes = pcm16)
        {
            return NativeAudioMethods.PollEvent(
                handle,
                ref nativeEvent,
                (short*)bytes,
                checked((uint)(pcm16.Length / sizeof(short))));
        }
    }
}
