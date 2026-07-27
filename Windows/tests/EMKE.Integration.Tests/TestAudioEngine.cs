using System.Collections.Concurrent;
using System.Threading.Channels;
using EMKE.Core;

namespace EMKE.Integration.Tests;

internal sealed class TestAudioEngine : ITranslationAudioEngine
{
    private readonly Channel<AudioEngineEvent> _events =
        Channel.CreateUnbounded<AudioEngineEvent>();
    private readonly ConcurrentQueue<byte[]> _inboundTranslations = new();
    private readonly ConcurrentQueue<byte[]> _outboundTranslations = new();
    private readonly ConcurrentQueue<byte[]> _virtualMicrophone = new();
    private long _sequence;
    private int _outboundRoute = (int)OutboundRoute.Stopped;
    private readonly TaskCompletionSource _failClosedOrStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<byte[]> InboundTranslations =>
        _inboundTranslations.ToArray();

    public IReadOnlyList<byte[]> OutboundTranslations =>
        _outboundTranslations.ToArray();

    public OutboundRoute CurrentOutboundRoute =>
        (OutboundRoute)Volatile.Read(ref _outboundRoute);

    public Exception? OutboundEnqueueException { get; set; }

    public byte[] VirtualMicrophoneOutput =>
        _virtualMicrophone.SelectMany(static chunk => chunk).ToArray();

    public Task FailClosedOrStopped => _failClosedOrStopped.Task;

    public void EmitCaptured(
        AudioDirection direction,
        ReadOnlyMemory<byte> pcm16)
    {
#pragma warning disable CA2000 // Ownership transfers into AudioEngineEvent.
        TestPcmLease lease = new(pcm16);
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
                "The test audio event queue is closed.");
        }
    }

    public void EmitControl(AudioEngineEvent audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!_events.Writer.TryWrite(audio))
        {
            audio.Dispose();
            throw new InvalidOperationException(
                "The test audio event queue is closed.");
        }
    }

    public void ClearVirtualMicrophone()
    {
        _virtualMicrophone.Clear();
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
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _outboundRoute, (int)OutboundRoute.Stopped);
        _failClosedOrStopped.TrySetResult();
        return Task.CompletedTask;
    }

    public async ValueTask<AudioEngineEvent?> PollEventAsync(
        CancellationToken cancellationToken)
    {
        return await _events.Reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false);
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
        if (OutboundEnqueueException is not null)
        {
            throw OutboundEnqueueException;
        }

        _outboundTranslations.Enqueue(pcm16.ToArray());
        if (CurrentOutboundRoute == OutboundRoute.Translated)
        {
            _virtualMicrophone.Enqueue(pcm16.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SetInboundRouteAsync(
        InboundRoute route,
        CancellationToken cancellationToken)
    {
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

        public TestPcmLease(ReadOnlyMemory<byte> pcm16)
        {
            _pcm16 = pcm16.ToArray();
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
            }
        }
    }
}
