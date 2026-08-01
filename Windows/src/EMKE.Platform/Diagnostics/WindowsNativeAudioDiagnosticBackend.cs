using System.Runtime.InteropServices;
using EMKE.Platform.Native;

namespace EMKE.Platform.Diagnostics;

public sealed class WindowsNativeAudioDiagnosticBackend
    : IWindowsAudioDiagnosticBackend
{
    public const string InteractiveAudioUnavailableCode =
        "windowsAudioDiagnostics.interactiveAudioUnavailable";

    private readonly INativeAudioApi _native;

    public WindowsNativeAudioDiagnosticBackend()
        : this(PInvokeNativeAudioApi.Instance)
    {
    }

    internal WindowsNativeAudioDiagnosticBackend(INativeAudioApi native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public Task<double> MeasureInputLevelAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<double>(
            new PlatformNotSupportedException(
                InteractiveAudioUnavailableCode));
    }

    public Task PlayLocalPcm16Async(
        string endpointId,
        ReadOnlyMemory<short> pcm16,
        int sampleRate,
        int channelCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        if (pcm16.IsEmpty)
        {
            throw new ArgumentException(
                "Local diagnostic PCM must not be empty.",
                nameof(pcm16));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(
            new PlatformNotSupportedException(
                InteractiveAudioUnavailableCode));
    }

    public Task<WindowsAudioDiagnosticSnapshot> InspectAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(InspectCore());
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
        {
            return Task.FromResult(
                Snapshot([], "nativeAudioUnavailable"));
        }
    }

    private unsafe WindowsAudioDiagnosticSnapshot InspectCore()
    {
        NativeAudioEndpointSnapshot snapshot = default;
        snapshot.Size = checked(
            (uint)Marshal.SizeOf<NativeAudioEndpointSnapshot>());
        snapshot.AbiVersion = NativeAudioConstants.AbiVersion;
        NativeAudioStatus status = _native.DiscoverEndpoints(ref snapshot);
        if (status != NativeAudioStatus.Ok)
        {
            return Snapshot(
                [],
                $"nativeAudio.{StatusName(status)}");
        }

        List<WindowsAudioEndpointDiagnostic> endpoints = [];
        ushort* input = snapshot.PhysicalInputEndpointId;
        AddPhysical(
            endpoints,
            input,
            snapshot.PhysicalInputEndpointIdLength,
            "Default physical input",
            WindowsAudioEndpointRole.PhysicalInput);
        ushort* output = snapshot.PhysicalOutputEndpointId;
        AddPhysical(
            endpoints,
            output,
            snapshot.PhysicalOutputEndpointIdLength,
            "Default physical output",
            WindowsAudioEndpointRole.PhysicalOutput);
        AddVirtual(endpoints, snapshot.VirtualEndpoint0);
        AddVirtual(endpoints, snapshot.VirtualEndpoint1);
        AddVirtual(endpoints, snapshot.VirtualEndpoint2);
        AddVirtual(endpoints, snapshot.VirtualEndpoint3);

        NativeAudioEndpointDiscoveryStatus discovery = Enum.IsDefined(
            typeof(NativeAudioEndpointDiscoveryStatus),
            snapshot.DiscoveryStatus)
                ? (NativeAudioEndpointDiscoveryStatus)snapshot.DiscoveryStatus
                : NativeAudioEndpointDiscoveryStatus.SourceError;
        return Snapshot(
            endpoints,
            discovery == NativeAudioEndpointDiscoveryStatus.Ready
                ? "none"
                : $"endpointDiscovery.{DiscoveryName(discovery)}");
    }

    private static unsafe void AddPhysical(
        List<WindowsAudioEndpointDiagnostic> endpoints,
        ushort* endpointId,
        uint length,
        string friendlyName,
        WindowsAudioEndpointRole role)
    {
        string? id = ReadEndpointId(endpointId, length);
        if (id is null)
        {
            return;
        }

        endpoints.Add(new WindowsAudioEndpointDiagnostic(
            id,
            friendlyName,
            role,
            "System mix format",
            IsAvailable: true));
    }

    private static unsafe void AddVirtual(
        List<WindowsAudioEndpointDiagnostic> endpoints,
        NativeAudioDiscoveredEndpoint endpoint)
    {
        ushort* endpointId = endpoint.EndpointId;
        string? id = ReadEndpointId(endpointId, endpoint.EndpointIdLength);
        if (id is null
            || !Enum.IsDefined(typeof(NativeAudioEndpointRole), endpoint.Role))
        {
            return;
        }

        NativeAudioEndpointRole nativeRole =
            (NativeAudioEndpointRole)endpoint.Role;
        (WindowsAudioEndpointRole role, string name) = nativeRole switch
        {
            NativeAudioEndpointRole.MeetingSpeakerRender =>
                (WindowsAudioEndpointRole.MeetingSpeakerRender,
                    "EMKE Virtual Speaker"),
            NativeAudioEndpointRole.AppSpeakerCapture =>
                (WindowsAudioEndpointRole.AppSpeakerCapture,
                    "EMKE Internal Speaker Capture"),
            NativeAudioEndpointRole.AppMicrophoneRender =>
                (WindowsAudioEndpointRole.AppMicrophoneRender,
                    "EMKE Internal Microphone Render"),
            NativeAudioEndpointRole.MeetingMicrophoneCapture =>
                (WindowsAudioEndpointRole.MeetingMicrophoneCapture,
                    "EMKE Virtual Microphone"),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };
        endpoints.Add(new WindowsAudioEndpointDiagnostic(
            id,
            name,
            role,
            "48000 Hz, 2 channel, float32",
            IsAvailable: endpoint.State == 1));
    }

    private static unsafe string? ReadEndpointId(
        ushort* endpointId,
        uint length)
    {
        if (length == 0 || length >= NativeAudioConstants.EndpointIdCapacity)
        {
            return null;
        }

        return new string(
            (char*)endpointId,
            0,
            checked((int)length));
    }

    private static WindowsAudioDiagnosticSnapshot Snapshot(
        IEnumerable<WindowsAudioEndpointDiagnostic> endpoints,
        string category)
    {
        return new WindowsAudioDiagnosticSnapshot(
            endpoints,
            new WindowsAudioDiagnosticCounters(
                category,
                Underruns: 0,
                Overflows: 0,
                DroppedFrames: 0));
    }

    private static string StatusName(NativeAudioStatus status)
    {
        return status switch
        {
            NativeAudioStatus.InvalidArgument => "invalidArgument",
            NativeAudioStatus.AbiMismatch => "abiMismatch",
            NativeAudioStatus.DeviceMissing => "deviceMissing",
            NativeAudioStatus.FormatUnsupported => "formatUnsupported",
            NativeAudioStatus.QueueFull => "queueFull",
            NativeAudioStatus.NotRunning => "notRunning",
            NativeAudioStatus.InternalError => "internalError",
            NativeAudioStatus.Ok => "none",
            _ => "unknown",
        };
    }

    private static string DiscoveryName(
        NativeAudioEndpointDiscoveryStatus status)
    {
        return status switch
        {
            NativeAudioEndpointDiscoveryStatus.DriverMissing =>
                "driverMissing",
            NativeAudioEndpointDiscoveryStatus.VirtualEndpointsPartial =>
                "virtualEndpointsPartial",
            NativeAudioEndpointDiscoveryStatus.PhysicalInputMissing =>
                "physicalInputMissing",
            NativeAudioEndpointDiscoveryStatus.PhysicalOutputMissing =>
                "physicalOutputMissing",
            NativeAudioEndpointDiscoveryStatus.SourceError => "sourceError",
            NativeAudioEndpointDiscoveryStatus.Ready => "ready",
            _ => "unknown",
        };
    }
}
