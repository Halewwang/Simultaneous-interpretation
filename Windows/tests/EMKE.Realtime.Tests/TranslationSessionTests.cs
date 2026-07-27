using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using EMKE.Core;

namespace EMKE.Realtime.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // TranslationSession owns fake transports in these tests.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class TranslationSessionTests
{
    private static readonly Uri Endpoint =
        new("wss://api.example.test/realtime/translations?model=test");

    [TestMethod]
    public async Task SharedHandshakeFixtureIsExecutedBySessionAndCreationPolicy()
    {
        using JsonDocument fixture = LoadFixture("text-frame-handshake.json");

        foreach (JsonElement fixtureCase in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = fixtureCase.GetProperty("name").GetString()!;
            JsonElement configuration = fixtureCase.GetProperty("configuration");
            LanguageCode native = ParseLanguage(configuration.GetProperty("nativeLanguage"));
            LanguageCode meeting = ParseLanguage(configuration.GetProperty("meetingLanguage"));
            JsonElement expected = fixtureCase.GetProperty("expected");

            if (fixtureCase.TryGetProperty("sockets", out _)
                || expected.TryGetProperty("outboundChannelState", out _))
            {
                Assert.AreEqual(
                    expected.GetProperty("outboundSocketCount").GetInt32() == 1,
                    TranslationSessionCreationPolicy.RequiresOutboundSession(native, meeting),
                    name);
            }

            if (!fixtureCase.TryGetProperty("steps", out JsonElement steps))
            {
                continue;
            }

            if (steps[0].GetProperty("direction").GetString() == "local")
            {
                Assert.AreEqual(native, meeting, name);
                continue;
            }

            FakeTranslationTransport transport = new()
            {
                BlockSessionUpdate = true,
            };
            TranslationSession session = CreateSession(transport, target: native);
            Task connect = session.ConnectAsync(CancellationToken.None);

            foreach (JsonElement step in steps.EnumerateArray())
            {
                string direction = step.GetProperty("direction").GetString()!;
                string frameType = step.GetProperty("frameType").GetString()!;
                string eventType = step.GetProperty("eventType").GetString()!;
                string expectedState = step.GetProperty("expectedState").GetString()!;
                if (direction == "serverToClient")
                {
                    transport.Enqueue(Event(eventType));
                    if (expectedState == "protocolFailure")
                    {
                        TranslationSessionException exception =
                            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                                () => connect);
                        Assert.AreEqual(ErrorCategory.Protocol, exception.Error.Category, name);
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
                    await WaitUntilAsync(() => transport.SessionUpdateCount == 1, name);
                    Assert.AreEqual(
                        ParseLanguage(
                            step.GetProperty("payload").GetProperty("target_language")),
                        transport.LastUpdateLanguage,
                        name);
                    if (frameType == "binary")
                    {
                        transport.SessionUpdateError = Error(
                            ErrorCategory.Protocol,
                            "binaryTranslationEvent");
                        transport.ReleaseSessionUpdate();
                        TranslationSessionException exception =
                            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                                () => connect);
                        Assert.AreEqual(ErrorCategory.Protocol, exception.Error.Category, name);
                    }
                    else
                    {
                        Assert.AreEqual("text", frameType, name);
                        transport.ReleaseSessionUpdate();
                        await WaitUntilAsync(
                            () => session.State == ParseState(expectedState),
                            name);
                    }
                }
            }

            if (expected.GetProperty("inboundChannelState").GetString() == "connected")
            {
                await connect;
                Assert.AreEqual(TranslationSessionState.Connected, session.State, name);
            }
        }
    }

    [TestMethod]
    public async Task TwoLanguageFixtureCreatesTwoIndependentConnectedSessions()
    {
        using JsonDocument fixture = LoadFixture("text-frame-handshake.json");
        JsonElement fixtureCase = fixture.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Single(static item =>
                item.GetProperty("name").GetString()
                    == "two language setup creates two independent sockets");
        List<TranslationSession> sessions = [];
        List<FakeTranslationTransport> transports = [];

        foreach (JsonElement socketCase in fixtureCase.GetProperty("sockets").EnumerateArray())
        {
            JsonElement update = socketCase.GetProperty("steps")[1]
                .GetProperty("payload")
                .GetProperty("target_language");
            FakeTranslationTransport transport = new();
            TranslationSession session = CreateSession(transport, ParseLanguage(update));
            sessions.Add(session);
            transports.Add(transport);

            Task connect = session.ConnectAsync(CancellationToken.None);
            transport.Enqueue(Event("session.created"));
            await WaitUntilAsync(() => transport.SessionUpdateCount == 1, "session.update");
            transport.Enqueue(Event("session.updated"));
            await connect;
        }

        Assert.HasCount(2, sessions);
        Assert.AreNotSame(transports[0], transports[1]);
        Assert.IsTrue(sessions.All(static session => session.State == TranslationSessionState.Connected));
        Assert.AreEqual(LanguageCode.Zh, transports[0].LastUpdateLanguage);
        Assert.AreEqual(LanguageCode.En, transports[1].LastUpdateLanguage);
    }

    [TestMethod]
    public async Task AudioIsRejectedBeforeConnectedAndSendFailureIsStableAndSecretFree()
    {
        const string secret = "sk-1234567890abcdef";
        FakeTranslationTransport transport = new();
        TranslationSession session = CreateSession(transport);

        TranslationSessionException early =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => session.SendPcmAsync(new byte[2], CancellationToken.None).AsTask());
        Assert.AreEqual("translationSession.notConnected", early.Error.Code);

        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;
        transport.SendAudioError = new RuntimeError(
            ErrorCategory.Network,
            "translationSocket.sendFailed",
            new Dictionary<string, string>(),
            RecoveryAction.Retry);

        TranslationSessionException sendFailure =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => session.SendPcmAsync(
                    new byte[PcmFrameBatcher.FrameBytes],
                    CancellationToken.None).AsTask());

        Assert.AreEqual("translationSocket.sendFailed", sendFailure.Error.Code);
        Assert.AreEqual(TranslationSessionState.Failed, session.State);
        Assert.IsFalse(sendFailure.ToString().Contains(secret, StringComparison.Ordinal));
        Assert.HasCount(0, sendFailure.Error.Parameters);
    }

    [TestMethod]
    public async Task ReceiveLoopMapsCaptionsDoneAndPooledAudioWithConsumerOwnership()
    {
        FakeTranslationTransport transport = new();
        TrackingArrayPool pool = new();
        TranslationSession session = CreateSession(transport, pool: pool);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;

        transport.Enqueue(Event("input_audio_transcription.delta", delta: "hello"));
        transport.Enqueue(Event(
            "translation_audio.delta",
            pcm16: new byte[] { 1, 2, 3, 4 }));
        transport.Enqueue(Event("translation_audio.done"));

        await using IAsyncEnumerator<TranslationSessionEvent> events =
            session.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await events.MoveNextAsync());
        TranslationSessionEvent.SourceCaption caption =
            (TranslationSessionEvent.SourceCaption)events.Current;
        Assert.AreEqual("hello", caption.Text);
        Assert.IsFalse(caption.IsFinal);

        Assert.IsTrue(await events.MoveNextAsync());
        TranslationSessionEvent.AudioDelta audio =
            (TranslationSessionEvent.AudioDelta)events.Current;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, audio.Pcm16.ToArray());
        Assert.AreEqual(0, pool.ReturnCount);
        audio.Dispose();
        audio.Dispose();
        Assert.AreEqual(1, pool.ReturnCount);

        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsInstanceOfType<TranslationSessionEvent.Completed>(events.Current);
    }

    [TestMethod]
    public async Task BoundedChannelAppliesBackpressureAndDeliversTailBeforeCloseCompletes()
    {
        FakeTranslationTransport transport = new();
        TrackingArrayPool pool = new();
        ManualClock clock = new();
        TranslationSession session = CreateSession(
            transport,
            capacity: 1,
            pool: pool,
            clock: clock);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;

        transport.Enqueue(Event("input_audio_transcription.delta", delta: "fills-channel"));
        transport.Enqueue(Event(
            "translation_audio.delta",
            pcm16: new byte[] { 1, 2 }));
        transport.Enqueue(Event("session.closed"));
        Task close = session.CloseAsync(CancellationToken.None);
        await WaitUntilAsync(() => transport.CloseCount == 1, "close send");
        await Task.Delay(50);
        Assert.IsFalse(close.IsCompleted, "The receive loop must not drop the full-channel tail.");

        await using IAsyncEnumerator<TranslationSessionEvent> events =
            session.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsInstanceOfType<TranslationSessionEvent.SourceCaption>(events.Current);
        Assert.IsTrue(await events.MoveNextAsync());
        TranslationSessionEvent.AudioDelta tail =
            (TranslationSessionEvent.AudioDelta)events.Current;
        Assert.IsFalse(close.IsCompleted, "The tail must be observable before close completes.");
        tail.Dispose();

        await close;
        Assert.AreEqual(TranslationSessionState.Closed, session.State);
        Assert.AreEqual(1, pool.ReturnCount);
        Assert.AreEqual(1, transport.DisposeCount);
    }

    [TestMethod]
    public async Task PublicationFailureDisposesLeaseExactlyOnce()
    {
        FakeTranslationTransport transport = new();
        TrackingArrayPool pool = new();
        TranslationSession session = CreateSession(transport, capacity: 1, pool: pool);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;

        session.CompleteEventChannelForTest();
        transport.Enqueue(Event(
            "translation_audio.delta",
            pcm16: new byte[] { 1, 2 }));
        await WaitUntilAsync(() => session.State == TranslationSessionState.Failed, "publish failure");
        await WaitUntilAsync(() => pool.ReturnCount == 1, "lease return");

        Assert.AreEqual(1, pool.ReturnCount);
        Assert.AreEqual("translationSession.eventChannelClosed", session.LastError!.Code);
    }

    [TestMethod]
    public async Task UnexpectedOrderBinaryAndReceiveFailureBecomeStableProtocolOrNetworkFailures()
    {
        foreach (TranslationReceiveResult result in new[]
                 {
                     Event("session.updated"),
                     TranslationReceiveResult.Failed(Error(
                         ErrorCategory.Protocol,
                         "binaryTranslationEvent")),
                     TranslationReceiveResult.Failed(Error(
                         ErrorCategory.Network,
                         "translationSocket.receiveFailed")),
                 })
        {
            FakeTranslationTransport transport = new();
            TranslationSession session = CreateSession(transport);
            Task connect = session.ConnectAsync(CancellationToken.None);
            transport.Enqueue(result);

            TranslationSessionException exception =
                await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => connect);
            Assert.HasCount(0, exception.Error.Parameters);
            Assert.AreEqual(TranslationSessionState.Failed, session.State);
        }
    }

    [TestMethod]
    public async Task EmptyAudioDeltaBecomesProtocolFailureWithoutRentingALease()
    {
        FakeTranslationTransport transport = new();
        TrackingArrayPool pool = new();
        TranslationSession session = CreateSession(transport, pool: pool);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;

        transport.Enqueue(Event(
            "translation_audio.delta",
            pcm16: ReadOnlyMemory<byte>.Empty));
        await WaitUntilAsync(
            () => session.State == TranslationSessionState.Failed,
            "empty audio failure");

        Assert.AreEqual("translationEvent.invalidPcm16", session.LastError!.Code);
        Assert.AreEqual(0, pool.ReturnCount);
    }

    [TestMethod]
    public async Task BlockedAudioSendCannotDelayTheLocalCloseDeadline()
    {
        FakeTranslationTransport transport = new()
        {
            BlockAudioSend = true,
        };
        ManualClock clock = new();
        TranslationSession session = CreateSession(transport, clock: clock);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;

        Task audio = session.SendPcmAsync(
            new byte[PcmFrameBatcher.FrameBytes],
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => transport.AudioSendCount == 1, "audio send");

        Task close = session.CloseAsync(CancellationToken.None);
        await WaitUntilAsync(() => transport.CloseCount == 1, "close send");
        Assert.AreEqual(0, clock.DelayStartedAtMs);
        clock.AdvanceTo(SessionCloseCoordinator.DeadlineMilliseconds);

        TranslationSessionException timeout =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => close);
        Assert.AreEqual(ErrorCategory.CloseTimeout, timeout.Error.Category);
        transport.ReleaseAudioSend();
        await audio;
    }

    [TestMethod]
    public async Task ReceiveFailureWhileClosingIsSurfacedToCloseCallers()
    {
        FakeTranslationTransport transport = new();
        TranslationSession session = CreateSession(transport);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;

        Task close = session.CloseAsync(CancellationToken.None);
        transport.Enqueue(TranslationReceiveResult.Failed(Error(
            ErrorCategory.Network,
            "translationSocket.receiveFailed")));

        TranslationSessionException failure =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => close);
        Assert.AreEqual("translationSocket.receiveFailed", failure.Error.Code);
        Assert.AreEqual(TranslationSessionState.Failed, session.State);
        Assert.AreEqual(1, transport.DisposeCount);
    }

    [TestMethod]
    public async Task DuplicateConnectCloseAndDisposeAreDeterministic()
    {
        FakeTranslationTransport transport = new();
        ManualClock clock = new();
        TranslationSession session = CreateSession(transport, clock: clock);
        Task firstConnect = session.ConnectAsync(CancellationToken.None);
        Task secondConnect = session.ConnectAsync(CancellationToken.None);
        Assert.AreSame(firstConnect, secondConnect);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await firstConnect;

        Task firstClose = session.CloseAsync(CancellationToken.None);
        Task secondClose = session.CloseAsync(CancellationToken.None);
        transport.Enqueue(Event("session.closed"));
        await Task.WhenAll(firstClose, secondClose);
        session.Dispose();
        session.Dispose();

        Assert.AreEqual(1, transport.ConnectCount);
        Assert.AreEqual(1, transport.CloseCount);
        Assert.AreEqual(1, transport.DisposeCount);
    }

    private static TranslationSession CreateSession(
        FakeTranslationTransport transport,
        LanguageCode target = LanguageCode.Zh,
        int capacity = 8,
        ArrayPool<byte>? pool = null,
        IClock? clock = null)
    {
        return new TranslationSession(
            transport,
            Endpoint,
            new TranslationSessionConfiguration(LanguageCode.En, target, "test"),
            clock ?? new ManualClock(),
            capacity,
            pool ?? ArrayPool<byte>.Shared);
    }

    private static TranslationReceiveResult Event(
        string type,
        string? delta = null,
        ReadOnlyMemory<byte> pcm16 = default)
    {
        return TranslationReceiveResult.Received(
            new TranslationProtocolEvent(type, null, null, pcm16, delta, null, null));
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
            _ => throw new InvalidDataException("Unknown fixture language."),
        };
    }

    private static TranslationSessionState ParseState(string value)
    {
        return value switch
        {
            "created" => TranslationSessionState.Created,
            "updating" => TranslationSessionState.Updating,
            "connected" => TranslationSessionState.Connected,
            _ => throw new InvalidDataException("Unknown fixture state."),
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

    private static JsonDocument LoadFixture(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth <= 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "Shared",
                "TestVectors",
                "Realtime",
                fileName);
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllBytes(candidate));
            }
        }

        throw new FileNotFoundException(
            $"Unable to locate Shared/TestVectors/Realtime/{fileName}.");
    }

    private sealed class FakeTranslationTransport : ITranslationTransport
    {
        private readonly Channel<TranslationReceiveResult> _receives =
            Channel.CreateUnbounded<TranslationReceiveResult>();
        private int _disposed;

        public int ConnectCount { get; private set; }

        public int SessionUpdateCount { get; private set; }

        public int CloseCount { get; private set; }

        public int AudioSendCount { get; private set; }

        public int DisposeCount => Volatile.Read(ref _disposed);

        public LanguageCode? LastUpdateLanguage { get; private set; }

        public RuntimeError? SendAudioError { get; set; }

        public RuntimeError? SessionUpdateError { get; set; }

        public bool BlockSessionUpdate { get; init; }

        public bool BlockAudioSend { get; init; }

        private TaskCompletionSource SessionUpdateRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource AudioSendRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RuntimeError?> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            ConnectCount++;
            return Task.FromResult<RuntimeError?>(null);
        }

        public async ValueTask<RuntimeError?> SendSessionUpdateAsync(
            LanguageCode targetLanguage,
            CancellationToken cancellationToken)
        {
            SessionUpdateCount++;
            LastUpdateLanguage = targetLanguage;
            if (BlockSessionUpdate)
            {
                await SessionUpdateRelease.Task.WaitAsync(cancellationToken);
            }

            return SessionUpdateError;
        }

        public async ValueTask<RuntimeError?> SendAudioAppendAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            AudioSendCount++;
            if (BlockAudioSend)
            {
                await AudioSendRelease.Task.WaitAsync(cancellationToken);
            }

            return SendAudioError;
        }

        public ValueTask<RuntimeError?> SendSessionCloseAsync(
            CancellationToken cancellationToken)
        {
            CloseCount++;
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

        public void ReleaseSessionUpdate()
        {
            SessionUpdateRelease.TrySetResult();
        }

        public void ReleaseAudioSend()
        {
            AudioSendRelease.TrySetResult();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _receives.Writer.TryComplete();
            }
        }
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        private int _returnCount;

        public int ReturnCount => Volatile.Read(ref _returnCount);

        public override byte[] Rent(int minimumLength)
        {
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            if (clearArray)
            {
                Array.Clear(array);
            }

            Interlocked.Increment(ref _returnCount);
        }
    }

    private sealed class ManualClock : IClock
    {
        private readonly List<(long Deadline, TaskCompletionSource Completion)> _delays = [];

        public int NowMs { get; private set; }

        public int DelayStartedAtMs { get; private set; } = -1;

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
