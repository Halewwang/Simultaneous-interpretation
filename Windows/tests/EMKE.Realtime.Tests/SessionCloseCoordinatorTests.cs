using System.Text.Json;
using EMKE.Core;

namespace EMKE.Realtime.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class SessionCloseCoordinatorTests
{
    [TestMethod]
    public async Task SharedCloseFixtureIsExecutedByGenerationAwareCoordinator()
    {
        using JsonDocument fixture = LoadFixture();
        foreach (JsonElement fixtureCase in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = fixtureCase.GetProperty("name").GetString()!;
            JsonElement input = fixtureCase.GetProperty("input");
            JsonElement expected = fixtureCase.GetProperty("expected");
            if (input.TryGetProperty("closeRequests", out _))
            {
                await AssertConcurrentRoutesAsync(input, expected, name);
            }
            else if (input.TryGetProperty("closingGeneration", out _))
            {
                await AssertOldGenerationIsolationAsync(input, expected, name);
            }
            else
            {
                await AssertSingleGenerationCaseAsync(input, expected, name);
            }
        }
    }

    [TestMethod]
    public async Task SameGenerationCallersReceiveTheExactSameTaskAndReleaseOnce()
    {
        FakeClock clock = new();
        int releases = 0;
        SessionCloseCoordinator coordinator = new(
            clock,
            _ => Interlocked.Increment(ref releases));
        coordinator.Activate(7);
        TaskCompletionSource remoteClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<SessionCloseOutcome> first = coordinator.CloseAsync(
            7,
            static _ => ValueTask.FromResult<RuntimeError?>(null),
            remoteClosed.Task);
        Task<SessionCloseOutcome> second = coordinator.CloseAsync(
            7,
            static _ => throw new InvalidOperationException("must not be invoked"),
            Task.CompletedTask);

        Assert.AreSame(first, second);
        remoteClosed.SetResult();
        SessionCloseOutcome outcome = await first;
        Assert.AreEqual(SessionCloseCompletion.Closed, outcome.Completion);
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public async Task SendFailureIsSecretFreeAndResourcesAreReleasedExactlyOnce()
    {
        const string secret = "sk-1234567890abcdef";
        FakeClock clock = new();
        int releases = 0;
        SessionCloseCoordinator coordinator = new(
            clock,
            _ => Interlocked.Increment(ref releases));
        coordinator.Activate(1);

        SessionCloseOutcome outcome = await coordinator.CloseAsync(
            1,
            static _ => ValueTask.FromResult<RuntimeError?>(new RuntimeError(
                ErrorCategory.Network,
                "translationSocket.sendFailed",
                new Dictionary<string, string>(),
                RecoveryAction.Retry)),
            new TaskCompletionSource().Task);

        Assert.AreEqual(SessionCloseCompletion.Failed, outcome.Completion);
        Assert.AreEqual("translationSocket.sendFailed", outcome.Error!.Code);
        Assert.IsFalse(outcome.Error.ToString()!.Contains(secret, StringComparison.Ordinal));
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public void CloseRejectsAGenerationThatWasNeverActivated()
    {
        SessionCloseCoordinator coordinator = new(
            new FakeClock(),
            static _ => { });

        Assert.ThrowsExactly<InvalidOperationException>(
            () => coordinator.CloseAsync(
                9,
                static _ => ValueTask.FromResult<RuntimeError?>(null),
                Task.CompletedTask));
    }

    private static async Task AssertSingleGenerationCaseAsync(
        JsonElement input,
        JsonElement expected,
        string name)
    {
        Assert.AreEqual(
            SessionCloseCoordinator.DeadlineMilliseconds,
            input.GetProperty("deadlineMs").GetInt32(),
            name);
        long generation = input.GetProperty("generation").GetInt64();
        FakeClock clock = new();
        int releases = 0;
        SessionCloseCoordinator coordinator = new(
            clock,
            _ => Interlocked.Increment(ref releases));
        coordinator.Activate(generation);
        TaskCompletionSource remoteClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource blockedSend =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool sendBlocked = input.TryGetProperty("closeSend", out JsonElement closeSend)
            && closeSend.GetString() == "blocked";
        Task<SessionCloseOutcome> close = coordinator.CloseAsync(
            generation,
            sendBlocked
                ? _ => new ValueTask<RuntimeError?>(blockedSend.Task.ContinueWith(
                    static _ => (RuntimeError?)null,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default))
                : static _ => ValueTask.FromResult<RuntimeError?>(null),
            remoteClosed.Task);

        Assert.AreEqual(0, clock.DelayStartedAtMs, name);
        if (input.TryGetProperty("closeCallerCount", out JsonElement callerCount))
        {
            Assert.AreEqual(2, callerCount.GetInt32(), name);
            Task<SessionCloseOutcome> second = coordinator.CloseAsync(
                generation,
                static _ => throw new InvalidOperationException(),
                Task.CompletedTask);
            Assert.AreSame(close, second, name);
        }

        if (input.TryGetProperty("sessionClosedAtMs", out JsonElement closedAt))
        {
            clock.AdvanceTo(closedAt.GetInt32() - 1);
            Assert.IsFalse(close.IsCompleted, name);
            clock.AdvanceTo(closedAt.GetInt32());
            remoteClosed.SetResult();
        }
        else
        {
            clock.AdvanceTo(expected.GetProperty("completionAtMs").GetInt32());
        }

        SessionCloseOutcome outcome = await close;
        Assert.AreEqual(
            expected.GetProperty("completion").GetString() == "closed"
                ? SessionCloseCompletion.Closed
                : SessionCloseCompletion.CloseTimeout,
            outcome.Completion,
            name);
        Assert.AreEqual(expected.GetProperty("completionAtMs").GetInt32(), clock.NowMs, name);
        Assert.AreEqual(1, releases, name);
    }

    private static async Task AssertConcurrentRoutesAsync(
        JsonElement input,
        JsonElement expected,
        string name)
    {
        Assert.AreEqual(
            SessionCloseCoordinator.DeadlineMilliseconds,
            input.GetProperty("deadlineMs").GetInt32(),
            name);
        FakeClock clock = new();
        SessionCloseCoordinator inbound = new(clock, static _ => { });
        SessionCloseCoordinator outbound = new(clock, static _ => { });
        long generation = input.GetProperty("generation").GetInt64();
        inbound.Activate(generation);
        outbound.Activate(generation);
        TaskCompletionSource inboundClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource outboundClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<SessionCloseOutcome> inboundTask = inbound.CloseAsync(
            generation,
            static _ => ValueTask.FromResult<RuntimeError?>(null),
            inboundClosed.Task);
        Task<SessionCloseOutcome> outboundTask = outbound.CloseAsync(
            generation,
            static _ => ValueTask.FromResult<RuntimeError?>(null),
            outboundClosed.Task);

        Assert.AreEqual(2, clock.PendingDelayCount, name);
        JsonElement routes = expected.GetProperty("routeCompletions");
        clock.AdvanceTo(routes.GetProperty("inbound").GetInt32());
        inboundClosed.SetResult();
        clock.AdvanceTo(routes.GetProperty("outbound").GetInt32());
        outboundClosed.SetResult();

        SessionCloseOutcome[] outcomes = await Task.WhenAll(inboundTask, outboundTask);
        Assert.IsTrue(outcomes.All(static value => value.Completion == SessionCloseCompletion.Closed));
        Assert.AreEqual(expected.GetProperty("completionAtMs").GetInt32(), clock.NowMs, name);
    }

    private static async Task AssertOldGenerationIsolationAsync(
        JsonElement input,
        JsonElement expected,
        string name)
    {
        Assert.AreEqual(
            SessionCloseCoordinator.DeadlineMilliseconds,
            input.GetProperty("deadlineMs").GetInt32(),
            name);
        FakeClock clock = new();
        List<long> releasedGenerations = [];
        SessionCloseCoordinator coordinator = new(
            clock,
            generation => releasedGenerations.Add(generation));
        long closing = input.GetProperty("closingGeneration").GetInt64();
        long active = input.GetProperty("activeGeneration").GetInt64();
        coordinator.Activate(closing);
        TaskCompletionSource oldClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SessionCloseOutcome> oldClose = coordinator.CloseAsync(
            closing,
            static _ => ValueTask.FromResult<RuntimeError?>(null),
            oldClosed.Task);
        coordinator.Activate(active);

        clock.AdvanceTo(input.GetProperty("oldGenerationCompletionAtMs").GetInt32());
        oldClosed.SetResult();
        SessionCloseOutcome outcome = await oldClose;

        Assert.AreEqual(SessionCloseCompletion.Closed, outcome.Completion, name);
        Assert.AreEqual(active, coordinator.ActiveGeneration, name);
        CollectionAssert.AreEqual(new[] { closing }, releasedGenerations, name);
        Assert.IsFalse(expected.GetProperty("clearActiveGeneration").GetBoolean(), name);
    }

    private static JsonDocument LoadFixture()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth <= 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "Shared",
                "TestVectors",
                "Realtime",
                "close-deadline.json");
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllBytes(candidate));
            }
        }

        throw new FileNotFoundException(
            "Unable to locate Shared/TestVectors/Realtime/close-deadline.json.");
    }

    private sealed class FakeClock : IClock
    {
        private readonly List<(long Deadline, TaskCompletionSource Completion)> _delays = [];

        public int NowMs { get; private set; }

        public int DelayStartedAtMs { get; private set; } = -1;

        public int PendingDelayCount => _delays.Count(static item => !item.Completion.Task.IsCompleted);

        public TimeSpan MonotonicNow => TimeSpan.FromMilliseconds(NowMs);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayStartedAtMs = NowMs;
            TaskCompletionSource completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                    completion);
            }

            _delays.Add(((long)delay.TotalMilliseconds + NowMs, completion));
            return new ValueTask(completion.Task);
        }

        public void AdvanceTo(int milliseconds)
        {
            NowMs = milliseconds;
            foreach ((long deadline, TaskCompletionSource completion) in _delays)
            {
                if (deadline <= NowMs)
                {
                    completion.TrySetResult();
                }
            }
        }
    }
}
