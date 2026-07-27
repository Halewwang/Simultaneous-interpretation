using System.Runtime.InteropServices;
using EMKE.Core;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Platform.Native;

internal static class NativeAudioConstants
{
    public const uint AbiVersion = 1;
    public const int EndpointIdCapacity = 512;
    public const int NetworkSampleRate = 24_000;
    public const int NetworkChannelCount = 1;
    public const int EventChannelCapacity = 64;
}

internal enum NativeAudioStatus
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    DeviceMissing = 3,
    FormatUnsupported = 4,
    QueueFull = 5,
    NotRunning = 6,
    InternalError = 7,
}

internal enum NativeAudioRoute
{
    Stopped = 0,
    Translated = 1,
    OriginalFailOpen = 2,
    OriginalBypass = 3,
    MutedFailClosed = 4,
}

internal enum NativeAudioEventKind
{
    None = 0,
    InboundPcm16 = 1,
    OutboundPcm16 = 2,
    DeviceChanged = 3,
    StreamError = 4,
    Backpressure = 5,
}

internal enum NativeAudioEndpointRole
{
    MeetingSpeakerRender = 0,
    AppSpeakerCapture = 1,
    AppMicrophoneRender = 2,
    MeetingMicrophoneCapture = 3,
}

internal enum NativeAudioEndpointDataFlow
{
    Render = 0,
    Capture = 1,
}

internal enum NativeAudioEndpointDiscoveryStatus
{
    Ready = 0,
    DriverMissing = 1,
    VirtualEndpointsPartial = 2,
    PhysicalInputMissing = 3,
    PhysicalOutputMissing = 4,
    SourceError = 5,
}

#pragma warning disable CA1815 // Native ABI structs use fixed binary identity, not managed value equality.

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAudioConfiguration
{
    public uint Size;
    public uint AbiVersion;
    public fixed ushort PhysicalInputEndpointId[NativeAudioConstants.EndpointIdCapacity];
    public fixed ushort PhysicalOutputEndpointId[NativeAudioConstants.EndpointIdCapacity];
    public fixed ushort VirtualSpeakerRenderEndpointId[NativeAudioConstants.EndpointIdCapacity];
    public fixed ushort VirtualSpeakerCaptureEndpointId[NativeAudioConstants.EndpointIdCapacity];
    public fixed ushort VirtualMicrophoneRenderEndpointId[NativeAudioConstants.EndpointIdCapacity];
    public fixed ushort VirtualMicrophoneCaptureEndpointId[NativeAudioConstants.EndpointIdCapacity];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAudioDiscoveredEndpoint
{
    public uint Size;
    public uint AbiVersion;
    public uint Role;
    public uint DataFlow;
    public uint State;
    public uint EndpointIdLength;
    public fixed ushort EndpointId[NativeAudioConstants.EndpointIdCapacity];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAudioEndpointSnapshot
{
    public uint Size;
    public uint AbiVersion;
    public uint DiscoveryStatus;
    public uint SourceOperation;
    public int SourceNativeCode;
    public uint Reserved;
    public NativeAudioDiscoveredEndpoint VirtualEndpoint0;
    public NativeAudioDiscoveredEndpoint VirtualEndpoint1;
    public NativeAudioDiscoveredEndpoint VirtualEndpoint2;
    public NativeAudioDiscoveredEndpoint VirtualEndpoint3;
    public uint PhysicalInputEndpointIdLength;
    public fixed ushort PhysicalInputEndpointId[NativeAudioConstants.EndpointIdCapacity];
    public uint PhysicalOutputEndpointIdLength;
    public fixed ushort PhysicalOutputEndpointId[NativeAudioConstants.EndpointIdCapacity];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioEvent
{
    public uint Size;
    public uint AbiVersion;
    public uint Kind;
    public uint Status;
    public uint Route;
    public uint FrameCount;
    public ulong Sequence;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioDiagnostics
{
    public uint Size;
    public uint AbiVersion;
    public uint IsRunning;
    public uint InboundRoute;
    public uint OutboundRoute;
    public uint QueuedInboundTranslationFrames;
    public uint QueuedOutboundTranslationFrames;
    public uint Reserved;
    public ulong CapturedInboundFrames;
    public ulong CapturedOutboundFrames;
    public ulong ConsumedInboundTranslationFrames;
    public ulong ConsumedOutboundTranslationFrames;
    public ulong DroppedFrames;
    public ulong QueueFullEvents;
    public ulong OutboundUnderruns;
    public ulong InboundTranslationFailures;
    public ulong DeviceFailures;
}

#pragma warning restore CA1815

#pragma warning disable CA1032 // The stable native status is required for runtime recovery decisions.

public sealed class NativeAudioException : Exception
{
    public NativeAudioException(AudioEngineStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public AudioEngineStatus Status { get; }
}

#pragma warning restore CA1032

internal sealed class SafeNativeAudioHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly INativeAudioApi _owner;

    public SafeNativeAudioHandle(INativeAudioApi owner, nint handle)
        : base(ownsHandle: true)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
#pragma warning disable CA1031 // SafeHandle release and critical finalization must never propagate.
        try
        {
            _owner.Destroy(handle);
            return true;
        }
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }
}

internal interface INativeAudioApi
{
    uint GetAbiVersion();

    uint GetConfigurationSize();

    uint GetEventSize();

    uint GetDiagnosticsSize();

    uint GetDiscoveredEndpointSize();

    uint GetEndpointSnapshotSize();

    NativeAudioStatus DiscoverEndpoints(ref NativeAudioEndpointSnapshot snapshot);

    NativeAudioStatus Create(
        in NativeAudioConfiguration configuration,
        out SafeNativeAudioHandle? handle);

    void Destroy(nint handle);

    NativeAudioStatus Start(SafeNativeAudioHandle handle);

    NativeAudioStatus Stop(SafeNativeAudioHandle handle);

    NativeAudioStatus SetInboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route);

    NativeAudioStatus SetOutboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route);

    NativeAudioStatus EnqueueInboundTranslation(
        SafeNativeAudioHandle handle,
        ReadOnlySpan<byte> pcm16);

    NativeAudioStatus EnqueueOutboundTranslation(
        SafeNativeAudioHandle handle,
        ReadOnlySpan<byte> pcm16);

    NativeAudioStatus Poll(
        SafeNativeAudioHandle handle,
        ref NativeAudioEvent nativeEvent,
        Span<byte> pcm16);
}

internal interface INativeAudioPollDelay
{
    ValueTask DelayAsync(CancellationToken cancellationToken);
}
