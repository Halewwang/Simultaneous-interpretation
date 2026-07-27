using System.Collections.Concurrent;
using EMKE.Application;
using EMKE.Core;

namespace EMKE.Application.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // ChannelSupervisor takes ownership of fake sessions.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class ChannelSupervisorTests
{
    private static readonly double[] ExpectedReconnectMilliseconds =
        [250, 500, 1_000, 2_000, 5_000];

    [TestMethod]
    public async Task PriorityItemIsReadableWhenNormalQueueIsFullAndDroppedOwnerIsReleased()
    {
        List<MailboxProbe> dropped = [];
        using RuntimeCommandMailbox<MailboxProbe> mailbox = new(
            capacity: 2,
            probe =>
            {
                probe.Drop();
                dropped.Add(probe);
            });
        MailboxProbe first = new("first");
        MailboxProbe second = new("second");
        MailboxProbe overflow = new("overflow");
        MailboxProbe stop = new("stop");

        Assert.IsTrue(mailbox.TryWrite(first));
        Assert.IsTrue(mailbox.TryWrite(second));
        Assert.IsFalse(mailbox.TryWrite(overflow));
        Assert.IsTrue(mailbox.TryWritePriority(stop));

        RuntimeMailboxRead<MailboxProbe> read =
            await mailbox.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(read.IsPriority);
        Assert.AreSame(stop, read.Item);
        CollectionAssert.AreEqual(new[] { overflow }, dropped);
        Assert.IsTrue(overflow.Completion.Task.IsCompleted);
        Assert.AreEqual(1, overflow.Owner.DisposeCount);
    }

    [TestMethod]
    public void DroppedAudioMailboxItemDisposesItsRealPcmLease()
    {
        TrackingPcmLease retainedLease = new([1, 0]);
        TrackingPcmLease droppedLease = new([2, 0]);
        using RuntimeCommandMailbox<AudioEngineEvent> mailbox = new(
            capacity: 1,
            static audio => audio.Dispose());
        AudioEngineEvent retained = AudioEngineEvent.CreatePcm(
            retainedLease,
            AudioDirection.Inbound,
            AudioEngineRoute.Translated,
            AudioEngineStatus.Ok,
            frameCount: 1,
            sequence: 1);
        AudioEngineEvent dropped = AudioEngineEvent.CreatePcm(
            droppedLease,
            AudioDirection.Inbound,
            AudioEngineRoute.Translated,
            AudioEngineStatus.Ok,
            frameCount: 1,
            sequence: 2);

        Assert.IsTrue(mailbox.TryWrite(retained));
        Assert.IsFalse(mailbox.TryWrite(dropped));

        Assert.AreEqual(0, retainedLease.DisposeCount);
        Assert.AreEqual(1, droppedLease.DisposeCount);
        retained.Dispose();
    }

    [TestMethod]
    public void DisposingMailboxDropsQueuedOwnerAndCompletesItsWaiter()
    {
        MailboxProbe queued = new("queued");
        RuntimeCommandMailbox<MailboxProbe> mailbox = new(
            capacity: 1,
            static probe => probe.Drop());
        Assert.IsTrue(mailbox.TryWrite(queued));

        mailbox.Dispose();

        Assert.AreEqual(1, queued.Owner.DisposeCount);
        Assert.IsTrue(queued.Completion.Task.IsCompleted);
    }

    [TestMethod]
    public async Task TransientNetworkFailureUsesExactBoundedBackoffSchedule()
    {
        RecordingClock clock = new(completeImmediately: true);
        QueueSessionFactory factory = new(
        [
            FakeSupervisorSession.ReceiveFailure(NetworkError()),
            FakeSupervisorSession.ConnectFailure(NetworkError()),
            FakeSupervisorSession.ConnectFailure(NetworkError()),
            FakeSupervisorSession.ConnectFailure(NetworkError()),
            FakeSupervisorSession.ConnectFailure(NetworkError()),
            FakeSupervisorSession.ConnectFailure(NetworkError()),
        ]);
        TaskCompletionSource<ChannelSupervisorNotification> terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ChannelSupervisor supervisor = CreateSupervisor(
            factory,
            clock,
            notification =>
            {
                if (notification.State == ChannelState.Failed)
                {
                    terminal.TrySetResult(notification);
                }

                return ValueTask.CompletedTask;
            });

        Assert.IsNull(await supervisor.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false));
        ChannelSupervisorNotification failed =
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Network, failed.Error?.Category);
        CollectionAssert.AreEqual(
            ExpectedReconnectMilliseconds,
            clock.Delays.Select(static delay => delay.TotalMilliseconds).ToArray());
        Assert.AreEqual(6, factory.CreateCount);
    }

    [TestMethod]
    public async Task NonRetryableFailureDoesNotReconnect()
    {
        RecordingClock clock = new(completeImmediately: true);
        QueueSessionFactory factory = new(
        [
            FakeSupervisorSession.ReceiveFailure(AuthenticationError()),
        ]);
        TaskCompletionSource<ChannelSupervisorNotification> terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ChannelSupervisor supervisor = CreateSupervisor(
            factory,
            clock,
            notification =>
            {
                if (notification.State == ChannelState.Failed)
                {
                    terminal.TrySetResult(notification);
                }

                return ValueTask.CompletedTask;
            });

        Assert.IsNull(await supervisor.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false));
        ChannelSupervisorNotification failed =
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Authentication, failed.Error?.Category);
        Assert.IsEmpty(clock.Delays);
        Assert.AreEqual(1, factory.CreateCount);
    }

    [TestMethod]
    public async Task ClosingDuringReconnectPreventsOldGenerationFromReopening()
    {
        RecordingClock clock = new(completeImmediately: false);
        QueueSessionFactory factory = new(
        [
            FakeSupervisorSession.ReceiveFailure(NetworkError()),
            FakeSupervisorSession.Connected(),
        ]);
        TaskCompletionSource reconnecting =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ChannelSupervisor supervisor = CreateSupervisor(
            factory,
            clock,
            notification =>
            {
                if (notification.State == ChannelState.Reconnecting)
                {
                    reconnecting.TrySetResult();
                }

                return ValueTask.CompletedTask;
            });

        Assert.IsNull(await supervisor.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false));
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Task close = supervisor.CloseAsync(CancellationToken.None);
        clock.ReleaseAll();
        await close.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(ChannelState.Inactive, supervisor.State);
    }

    [TestMethod]
    public async Task TransientSendFailureStartsReconnectWhileNonRetryableDoesNot()
    {
        RecordingClock transientClock = new(completeImmediately: true);
        FakeSupervisorSession transientFirst =
            FakeSupervisorSession.Connected(sendError: NetworkError());
        QueueSessionFactory transientFactory = new(
        [
            transientFirst,
            FakeSupervisorSession.Connected(),
        ]);
        TaskCompletionSource reconnected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int connectedCount = 0;
        await using ChannelSupervisor transient = CreateSupervisor(
            transientFactory,
            transientClock,
            notification =>
            {
                if (notification.State == ChannelState.Connected
                    && Interlocked.Increment(ref connectedCount) == 2)
                {
                    reconnected.TrySetResult();
                }

                return ValueTask.CompletedTask;
            });
        Assert.IsNull(await transient.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false));

        RuntimeError? transientError = await transient.SendPcmAsync(
            new byte[] { 1, 0 },
            CancellationToken.None).ConfigureAwait(false);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Network, transientError?.Category);
        Assert.AreEqual(2, transientFactory.CreateCount);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromMilliseconds(250) },
            transientClock.Delays.ToArray());

        RecordingClock authClock = new(completeImmediately: true);
        QueueSessionFactory authFactory = new(
        [
            FakeSupervisorSession.Connected(sendError: AuthenticationError()),
        ]);
        TaskCompletionSource failed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ChannelSupervisor authentication = CreateSupervisor(
            authFactory,
            authClock,
            notification =>
            {
                if (notification.State == ChannelState.Failed)
                {
                    failed.TrySetResult();
                }

                return ValueTask.CompletedTask;
            });
        Assert.IsNull(await authentication.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false));

        RuntimeError? authError = await authentication.SendPcmAsync(
            new byte[] { 1, 0 },
            CancellationToken.None).ConfigureAwait(false);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Authentication, authError?.Category);
        Assert.AreEqual(1, authFactory.CreateCount);
        Assert.IsEmpty(authClock.Delays);
    }

    private static ChannelSupervisor CreateSupervisor(
        ITranslationSessionFactory factory,
        IClock clock,
        Func<ChannelSupervisorNotification, ValueTask> notify)
    {
        return new ChannelSupervisor(
            AudioDirection.Inbound,
            generation: 7,
            factory,
            new TranslationSessionConfiguration(
                LanguageCode.En,
                LanguageCode.Zh,
                "gpt-realtime-translate"),
            clock,
            notify);
    }

    private static RuntimeError NetworkError()
    {
        return Error(ErrorCategory.Network, "test.network");
    }

    private static RuntimeError AuthenticationError()
    {
        return Error(ErrorCategory.Authentication, "test.authentication");
    }

    private static RuntimeError Error(ErrorCategory category, string code)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.Retry);
    }

    private sealed class MailboxProbe
    {
        public MailboxProbe(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public TrackingDisposable Owner { get; } = new();

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Drop()
        {
            Owner.Dispose();
            Completion.TrySetResult();
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private int _disposed;

        public int DisposeCount => Volatile.Read(ref _disposed);

        public void Dispose()
        {
            Interlocked.CompareExchange(ref _disposed, 1, 0);
        }
    }

    private sealed class RecordingClock(bool completeImmediately) : IClock
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _pending = new();

        public ConcurrentQueue<TimeSpan> Delays { get; } = new();

        public TimeSpan MonotonicNow { get; private set; }

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Delays.Enqueue(delay);
            MonotonicNow += delay;
            if (completeImmediately)
            {
                return ValueTask.CompletedTask;
            }

            TaskCompletionSource completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            _pending.Enqueue(completion);
            return new ValueTask(completion.Task);
        }

        public void ReleaseAll()
        {
            while (_pending.TryDequeue(out TaskCompletionSource? completion))
            {
                completion.TrySetResult();
            }
        }
    }

    private sealed class QueueSessionFactory(
        IEnumerable<FakeSupervisorSession> sessions) : ITranslationSessionFactory
    {
        private readonly ConcurrentQueue<FakeSupervisorSession> _sessions =
            new(sessions);
        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCount);
            if (!_sessions.TryDequeue(out FakeSupervisorSession? session))
            {
                throw new InvalidOperationException("No fake session remains.");
            }

            return ValueTask.FromResult<ITranslationSession>(session);
        }
    }

    private sealed class FakeSupervisorSession : ITranslationSession, IDisposable
    {
        private readonly RuntimeError? _connectError;
        private readonly RuntimeError? _receiveError;
        private readonly RuntimeError? _sendError;
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        private FakeSupervisorSession(
            RuntimeError? connectError,
            RuntimeError? receiveError,
            RuntimeError? sendError)
        {
            _connectError = connectError;
            _receiveError = receiveError;
            _sendError = sendError;
        }

        public static FakeSupervisorSession Connected(
            RuntimeError? sendError = null)
        {
            return new FakeSupervisorSession(null, null, sendError);
        }

        public static FakeSupervisorSession ConnectFailure(RuntimeError error)
        {
            return new FakeSupervisorSession(error, null, null);
        }

        public static FakeSupervisorSession ReceiveFailure(RuntimeError error)
        {
            return new FakeSupervisorSession(null, error, null);
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return _connectError is null
                ? Task.CompletedTask
                : Task.FromException(new RuntimeOperationException(_connectError));
        }

        public ValueTask SendPcmAsync(
            ReadOnlyMemory<byte> pcm,
            CancellationToken cancellationToken)
        {
            return _sendError is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(
                    new RuntimeOperationException(_sendError));
        }

        public async IAsyncEnumerable<TranslationSessionEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (_receiveError is not null)
            {
                throw new RuntimeOperationException(_receiveError);
            }

            await _closed.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            yield break;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Interlocked.CompareExchange(ref _disposed, 1, 0);
            _closed.TrySetResult();
        }
    }
}
