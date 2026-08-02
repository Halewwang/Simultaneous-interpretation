using System.Runtime.InteropServices;
using EMKE.Core;

namespace EMKE.Platform.Native;

/// <summary>Projects the bounded native endpoint catalog into Core device data.</summary>
public sealed class WindowsAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly INativeAudioApi _native;

    public WindowsAudioDeviceCatalog()
        : this(PInvokeNativeAudioApi.Instance)
    {
    }

    internal WindowsAudioDeviceCatalog(INativeAudioApi native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public Task<AudioDeviceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAbi();

        ThrowIfFailed(_native.EnumerateEndpointsV1([], out uint requiredCount), "count");
        cancellationToken.ThrowIfCancellationRequested();
        if (requiredCount > NativeAudioConstants.MaximumEnumeratedEndpoints)
        {
            throw InvalidCatalog("count exceeds the supported maximum.");
        }

        NativeAudioEndpointDescriptorV1[] descriptors = [];
        for (int attempt = 0; attempt != 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            descriptors = new NativeAudioEndpointDescriptorV1[checked((int)requiredCount)];
            NativeAudioStatus status = _native.EnumerateEndpointsV1(
                descriptors,
                out uint filledCount);
            cancellationToken.ThrowIfCancellationRequested();

            if (filledCount > NativeAudioConstants.MaximumEnumeratedEndpoints)
            {
                throw InvalidCatalog("count exceeds the supported maximum.");
            }

            if (filledCount > requiredCount
                && status == NativeAudioStatus.InvalidArgument
                && attempt == 0)
            {
                requiredCount = filledCount;
                continue;
            }

            ThrowIfFailed(status, "fill");
            if (filledCount > requiredCount)
            {
                throw InvalidCatalog("count grew more than once.");
            }

            return Task.FromResult(Map(descriptors.AsSpan(0, checked((int)filledCount))));
        }

        throw InvalidCatalog("count grew more than once.");
    }

    private void ValidateAbi()
    {
        if (_native.GetAbiVersion() != NativeAudioConstants.AbiVersion
            || _native.GetEndpointDescriptorV1Size()
                != checked((uint)Marshal.SizeOf<NativeAudioEndpointDescriptorV1>()))
        {
            throw new NativeAudioException(
                AudioEngineStatus.AbiMismatch,
                "Native audio endpoint enumeration ABI is incompatible.");
        }
    }

    private static unsafe AudioDeviceSnapshot Map(
        ReadOnlySpan<NativeAudioEndpointDescriptorV1> descriptors)
    {
        List<AudioDeviceDescriptor> mapped = new(descriptors.Length);
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<NativeAudioEndpointRole> roles = [];

        foreach (NativeAudioEndpointDescriptorV1 descriptor in descriptors)
        {
            if (descriptor.Size != checked((uint)Marshal.SizeOf<NativeAudioEndpointDescriptorV1>()))
            {
                throw InvalidCatalog("descriptor size is incompatible.");
            }

            NativeAudioEndpointFlags flags = (NativeAudioEndpointFlags)descriptor.Flags;
            const NativeAudioEndpointFlags knownFlags = NativeAudioEndpointFlags.Active
                | NativeAudioEndpointFlags.PhysicalDefault
                | NativeAudioEndpointFlags.VirtualRole;
            if ((flags & ~knownFlags) != 0
                || (flags & NativeAudioEndpointFlags.Active) == 0)
            {
                throw InvalidCatalog("descriptor flags are invalid.");
            }

            NativeAudioEndpointDataFlow flow = descriptor.Direction switch
            {
                (uint)NativeAudioEndpointDataFlow.Render => NativeAudioEndpointDataFlow.Render,
                (uint)NativeAudioEndpointDataFlow.Capture => NativeAudioEndpointDataFlow.Capture,
                _ => throw InvalidCatalog("descriptor direction is invalid."),
            };
            AudioDeviceDirection direction = flow == NativeAudioEndpointDataFlow.Render
                ? AudioDeviceDirection.Output
                : AudioDeviceDirection.Input;
            ushort* idBuffer = descriptor.Id;
            ushort* nameBuffer = descriptor.Name;
            ushort* roleBuffer = descriptor.Role;
            string id = ReadTerminated(idBuffer, NativeAudioConstants.EndpointIdCapacity, "ID");
            string nativeName = ReadTerminated(
                nameBuffer,
                NativeAudioConstants.EndpointNameCapacity,
                "name");
            string roleText = ReadOptionalTerminated(
                roleBuffer,
                NativeAudioConstants.EndpointRoleCapacity,
                "role");
            if (!ids.Add(id))
            {
                throw InvalidCatalog("descriptor IDs are duplicated.");
            }

            bool isVirtual = (flags & NativeAudioEndpointFlags.VirtualRole) != 0;
            bool isDefault = (flags & NativeAudioEndpointFlags.PhysicalDefault) != 0;
            if (isVirtual == isDefault)
            {
                if (isVirtual || !string.IsNullOrEmpty(roleText))
                {
                    throw InvalidCatalog("descriptor role flags are invalid.");
                }
            }

            string label = nativeName;
            if (isVirtual)
            {
                NativeAudioEndpointRole role = ParseRole(roleText, flow);
                if (!roles.Add(role))
                {
                    throw InvalidCatalog("virtual roles are duplicated.");
                }
                label = LabelForRole(role);
            }
            else if (!string.IsNullOrEmpty(roleText))
            {
                throw InvalidCatalog("physical descriptor contains a role.");
            }

            mapped.Add(new AudioDeviceDescriptor(id, label, direction, isDefault, true));
        }

        if (roles.Count != 4)
        {
            throw new NativeAudioException(
                AudioEngineStatus.DeviceMissing,
                "Native audio endpoint enumeration is missing a virtual role.");
        }

        return new AudioDeviceSnapshot(mapped);
    }

    private static NativeAudioEndpointRole ParseRole(
        string value,
        NativeAudioEndpointDataFlow flow)
    {
        NativeAudioEndpointRole role = value switch
        {
            "emke.meeting-speaker.render" => NativeAudioEndpointRole.MeetingSpeakerRender,
            "emke.app-speaker.capture" => NativeAudioEndpointRole.AppSpeakerCapture,
            "emke.app-microphone.render" => NativeAudioEndpointRole.AppMicrophoneRender,
            "emke.meeting-microphone.capture" => NativeAudioEndpointRole.MeetingMicrophoneCapture,
            _ => throw InvalidCatalog("virtual role is unknown."),
        };
        NativeAudioEndpointDataFlow expected = role is NativeAudioEndpointRole.MeetingSpeakerRender
            or NativeAudioEndpointRole.AppMicrophoneRender
            ? NativeAudioEndpointDataFlow.Render
            : NativeAudioEndpointDataFlow.Capture;
        if (flow != expected)
        {
            throw InvalidCatalog("virtual role direction is invalid.");
        }

        return role;
    }

    private static string LabelForRole(NativeAudioEndpointRole role) => role switch
    {
        NativeAudioEndpointRole.MeetingSpeakerRender => "Meeting speaker render",
        NativeAudioEndpointRole.AppSpeakerCapture => "App speaker capture",
        NativeAudioEndpointRole.AppMicrophoneRender => "App microphone render",
        NativeAudioEndpointRole.MeetingMicrophoneCapture => "Meeting microphone capture",
        _ => throw InvalidCatalog("virtual role is unknown."),
    };

    private static unsafe string ReadTerminated(ushort* buffer, int capacity, string name)
    {
        string value = ReadOptionalTerminated(buffer, capacity, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidCatalog($"descriptor {name} is blank.");
        }

        return value;
    }

    private static unsafe string ReadOptionalTerminated(ushort* buffer, int capacity, string name)
    {
        int length = 0;
        while (length < capacity && buffer[length] != 0)
        {
            length++;
        }
        if (length == capacity)
        {
            throw InvalidCatalog($"descriptor {name} is not terminated.");
        }

        return new string((char*)buffer, 0, length);
    }

    private static void ThrowIfFailed(NativeAudioStatus status, string operation)
    {
        if (status == NativeAudioStatus.Ok)
        {
            return;
        }

        throw new NativeAudioException(MapStatus(status),
            $"Native audio endpoint enumeration {operation} failed with {status}.");
    }

    private static NativeAudioException InvalidCatalog(string detail) => new(
        AudioEngineStatus.InternalError,
        $"Native audio endpoint enumeration returned an invalid catalog: {detail}");

    private static AudioEngineStatus MapStatus(NativeAudioStatus status) => status switch
    {
        NativeAudioStatus.InvalidArgument => AudioEngineStatus.InvalidArgument,
        NativeAudioStatus.AbiMismatch => AudioEngineStatus.AbiMismatch,
        NativeAudioStatus.DeviceMissing => AudioEngineStatus.DeviceMissing,
        NativeAudioStatus.FormatUnsupported => AudioEngineStatus.FormatUnsupported,
        NativeAudioStatus.QueueFull => AudioEngineStatus.QueueFull,
        NativeAudioStatus.NotRunning => AudioEngineStatus.NotRunning,
        _ => AudioEngineStatus.InternalError,
    };
}
