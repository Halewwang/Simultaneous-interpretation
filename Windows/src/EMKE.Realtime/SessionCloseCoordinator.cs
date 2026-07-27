using EMKE.Core;

namespace EMKE.Realtime;

internal enum SessionCloseCompletion
{
    Closed,
    CloseTimeout,
    Failed,
}

internal sealed record SessionCloseOutcome(
    long Generation,
    SessionCloseCompletion Completion,
    RuntimeError? Error);

internal sealed class SessionCloseCoordinator
{
    public const int DeadlineMilliseconds = 1_000;

    private readonly IClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<long, Task<SessionCloseOutcome>> _completions = [];
    private long _activeGeneration;

    public SessionCloseCoordinator(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public long ActiveGeneration
    {
        get
        {
            lock (_sync)
            {
                return _activeGeneration;
            }
        }
    }

    public void Activate(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        lock (_sync)
        {
            if (generation <= _activeGeneration)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(generation),
                    "A session generation must increase monotonically.");
            }

            _activeGeneration = generation;
        }
    }

    public Task<SessionCloseOutcome> CloseAsync(
        long generation,
        Func<CancellationToken, ValueTask<RuntimeError?>> sendClose,
        Task remoteClosed)
    {
        // The sender must observe the supplied token. Local close still completes
        // at the deadline for a non-cooperative sender, but its CTS remains owned
        // by the observer until that sender eventually converges.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentNullException.ThrowIfNull(sendClose);
        ArgumentNullException.ThrowIfNull(remoteClosed);

        lock (_sync)
        {
            if (_completions.TryGetValue(generation, out Task<SessionCloseOutcome>? existing))
            {
                return existing;
            }

            if (_activeGeneration != generation)
            {
                throw new InvalidOperationException(
                    "Only the active session generation can begin closing.");
            }

            Task<SessionCloseOutcome> created =
                CloseGenerationAsync(generation, sendClose, remoteClosed);
            _completions.Add(generation, created);
            return created;
        }
    }

    private async Task<SessionCloseOutcome> CloseGenerationAsync(
        long generation,
        Func<CancellationToken, ValueTask<RuntimeError?>> sendClose,
        Task remoteClosed)
    {
        CancellationTokenSource deadlineCancellation = new();
        CancellationTokenSource sendCancellation = new();
        Task deadline = _clock.DelayAsync(
            TimeSpan.FromMilliseconds(DeadlineMilliseconds),
            deadlineCancellation.Token).AsTask();
        Task<RuntimeError?> send = InvokeSendAsync(
            sendClose,
            sendCancellation.Token);

        try
        {
            Task first = await Task.WhenAny(remoteClosed, deadline, send).ConfigureAwait(false);
            if (first == remoteClosed)
            {
                await remoteClosed.ConfigureAwait(false);
                return new SessionCloseOutcome(
                    generation,
                    SessionCloseCompletion.Closed,
                    null);
            }

            if (first == deadline)
            {
                await deadline.ConfigureAwait(false);
                return new SessionCloseOutcome(
                    generation,
                    SessionCloseCompletion.CloseTimeout,
                    Error(
                        ErrorCategory.CloseTimeout,
                        "translationSession.closeTimeout",
                        RecoveryAction.Retry));
            }

            RuntimeError? sendError = await send.ConfigureAwait(false);
            if (sendError is not null)
            {
                return new SessionCloseOutcome(
                    generation,
                    SessionCloseCompletion.Failed,
                    sendError);
            }

            first = await Task.WhenAny(remoteClosed, deadline).ConfigureAwait(false);
            if (first == remoteClosed)
            {
                await remoteClosed.ConfigureAwait(false);
                return new SessionCloseOutcome(
                    generation,
                    SessionCloseCompletion.Closed,
                    null);
            }

            await deadline.ConfigureAwait(false);
            return new SessionCloseOutcome(
                generation,
                SessionCloseCompletion.CloseTimeout,
                Error(
                    ErrorCategory.CloseTimeout,
                    "translationSession.closeTimeout",
                    RecoveryAction.Retry));
        }
        finally
        {
            await sendCancellation.CancelAsync().ConfigureAwait(false);
            await deadlineCancellation.CancelAsync().ConfigureAwait(false);
            await ObserveCancellationAsync(deadline).ConfigureAwait(false);
            if (send.IsCompleted)
            {
                _ = await send.ConfigureAwait(false);
                sendCancellation.Dispose();
            }
            else
            {
#pragma warning disable CA2025 // A non-cooperative sender cannot delay local close; the observer owns the CTS until it converges.
                _ = DisposeAfterSendCompletesAsync(send, sendCancellation);
#pragma warning restore CA2025
            }

            deadlineCancellation.Dispose();
            lock (_sync)
            {
                if (_activeGeneration == generation)
                {
                    _activeGeneration = 0;
                }
            }
        }
    }

    private static async Task<RuntimeError?> InvokeSendAsync(
        Func<CancellationToken, ValueTask<RuntimeError?>> sendClose,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sendClose(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Error(
                ErrorCategory.Network,
                "translationSocket.sendCanceled",
                RecoveryAction.Retry);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or ArgumentException)
        {
            return Error(
                ErrorCategory.Network,
                "translationSocket.sendFailed",
                RecoveryAction.Retry);
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task DisposeAfterSendCompletesAsync(
        Task<RuntimeError?> send,
        CancellationTokenSource cancellation)
    {
        try
        {
            _ = await send.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static RuntimeError Error(
        ErrorCategory category,
        string code,
        RecoveryAction recoveryAction)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            recoveryAction);
    }
}
