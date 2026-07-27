using System.Net.WebSockets;
using System.Text;
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
            bool requestOutbound =
                fixtureCase.TryGetProperty("sockets", out JsonElement sockets)
                || fixtureCase.GetProperty("steps")[0]
                    .GetProperty("direction").GetString() == "local";
            TranslationSessionCreationPlan plan =
                TranslationSessionCreationPolicy.CreatePlan(
                    native,
                    meeting,
                    requestOutbound);
            Assert.AreEqual(
                expected.GetProperty("inboundSocketCount").GetInt32(),
                plan.InboundSocketCount,
                name);
            Assert.AreEqual(
                expected.GetProperty("outboundSocketCount").GetInt32(),
                plan.OutboundSocketCount,
                name);

            if (fixtureCase.TryGetProperty("sockets", out sockets))
            {
                Assert.AreEqual(2, sockets.GetArrayLength(), name);
                List<HandshakeResult> results = [];
                foreach (JsonElement socket in sockets.EnumerateArray())
                {
                    results.Add(await DriveSessionAsync(socket.GetProperty("steps"), name));
                }

                Assert.AreEqual(
                    Enum.Parse<TranslationSessionState>(
                        expected.GetProperty("inboundChannelState").GetString()!,
                        ignoreCase: true),
                    results[0].State,
                    name);
                Assert.AreEqual(
                    Enum.Parse<TranslationSessionState>(
                        expected.GetProperty("outboundChannelState").GetString()!,
                        ignoreCase: true),
                    results[1].State,
                    name);
            }
            else if (fixtureCase.GetProperty("steps")[0]
                         .GetProperty("direction").GetString() == "local")
            {
                Assert.IsTrue(plan.OutboundBypassed, name);
            }
            else
            {
                HandshakeResult result = await DriveSessionAsync(
                    fixtureCase.GetProperty("steps"),
                    name);
                Assert.AreEqual(
                    Enum.Parse<TranslationSessionState>(
                        expected.GetProperty("inboundChannelState").GetString()!,
                        ignoreCase: true),
                    result.State,
                    name);
                if (expected.TryGetProperty("errorCategory", out JsonElement errorCategory))
                {
                    Assert.AreEqual(
                        errorCategory.GetString(),
                        JsonNamingPolicy.CamelCase.ConvertName(result.ErrorCategory!.Value.ToString()),
                        name);
                }
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

    public static async Task ValidatePcmBatchingAsync(JsonElement fixture)
    {
        Assert.AreEqual("audio.pcm-batching.v1", fixture.GetProperty("fixtureId").GetString());
        Assert.AreEqual(
            PcmFrameBatcher.FrameBytes,
            fixture.GetProperty("metadata")
                .GetProperty("networkBatch")
                .GetProperty("byteCount")
                .GetInt32());
        foreach (JsonElement fixtureCase in fixture.GetProperty("cases").EnumerateArray())
        {
            PcmFrameBatcher batcher = new();
            List<int> emitted = [];
            JsonElement expected = fixtureCase.GetProperty("expected");
            foreach (JsonElement countElement in fixtureCase
                         .GetProperty("input")
                         .GetProperty("appendByteCounts")
                         .EnumerateArray())
            {
                int count = countElement.GetInt32();
                if (expected.TryGetProperty("errorCode", out JsonElement errorCode))
                {
                    PcmFrameBatcherException failure =
                        await Assert.ThrowsExactlyAsync<PcmFrameBatcherException>(
                            () => batcher.AppendAsync(
                                new byte[count],
                                CaptureAsync,
                                CancellationToken.None).AsTask());
                    Assert.AreEqual(errorCode.GetString(), failure.Error.Code);
                }
                else
                {
                    await batcher.AppendAsync(
                        new byte[count],
                        CaptureAsync,
                        CancellationToken.None);
                }
            }

            int[] expectedFrames = expected.TryGetProperty(
                "emittedFrameByteCounts",
                out JsonElement frameCounts)
                ? frameCounts.EnumerateArray()
                    .Select(static value => value.GetInt32())
                    .ToArray()
                : [];
            CollectionAssert.AreEqual(expectedFrames, emitted);
            if (expected.TryGetProperty("retainedByteCountBeforeFlush", out JsonElement before))
            {
                Assert.AreEqual(before.GetInt32(), batcher.RetainedByteCount);
                Assert.AreEqual(expected.GetProperty("discardedByteCount").GetInt32(), batcher.Stop());
                Assert.AreEqual(
                    expected.GetProperty("retainedByteCountAfterFlush").GetInt32(),
                    batcher.RetainedByteCount);
            }
            else
            {
                Assert.AreEqual(
                    expected.GetProperty("retainedByteCount").GetInt32(),
                    batcher.RetainedByteCount);
            }

            ValueTask CaptureAsync(ReadOnlyMemory<byte> frame, CancellationToken _)
            {
                emitted.Add(frame.Length);
                return ValueTask.CompletedTask;
            }
        }
    }

    private static async Task<HandshakeResult> DriveSessionAsync(
        JsonElement steps,
        string name)
    {
        JsonElement updatePayload = steps.EnumerateArray()
            .FirstOrDefault(static step =>
                step.GetProperty("eventType").GetString() == "session.update");
        LanguageCode target = updatePayload.ValueKind == JsonValueKind.Undefined
            ? LanguageCode.Zh
            : ParseLanguage(updatePayload.GetProperty("payload").GetProperty("target_language"));
        WebSocketMessageType updateFrameType = updatePayload.ValueKind == JsonValueKind.Undefined
            ? WebSocketMessageType.Text
            : ParseFrameType(updatePayload.GetProperty("frameType"));
        FixtureTransport transport = new(updateFrameType);
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
                transport.EnqueueEvent(eventType);
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
                transport.ReleaseUpdate();
                if (updateFrameType == WebSocketMessageType.Binary)
                {
                    TranslationSessionException failure =
                        await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                            () => connect);
                    Assert.AreEqual(ErrorCategory.Protocol, failure.Error.Category, name);
                    Assert.AreEqual(0, transport.SendCount, name);
                }
                else
                {
                    await WaitUntilAsync(
                        () => session.State == ParseState(expectedState),
                        name);
                    Assert.AreEqual(WebSocketMessageType.Text, transport.LastClientFrameType, name);
                    Assert.AreEqual(1, transport.SendCount, name);
                }
            }
        }

        if (steps.EnumerateArray().Last().GetProperty("expectedState").GetString() == "connected")
        {
            await connect;
            Assert.AreEqual(TranslationSessionState.Connected, session.State, name);
        }

        HandshakeResult result = new(
            session.State,
            session.LastError?.Category);
        await session.DisposeAsync();
        return result;
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
        SessionCloseCoordinator coordinator = new(clock);
        coordinator.Activate(generation);
        TaskCompletionSource remoteClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource sendCancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool blocked = input.TryGetProperty("closeSend", out JsonElement closeSend)
            && closeSend.GetString() == "blocked";
        int sendInvocationCount = 0;
        int deadlineAtSend = -1;
        Task<SessionCloseOutcome> close = coordinator.CloseAsync(
            generation,
            blocked
                ? async cancellationToken =>
                {
                    Interlocked.Increment(ref sendInvocationCount);
                    deadlineAtSend = clock.DelayStartedAtMs;
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return null;
                    }
                    catch (OperationCanceledException)
                    {
                        sendCancellationObserved.TrySetResult();
                        throw;
                    }
                }
        : _ =>
        {
            Interlocked.Increment(ref sendInvocationCount);
            deadlineAtSend = clock.DelayStartedAtMs;
            return ValueTask.FromResult<RuntimeError?>(null);
        },
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
        Assert.AreEqual(0, deadlineAtSend, name);
        if (input.TryGetProperty("closeCallerCount", out _))
        {
            Task<SessionCloseOutcome> second = coordinator.CloseAsync(
                generation,
                static _ => throw new InvalidOperationException(),
                Task.CompletedTask);
            Assert.AreSame(close, second, name);
            Assert.IsTrue(expected.GetProperty("sameCompletion").GetBoolean(), name);
        }

        if (input.TryGetProperty("sessionClosedAtMs", out JsonElement closedAt))
        {
            clock.AdvanceTo(closedAt.GetInt32());
            remoteClosed.SetResult();
        }
        else
        {
            clock.AdvanceTo(input.GetProperty("deadlineMs").GetInt32());
        }

        SessionCloseOutcome outcome = await close;
        if (blocked)
        {
            await sendCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (expected.TryGetProperty("localCompletion", out JsonElement localCompletion))
            {
                Assert.IsTrue(localCompletion.GetBoolean(), name);
            }
        }

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
        Assert.AreEqual(1, sendInvocationCount, name);
        if (expected.TryGetProperty("completionCount", out JsonElement completionCount))
        {
            int actualCompletionCount = outcome.Generation == generation ? 1 : 0;
            Assert.AreEqual(completionCount.GetInt32(), actualCompletionCount, name);
        }
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
        const int InboundCompletionMs = 300;
        const int OutboundCompletionMs = 400;
        Assert.AreEqual(
            SessionCloseCoordinator.DeadlineMilliseconds,
            input.GetProperty("deadlineMs").GetInt32(),
            name);
        long generation = input.GetProperty("generation").GetInt64();
        FixtureClock clock = new();
        SessionCloseCoordinator inbound = new(clock);
        SessionCloseCoordinator outbound = new(clock);
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
        Assert.IsFalse(inboundClose.IsCompleted, name);
        Assert.IsFalse(outboundClose.IsCompleted, name);
        Assert.IsTrue(expected.GetProperty("concurrent").GetBoolean(), name);
        clock.AdvanceTo(InboundCompletionMs);
        int actualInboundCompletionMs = clock.NowMs;
        inboundClosed.SetResult();
        clock.AdvanceTo(OutboundCompletionMs);
        int actualOutboundCompletionMs = clock.NowMs;
        outboundClosed.SetResult();

        SessionCloseOutcome[] outcomes =
            await Task.WhenAll(inboundClose, outboundClose);
        Assert.IsTrue(
            outcomes.All(static outcome =>
                outcome.Completion == SessionCloseCompletion.Closed),
            name);
        string actualCompletion = outcomes.All(static outcome =>
            outcome.Completion == SessionCloseCompletion.Closed)
            ? "closed"
            : "closeTimeout";
        Assert.AreEqual(
            expected.GetProperty("completion").GetString(),
            actualCompletion,
            name);
        Assert.AreEqual(
            expected.GetProperty("completionAtMs").GetInt32(),
            clock.NowMs,
            name);
        JsonElement completions = expected.GetProperty("routeCompletions");
        Assert.AreEqual(
            completions.GetProperty("inbound").GetInt32(),
            actualInboundCompletionMs,
            name);
        Assert.AreEqual(
            completions.GetProperty("outbound").GetInt32(),
            actualOutboundCompletionMs,
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
        SessionCloseCoordinator coordinator = new(clock);
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
        Assert.AreEqual(
            expected.GetProperty("completionGeneration").GetInt64(),
            outcome.Generation,
            name);
        Assert.AreEqual(
            expected.GetProperty("completion").GetString(),
            outcome.Completion == SessionCloseCompletion.Closed ? "closed" : "closeTimeout",
            name);
        Assert.AreEqual(activeGeneration, coordinator.ActiveGeneration, name);
        Assert.IsFalse(expected.GetProperty("clearActiveGeneration").GetBoolean(), name);
    }

    private static async Task ValidateTailDrainAsync(string name)
    {
        FixtureTransport transport = new(WebSocketMessageType.Text);
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
        transport.EnqueueEvent("session.created");
        await WaitUntilAsync(() => transport.UpdateCount == 1, name);
        transport.ReleaseUpdate();
        transport.EnqueueEvent("session.updated");
        await connect;

        transport.EnqueueEvent("translation_audio.done");
        transport.EnqueueEvent(
            "translation_audio.delta",
            new byte[] { 1, 2 });
        transport.EnqueueEvent("session.closed");
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

    private static WebSocketMessageType ParseFrameType(JsonElement value)
    {
        return value.GetString() switch
        {
            "text" => WebSocketMessageType.Text,
            "binary" => WebSocketMessageType.Binary,
            _ => throw new InvalidDataException("Unknown realtime fixture frame type."),
        };
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

    private sealed record HandshakeResult(
        TranslationSessionState State,
        ErrorCategory? ErrorCategory);

#pragma warning disable CA2213 // TranslationSocket takes ownership of the fixture adapter.

    private sealed class FixtureTransport : ITranslationTransport
    {
        private readonly WebSocketMessageType _sessionUpdateFrameType;
        private readonly FixtureClientWebSocket _adapter = new();
        private readonly TranslationSocket _socket;
        private readonly TaskCompletionSource _releaseUpdate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FixtureTransport(WebSocketMessageType sessionUpdateFrameType)
        {
            _sessionUpdateFrameType = sessionUpdateFrameType;
            _socket = new TranslationSocket(_adapter, receiveLimit: 1024);
        }

        public int UpdateCount { get; private set; }

        public WebSocketMessageType? LastClientFrameType =>
            _adapter.Sends.Count == 0
                ? null
                : _adapter.Sends[^1].MessageType;

        public int SendCount => _adapter.Sends.Count;

        public Task<RuntimeError?> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            return _socket.ConnectAsync(endpoint, cancellationToken);
        }

        public async ValueTask<RuntimeError?> SendSessionUpdateAsync(
            LanguageCode targetLanguage,
            CancellationToken cancellationToken)
        {
            UpdateCount++;
            await _releaseUpdate.Task.WaitAsync(cancellationToken);
            return await _socket.SendClientEventAsync(
                TranslationEventCodec.EncodeSessionUpdate(targetLanguage),
                _sessionUpdateFrameType,
                cancellationToken);
        }

        public ValueTask<RuntimeError?> SendAudioAppendAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            return ((ITranslationTransport)_socket).SendAudioAppendAsync(
                pcm16,
                cancellationToken);
        }

        public ValueTask<RuntimeError?> SendSessionCloseAsync(
            CancellationToken cancellationToken)
        {
            return _socket.SendSessionCloseAsync(cancellationToken);
        }

        public ValueTask<TranslationReceiveResult> ReceiveEventAsync(
            CancellationToken cancellationToken)
        {
            return _socket.ReceiveEventAsync(cancellationToken);
        }

        public void EnqueueEvent(
            string type,
            ReadOnlyMemory<byte> pcm16 = default)
        {
            string json = type == "translation_audio.delta"
                ? JsonSerializer.Serialize(new
                {
                    type,
                    delta = Convert.ToBase64String(pcm16.Span),
                })
                : JsonSerializer.Serialize(new { type });
            _adapter.EnqueueText(json);
        }

        public void ReleaseUpdate()
        {
            _releaseUpdate.TrySetResult();
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }

#pragma warning restore CA2213

    private sealed class FixtureClientWebSocket : IClientWebSocket
    {
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();

        public List<(WebSocketMessageType MessageType, bool EndOfMessage)> Sends { get; } = [];

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Sends.Add((messageType, endOfMessage));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            byte[] frame = await _frames.Reader.ReadAsync(cancellationToken);
            frame.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(
                frame.Length,
                WebSocketMessageType.Text,
                endOfMessage: true);
        }

        public void EnqueueText(string json)
        {
            Assert.IsTrue(_frames.Writer.TryWrite(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose()
        {
            _frames.Writer.TryComplete();
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
