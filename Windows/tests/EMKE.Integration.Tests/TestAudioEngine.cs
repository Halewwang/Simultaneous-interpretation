using System.Collections.Concurrent;
using System.Threading.Channels;
using EMKE.Application;
using EMKE.Core;

namespace EMKE.Integration.Tests;

internal sealed class TestAudioEngine : ITranslationAudioEngine
{
    private const int EventCapacity = 8;
    private const int OutboundTranslationCapacity = 1;
    private readonly Channel<AudioEngineEvent> _events =
        Channel.CreateBounded<AudioEngineEvent>(
            new BoundedChannelOptions(EventCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    private readonly Channel<byte[]> _outboundTranslationQueue =
        Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(OutboundTranslationCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    private readonly ConcurrentQueue<byte[]> _inboundTranslations = new();
    private readonly ConcurrentQueue<byte[]> _outboundTranslations = new();
    private readonly ConcurrentQueue<byte[]> _virtualMicrophone = new();
    private readonly ConcurrentQueue<byte[]> _meetingSpeaker = new();
    private long _sequence;
    private int _inboundRoute = (int)InboundRoute.Stopped;
    private int _outboundRoute = (int)OutboundRoute.Stopped;
    private int _startCount;
    private int _stopCount;
    private int _activePcmLeaseCount;
    private int _pendingEventCount;
    private int _pendingOutboundTranslationCount;
    private int _activePollCount;
    private readonly TaskCompletionSource _failClosedOrStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _virtualMicrophoneTranslated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<byte[]> InboundTranslations =>
        _inboundTranslations.ToArray();

    public IReadOnlyList<byte[]> OutboundTranslations =>
        _outboundTranslations.ToArray();

    public OutboundRoute CurrentOutboundRoute =>
        (OutboundRoute)Volatile.Read(ref _outboundRoute);

    public InboundRoute CurrentInboundRoute =>
        (InboundRoute)Volatile.Read(ref _inboundRoute);

    public int StartCount => Volatile.Read(ref _startCount);

    public int StopCount => Volatile.Read(ref _stopCount);

    public int ActivePcmLeaseCount => Volatile.Read(ref _activePcmLeaseCount);

    public int PendingEventCount => Volatile.Read(ref _pendingEventCount);

    public int PendingOutboundTranslationCount =>
        Volatile.Read(ref _pendingOutboundTranslationCount);

    public int ActivePollCount => Volatile.Read(ref _activePollCount);

    public byte[] VirtualMicrophoneOutput =>
        _virtualMicrophone.SelectMany(static chunk => chunk).ToArray();

    public byte[] MeetingSpeakerOutput =>
        _meetingSpeaker.SelectMany(static chunk => chunk).ToArray();

    public Task FailClosedOrStopped => _failClosedOrStopped.Task;

    public Task VirtualMicrophoneTranslated => _virtualMicrophoneTranslated.Task;

    public void EmitCaptured(
        AudioDirection direction,
        ReadOnlyMemory<byte> pcm16)
    {
#pragma warning disable CA2000 // Ownership transfers into AudioEngineEvent.
        Interlocked.Increment(ref _activePcmLeaseCount);
        TestPcmLease lease = new(
            pcm16,
            () => Interlocked.Decrement(ref _activePcmLeaseCount));
        AudioEngineEvent audio = AudioEngineEvent.CreatePcm(
            lease,
            direction,
            AudioEngineRoute.Translated,
            AudioEngineStatus.Ok,
            (uint)(pcm16.Length / sizeof(short)),
            (ulong)Interlocked.Increment(ref _sequence));
#pragma warning restore CA2000
        if (!_events.Writer.TryWrite(audio))
        {
            audio.Dispose();
            throw new InvalidOperationException(
                "The test audio event queue is full.");
        }

        Interlocked.Increment(ref _pendingEventCount);
    }

    public void EmitControl(AudioEngineEvent audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!_events.Writer.TryWrite(audio))
        {
            audio.Dispose();
            throw new InvalidOperationException(
                "The test audio event queue is full.");
        }

        Interlocked.Increment(ref _pendingEventCount);
    }

    public void ClearVirtualMicrophone()
    {
        _virtualMicrophone.Clear();
    }

    public void ClearMeetingSpeaker()
    {
        _meetingSpeaker.Clear();
    }

    public void RenderMeetingSpeaker(ReadOnlyMemory<byte> originalPcm16)
    {
        byte[] rendered = CurrentInboundRoute is InboundRoute.OriginalFailOpen
            or InboundRoute.OriginalBypass
            ? originalPcm16.ToArray()
            : new byte[originalPcm16.Length];
        _meetingSpeaker.Enqueue(rendered);
    }

    public void RenderVirtualMicrophone(ReadOnlyMemory<byte> physicalPcm16)
    {
        byte[] rendered = CurrentOutboundRoute == OutboundRoute.OriginalBypass
            ? physicalPcm16.ToArray()
            : new byte[physicalPcm16.Length];
        _virtualMicrophone.Enqueue(rendered);
    }

    public Task StartAsync(
        AudioEngineConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCount);
        Volatile.Write(ref _inboundRoute, (int)InboundRoute.Stopped);
        Volatile.Write(ref _outboundRoute, (int)OutboundRoute.Stopped);
        while (_events.Reader.TryRead(out AudioEngineEvent? pendingEvent))
        {
            Interlocked.Decrement(ref _pendingEventCount);
            pendingEvent.Dispose();
        }

        while (_outboundTranslationQueue.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _pendingOutboundTranslationCount);
        }

        _failClosedOrStopped.TrySetResult();
        return Task.CompletedTask;
    }

    public async ValueTask<AudioEngineEvent?> PollEventAsync(
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activePollCount);
        try
        {
            AudioEngineEvent audio = await _events.Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Decrement(ref _pendingEventCount);
            return audio;
        }
        finally
        {
            Interlocked.Decrement(ref _activePollCount);
        }
    }

    public ValueTask EnqueueInboundTranslationAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken)
    {
        _inboundTranslations.Enqueue(pcm16.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask EnqueueOutboundTranslationAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken)
    {
        byte[] copy = pcm16.ToArray();
        if (!_outboundTranslationQueue.Writer.TryWrite(copy))
        {
            throw new RuntimeOperationException(new RuntimeError(
                ErrorCategory.Backpressure,
                "testAudioEngine.outboundQueueFull",
                new Dictionary<string, string>(),
                RecoveryAction.Retry));
        }

        Interlocked.Increment(ref _pendingOutboundTranslationCount);

        _outboundTranslations.Enqueue(copy);
        if (CurrentOutboundRoute == OutboundRoute.Translated)
        {
            _virtualMicrophone.Enqueue(copy);
            _virtualMicrophoneTranslated.TrySetResult();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SetInboundRouteAsync(
        InboundRoute route,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _inboundRoute, (int)route);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetOutboundRouteAsync(
        OutboundRoute route,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _outboundRoute, (int)route);
        if (route is OutboundRoute.MutedFailClosed or OutboundRoute.Stopped)
        {
            _failClosedOrStopped.TrySetResult();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class TestPcmLease : IPcmBufferLease
    {
        private byte[]? _pcm16;
        private readonly Action _onDisposed;

        public TestPcmLease(ReadOnlyMemory<byte> pcm16, Action onDisposed)
        {
            _pcm16 = pcm16.ToArray();
            _onDisposed = onDisposed ?? throw new ArgumentNullException(nameof(onDisposed));
        }

        public ReadOnlyMemory<byte> Memory =>
            _pcm16
            ?? throw new ObjectDisposedException(nameof(TestPcmLease));

        public void Dispose()
        {
            byte[]? pcm16 = Interlocked.Exchange(ref _pcm16, null);
            if (pcm16 is not null)
            {
                Array.Clear(pcm16);
                _onDisposed();
            }
        }
    }
}
