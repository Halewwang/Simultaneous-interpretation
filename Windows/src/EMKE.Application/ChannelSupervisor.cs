using System.Threading.Channels;
using EMKE.Core;
using EMKE.Realtime;

namespace EMKE.Application;

internal readonly record struct RuntimeMailboxRead<T>(bool IsPriority, T Item);

internal sealed class RuntimeCommandMailbox<T> : IDisposable
{
    private readonly Channel<T> _normal;
    private readonly Channel<T> _priority;
#pragma warning disable CA2213 // Never creates a WaitHandle; GC cleanup avoids racing late producers during actor exit.
    private readonly SemaphoreSlim _normalSlots;
    private readonly SemaphoreSlim _available = new(0);
#pragma warning restore CA2213
    private readonly Action<T> _drop;
    private int _disposed;

    public RuntimeCommandMailbox(int capacity, Action<T> drop)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _drop = drop ?? throw new ArgumentNullException(nameof(drop));
        _normalSlots = new SemaphoreSlim(capacity, capacity);
        _normal = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        _priority = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
    }

    public bool TryWrite(T item)
    {
        if (!_normalSlots.Wait(0))
        {
            _drop(item);
            return false;
        }

        if (_normal.Writer.TryWrite(item))
        {
            _available.Release();
            return true;
        }

        _normalSlots.Release();
        _drop(item);
        return false;
    }

    public bool TryWritePriority(T item)
    {
        if (!_priority.Writer.TryWrite(item))
        {
            return false;
        }

        _available.Release();
        return true;
    }

    public async ValueTask WriteReliableAsync(
        T item,
        CancellationToken cancellationToken)
    {
        await _normalSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_normal.Writer.TryWrite(item))
        {
            _available.Release();
            return;
        }

        _normalSlots.Release();
        _drop(item);
    }

    public async ValueTask<RuntimeMailboxRead<T>> ReadAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (_priority.Reader.TryRead(out T? priority))
            {
                return new RuntimeMailboxRead<T>(true, priority);
            }

            if (_normal.Reader.TryRead(out T? normal))
            {
                _normalSlots.Release();
                return new RuntimeMailboxRead<T>(false, normal);
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new OperationCanceledException(
                    "The runtime mailbox is closed.",
                    cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _normal.Writer.TryComplete();
        _priority.Writer.TryComplete();
        while (_normal.Reader.TryRead(out T? normal))
        {
            _normalSlots.Release();
            _drop(normal);
        }

        while (_priority.Reader.TryRead(out T? priority))
        {
            _drop(priority);
        }

        _available.Release();
    }
}

internal sealed record ChannelSupervisorNotification(
    long Generation,
    AudioDirection Direction,
    ChannelState State,
    TranslationSessionEvent? Event,
    RuntimeError? Error);

internal sealed class ChannelSupervisor : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan[] ReconnectSchedule =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    private readonly object _sync = new();
    private readonly AudioDirection _direction;
    private readonly long _generation;
    private readonly ITranslationSessionFactory _factory;
    private readonly TranslationSessionRequest _request;
    private readonly IClock _clock;
    private readonly Func<ChannelSupervisorNotification, ValueTask> _notify;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _reconnectCancellation = new();
    private readonly CancellationTokenSource _closeCancellation = new();
    private ITranslationSession? _session;
    private Task<RuntimeError?>? _connectTask;
    private Task? _receiveTask;
    private Task? _closeTask;
#pragma warning disable CA2213 // Non-owning alias; the receive loop's using declaration owns disposal.
    private CancellationTokenSource? _receiveCycleCancellation;
#pragma warning restore CA2213
    private RuntimeError? _forcedFailure;
    private ChannelState _state = ChannelState.Inactive;
    private bool _closing;
    private int _disposed;
    private int _cancellationSourcesReleased;

    public ChannelSupervisor(
        AudioDirection direction,
        long generation,
        ITranslationSessionFactory factory,
        TranslationSessionRequest request,
        IClock clock,
        Func<ChannelSupervisorNotification, ValueTask> notify)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        _direction = direction;
        _generation = generation;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    public long Generation => _generation;

    public AudioDirection Direction => _direction;

    public ChannelState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public Task<RuntimeError?> ConnectAsync(CancellationToken cancellationToken)
    {
        Task<RuntimeError?> connect;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            _connectTask ??= ConnectCoreAsync();
            connect = _connectTask;
        }

        return cancellationToken.CanBeCanceled
            ? connect.WaitAsync(cancellationToken)
            : connect;
    }

    public async ValueTask<RuntimeError?> SendPcmAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken)
    {
        ITranslationSession? session;
        lock (_sync)
        {
            session = !_closing && _state == ChannelState.Connected
                ? _session
                : null;
        }

        if (session is null)
        {
            return Error(
                ErrorCategory.Protocol,
                "translationRuntime.channelNotConnected",
                RecoveryAction.Retry);
        }

        try
        {
            await session.SendPcmAsync(pcm16, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsRuntimeFailure(exception))
        {
            RuntimeError error = MapError(exception);
            await HandleSendFailureAsync(error).ConfigureAwait(false);
            return error;
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Task close;
        lock (_sync)
        {
            _closing = true;
            _ = _reconnectCancellation.CancelAsync();
            _closeTask ??= CloseCoreAsync();
            close = _closeTask;
        }

        return cancellationToken.CanBeCanceled
            ? close.WaitAsync(cancellationToken)
            : close;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_sync)
        {
            _closing = true;
        }

        _reconnectCancellation.Cancel();
        _closeCancellation.Cancel();
        _receiveCycleCancellation?.Cancel();
        _lifetime.Cancel();
        ITranslationSession? session = TakeSession();
        DisposeSession(session);
        Task? close;
        lock (_sync)
        {
            close = _closeTask;
        }

        if (close is null || close.IsCompleted)
        {
            ReleaseCancellationSources();
        }
        else
        {
            _ = close.ContinueWith(
                static (completed, state) =>
                {
                    _ = completed.Exception;
                    ((ChannelSupervisor)state!).ReleaseCancellationSources();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void ReleaseCancellationSources()
    {
        if (Interlocked.Exchange(ref _cancellationSourcesReleased, 1) != 0)
        {
            return;
        }

        _lifetime.Dispose();
        _reconnectCancellation.Dispose();
        _closeCancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Dispose();
        }
    }

    private async Task<RuntimeError?> ConnectCoreAsync()
    {
        SetState(ChannelState.Connecting);
        RuntimeError? error;
        ITranslationSession? created = null;
        try
        {
            created = await _factory.CreateAsync(
                _request,
                _lifetime.Token).ConfigureAwait(false);
            await created.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
            if (!TryInstallSession(created))
            {
                await CloseAndDisposeAsync(created).ConfigureAwait(false);
                return Error(
                    ErrorCategory.Protocol,
                    "translationRuntime.staleConnect",
                    RecoveryAction.None);
            }

            created = null;
            SetState(ChannelState.Connected);
            await NotifyAsync(ChannelState.Connected, null, null)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _receiveTask = ReceiveAndReconnectAsync();
            }

            return null;
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested || IsClosing())
        {
            error = Error(
                ErrorCategory.Protocol,
                "translationRuntime.connectCanceled",
                RecoveryAction.None);
        }
        catch (Exception exception) when (IsRuntimeFailure(exception))
        {
            error = MapError(exception);
        }
        finally
        {
            if (created is not null)
            {
                await CloseAndDisposeAsync(created).ConfigureAwait(false);
            }
        }

        SetState(ChannelState.Failed);
        await NotifyAsync(ChannelState.Failed, null, error).ConfigureAwait(false);
        return error;
    }

    private async Task ReceiveAndReconnectAsync()
    {
        while (!IsClosing())
        {
            ITranslationSession? session = CurrentSession();
            if (session is null)
            {
                return;
            }

            RuntimeError failure;
            using CancellationTokenSource receiveCycle =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetime.Token);
            lock (_sync)
            {
                _receiveCycleCancellation = receiveCycle;
            }

            try
            {
                if (TryTakeForcedFailure(out RuntimeError forced))
                {
                    failure = forced;
                }
                else
                {
                    await foreach (TranslationSessionEvent sessionEvent in
                                   session.ReceiveAsync(receiveCycle.Token)
                                       .ConfigureAwait(false))
                    {
                        bool transferred = false;
                        try
                        {
                            await NotifyAsync(
                                State,
                                sessionEvent,
                                null).ConfigureAwait(false);
                            transferred = true;
                        }
                        finally
                        {
                            if (!transferred
                                && sessionEvent is IDisposable disposable)
                            {
                                disposable.Dispose();
                            }
                        }
                    }

                    if (IsClosing())
                    {
                        return;
                    }

                    failure = Error(
                        ErrorCategory.Protocol,
                        "translationRuntime.sessionEnded",
                        RecoveryAction.Retry);
                }
            }
            catch (OperationCanceledException) when (
                _lifetime.IsCancellationRequested || IsClosing())
            {
                return;
            }
            catch (OperationCanceledException) when (
                TryTakeForcedFailure(out RuntimeError forced))
            {
                failure = forced;
            }
            catch (Exception exception) when (IsRuntimeFailure(exception))
            {
                failure = MapError(exception);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(
                            _receiveCycleCancellation,
                            receiveCycle))
                    {
                        _receiveCycleCancellation = null;
                    }
                }
            }

            if (failure.Category != ErrorCategory.Network)
            {
                SetState(ChannelState.Failed);
                await NotifyAsync(ChannelState.Failed, null, failure)
                    .ConfigureAwait(false);
                return;
            }

            SetState(ChannelState.Reconnecting);
            await NotifyAsync(ChannelState.Reconnecting, null, failure)
                .ConfigureAwait(false);
            ITranslationSession? failedSession = TakeSession(session);
            await CloseAndDisposeAsync(failedSession).ConfigureAwait(false);

            ReconnectResult reconnect =
                await TryReconnectAsync().ConfigureAwait(false);
            if (reconnect.Connected)
            {
                SetState(ChannelState.Connected);
                await NotifyAsync(ChannelState.Connected, null, null)
                    .ConfigureAwait(false);
                continue;
            }

            if (reconnect.Canceled)
            {
                return;
            }

            SetState(ChannelState.Failed);
            await NotifyAsync(ChannelState.Failed, null, reconnect.Error)
                .ConfigureAwait(false);
            return;
        }
    }

    private async Task<ReconnectResult> TryReconnectAsync()
    {
        RuntimeError last = Error(
            ErrorCategory.Network,
            "translationRuntime.reconnectExhausted",
            RecoveryAction.Retry);
        foreach (TimeSpan delay in ReconnectSchedule)
        {
            try
            {
                await _clock.DelayAsync(
                    delay,
                    _reconnectCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _reconnectCancellation.IsCancellationRequested || IsClosing())
            {
                return ReconnectResult.CanceledResult;
            }

            if (IsClosing())
            {
                return ReconnectResult.CanceledResult;
            }

            ITranslationSession? created = null;
            try
            {
                created = await _factory.CreateAsync(
                    _request,
                    _reconnectCancellation.Token).ConfigureAwait(false);
                await created.ConnectAsync(_reconnectCancellation.Token)
                    .ConfigureAwait(false);
                if (!TryInstallSession(created))
                {
                    await CloseAndDisposeAsync(created).ConfigureAwait(false);
                    return ReconnectResult.CanceledResult;
                }

                created = null;
                return ReconnectResult.Success;
            }
            catch (OperationCanceledException) when (
                _reconnectCancellation.IsCancellationRequested || IsClosing())
            {
                return ReconnectResult.CanceledResult;
            }
            catch (Exception exception) when (IsRuntimeFailure(exception))
            {
                last = MapError(exception);
                if (last.Category != ErrorCategory.Network)
                {
                    return new ReconnectResult(false, false, last);
                }
            }
            finally
            {
                if (created is not null)
                {
                    await CloseAndDisposeAsync(created).ConfigureAwait(false);
                }
            }
        }

        return new ReconnectResult(
            false,
            false,
            Error(
                ErrorCategory.Network,
                "translationRuntime.reconnectExhausted",
                RecoveryAction.Retry));
    }

    private async Task HandleSendFailureAsync(RuntimeError error)
    {
        CancellationTokenSource? receiveCycle;
        lock (_sync)
        {
            if (_closing)
            {
                return;
            }

            _forcedFailure ??= error;
            receiveCycle = _receiveCycleCancellation;
        }

        if (receiveCycle is not null)
        {
            await receiveCycle.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task CloseCoreAsync()
    {
        ITranslationSession? session = CurrentSession();
        bool gracefulClose = true;
        if (session is not null)
        {
            try
            {
                await session.CloseAsync(_closeCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _closeCancellation.IsCancellationRequested)
            {
                gracefulClose = false;
            }
            catch (Exception exception) when (IsRuntimeFailure(exception))
            {
                _ = MapError(exception);
                gracefulClose = false;
            }
        }

        Task? receive;
        lock (_sync)
        {
            receive = _receiveTask;
        }

        if (!gracefulClose)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (receive is not null)
        {
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _lifetime.IsCancellationRequested)
            {
            }
        }

        if (gracefulClose)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        DisposeSession(TakeSession());
        SetState(ChannelState.Inactive);
        await NotifyAsync(ChannelState.Inactive, null, null)
            .ConfigureAwait(false);
    }

    private bool TryInstallSession(ITranslationSession session)
    {
        lock (_sync)
        {
            if (_closing || _session is not null)
            {
                return false;
            }

            _session = session;
            return true;
        }
    }

    private ITranslationSession? CurrentSession()
    {
        lock (_sync)
        {
            return _session;
        }
    }

    private ITranslationSession? TakeSession(
        ITranslationSession? expected = null)
    {
        lock (_sync)
        {
            if (expected is not null && !ReferenceEquals(_session, expected))
            {
                return null;
            }

            ITranslationSession? session = _session;
            _session = null;
            return session;
        }
    }

    private bool IsClosing()
    {
        lock (_sync)
        {
            return _closing;
        }
    }

    private bool TryTakeForcedFailure(out RuntimeError error)
    {
        lock (_sync)
        {
            RuntimeError? forced = _forcedFailure;
            _forcedFailure = null;
            if (forced is null)
            {
                error = null!;
                return false;
            }

            error = forced;
            return true;
        }
    }

    private void SetState(ChannelState state)
    {
        lock (_sync)
        {
            _state = state;
        }
    }

    private ValueTask NotifyAsync(
        ChannelState state,
        TranslationSessionEvent? sessionEvent,
        RuntimeError? error)
    {
        return _notify(new ChannelSupervisorNotification(
            _generation,
            _direction,
            state,
            sessionEvent,
            error));
    }

    private static bool IsRuntimeFailure(Exception exception)
    {
        return exception is RuntimeOperationException
            or TranslationSessionException
            or IOException
            or InvalidOperationException;
    }

    private static RuntimeError MapError(Exception exception)
    {
        return exception switch
        {
            RuntimeOperationException operation => operation.Error,
            TranslationSessionException translation => translation.Error,
            IOException => Error(
                ErrorCategory.Network,
                "translationRuntime.networkFailure",
                RecoveryAction.Retry),
            _ => Error(
                ErrorCategory.Protocol,
                "translationRuntime.sessionFailure",
                RecoveryAction.ReportCompatibility),
        };
    }

    private static async Task CloseAndDisposeAsync(
        ITranslationSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await session.CloseAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort cleanup must continue to release ownership.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        DisposeSession(session);
    }

    private static void DisposeSession(ITranslationSession? session)
    {
        switch (session)
        {
            case IAsyncDisposable asyncDisposable:
                _ = ObserveDisposeAsync(asyncDisposable);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static async Task ObserveDisposeAsync(
        IAsyncDisposable asyncDisposable)
    {
        try
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Asynchronous disposal is best-effort after ownership release.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static RuntimeError Error(
        ErrorCategory category,
        string code,
        RecoveryAction recovery)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            recovery);
    }

    private sealed record ReconnectResult(
        bool Connected,
        bool Canceled,
        RuntimeError? Error)
    {
        public static ReconnectResult Success { get; } =
            new(true, false, null);

        public static ReconnectResult CanceledResult { get; } =
            new(false, true, null);
    }
}
