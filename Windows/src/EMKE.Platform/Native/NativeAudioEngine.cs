using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using EMKE.Core;

namespace EMKE.Platform.Native;

#pragma warning disable CA1001 // Dispose and DisposeAsync release the owned lifecycle resources.
#pragma warning disable CA1031 // The polling boundary propagates every native/projection failure through Channel completion.
#pragma warning disable CA2000 // Poll results and leases explicitly transfer ownership to the channel/Core event.
#pragma warning disable CA2025 // StopCore joins the polling task before disposing its captured handle and token source.
#pragma warning disable CA2213 // The lifecycle gate remains available so repeated DisposeAsync calls stay idempotent.

public sealed class NativeAudioEngine :
    ITranslationAudioEngine,
    IDisposable,
    IAsyncDisposable
{
    private readonly ArrayPool<byte> _pool;
    private readonly INativeAudioApi _native;
    private readonly INativeAudioPollDelay _pollDelay;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _pollCancellation;
    private Channel<PollResult>? _pollResults;
    private Task? _pollTask;
    private SafeNativeAudioHandle? _handle;
    private long _droppedEventCount;
    private int _noneQueued;
    private bool _disposed;

    public NativeAudioEngine()
        : this(
            PInvokeNativeAudioApi.Instance,
            ArrayPool<byte>.Shared,
            SystemNativeAudioPollDelay.Instance)
    {
    }

    internal NativeAudioEngine(
        INativeAudioApi native,
        ArrayPool<byte> pool,
        INativeAudioPollDelay pollDelay)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _pollDelay = pollDelay ?? throw new ArgumentNullException(nameof(pollDelay));
    }

    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    public async Task StartAsync(
        AudioEngineConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle is not null)
            {
                throw new InvalidOperationException("The native audio engine is already running.");
            }

            ValidateNetworkFormat(configuration);
            ValidateAbi();
            NativeAudioConfiguration nativeConfiguration =
                DiscoverAndMapConfiguration(configuration);
            cancellationToken.ThrowIfCancellationRequested();

            NativeAudioStatus createStatus =
                _native.Create(in nativeConfiguration, out SafeNativeAudioHandle? handle);
            if (createStatus != NativeAudioStatus.Ok)
            {
                handle?.Dispose();
                throw CreateStatusException(createStatus, "create");
            }

            if (handle is null || handle.IsInvalid)
            {
                handle?.Dispose();
                throw CreateStatusException(
                    NativeAudioStatus.InternalError,
                    "create returned no handle");
            }

            NativeAudioStatus startStatus;
            try
            {
                startStatus = _native.Start(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            if (startStatus != NativeAudioStatus.Ok)
            {
                handle.Dispose();
                throw CreateStatusException(startStatus, "start");
            }

            Channel<PollResult> channel = Channel.CreateBounded<PollResult>(
                new BoundedChannelOptions(NativeAudioConstants.EventChannelCapacity)
                {
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true,
                });
            CancellationTokenSource pollCancellation = new();

            _handle = handle;
            _pollResults = channel;
            _pollCancellation = pollCancellation;
            Interlocked.Exchange(ref _droppedEventCount, 0);
            Volatile.Write(ref _noneQueued, 0);
            _pollTask = Task.Run(
                () => PollLoopAsync(handle, channel, pollCancellation.Token),
                CancellationToken.None);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed && _handle is null)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<AudioEngineEvent?> PollEventAsync(
        CancellationToken cancellationToken)
    {
        Channel<PollResult> channel = Volatile.Read(ref _pollResults)
            ?? throw new InvalidOperationException("The native audio engine is not running.");
        PollResult result =
            await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsNone)
        {
            Volatile.Write(ref _noneQueued, 0);
        }

        return result.Event;
    }

    public async ValueTask EnqueueInboundTranslationAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken)
    {
        ValidatePcm16(pcm16);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeNativeAudioHandle handle = GetRunningHandle();
            ThrowIfStatusFailed(
                _native.EnqueueInboundTranslation(handle, pcm16.Span),
                "enqueue inbound translation");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask EnqueueOutboundTranslationAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken)
    {
        ValidatePcm16(pcm16);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeNativeAudioHandle handle = GetRunningHandle();
            ThrowIfStatusFailed(
                _native.EnqueueOutboundTranslation(handle, pcm16.Span),
                "enqueue outbound translation");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask SetInboundRouteAsync(
        InboundRoute route,
        CancellationToken cancellationToken)
    {
        NativeAudioRoute nativeRoute = route switch
        {
            InboundRoute.Stopped => NativeAudioRoute.Stopped,
            InboundRoute.Translated => NativeAudioRoute.Translated,
            InboundRoute.OriginalFailOpen => NativeAudioRoute.OriginalFailOpen,
            InboundRoute.OriginalBypass => NativeAudioRoute.OriginalBypass,
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStatusFailed(
                _native.SetInboundRoute(GetRunningHandle(), nativeRoute),
                "set inbound route");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask SetOutboundRouteAsync(
        OutboundRoute route,
        CancellationToken cancellationToken)
    {
        NativeAudioRoute nativeRoute = route switch
        {
            OutboundRoute.Stopped => NativeAudioRoute.Stopped,
            OutboundRoute.Translated => NativeAudioRoute.Translated,
            OutboundRoute.MutedFailClosed => NativeAudioRoute.MutedFailClosed,
            OutboundRoute.OriginalBypass => NativeAudioRoute.OriginalBypass,
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStatusFailed(
                _native.SetOutboundRoute(GetRunningHandle(), nativeRoute),
                "set outbound route");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            GC.SuppressFinalize(this);
        }
    }

    private static void ValidateNetworkFormat(AudioEngineConfiguration configuration)
    {
        if (configuration.SampleRate != NativeAudioConstants.NetworkSampleRate
            || configuration.ChannelCount != NativeAudioConstants.NetworkChannelCount)
        {
            throw new NativeAudioException(
                AudioEngineStatus.FormatUnsupported,
                "Native audio requires 24 kHz mono PCM16 network audio.");
        }
    }

    private void ValidateAbi()
    {
        uint nativeVersion = _native.GetAbiVersion();
        if (nativeVersion != NativeAudioConstants.AbiVersion)
        {
            throw new NativeAudioException(
                AudioEngineStatus.AbiMismatch,
                $"Native audio ABI {nativeVersion} is incompatible with ABI {NativeAudioConstants.AbiVersion}.");
        }

        ValidateStructSize<NativeAudioConfiguration>(
            "configuration",
            _native.GetConfigurationSize());
        ValidateStructSize<NativeAudioEvent>("event", _native.GetEventSize());
        ValidateStructSize<NativeAudioDiagnostics>(
            "diagnostics",
            _native.GetDiagnosticsSize());
        ValidateStructSize<NativeAudioDiscoveredEndpoint>(
            "discovered endpoint",
            _native.GetDiscoveredEndpointSize());
        ValidateStructSize<NativeAudioEndpointSnapshot>(
            "endpoint snapshot",
            _native.GetEndpointSnapshotSize());
    }

    private static void ValidateStructSize<T>(string name, uint nativeSize)
        where T : struct
    {
        uint managedSize = checked((uint)Marshal.SizeOf<T>());
        if (nativeSize != managedSize)
        {
            throw new NativeAudioException(
                AudioEngineStatus.AbiMismatch,
                $"Native audio {name} size {nativeSize} is incompatible with managed size {managedSize}.");
        }
    }

    private unsafe NativeAudioConfiguration DiscoverAndMapConfiguration(
        AudioEngineConfiguration configuration)
    {
        NativeAudioEndpointSnapshot snapshot = default;
        snapshot.Size = checked((uint)Marshal.SizeOf<NativeAudioEndpointSnapshot>());
        snapshot.AbiVersion = NativeAudioConstants.AbiVersion;
        ThrowIfStatusFailed(
            _native.DiscoverEndpoints(ref snapshot),
            "discover endpoints");
        if (snapshot.Size != Marshal.SizeOf<NativeAudioEndpointSnapshot>()
            || snapshot.AbiVersion != NativeAudioConstants.AbiVersion)
        {
            throw new NativeAudioException(
                AudioEngineStatus.AbiMismatch,
                "Native endpoint discovery returned an incompatible snapshot.");
        }

        NativeAudioEndpointDiscoveryStatus discoveryStatus =
            ParseEnum<NativeAudioEndpointDiscoveryStatus>(
                snapshot.DiscoveryStatus,
                "endpoint discovery status");
        if (discoveryStatus != NativeAudioEndpointDiscoveryStatus.Ready)
        {
            throw new NativeAudioException(
                AudioEngineStatus.DeviceMissing,
                $"Native endpoint discovery is not ready: {discoveryStatus}.");
        }

        Dictionary<NativeAudioEndpointRole, string> virtualEndpoints = [];
        AddVirtualEndpoint(virtualEndpoints, snapshot.VirtualEndpoint0);
        AddVirtualEndpoint(virtualEndpoints, snapshot.VirtualEndpoint1);
        AddVirtualEndpoint(virtualEndpoints, snapshot.VirtualEndpoint2);
        AddVirtualEndpoint(virtualEndpoints, snapshot.VirtualEndpoint3);

        string physicalInput;
        string physicalOutput;
        ushort* snapshotInput = snapshot.PhysicalInputEndpointId;
        ushort* snapshotOutput = snapshot.PhysicalOutputEndpointId;
        physicalInput = ResolvePhysicalEndpoint(
            configuration.InputDeviceId,
            snapshotInput,
            snapshot.PhysicalInputEndpointIdLength,
            "physical input");
        physicalOutput = ResolvePhysicalEndpoint(
            configuration.OutputDeviceId,
            snapshotOutput,
            snapshot.PhysicalOutputEndpointIdLength,
            "physical output");

        NativeAudioConfiguration result = default;
        result.Size = checked((uint)Marshal.SizeOf<NativeAudioConfiguration>());
        result.AbiVersion = NativeAudioConstants.AbiVersion;
        ushort* resultInput = result.PhysicalInputEndpointId;
        ushort* resultOutput = result.PhysicalOutputEndpointId;
        ushort* speakerRender = result.VirtualSpeakerRenderEndpointId;
        ushort* speakerCapture = result.VirtualSpeakerCaptureEndpointId;
        ushort* microphoneRender = result.VirtualMicrophoneRenderEndpointId;
        ushort* microphoneCapture = result.VirtualMicrophoneCaptureEndpointId;
        WriteEndpointId(resultInput, physicalInput, "physical input");
        WriteEndpointId(resultOutput, physicalOutput, "physical output");
        WriteEndpointId(
            speakerRender,
            RequireRole(
                virtualEndpoints,
                NativeAudioEndpointRole.MeetingSpeakerRender),
            "meeting speaker render");
        WriteEndpointId(
            speakerCapture,
            RequireRole(
                virtualEndpoints,
                NativeAudioEndpointRole.AppSpeakerCapture),
            "app speaker capture");
        WriteEndpointId(
            microphoneRender,
            RequireRole(
                virtualEndpoints,
                NativeAudioEndpointRole.AppMicrophoneRender),
            "app microphone render");
        WriteEndpointId(
            microphoneCapture,
            RequireRole(
                virtualEndpoints,
                NativeAudioEndpointRole.MeetingMicrophoneCapture),
            "meeting microphone capture");

        return result;
    }

    private static unsafe void AddVirtualEndpoint(
        IDictionary<NativeAudioEndpointRole, string> endpoints,
        NativeAudioDiscoveredEndpoint endpoint)
    {
        if (endpoint.Size != Marshal.SizeOf<NativeAudioDiscoveredEndpoint>()
            || endpoint.AbiVersion != NativeAudioConstants.AbiVersion)
        {
            throw new NativeAudioException(
                AudioEngineStatus.AbiMismatch,
                "Native endpoint discovery returned an incompatible endpoint.");
        }

        NativeAudioEndpointRole role =
            ParseEnum<NativeAudioEndpointRole>(endpoint.Role, "endpoint role");
        NativeAudioEndpointDataFlow actualFlow =
            ParseEnum<NativeAudioEndpointDataFlow>(
                endpoint.DataFlow,
                "endpoint data flow");
        NativeAudioEndpointDataFlow expectedFlow = role switch
        {
            NativeAudioEndpointRole.MeetingSpeakerRender =>
                NativeAudioEndpointDataFlow.Render,
            NativeAudioEndpointRole.AppSpeakerCapture =>
                NativeAudioEndpointDataFlow.Capture,
            NativeAudioEndpointRole.AppMicrophoneRender =>
                NativeAudioEndpointDataFlow.Render,
            NativeAudioEndpointRole.MeetingMicrophoneCapture =>
                NativeAudioEndpointDataFlow.Capture,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };
        if (actualFlow != expectedFlow)
        {
            throw new NativeAudioException(
                AudioEngineStatus.DeviceMissing,
                $"Native endpoint role {role} has the wrong data flow.");
        }

        ushort* endpointId = endpoint.EndpointId;
        string id = ReadEndpointId(
            endpointId,
            endpoint.EndpointIdLength,
            role.ToString());

        if (!endpoints.TryAdd(role, id))
        {
            throw new NativeAudioException(
                AudioEngineStatus.DeviceMissing,
                $"Native endpoint discovery returned duplicate role {role}.");
        }
    }

    private static string RequireRole(
        Dictionary<NativeAudioEndpointRole, string> endpoints,
        NativeAudioEndpointRole role)
    {
        if (!endpoints.TryGetValue(role, out string? endpointId))
        {
            throw new NativeAudioException(
                AudioEngineStatus.DeviceMissing,
                $"Native endpoint discovery did not return role {role}.");
        }

        return endpointId;
    }

    private static unsafe string ResolvePhysicalEndpoint(
        string? selectedId,
        ushort* discoveredId,
        uint discoveredLength,
        string role)
    {
        if (selectedId is not null)
        {
            if (string.IsNullOrWhiteSpace(selectedId))
            {
                throw new NativeAudioException(
                    AudioEngineStatus.InvalidArgument,
                    $"The selected {role} endpoint ID is empty.");
            }

            return selectedId;
        }

        return ReadEndpointId(discoveredId, discoveredLength, role);
    }

    private static unsafe string ReadEndpointId(
        ushort* endpointId,
        uint length,
        string role)
    {
        if (length == 0 || length >= NativeAudioConstants.EndpointIdCapacity)
        {
            throw new NativeAudioException(
                AudioEngineStatus.DeviceMissing,
                $"Native endpoint discovery returned an invalid {role} endpoint ID.");
        }

        return new string((char*)endpointId, 0, checked((int)length));
    }

    private static unsafe void WriteEndpointId(
        ushort* destination,
        string endpointId,
        string role)
    {
        if (endpointId.Length == 0
            || endpointId.Length >= NativeAudioConstants.EndpointIdCapacity)
        {
            throw new NativeAudioException(
                AudioEngineStatus.InvalidArgument,
                $"The {role} endpoint ID cannot fit the native configuration.");
        }

        for (int index = 0; index < endpointId.Length; index++)
        {
            destination[index] = endpointId[index];
        }

        destination[endpointId.Length] = 0;
    }

    private async Task PollLoopAsync(
        SafeNativeAudioHandle handle,
        Channel<PollResult> channel,
        CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PollResult result = PollNativeOnce(handle);
                if (result.IsNone)
                {
                    QueueNoneIfNeeded(channel);
                    await _pollDelay.DelayAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!channel.Writer.TryWrite(result))
                {
                    Interlocked.Increment(ref _droppedEventCount);
                    result.Event?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            completionError = exception;
        }
        finally
        {
            channel.Writer.TryComplete(completionError);
        }
    }

    private PollResult PollNativeOnce(SafeNativeAudioHandle handle)
    {
        NativeAudioEvent nativeEvent = CreateEmptyNativeEvent();
        NativeAudioStatus status =
            _native.Poll(handle, ref nativeEvent, Span<byte>.Empty);
        NativeAudioEvent metadata = nativeEvent;
        NativeAudioEventKind kind =
            ParseEnum<NativeAudioEventKind>(metadata.Kind, "event kind");

        if (kind is NativeAudioEventKind.InboundPcm16
            or NativeAudioEventKind.OutboundPcm16)
        {
            if (status != NativeAudioStatus.InvalidArgument
                || metadata.FrameCount == 0)
            {
                throw CreateStatusException(status, "probe PCM event");
            }

            return PollPcmEvent(handle, metadata);
        }

        ThrowIfStatusFailed(status, "poll event");
        if (kind == NativeAudioEventKind.None)
        {
            return PollResult.None;
        }

        AudioEngineStatus eventStatus =
            MapStatus(ParseEnum<NativeAudioStatus>(metadata.Status, "event status"));
        AudioEngineRoute route =
            MapRoute(ParseEnum<NativeAudioRoute>(metadata.Route, "event route"));
        AudioEngineEventKind eventKind = kind switch
        {
            NativeAudioEventKind.DeviceChanged => AudioEngineEventKind.DeviceChanged,
            NativeAudioEventKind.StreamError => AudioEngineEventKind.StreamError,
            NativeAudioEventKind.Backpressure => AudioEngineEventKind.Backpressure,
            _ => throw new NativeAudioException(
                AudioEngineStatus.InternalError,
                $"Native audio returned unsupported event kind {kind}."),
        };
        return new PollResult(
            AudioEngineEvent.CreateControl(
                eventKind,
                eventStatus,
                route,
                metadata.Sequence),
            IsNone: false);
    }

    private PollResult PollPcmEvent(
        SafeNativeAudioHandle handle,
        NativeAudioEvent metadata)
    {
        int byteCount = checked((int)metadata.FrameCount * sizeof(short));
        byte[]? buffer = _pool.Rent(byteCount);
        try
        {
            NativeAudioEvent retryEvent = CreateEmptyNativeEvent();
            NativeAudioStatus status = _native.Poll(
                handle,
                ref retryEvent,
                buffer.AsSpan(0, byteCount));
            ThrowIfStatusFailed(status, "read PCM event");
            if (retryEvent.Kind != metadata.Kind
                || retryEvent.FrameCount != metadata.FrameCount
                || retryEvent.Sequence != metadata.Sequence)
            {
                throw new NativeAudioException(
                    AudioEngineStatus.InternalError,
                    "Native audio changed PCM metadata during the capacity retry.");
            }

            AudioDirection direction =
                ParseEnum<NativeAudioEventKind>(metadata.Kind, "PCM event kind") switch
                {
                    NativeAudioEventKind.InboundPcm16 => AudioDirection.Inbound,
                    NativeAudioEventKind.OutboundPcm16 => AudioDirection.Outbound,
                    _ => throw new NativeAudioException(
                        AudioEngineStatus.InternalError,
                        "Native audio returned a non-PCM event during PCM copy."),
                };
            AudioEngineStatus eventStatus =
                MapStatus(ParseEnum<NativeAudioStatus>(metadata.Status, "PCM status"));
            AudioEngineRoute route =
                MapRoute(ParseEnum<NativeAudioRoute>(metadata.Route, "PCM route"));
            PooledPcmLease lease = new(_pool, buffer, byteCount);
            buffer = null;
            return new PollResult(
                AudioEngineEvent.CreatePcm(
                    lease,
                    direction,
                    route,
                    eventStatus,
                    metadata.FrameCount,
                    metadata.Sequence),
                IsNone: false);
        }
        finally
        {
            if (buffer is not null)
            {
                _pool.Return(buffer, clearArray: true);
            }
        }
    }

    private void QueueNoneIfNeeded(Channel<PollResult> channel)
    {
        if (Interlocked.CompareExchange(ref _noneQueued, 1, 0) != 0)
        {
            return;
        }

        if (!channel.Writer.TryWrite(PollResult.None))
        {
            Volatile.Write(ref _noneQueued, 0);
        }
    }

    private async Task StopCoreAsync()
    {
        SafeNativeAudioHandle? handle = _handle;
        if (handle is null)
        {
            return;
        }

        CancellationTokenSource? pollCancellation = _pollCancellation;
        Task? pollTask = _pollTask;
        Channel<PollResult>? channel = _pollResults;
        _handle = null;
        _pollCancellation = null;
        _pollTask = null;
        _pollResults = null;

        if (pollCancellation is not null)
        {
            await pollCancellation.CancelAsync().ConfigureAwait(false);
        }
        if (pollTask is not null)
        {
            await pollTask.ConfigureAwait(false);
        }

        NativeAudioStatus stopStatus;
        try
        {
            stopStatus = _native.Stop(handle);
        }
        finally
        {
            handle.Dispose();
            pollCancellation?.Dispose();
            Drain(channel);
            Volatile.Write(ref _noneQueued, 0);
        }

        if (stopStatus is not NativeAudioStatus.Ok
            and not NativeAudioStatus.NotRunning)
        {
            throw CreateStatusException(stopStatus, "stop");
        }
    }

    private static void Drain(Channel<PollResult>? channel)
    {
        if (channel is null)
        {
            return;
        }

        while (channel.Reader.TryRead(out PollResult? result))
        {
            result?.Event?.Dispose();
        }
    }

    private SafeNativeAudioHandle GetRunningHandle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _handle
            ?? throw new InvalidOperationException("The native audio engine is not running.");
    }

    private static NativeAudioEvent CreateEmptyNativeEvent()
    {
        return new NativeAudioEvent
        {
            Size = checked((uint)Marshal.SizeOf<NativeAudioEvent>()),
            AbiVersion = NativeAudioConstants.AbiVersion,
        };
    }

    private static void ValidatePcm16(ReadOnlyMemory<byte> pcm16)
    {
        if (pcm16.IsEmpty || (pcm16.Length & 1) != 0)
        {
            throw new ArgumentException(
                "PCM16 buffers must contain a non-empty, even number of bytes.",
                nameof(pcm16));
        }
    }

    private static void ThrowIfStatusFailed(
        NativeAudioStatus status,
        string operation)
    {
        if (status != NativeAudioStatus.Ok)
        {
            throw CreateStatusException(status, operation);
        }
    }

    private static NativeAudioException CreateStatusException(
        NativeAudioStatus status,
        string operation)
    {
        return new NativeAudioException(
            MapStatus(status),
            $"Native audio {operation} failed with {status}.");
    }

    private static AudioEngineStatus MapStatus(NativeAudioStatus status)
    {
        return status switch
        {
            NativeAudioStatus.Ok => AudioEngineStatus.Ok,
            NativeAudioStatus.InvalidArgument => AudioEngineStatus.InvalidArgument,
            NativeAudioStatus.AbiMismatch => AudioEngineStatus.AbiMismatch,
            NativeAudioStatus.DeviceMissing => AudioEngineStatus.DeviceMissing,
            NativeAudioStatus.FormatUnsupported => AudioEngineStatus.FormatUnsupported,
            NativeAudioStatus.QueueFull => AudioEngineStatus.QueueFull,
            NativeAudioStatus.NotRunning => AudioEngineStatus.NotRunning,
            NativeAudioStatus.InternalError => AudioEngineStatus.InternalError,
            _ => AudioEngineStatus.InternalError,
        };
    }

    private static AudioEngineRoute MapRoute(NativeAudioRoute route)
    {
        return route switch
        {
            NativeAudioRoute.Stopped => AudioEngineRoute.Stopped,
            NativeAudioRoute.Translated => AudioEngineRoute.Translated,
            NativeAudioRoute.OriginalFailOpen => AudioEngineRoute.OriginalFailOpen,
            NativeAudioRoute.OriginalBypass => AudioEngineRoute.OriginalBypass,
            NativeAudioRoute.MutedFailClosed => AudioEngineRoute.MutedFailClosed,
            _ => throw new NativeAudioException(
                AudioEngineStatus.InternalError,
                $"Native audio returned unsupported route {route}."),
        };
    }

    private static TEnum ParseEnum<TEnum>(uint value, string name)
        where TEnum : struct, Enum
    {
        TEnum parsed = (TEnum)Enum.ToObject(typeof(TEnum), value);
        if (!Enum.IsDefined(parsed))
        {
            throw new NativeAudioException(
                AudioEngineStatus.InternalError,
                $"Native audio returned invalid {name} value {value}.");
        }

        return parsed;
    }

    private sealed record PollResult(AudioEngineEvent? Event, bool IsNone)
    {
        public static PollResult None { get; } = new(null, IsNone: true);
    }

    private sealed class PooledPcmLease : IPcmBufferLease
    {
        private readonly int _length;
        private readonly ArrayPool<byte> _pool;
        private byte[]? _buffer;

        public PooledPcmLease(ArrayPool<byte> pool, byte[] buffer, int length)
        {
            _pool = pool;
            _buffer = buffer;
            _length = length;
        }

        public ReadOnlyMemory<byte> Memory
        {
            get
            {
                byte[] buffer = Volatile.Read(ref _buffer)
                    ?? throw new ObjectDisposedException(nameof(PooledPcmLease));
                return buffer.AsMemory(0, _length);
            }
        }

        public void Dispose()
        {
            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                _pool.Return(buffer, clearArray: true);
            }
        }
    }

    private sealed class SystemNativeAudioPollDelay : INativeAudioPollDelay
    {
        public static SystemNativeAudioPollDelay Instance { get; } = new();

        private SystemNativeAudioPollDelay()
        {
        }

        public async ValueTask DelayAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                cancellationToken).ConfigureAwait(false);
        }
    }
}

#pragma warning restore CA1001
#pragma warning restore CA1031
#pragma warning restore CA2000
#pragma warning restore CA2025
#pragma warning restore CA2213
