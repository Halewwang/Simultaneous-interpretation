using System.Text.Json;
using System.Threading.Channels;
using EMKE.Core;
using EMKE.Realtime;

namespace EMKE.Contract.Tests;

#pragma warning disable CA2000 // TranslationSession takes ownership of fixture transports.
#pragma warning disable CA2007 // Test adapters intentionally use their test synchronization context.

internal static class RealtimeFixtureAdapter
{
    private static readonly Uri Endpoint =
        new("wss://api.example.test/realtime/translations?model=contract");

    public static async Task ValidateHandshakeAsync(JsonElement fixture)
    {
        Assert.AreEqual("realtime.text-frame-handshake.v1", fixture.GetProperty("fixtureId").GetString());
        foreach (JsonElement fixtureCase in fixture.GetProperty("cases").EnumerateArray())
        {
            JsonElement configuration = fixtureCase.GetProperty("configuration");
            LanguageCode native = ParseLanguage(configuration.GetProperty("nativeLanguage"));
            LanguageCode meeting = ParseLanguage(configuration.GetProperty("meetingLanguage"));
            JsonElement expected = fixtureCase.GetProperty("expected");
            string name = fixtureCase.GetProperty("name").GetString()!;

            if (fixtureCase.TryGetProperty("sockets", out JsonElement sockets))
            {
                Assert.IsTrue(
                    TranslationSessionCreationPolicy.RequiresOutboundSession(native, meeting),
                    name);
                Assert.AreEqual(2, sockets.GetArrayLength(), name);
                foreach (JsonElement socket in sockets.EnumerateArray())
                {
                    await DriveSessionAsync(socket.GetProperty("steps"), name);
                }
            }
            else if (fixtureCase.GetProperty("steps")[0]
                         .GetProperty("direction").GetString() == "local")
            {
                Assert.IsFalse(
                    TranslationSessionCreationPolicy.RequiresOutboundSession(native, meeting),
                    name);
                Assert.AreEqual(0, expected.GetProperty("outboundSocketCount").GetInt32(), name);
            }
            else
            {
                await DriveSessionAsync(fixtureCase.GetProperty("steps"), name);
            }
        }
    }

    public static async Task ValidateCloseDeadlineAsync(JsonElement fixture)
    {
        Assert.AreEqual("realtime.close-deadline.v1", fixture.GetProperty("fixtureId").GetString());
        foreach (JsonElement fixtureCase in fixture.GetProperty("cases").EnumerateArray())
        {
            JsonElement input = fixtureCase.GetProperty("input");
            JsonElement expected = fixtureCase.GetProperty("expected");
            string name = fixtureCase.GetProperty("name").GetString()!;
            if (input.TryGetProperty("closeRequests", out _))
            {
                await ValidateConcurrentCloseAsync(input, expected, name);
            }
            else if (input.TryGetProperty("closingGeneration", out _))
            {
                await ValidateGenerationIsolationAsync(input, expected, name);
            }
            else
            {
                await ValidateOneCloseAsync(input, expected, name);
            }
        }
    }

    private static async Task DriveSessionAsync(JsonElement steps, string name)
    {
        FixtureTransport transport = new();
        JsonElement updatePayload = steps.EnumerateArray()
            .FirstOrDefault(static step =>
                step.GetProperty("eventType").GetString() == "session.update");
        LanguageCode target = updatePayload.ValueKind == JsonValueKind.Undefined
            ? LanguageCode.Zh
            : ParseLanguage(updatePayload.GetProperty("payload").GetProperty("target_language"));
        TranslationSession session = new(
            transport,
            Endpoint,
            new TranslationSessionConfiguration(LanguageCode.En, target, "contract"),
            new FixtureClock(),
            eventCapacity: 4,
            System.Buffers.ArrayPool<byte>.Shared);
        Task connect = session.ConnectAsync(CancellationToken.None);

        foreach (JsonElement step in steps.EnumerateArray())
        {
            string direction = step.GetProperty("direction").GetString()!;
            string eventType = step.GetProperty("eventType").GetString()!;
            string expectedState = step.GetProperty("expectedState").GetString()!;
            if (direction == "serverToClient")
            {
                transport.Enqueue(Event(eventType));
                if (expectedState == "protocolFailure")
                {
                    TranslationSessionException failure =
                        await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                            () => connect);
                    Assert.AreEqual(ErrorCategory.Protocol, failure.Error.Category, name);
                }
                else
                {
                    await WaitUntilAsync(
                        () => session.State == ParseState(expectedState),
                        name);
                }
            }
            else
            {
                await WaitUntilAsync(() => transport.UpdateCount == 1, name);
                if (step.GetProperty("frameType").GetString() == "binary")
                {
                    transport.UpdateError = Error(
                        ErrorCategory.Protocol,
                        "binaryTranslationEvent");
                    transport.ReleaseUpdate();
                    TranslationSessionException failure =
                        await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                            () => connect);
                    Assert.AreEqual(ErrorCategory.Protocol, failure.Error.Category, name);
                }
                else
                {
                    transport.ReleaseUpdate();
                    await WaitUntilAsync(
                        () => session.State == ParseState(expectedState),
                        name);
                }
            }
        }

        if (steps.EnumerateArray().Last().GetProperty("expectedState").GetString() == "connected")
        {
            await connect;
            Assert.AreEqual(TranslationSessionState.Connected, session.State, name);
        }

        session.Dispose();
    }

    private static async Task ValidateOneCloseAsync(
        JsonElement input,
        JsonElement expected,
        string name)
    {
        Assert.AreEqual(
            SessionCloseCoordinator.DeadlineMilliseconds,
            input.GetProperty("deadlineMs").GetInt32(),
            name);
        long generation = input.GetProperty("generation").GetInt64();
        FixtureClock clock = new();
        int releases = 0;
        SessionCloseCoordinator coordinator = new(
            clock,
            _ => Interlocked.Increment(ref releases));
        coordinator.Activate(generation);
        TaskCompletionSource remoteClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource blockedSend =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool blocked = input.TryGetProperty("closeSend", out JsonElement closeSend)
            && closeSend.GetString() == "blocked";
        Task<SessionCloseOutcome> close = coordinator.CloseAsync(
            generation,
            blocked
                ? _ => new ValueTask<RuntimeError?>(blockedSend.Task.ContinueWith(
                    static _ => (RuntimeError?)null,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default))
                : static _ => ValueTask.FromResult<RuntimeError?>(null),
            remoteClosed.Task);

        Assert.IsTrue(
            input.GetProperty("startDeadlineBeforeCloseSend").GetBoolean(),
            name);
        Assert.AreEqual(
            expected.TryGetProperty("deadlineStartsAtMs", out JsonElement deadlineStarted)
                ? deadlineStarted.GetInt32()
                : 0,
            clock.DelayStartedAtMs,
            name);
        if (input.TryGetProperty("closeCallerCount", out _))
        {
            Assert.AreSame(
                close,
                coordinator.CloseAsync(
                    generation,
                    static _ => throw new InvalidOperationException(),
                    Task.CompletedTask),
                name);
        }

        if (input.TryGetProperty("sessionClosedAtMs", out JsonElement closedAt))
        {
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
        Assert.AreEqual(
            expected.GetProperty("completionAtMs").GetInt32(),
            clock.NowMs,
            name);
        Assert.AreEqual(1, releases, name);
        if (input.TryGetProperty("queuedTailAudio", out JsonElement queuedTailAudio)
            && queuedTailAudio.GetBoolean())
        {
            Assert.AreEqual(
                "draining",
                expected.GetProperty("tailState").GetString(),
                name);
            await ValidateTailDrainAsync(name);
        }
    }

    private static async Task ValidateConcurrentCloseAsync(
        JsonElement input,
        JsonElement expected,
        string name)
    {
        Assert.AreEqual(
            SessionCloseCoordinator.DeadlineMilliseconds,
            input.GetProperty("deadlineMs").GetInt32(),
            name);
        long generation = input.GetProperty("generation").GetInt64();
        FixtureClock clock = new();
        SessionCloseCoordinator inbound = new(clock, static _ => { });
        SessionCloseCoordinator outbound = new(clock, static _ => { });
        inbound.Activate(generation);
        outbound.Activate(generation);
        TaskCompletionSource inboundClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource outboundClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SessionCloseOutcome> inboundClose = inbound.CloseAsync(
            generation,
            static _ => ValueTask.FromResult<RuntimeError?>(null),
            inboundClosed.Task);
        Task<SessionCloseOutcome> outboundClose = outbound.CloseAsync(
            generation,
            static _ => ValueTask.FromResult<RuntimeError?>(null),
            outboundClosed.Task);

        Assert.AreEqual(2, clock.PendingDelayCount, name);
        JsonElement completions = expected.GetProperty("routeCompletions");
        clock.AdvanceTo(completions.GetProperty("inbound").GetInt32());
        inboundClosed.SetResult();
        clock.AdvanceTo(completions.GetProperty("outbound").GetInt32());
        outboundClosed.SetResult();

        SessionCloseOutcome[] outcomes =
            await Task.WhenAll(inboundClose, outboundClose);
        Assert.IsTrue(
            outcomes.All(static outcome =>
                outcome.Completion == SessionCloseCompletion.Closed),
            name);
    }

    private static async Task ValidateGenerationIsolationAsync(
        JsonElement input,
        JsonElement expected,
        string name)
    {
        Assert.AreEqual(
            SessionCloseCoordinator.DeadlineMilliseconds,
            input.GetProperty("deadlineMs").GetInt32(),
            name);
        long oldGeneration = input.GetProperty("closingGeneration").GetInt64();
        long activeGeneration = input.GetProperty("activeGeneration").GetInt64();
        FixtureClock clock = new();
        List<long> releasedGenerations = [];
        SessionCloseCoordinator coordinator = new(
            clock,
            generation => releasedGenerations.Add(generation));
        coordinator.Activate(oldGeneration);
        TaskCompletionSource oldClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SessionCloseOutcome> oldClose = coordinator.CloseAsync(
            oldGeneration,
            static _ => ValueTask.FromResult<RuntimeError?>(null),
            oldClosed.Task);
        coordinator.Activate(activeGeneration);
        clock.AdvanceTo(input.GetProperty("oldGenerationCompletionAtMs").GetInt32());
        oldClosed.SetResult();

        SessionCloseOutcome outcome = await oldClose;
        Assert.AreEqual(oldGeneration, outcome.Generation, name);
        Assert.AreEqual(activeGeneration, coordinator.ActiveGeneration, name);
        CollectionAssert.AreEqual(new[] { oldGeneration }, releasedGenerations, name);
        Assert.IsFalse(expected.GetProperty("clearActiveGeneration").GetBoolean(), name);
    }

    private static async Task ValidateTailDrainAsync(string name)
    {
        FixtureTransport transport = new();
        TranslationSession session = new(
            transport,
            Endpoint,
            new TranslationSessionConfiguration(
                LanguageCode.En,
                LanguageCode.Zh,
                "contract"),
            new FixtureClock(),
            eventCapacity: 1,
            System.Buffers.ArrayPool<byte>.Shared);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        await WaitUntilAsync(() => transport.UpdateCount == 1, name);
        transport.ReleaseUpdate();
        transport.Enqueue(Event("session.updated"));
        await connect;

        transport.Enqueue(Event("translation_audio.done"));
        transport.Enqueue(Event(
            "translation_audio.delta",
            new byte[] { 1, 2 }));
        transport.Enqueue(Event("session.closed"));
        Task close = session.CloseAsync(CancellationToken.None);

        await using IAsyncEnumerator<TranslationSessionEvent> events =
            session.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await events.MoveNextAsync(), name);
        Assert.IsInstanceOfType<TranslationSessionEvent.Completed>(events.Current);
        Assert.IsTrue(await events.MoveNextAsync(), name);
        TranslationSessionEvent.AudioDelta tail =
            (TranslationSessionEvent.AudioDelta)events.Current;
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, tail.Pcm16.ToArray(), name);
        tail.Dispose();
        await close;
        Assert.AreEqual(TranslationSessionState.Closed, session.State, name);
    }

    private static TranslationReceiveResult Event(
        string type,
        ReadOnlyMemory<byte> pcm16 = default)
    {
        return TranslationReceiveResult.Received(
            new TranslationProtocolEvent(
                type,
                null,
                null,
                pcm16,
                null,
                null,
                null));
    }

    private static RuntimeError Error(ErrorCategory category, string code)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.Retry);
    }

    private static LanguageCode ParseLanguage(JsonElement value)
    {
        return value.GetString() switch
        {
            "zh" => LanguageCode.Zh,
            "en" => LanguageCode.En,
            "de" => LanguageCode.De,
            _ => throw new InvalidDataException("Unknown realtime fixture language."),
        };
    }

    private static TranslationSessionState ParseState(string value)
    {
        return value switch
        {
            "created" => TranslationSessionState.Created,
            "updating" => TranslationSessionState.Updating,
            "connected" => TranslationSessionState.Connected,
            _ => throw new InvalidDataException("Unknown realtime fixture state."),
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string message)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.IsTrue(predicate(), message);
    }

    private sealed class FixtureTransport : ITranslationTransport
    {
        private readonly Channel<TranslationReceiveResult> _receives =
            Channel.CreateUnbounded<TranslationReceiveResult>();
        private readonly TaskCompletionSource _releaseUpdate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int UpdateCount { get; private set; }

        public RuntimeError? UpdateError { get; set; }

        public Task<RuntimeError?> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            return Task.FromResult<RuntimeError?>(null);
        }

        public async ValueTask<RuntimeError?> SendSessionUpdateAsync(
            LanguageCode targetLanguage,
            CancellationToken cancellationToken)
        {
            UpdateCount++;
            await _releaseUpdate.Task.WaitAsync(cancellationToken);
            return UpdateError;
        }

        public ValueTask<RuntimeError?> SendAudioAppendAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RuntimeError?>(null);
        }

        public ValueTask<RuntimeError?> SendSessionCloseAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RuntimeError?>(null);
        }

        public ValueTask<TranslationReceiveResult> ReceiveEventAsync(
            CancellationToken cancellationToken)
        {
            return _receives.Reader.ReadAsync(cancellationToken);
        }

        public void Enqueue(TranslationReceiveResult result)
        {
            Assert.IsTrue(_receives.Writer.TryWrite(result));
        }

        public void ReleaseUpdate()
        {
            _releaseUpdate.TrySetResult();
        }

        public void Dispose()
        {
            _receives.Writer.TryComplete();
        }
    }

    private sealed class FixtureClock : IClock
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
            cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            _delays.Add(((long)delay.TotalMilliseconds + NowMs, completion));
            return new ValueTask(completion.Task);
        }

        public void AdvanceTo(int milliseconds)
        {
            NowMs = milliseconds;
            foreach ((long deadline, TaskCompletionSource completion) in _delays)
            {
                if (deadline <= milliseconds)
                {
                    completion.TrySetResult();
                }
            }
        }
    }
}
