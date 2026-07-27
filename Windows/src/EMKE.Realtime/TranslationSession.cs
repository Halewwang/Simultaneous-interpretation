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
    public static TranslationSessionCreationPlan CreatePlan(
        LanguageCode nativeLanguage,
        LanguageCode meetingLanguage,
        bool requestOutbound)
    {
        bool requiresOutbound = RequiresOutboundSession(
            nativeLanguage,
            meetingLanguage);
        return new TranslationSessionCreationPlan(
            InboundSocketCount: 1,
            OutboundSocketCount: requestOutbound && requiresOutbound ? 1 : 0,
            Inbound: new TranslationInboundChannelPlan(
                ChannelState.Connected,
                InboundRoute.Translated),
            Outbound: requestOutbound
                ? requiresOutbound
                    ? new TranslationOutboundChannelPlan(
                        ChannelState.Connected,
                        OutboundRoute.Translated)
                    : new TranslationOutboundChannelPlan(
                        ChannelState.Bypassed,
                        OutboundRoute.OriginalBypass)
                : new TranslationOutboundChannelPlan(
                    ChannelState.Inactive,
                    OutboundRoute.Stopped));
    }

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

public sealed record TranslationSessionCreationPlan(
    int InboundSocketCount,
    int OutboundSocketCount,
    TranslationInboundChannelPlan Inbound,
    TranslationOutboundChannelPlan Outbound)
{
    public bool OutboundBypassed =>
        Outbound.ChannelState == ChannelState.Bypassed;
}

public sealed record TranslationInboundChannelPlan(
    ChannelState ChannelState,
    InboundRoute Route);

public sealed record TranslationOutboundChannelPlan(
    ChannelState ChannelState,
    OutboundRoute Route);

public static class TranslationSessionTopologyPolicy
{
    public static TranslationInboundChannelPlan ResolveInbound(
        TranslationSessionState state)
    {
        return state switch
        {
            TranslationSessionState.Disconnected => new(
                ChannelState.Inactive,
                InboundRoute.Stopped),
            TranslationSessionState.Connecting
                or TranslationSessionState.Created
                or TranslationSessionState.Updating => new(
                    ChannelState.Connecting,
                    InboundRoute.Stopped),
            TranslationSessionState.Connected
                or TranslationSessionState.Closing
                or TranslationSessionState.Closed => new(
                    ChannelState.Connected,
                    InboundRoute.Translated),
            TranslationSessionState.Failed => new(
                ChannelState.Failed,
                InboundRoute.OriginalFailOpen),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }

    public static TranslationOutboundChannelPlan ResolveOutbound(
        TranslationSessionState state)
    {
        return state switch
        {
            TranslationSessionState.Disconnected => new(
                ChannelState.Inactive,
                OutboundRoute.Stopped),
            TranslationSessionState.Connecting
                or TranslationSessionState.Created
                or TranslationSessionState.Updating => new(
                    ChannelState.Connecting,
                    OutboundRoute.Stopped),
            TranslationSessionState.Connected
                or TranslationSessionState.Closing
                or TranslationSessionState.Closed => new(
                    ChannelState.Connected,
                    OutboundRoute.Translated),
            TranslationSessionState.Failed => new(
                ChannelState.Failed,
                OutboundRoute.MutedFailClosed),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }
}

public sealed class TranslationSession : ITranslationSession, IDisposable, IAsyncDisposable
{
    private const int DefaultEventCapacity = 32;

    private readonly object _sync = new();
    private readonly ITranslationTransport _transport;
    private readonly Uri _endpoint;
    private readonly TranslationSessionConfiguration _configuration;
    private readonly ArrayPool<byte> _pool;
    private readonly Channel<TranslationSessionEvent> _events;
    private readonly PcmFrameBatcher _batcher = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _audioCancellation = new();
    private readonly CancellationToken _audioToken;
    private readonly TaskCompletionSource _handshake =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _remoteClosed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SessionCloseCoordinator _closeCoordinator;
    private TranslationSessionState _state;
    private RuntimeError? _lastError;
    private Task? _connectTask;
    private Task? _receiveLoop;
    private Task? _receiveObservation;
    private Task? _closeTask;
    private Task? _audioStopTask;
    private Task? _shutdownTask;
    private Task? _shutdownObservation;
    private int _pendingSendCount;
    private int _managedResourcesReleased;
    private int _transportReleased;

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
        ArgumentNullException.ThrowIfNull(clock);
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _audioToken = _audioCancellation.Token;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventCapacity);

        _events = Channel.CreateBounded<TranslationSessionEvent>(
            new BoundedChannelOptions(eventCapacity)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        _closeCoordinator = new SessionCloseCoordinator(clock);
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
        Interlocked.Increment(ref _pendingSendCount);
        try
        {
            using CancellationTokenSource sendCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _audioToken);
            try
            {
                await _sendGate.WaitAsync(sendCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _audioToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new TranslationSessionException(Error(
                    ErrorCategory.Protocol,
                    "translationSession.audioStopped"));
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfAudioStopped();
                ThrowIfNotConnected();
                await _batcher.AppendAsync(
                    pcm,
                    SendFrameAsync,
                    sendCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _audioToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new TranslationSessionException(Error(
                    ErrorCategory.Protocol,
                    "translationSession.audioStopped"));
            }
            finally
            {
                _sendGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSendCount);
        }
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
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        RuntimeError disposed = Error(
            ErrorCategory.Network,
            "translationSession.disposed");
        lock (_sync)
        {
            if (_state is not (
                TranslationSessionState.Closed
                or TranslationSessionState.Failed))
            {
                _lastError = disposed;
                _state = TranslationSessionState.Failed;
            }
        }

        _handshake.TrySetException(new TranslationSessionException(disposed));
        _remoteClosed.TrySetResult();
        _events.Writer.TryComplete();
        await CancelAudioAsync().ConfigureAwait(false);
        await CancelLifetimeAsync().ConfigureAwait(false);
        await EnsureShutdownAsync(drainQueuedEvents: true).ConfigureAwait(false);
        await AwaitReceiveObservationAsync().ConfigureAwait(false);
        DrainQueuedEvents();
    }

    internal void CompleteEventChannelForTest()
    {
        _events.Writer.TryComplete();
    }

    internal int RetainedPcmByteCountForTest => _batcher.RetainedByteCount;

    internal int PendingSendCountForTest => Volatile.Read(ref _pendingSendCount);

    internal Task? ShutdownTaskForTest
    {
        get
        {
            lock (_sync)
            {
                return _shutdownTask;
            }
        }
    }

    private async Task ConnectCoreAsync()
    {
        RuntimeError? connectError =
            await _transport.ConnectAsync(_endpoint, _lifetime.Token).ConfigureAwait(false);
        if (connectError is not null)
        {
            Fail(connectError);
            await EnsureShutdownAsync(drainQueuedEvents: true).ConfigureAwait(false);
            throw new TranslationSessionException(connectError);
        }

        Task receiveLoop = ReceiveLoopAsync();
        lock (_sync)
        {
            _receiveLoop = receiveLoop;
            _receiveObservation = ObserveReceiveLoopAsync(receiveLoop);
        }

        await _handshake.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
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
                if (!_lifetime.IsCancellationRequested)
                {
                    Fail(received.Error!);
                }

                return;
            }

            if (received.Status == TranslationReceiveStatus.Closed)
            {
                if (State == TranslationSessionState.Closing)
                {
                    CompleteRemoteClose();
                }
                else if (!_lifetime.IsCancellationRequested)
                {
                    Fail(Error(
                        ErrorCategory.Protocol,
                        "translationSession.unexpectedSocketClose"));
                }

                return;
            }

            bool shouldContinue;
            try
            {
                shouldContinue =
                    await HandleEventAsync(received.Event!).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or ArgumentException)
            {
                Fail(Error(
                    ErrorCategory.Network,
                    "translationSession.eventHandlingFailed"));
                return;
            }

            if (!shouldContinue)
            {
                return;
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
            if (_audioToken.IsCancellationRequested
                && cancellationToken.IsCancellationRequested
                && sendError.Code == "translationSocket.sendCanceled")
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Fail(sendError);
            throw new TranslationSessionException(sendError);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task CloseCoreAsync()
    {
        Task<SessionCloseOutcome> close = _closeCoordinator.CloseAsync(
            1,
            _transport.SendSessionCloseAsync,
            _remoteClosed.Task);
        await CancelAudioAsync().ConfigureAwait(false);
        _ = EnsureAudioStoppedAsync();
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
                await CancelLifetimeAsync().ConfigureAwait(false);
                await EnsureShutdownAsync(drainQueuedEvents: true).ConfigureAwait(false);
                await AwaitReceiveObservationAsync().ConfigureAwait(false);
                throw new TranslationSessionException(concurrentFailure);
            }

            await CancelLifetimeAsync().ConfigureAwait(false);
            await EnsureShutdownAsync(drainQueuedEvents: false).ConfigureAwait(false);
            await AwaitReceiveObservationAsync().ConfigureAwait(false);
            return;
        }

        RuntimeError error = outcome.Error ?? Error(
            ErrorCategory.CloseTimeout,
            "translationSession.closeTimeout");
        SetFailure(error, overwriteExisting: outcome.Completion == SessionCloseCompletion.CloseTimeout);
        RuntimeError finalError = LastError ?? error;
        _events.Writer.TryComplete();
        _handshake.TrySetException(new TranslationSessionException(finalError));
        _remoteClosed.TrySetResult();
        await CancelLifetimeAsync().ConfigureAwait(false);
        if (outcome.Completion == SessionCloseCompletion.CloseTimeout)
        {
            await AwaitReceiveLoopAsync().ConfigureAwait(false);
            ReleaseTransport();
            // A real transport is released after receive settles so disposal can
            // interrupt in-flight I/O. A sender that ignores both cancellation
            // and disposal may delay only managed-resource reclamation in the
            // observed shutdown; it cannot delay public close past the deadline.
            EnsureShutdownObserved(drainQueuedEvents: true);
            throw new TranslationSessionException(finalError);
        }

        await EnsureShutdownAsync(drainQueuedEvents: true).ConfigureAwait(false);
        await AwaitReceiveObservationAsync().ConfigureAwait(false);
        throw new TranslationSessionException(finalError);
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
        if (!SetFailure(error, overwriteExisting: false))
        {
            return;
        }

        _handshake.TrySetException(new TranslationSessionException(error));
        _events.Writer.TryComplete();
        _remoteClosed.TrySetResult();
        _audioCancellation.Cancel();
        _lifetime.Cancel();
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

    private void ThrowIfAudioStopped()
    {
        if (_audioToken.IsCancellationRequested)
        {
            throw new TranslationSessionException(Error(
                ErrorCategory.Protocol,
                "translationSession.audioStopped"));
        }
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

    private bool SetFailure(RuntimeError error, bool overwriteExisting)
    {
        lock (_sync)
        {
            if (_state == TranslationSessionState.Closed)
            {
                return false;
            }

            if (_state == TranslationSessionState.Failed && !overwriteExisting)
            {
                return false;
            }

            _lastError = error;
            _state = TranslationSessionState.Failed;
            return true;
        }
    }

    private async Task ObserveReceiveLoopAsync(Task receiveLoop)
    {
        try
        {
            await receiveLoop.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or ChannelClosedException)
        {
            Fail(Error(
                ErrorCategory.Network,
                "translationSocket.receiveFailed"));
        }

        if (State == TranslationSessionState.Failed)
        {
            await ObserveTaskAsync(
                EnsureShutdownAsync(drainQueuedEvents: true)).ConfigureAwait(false);
        }
    }

    private Task EnsureAudioStoppedAsync()
    {
        lock (_sync)
        {
            _audioStopTask ??= StopAudioCoreAsync();
            return _audioStopTask;
        }
    }

    private async Task AwaitReceiveObservationAsync()
    {
        Task? observation;
        lock (_sync)
        {
            observation = _receiveObservation;
        }

        if (observation is not null)
        {
            await observation.ConfigureAwait(false);
        }
    }

    private async Task AwaitReceiveLoopAsync()
    {
        Task? receiveLoop;
        lock (_sync)
        {
            receiveLoop = _receiveLoop;
        }

        if (receiveLoop is not null)
        {
            await receiveLoop.ConfigureAwait(false);
        }
    }

    private async Task StopAudioCoreAsync()
    {
        await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _ = _batcher.Discard();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private Task EnsureShutdownAsync(bool drainQueuedEvents)
    {
        lock (_sync)
        {
            _shutdownTask ??= ShutdownCoreAsync(drainQueuedEvents);
            return _shutdownTask;
        }
    }

    private void EnsureShutdownObserved(bool drainQueuedEvents)
    {
        Task shutdown = EnsureShutdownAsync(drainQueuedEvents);
        lock (_sync)
        {
            _shutdownObservation ??= ObserveTaskAsync(shutdown);
        }
    }

    private static Task ObserveTaskAsync(Task task)
    {
        return task.ContinueWith(
            static completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ShutdownCoreAsync(bool drainQueuedEvents)
    {
        await CancelAudioAsync().ConfigureAwait(false);
        await CancelLifetimeAsync().ConfigureAwait(false);

        Task? receiveLoop;
        lock (_sync)
        {
            receiveLoop = _receiveLoop;
        }

        if (receiveLoop is not null)
        {
            await receiveLoop.ConfigureAwait(false);
        }

        await EnsureAudioStoppedAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        if (drainQueuedEvents)
        {
            DrainQueuedEvents();
        }

        ReleaseResources();
    }

    private async ValueTask CancelAudioAsync()
    {
        if (!_audioCancellation.IsCancellationRequested)
        {
            await _audioCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask CancelLifetimeAsync()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private void DrainQueuedEvents()
    {
        while (_events.Reader.TryRead(out TranslationSessionEvent? sessionEvent))
        {
            if (sessionEvent is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private void ReleaseResources()
    {
        ReleaseTransport();
        if (Interlocked.Exchange(ref _managedResourcesReleased, 1) != 0)
        {
            return;
        }

        _audioCancellation.Dispose();
        _lifetime.Dispose();
        _sendGate.Dispose();
    }

    private void ReleaseTransport()
    {
        if (Interlocked.Exchange(ref _transportReleased, 1) == 0)
        {
            _transport.Dispose();
        }
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
