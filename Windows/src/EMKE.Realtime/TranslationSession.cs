using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using EMKE.Core;

namespace EMKE.Realtime;

public enum TranslationSessionState
{
    Disconnected,
    Connecting,
    Created,
    Updating,
    Connected,
    Closing,
    Closed,
    Failed,
}

#pragma warning disable CA1032 // Domain exceptions require a stable RuntimeError payload.

public sealed class TranslationSessionException : Exception
{
    public TranslationSessionException(RuntimeError error)
        : base(error?.Code)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RuntimeError Error { get; }
}

#pragma warning restore CA1032

public static class TranslationSessionCreationPolicy
{
    public static bool RequiresOutboundSession(
        LanguageCode nativeLanguage,
        LanguageCode meetingLanguage)
    {
        if (!Enum.IsDefined(nativeLanguage))
        {
            throw new ArgumentOutOfRangeException(nameof(nativeLanguage));
        }

        if (!Enum.IsDefined(meetingLanguage))
        {
            throw new ArgumentOutOfRangeException(nameof(meetingLanguage));
        }

        return nativeLanguage != meetingLanguage;
    }
}

public sealed class TranslationSession : ITranslationSession, IDisposable
{
    private const int DefaultEventCapacity = 32;

    private readonly object _sync = new();
    private readonly ITranslationTransport _transport;
    private readonly Uri _endpoint;
    private readonly TranslationSessionConfiguration _configuration;
    private readonly IClock _clock;
    private readonly ArrayPool<byte> _pool;
    private readonly Channel<TranslationSessionEvent> _events;
    private readonly PcmFrameBatcher _batcher = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _handshake =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _remoteClosed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SessionCloseCoordinator _closeCoordinator;
    private TranslationSessionState _state;
    private RuntimeError? _lastError;
    private Task? _connectTask;
    private Task? _receiveLoop;
    private Task? _closeTask;
    private Task<int>? _discardTask;
    private int _released;

    public TranslationSession(Uri endpoint, TranslationSessionConfiguration configuration)
        : this(
            new TranslationSocket(),
            endpoint,
            configuration,
            new SystemClock(),
            DefaultEventCapacity,
            ArrayPool<byte>.Shared)
    {
    }

    internal TranslationSession(
        ITranslationTransport transport,
        Uri endpoint,
        TranslationSessionConfiguration configuration,
        IClock clock,
        int eventCapacity,
        ArrayPool<byte> pool)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _configuration =
            configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventCapacity);

        _events = Channel.CreateBounded<TranslationSessionEvent>(
            new BoundedChannelOptions(eventCapacity)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        _closeCoordinator = new SessionCloseCoordinator(_clock, _ => ReleaseResources());
        _state = TranslationSessionState.Disconnected;
    }

    public TranslationSessionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public RuntimeError? LastError
    {
        get
        {
            lock (_sync)
            {
                return _lastError;
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        Task connect;
        lock (_sync)
        {
            if (_connectTask is null)
            {
                if (_state != TranslationSessionState.Disconnected)
                {
                    return Task.FromException(
                        new TranslationSessionException(Error(
                            ErrorCategory.Protocol,
                            "translationSession.invalidConnectState")));
                }

                _state = TranslationSessionState.Connecting;
                _closeCoordinator.Activate(1);
                _connectTask = ConnectCoreAsync();
            }

            connect = _connectTask;
        }

        return cancellationToken.CanBeCanceled
            ? connect.WaitAsync(cancellationToken)
            : connect;
    }

    public async ValueTask SendPcmAsync(
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken)
    {
        ThrowIfNotConnected();
        await _batcher.AppendAsync(
            pcm,
            SendFrameAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TranslationSessionEvent> ReceiveAsync(
        CancellationToken cancellationToken)
    {
        return _events.Reader.ReadAllAsync(cancellationToken);
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Task close;
        lock (_sync)
        {
            if (_closeTask is null)
            {
                if (_state is TranslationSessionState.Closed)
                {
                    _closeTask = Task.CompletedTask;
                }
                else if (_state is TranslationSessionState.Failed)
                {
                    _closeTask = Task.FromException(
                        new TranslationSessionException(_lastError!));
                }
                else
                {
                    _state = TranslationSessionState.Closing;
                    _closeTask = CloseCoreAsync();
                }
            }

            close = _closeTask;
        }

        return cancellationToken.CanBeCanceled
            ? close.WaitAsync(cancellationToken)
            : close;
    }

    public void Dispose()
    {
        ReleaseResources();
        _events.Writer.TryComplete();
        _handshake.TrySetException(
            new TranslationSessionException(Error(
                ErrorCategory.Network,
                "translationSession.disposed")));
        _remoteClosed.TrySetResult();
    }

    internal void CompleteEventChannelForTest()
    {
        _events.Writer.TryComplete();
    }

    private async Task ConnectCoreAsync()
    {
        RuntimeError? connectError =
            await _transport.ConnectAsync(_endpoint, _lifetime.Token).ConfigureAwait(false);
        if (connectError is not null)
        {
            Fail(connectError);
            throw new TranslationSessionException(connectError);
        }

        _receiveLoop = ReceiveLoopAsync();
        await _handshake.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TranslationReceiveResult received;
                try
                {
                    received = await _transport.ReceiveEventAsync(
                        _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (ChannelClosedException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException
                        or InvalidOperationException
                        or ChannelClosedException)
                {
                    Fail(Error(
                        ErrorCategory.Network,
                        "translationSocket.receiveFailed"));
                    return;
                }

                if (received.Status == TranslationReceiveStatus.Failed)
                {
                    Fail(received.Error!);
                    return;
                }

                if (received.Status == TranslationReceiveStatus.Closed)
                {
                    if (State == TranslationSessionState.Closing)
                    {
                        CompleteRemoteClose();
                    }
                    else
                    {
                        Fail(Error(
                            ErrorCategory.Protocol,
                            "translationSession.unexpectedSocketClose"));
                    }

                    return;
                }

                if (!await HandleEventAsync(received.Event!).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        finally
        {
            if (State == TranslationSessionState.Failed)
            {
                ReleaseResources();
            }
        }
    }

    private async ValueTask<bool> HandleEventAsync(TranslationProtocolEvent protocolEvent)
    {
        switch (protocolEvent.Type)
        {
            case "session.created":
                if (!TryTransition(
                        TranslationSessionState.Connecting,
                        TranslationSessionState.Created))
                {
                    return FailUnexpectedEvent();
                }

                RuntimeError? updateError = await _transport.SendSessionUpdateAsync(
                    _configuration.TargetLanguage,
                    _lifetime.Token).ConfigureAwait(false);
                if (updateError is not null)
                {
                    Fail(updateError);
                    return false;
                }

                if (!TryTransition(
                        TranslationSessionState.Created,
                        TranslationSessionState.Updating))
                {
                    return FailUnexpectedEvent();
                }

                return true;

            case "session.updated":
                if (!TryTransition(
                        TranslationSessionState.Updating,
                        TranslationSessionState.Connected))
                {
                    return FailUnexpectedEvent();
                }

                _handshake.TrySetResult();
                return true;

            case "input_audio_transcription.delta":
                if (State is not (
                        TranslationSessionState.Connected
                        or TranslationSessionState.Closing)
                    || protocolEvent.Delta is null)
                {
                    return FailUnexpectedEvent();
                }

                return await PublishAsync(
                    new TranslationSessionEvent.SourceCaption(
                        protocolEvent.Delta,
                        null,
                        isFinal: false)).ConfigureAwait(false);

            case "input_audio_transcription.done":
                return State is (
                        TranslationSessionState.Connected
                        or TranslationSessionState.Closing)
                    || FailUnexpectedEvent();

            case "translation_audio.delta":
                if (State is not (
                    TranslationSessionState.Connected
                    or TranslationSessionState.Closing))
                {
                    return FailUnexpectedEvent();
                }

                return await PublishAudioAsync(protocolEvent.Pcm16).ConfigureAwait(false);

            case "translation_audio.done":
                if (State is not (
                    TranslationSessionState.Connected
                    or TranslationSessionState.Closing))
                {
                    return FailUnexpectedEvent();
                }

                return await PublishAsync(
                    new TranslationSessionEvent.Completed()).ConfigureAwait(false);

            case "error":
                Fail(Error(ErrorCategory.Protocol, "translationSession.remoteError"));
                return false;

            case "session.closed":
                if (State != TranslationSessionState.Closing)
                {
                    return FailUnexpectedEvent();
                }

                CompleteRemoteClose();
                return false;

            default:
                return FailUnexpectedEvent();
        }
    }

    private async ValueTask<bool> PublishAudioAsync(ReadOnlyMemory<byte> pcm16)
    {
        if (pcm16.IsEmpty || (pcm16.Length & 1) != 0)
        {
            Fail(Error(
                ErrorCategory.Protocol,
                "translationEvent.invalidPcm16"));
            return false;
        }

        PooledPcmBufferLease lease = new(_pool, pcm16);
        TranslationSessionEvent.AudioDelta? audio = null;
        try
        {
            audio = new TranslationSessionEvent.AudioDelta(lease);
            lease = null!;
            if (!await PublishAsync(audio).ConfigureAwait(false))
            {
                audio.Dispose();
                return false;
            }

            audio = null;
            return true;
        }
        finally
        {
            audio?.Dispose();
            lease?.Dispose();
        }
    }

    private async ValueTask<bool> PublishAsync(TranslationSessionEvent sessionEvent)
    {
        try
        {
            await _events.Writer.WriteAsync(sessionEvent, _lifetime.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            Fail(Error(
                ErrorCategory.Backpressure,
                "translationSession.eventChannelClosed"));
            return false;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
    }

    private async ValueTask SendFrameAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken)
    {
        RuntimeError? sendError =
            await _transport.SendAudioAppendAsync(frame, cancellationToken)
                .ConfigureAwait(false);
        if (sendError is not null)
        {
            Fail(sendError);
            throw new TranslationSessionException(sendError);
        }
    }

    private async Task CloseCoreAsync()
    {
        Task<SessionCloseOutcome> close = _closeCoordinator.CloseAsync(
            1,
            _transport.SendSessionCloseAsync,
            _remoteClosed.Task);
        _discardTask = _batcher.DiscardAsync(CancellationToken.None).AsTask();
        SessionCloseOutcome outcome = await close.ConfigureAwait(false);

        if (outcome.Completion == SessionCloseCompletion.Closed)
        {
            RuntimeError? concurrentFailure;
            lock (_sync)
            {
                concurrentFailure = _state == TranslationSessionState.Failed
                    ? _lastError
                    : null;
                if (_state == TranslationSessionState.Closing)
                {
                    _state = TranslationSessionState.Closed;
                }
            }

            if (concurrentFailure is not null)
            {
                throw new TranslationSessionException(concurrentFailure);
            }

            return;
        }

        RuntimeError error = outcome.Error ?? Error(
            ErrorCategory.CloseTimeout,
            "translationSession.closeTimeout");
        Fail(error);
        throw new TranslationSessionException(error);
    }

    private void CompleteRemoteClose()
    {
        _events.Writer.TryComplete();
        _remoteClosed.TrySetResult();
    }

    private bool FailUnexpectedEvent()
    {
        Fail(Error(
            ErrorCategory.Protocol,
            "translationSession.unexpectedEventOrder"));
        return false;
    }

    private void Fail(RuntimeError error)
    {
        lock (_sync)
        {
            if (_state is TranslationSessionState.Closed
                or TranslationSessionState.Failed)
            {
                return;
            }

            _lastError = error;
            _state = TranslationSessionState.Failed;
        }

        _handshake.TrySetException(new TranslationSessionException(error));
        _events.Writer.TryComplete();
        _remoteClosed.TrySetResult();
        if (Volatile.Read(ref _released) == 0)
        {
            _lifetime.Cancel();
        }
    }

    private void ThrowIfNotConnected()
    {
        RuntimeError error;
        lock (_sync)
        {
            if (_state == TranslationSessionState.Connected)
            {
                return;
            }

            error = _lastError ?? Error(
                ErrorCategory.Protocol,
                "translationSession.notConnected");
        }

        throw new TranslationSessionException(error);
    }

    private bool TryTransition(
        TranslationSessionState expected,
        TranslationSessionState next)
    {
        lock (_sync)
        {
            if (_state != expected)
            {
                return false;
            }

            _state = next;
            return true;
        }
    }

    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _transport.Dispose();
        _lifetime.Dispose();
    }

    private static RuntimeError Error(ErrorCategory category, string code)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.Retry);
    }

    private sealed class PooledPcmBufferLease : IPcmBufferLease
    {
        private readonly ArrayPool<byte> _pool;
        private readonly int _length;
        private byte[]? _buffer;

        public PooledPcmBufferLease(ArrayPool<byte> pool, ReadOnlyMemory<byte> source)
        {
            _pool = pool;
            _length = source.Length;
            _buffer = pool.Rent(source.Length);
            source.Span.CopyTo(_buffer);
        }

        public ReadOnlyMemory<byte> Memory
        {
            get
            {
                byte[] buffer = Volatile.Read(ref _buffer)
                    ?? throw new ObjectDisposedException(nameof(PooledPcmBufferLease));
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

    private sealed class SystemClock : IClock
    {
        private static readonly double TickToSeconds = 1.0 / Stopwatch.Frequency;

        public TimeSpan MonotonicNow =>
            TimeSpan.FromSeconds(Stopwatch.GetTimestamp() * TickToSeconds);

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
