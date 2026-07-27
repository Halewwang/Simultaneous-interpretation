using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using EMKE.Core;
using EMKE.Platform.Native;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.
#pragma warning disable CA2000 // Tests intentionally transfer fake handles into the engine.

[TestClass]
public sealed class NativeAudioPollingTests
{
    private static readonly AudioEngineConfiguration ValidConfiguration =
        new(null, null, 24_000, 1);

    [TestMethod]
    public async Task SafeHandleReleasesExactlyOnce()
    {
        FakeNativeAudioApi native = new();
        NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());

        await engine.StartAsync(ValidConfiguration, CancellationToken.None);
        await engine.DisposeAsync();
        await engine.DisposeAsync();

        Assert.AreEqual(1, native.DestroyCount);
    }

    [TestMethod]
    public async Task FailedCreateReleasesUnexpectedNativeHandle()
    {
        FakeNativeAudioApi native = new()
        {
            CreateStatus = NativeAudioStatus.InternalError,
            ReturnHandleOnCreateFailure = true,
        };
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());

        NativeAudioException exception =
            await Assert.ThrowsExactlyAsync<NativeAudioException>(
                () => engine.StartAsync(ValidConfiguration, CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.InternalError, exception.Status);
        Assert.AreEqual(1, native.DestroyCount);
        Assert.AreEqual(0, native.StartCount);
    }

    [TestMethod]
    public async Task ThrownStartFailureReleasesCreatedNativeHandle()
    {
        FakeNativeAudioApi native = new()
        {
            StartException = new InvalidOperationException("synthetic start failure"),
        };
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => engine.StartAsync(ValidConfiguration, CancellationToken.None));

        Assert.AreEqual(1, native.DestroyCount);
    }

    [TestMethod]
    public async Task ConcurrentStopIsIdempotent()
    {
        FakeNativeAudioApi native = new();
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);

        await Task.WhenAll(
            engine.StopAsync(CancellationToken.None),
            engine.StopAsync(CancellationToken.None),
            engine.StopAsync(CancellationToken.None));

        Assert.AreEqual(1, native.StopCount);
        Assert.AreEqual(1, native.DestroyCount);
    }

    [TestMethod]
    public async Task DisposeCancelsAndJoinsTheOnlyPollingTask()
    {
        FakeNativeAudioApi native = new();
        ControlledPollDelay delay = new();
        NativeAudioEngine engine = new(native, ArrayPool<byte>.Shared, delay);
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);
        await delay.WaitForDelayCountAsync(1);

        await engine.DisposeAsync();

        Assert.AreEqual(1, delay.CancellationCount);
        Assert.AreEqual(0, delay.ActiveDelayCount);
        Assert.AreEqual(1, delay.MaximumActiveDelayCount);
        Assert.AreEqual(1, native.PollCount);
        Assert.AreEqual(1, native.StopCount);
        Assert.AreEqual(1, native.DestroyCount);
    }

    [TestMethod]
    public async Task NativeNoneReturnsNullAndPollingWaitsBeforeTryingAgain()
    {
        FakeNativeAudioApi native = new();
        ControlledPollDelay delay = new();
        await using NativeAudioEngine engine = new(native, ArrayPool<byte>.Shared, delay);
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);

        AudioEngineEvent? result = await engine.PollEventAsync(CancellationToken.None);
        await delay.WaitForDelayCountAsync(1);

        Assert.IsNull(result);
        Assert.AreEqual(1, native.PollCount);

        delay.ReleaseOne();
        await delay.WaitForDelayCountAsync(2);
        Assert.AreEqual(2, native.PollCount);
    }

    [TestMethod]
    public async Task PcmMetadataAndBytesAreCopiedBeforePollReturnsToConsumer()
    {
        FakeNativeAudioApi native = new();
        native.EnqueuePcm(
            NativeAudioEventKind.InboundPcm16,
            NativeAudioRoute.OriginalFailOpen,
            sequence: 42,
            [1, 2, 253, 255]);
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);

        using AudioEngineEvent? result =
            await engine.PollEventAsync(CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(AudioEngineEventKind.InboundPcm16, result.Kind);
        Assert.AreEqual(AudioDirection.Inbound, result.Direction);
        Assert.AreEqual(AudioEngineRoute.OriginalFailOpen, result.Route);
        Assert.AreEqual(AudioEngineStatus.Ok, result.Status);
        Assert.AreEqual(2U, result.FrameCount);
        Assert.AreEqual(42UL, result.Sequence);
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 253, 255 },
            result.Pcm16.ToArray());
        Assert.AreEqual(1, native.PcmProbeCount);
        Assert.AreEqual(1, native.PcmCopyCount);
    }

    [TestMethod]
    public async Task FullEventChannelDropsAndDisposesPcmLease()
    {
        FakeNativeAudioApi native = new();
        for (ulong sequence = 1; sequence <= 65; sequence++)
        {
            native.EnqueuePcm(
                NativeAudioEventKind.OutboundPcm16,
                NativeAudioRoute.Translated,
                sequence,
                [1, 0]);
        }

        TrackingBytePool pool = new();
        await using NativeAudioEngine engine =
            new(native, pool, new ControlledPollDelay());
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);

        await WaitUntilAsync(() => engine.DroppedEventCount == 1);

        Assert.AreEqual(1L, engine.DroppedEventCount);
        Assert.AreEqual(65, pool.RentCount);
        Assert.AreEqual(1, pool.ReturnCount);

        await engine.StopAsync(CancellationToken.None);
        Assert.AreEqual(65, pool.ReturnCount);
    }

    [TestMethod]
    public async Task RoutesAndTranslationPcmAreForwardedSynchronously()
    {
        FakeNativeAudioApi native = new();
        await using NativeAudioEngine engine = new(
            native,
            ArrayPool<byte>.Shared,
            new ControlledPollDelay());
        await engine.StartAsync(ValidConfiguration, CancellationToken.None);
        byte[] inbound = [1, 0, 2, 0];
        byte[] outbound = [3, 0, 4, 0];

        await engine.SetInboundRouteAsync(
            InboundRoute.OriginalFailOpen,
            CancellationToken.None);
        await engine.SetOutboundRouteAsync(
            OutboundRoute.MutedFailClosed,
            CancellationToken.None);
        await engine.EnqueueInboundTranslationAsync(inbound, CancellationToken.None);
        await engine.EnqueueOutboundTranslationAsync(outbound, CancellationToken.None);
        Array.Fill(inbound, (byte)0);
        Array.Fill(outbound, (byte)0);

        Assert.AreEqual(NativeAudioRoute.OriginalFailOpen, native.InboundRoute);
        Assert.AreEqual(NativeAudioRoute.MutedFailClosed, native.OutboundRoute);
        CollectionAssert.AreEqual(
            new byte[] { 1, 0, 2, 0 },
            native.InboundTranslation);
        CollectionAssert.AreEqual(
            new byte[] { 3, 0, 4, 0 },
            native.OutboundTranslation);
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

internal sealed class FakeNativeAudioApi : INativeAudioApi
{
    private readonly ConcurrentQueue<FakePollEvent> _events = new();
    private long _nextHandle;

    public uint AbiVersion { get; set; } = NativeAudioConstants.AbiVersion;

    public uint ConfigurationSize { get; set; } =
        checked((uint)Marshal.SizeOf<NativeAudioConfiguration>());

    public uint EventSize { get; set; } =
        checked((uint)Marshal.SizeOf<NativeAudioEvent>());

    public uint DiagnosticsSize { get; set; } =
        checked((uint)Marshal.SizeOf<NativeAudioDiagnostics>());

    public uint DiscoveredEndpointSize { get; set; } =
        checked((uint)Marshal.SizeOf<NativeAudioDiscoveredEndpoint>());

    public uint EndpointSnapshotSize { get; set; } =
        checked((uint)Marshal.SizeOf<NativeAudioEndpointSnapshot>());

    public NativeAudioEndpointDiscoveryStatus DiscoveryStatus { get; set; } =
        NativeAudioEndpointDiscoveryStatus.Ready;

    public NativeAudioStatus CreateStatus { get; set; } = NativeAudioStatus.Ok;

    public Exception? StartException { get; set; }

    public bool ReturnHandleOnCreateFailure { get; set; }

    public int CreateCount { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DestroyCount { get; private set; }

    public int PollCount { get; private set; }

    public int PcmProbeCount { get; private set; }

    public int PcmCopyCount { get; private set; }

    public NativeAudioConfiguration LastConfiguration { get; private set; }

    public NativeAudioRoute InboundRoute { get; private set; }

    public NativeAudioRoute OutboundRoute { get; private set; }

    public byte[]? InboundTranslation { get; private set; }

    public byte[]? OutboundTranslation { get; private set; }

    public uint GetAbiVersion() => AbiVersion;

    public uint GetConfigurationSize() => ConfigurationSize;

    public uint GetEventSize() => EventSize;

    public uint GetDiagnosticsSize() => DiagnosticsSize;

    public uint GetDiscoveredEndpointSize() => DiscoveredEndpointSize;

    public uint GetEndpointSnapshotSize() => EndpointSnapshotSize;

    public NativeAudioStatus DiscoverEndpoints(ref NativeAudioEndpointSnapshot snapshot)
    {
        snapshot = NativeAudioTestData.CreateSnapshot(DiscoveryStatus);
        return NativeAudioStatus.Ok;
    }

    public NativeAudioStatus Create(
        in NativeAudioConfiguration configuration,
        out SafeNativeAudioHandle? handle)
    {
        CreateCount++;
        LastConfiguration = configuration;
        if (CreateStatus == NativeAudioStatus.Ok || ReturnHandleOnCreateFailure)
        {
            handle = new SafeNativeAudioHandle(this, new nint(++_nextHandle));
        }
        else
        {
            handle = null;
        }

        return CreateStatus;
    }

    public void Destroy(nint handle)
    {
        Assert.AreNotEqual(nint.Zero, handle);
        DestroyCount++;
    }

    public NativeAudioStatus Start(SafeNativeAudioHandle handle)
    {
        StartCount++;
        if (StartException is not null)
        {
            throw StartException;
        }

        return NativeAudioStatus.Ok;
    }

    public NativeAudioStatus Stop(SafeNativeAudioHandle handle)
    {
        StopCount++;
        return NativeAudioStatus.Ok;
    }

    public NativeAudioStatus SetInboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route)
    {
        InboundRoute = route;
        return NativeAudioStatus.Ok;
    }

    public NativeAudioStatus SetOutboundRoute(
        SafeNativeAudioHandle handle,
        NativeAudioRoute route)
    {
        OutboundRoute = route;
        return NativeAudioStatus.Ok;
    }

    public NativeAudioStatus EnqueueInboundTranslation(
        SafeNativeAudioHandle handle,
        ReadOnlySpan<byte> pcm16)
    {
        InboundTranslation = pcm16.ToArray();
        return NativeAudioStatus.Ok;
    }

    public NativeAudioStatus EnqueueOutboundTranslation(
        SafeNativeAudioHandle handle,
        ReadOnlySpan<byte> pcm16)
    {
        OutboundTranslation = pcm16.ToArray();
        return NativeAudioStatus.Ok;
    }

    public NativeAudioStatus Poll(
        SafeNativeAudioHandle handle,
        ref NativeAudioEvent nativeEvent,
        Span<byte> pcm16)
    {
        PollCount++;
        if (!_events.TryPeek(out FakePollEvent? next) || next is null)
        {
            nativeEvent.Kind = (uint)NativeAudioEventKind.None;
            nativeEvent.Status = (uint)NativeAudioStatus.Ok;
            nativeEvent.Route = (uint)NativeAudioRoute.Stopped;
            nativeEvent.FrameCount = 0;
            nativeEvent.Sequence = 0;
            return NativeAudioStatus.Ok;
        }

        nativeEvent.Kind = (uint)next.Kind;
        nativeEvent.Status = (uint)NativeAudioStatus.Ok;
        nativeEvent.Route = (uint)next.Route;
        nativeEvent.FrameCount = checked((uint)(next.Pcm16.Length / sizeof(short)));
        nativeEvent.Sequence = next.Sequence;
        if (pcm16.Length < next.Pcm16.Length)
        {
            PcmProbeCount++;
            return NativeAudioStatus.InvalidArgument;
        }

        PcmCopyCount++;
        next.Pcm16.CopyTo(pcm16);
        Assert.IsTrue(_events.TryDequeue(out _));
        return NativeAudioStatus.Ok;
    }

    public void EnqueuePcm(
        NativeAudioEventKind kind,
        NativeAudioRoute route,
        ulong sequence,
        byte[] pcm16)
    {
        _events.Enqueue(new FakePollEvent(kind, route, sequence, pcm16.ToArray()));
    }

    private sealed record FakePollEvent(
        NativeAudioEventKind Kind,
        NativeAudioRoute Route,
        ulong Sequence,
        byte[] Pcm16);
}

internal sealed class ControlledPollDelay : INativeAudioPollDelay
{
    private readonly Channel<bool> _releases = Channel.CreateUnbounded<bool>();
    private readonly Channel<int> _delayCounts = Channel.CreateUnbounded<int>();
    private int _activeDelayCount;
    private int _delayCount;
    private int _maximumActiveDelayCount;

    public int ActiveDelayCount => Volatile.Read(ref _activeDelayCount);

    public int CancellationCount { get; private set; }

    public int MaximumActiveDelayCount => Volatile.Read(ref _maximumActiveDelayCount);

    public async ValueTask DelayAsync(CancellationToken cancellationToken)
    {
        int active = Interlocked.Increment(ref _activeDelayCount);
        InterlockedExtensions.Max(ref _maximumActiveDelayCount, active);
        int delayCount = Interlocked.Increment(ref _delayCount);
        Assert.IsTrue(_delayCounts.Writer.TryWrite(delayCount));
        try
        {
            await _releases.Reader.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancellationCount++;
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeDelayCount);
        }
    }

    public void ReleaseOne()
    {
        Assert.IsTrue(_releases.Writer.TryWrite(true));
    }

    public async Task WaitForDelayCountAsync(int expected)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref _delayCount) < expected)
        {
            await _delayCounts.Reader.ReadAsync(timeout.Token);
        }
    }
}

internal sealed class TrackingBytePool : ArrayPool<byte>
{
    public int RentCount { get; private set; }

    public int ReturnCount { get; private set; }

    public override byte[] Rent(int minimumLength)
    {
        RentCount++;
        return new byte[minimumLength];
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        ReturnCount++;
        if (clearArray)
        {
            Array.Clear(array);
        }
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref int location, int value)
    {
        int current = Volatile.Read(ref location);
        while (current < value)
        {
            int observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}

internal static class NativeAudioTestData
{
    public static unsafe NativeAudioEndpointSnapshot CreateSnapshot(
        NativeAudioEndpointDiscoveryStatus discoveryStatus)
    {
        NativeAudioEndpointSnapshot snapshot = default;
        snapshot.Size = checked((uint)Marshal.SizeOf<NativeAudioEndpointSnapshot>());
        snapshot.AbiVersion = NativeAudioConstants.AbiVersion;
        snapshot.DiscoveryStatus = (uint)discoveryStatus;
        snapshot.VirtualEndpoint0 = CreateEndpoint(
            NativeAudioEndpointRole.MeetingSpeakerRender,
            NativeAudioEndpointDataFlow.Render,
            "virtual-speaker-render");
        snapshot.VirtualEndpoint1 = CreateEndpoint(
            NativeAudioEndpointRole.AppSpeakerCapture,
            NativeAudioEndpointDataFlow.Capture,
            "virtual-speaker-capture");
        snapshot.VirtualEndpoint2 = CreateEndpoint(
            NativeAudioEndpointRole.AppMicrophoneRender,
            NativeAudioEndpointDataFlow.Render,
            "virtual-microphone-render");
        snapshot.VirtualEndpoint3 = CreateEndpoint(
            NativeAudioEndpointRole.MeetingMicrophoneCapture,
            NativeAudioEndpointDataFlow.Capture,
            "virtual-microphone-capture");
        ushort* input = snapshot.PhysicalInputEndpointId;
        ushort* output = snapshot.PhysicalOutputEndpointId;
        snapshot.PhysicalInputEndpointIdLength =
            WriteEndpointId(input, "default-input");
        snapshot.PhysicalOutputEndpointIdLength =
            WriteEndpointId(output, "default-output");

        return snapshot;
    }

    private static unsafe NativeAudioDiscoveredEndpoint CreateEndpoint(
        NativeAudioEndpointRole role,
        NativeAudioEndpointDataFlow dataFlow,
        string id)
    {
        NativeAudioDiscoveredEndpoint endpoint = default;
        endpoint.Size = checked((uint)Marshal.SizeOf<NativeAudioDiscoveredEndpoint>());
        endpoint.AbiVersion = NativeAudioConstants.AbiVersion;
        endpoint.Role = (uint)role;
        endpoint.DataFlow = (uint)dataFlow;
        endpoint.State = 1;
        ushort* destination = endpoint.EndpointId;
        endpoint.EndpointIdLength = WriteEndpointId(destination, id);

        return endpoint;
    }

    private static unsafe uint WriteEndpointId(ushort* destination, string id)
    {
        Assert.IsLessThan(NativeAudioConstants.EndpointIdCapacity, id.Length);
        for (int index = 0; index < id.Length; index++)
        {
            destination[index] = id[index];
        }

        destination[id.Length] = 0;
        return checked((uint)id.Length);
    }
}

#pragma warning restore CA2000
#pragma warning restore CA2007
#pragma warning restore CA1515
