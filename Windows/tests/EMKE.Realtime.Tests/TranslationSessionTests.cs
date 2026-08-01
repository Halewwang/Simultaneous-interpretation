using System.Buffers;
using System.Net.WebSockets;
using System.Text;
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
    public void CreationPlanOwnsOnlySocketCountsAndSameLanguageBypass()
    {
        TranslationSessionCreationPlan translated =
            TranslationSessionCreationPolicy.CreatePlan(
                LanguageCode.Zh,
                LanguageCode.En,
                requestOutbound: true);
        TranslationSessionCreationPlan bypassed =
            TranslationSessionCreationPolicy.CreatePlan(
                LanguageCode.Zh,
                LanguageCode.Zh,
                requestOutbound: true);

        Assert.AreEqual(1, translated.InboundSocketCount);
        Assert.AreEqual(1, translated.OutboundSocketCount);
        Assert.IsFalse(translated.OutboundBypassed);
        Assert.AreEqual(1, bypassed.InboundSocketCount);
        Assert.AreEqual(0, bypassed.OutboundSocketCount);
        Assert.IsTrue(bypassed.OutboundBypassed);
    }

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
                            step.GetProperty("payload")
                                .GetProperty("session")
                                .GetProperty("audio")
                                .GetProperty("output")
                                .GetProperty("language")),
                        transport.LastUpdateLanguage,
                        name);
                    if (frameType == "binary")
                    {
                        transport.SessionUpdateError =
                            TranslationClientFramePolicy.Validate(
                                System.Net.WebSockets.WebSocketMessageType.Binary);
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
                .GetProperty("session")
                .GetProperty("audio")
                .GetProperty("output")
                .GetProperty("language");
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
        await WaitUntilAsync(() => transport.DisposeCount == 1, "send failure shutdown");
        Assert.AreEqual(0, transport.ActiveReceiveCount);
        Assert.IsFalse(transport.DisposedWhileReceiveActive);
    }

    [TestMethod]
    public async Task ReceiveLoopMapsBothCaptionsAndPooledAudioWithConsumerOwnership()
    {
        FakeTranslationTransport transport = new();
        TrackingArrayPool pool = new();
        TranslationSession session = CreateSession(transport, pool: pool);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;

        transport.Enqueue(Event("session.input_transcript.delta", delta: "hello"));
        transport.Enqueue(Event("session.output_transcript.delta", delta: "你好"));
        transport.Enqueue(Event(
            "session.output_audio.delta",
            pcm16: new byte[] { 1, 2, 3, 4 }));

        await using IAsyncEnumerator<TranslationSessionEvent> events =
            session.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await events.MoveNextAsync());
        TranslationSessionEvent.SourceCaption caption =
            (TranslationSessionEvent.SourceCaption)events.Current;
        Assert.AreEqual("hello", caption.Text);
        Assert.IsFalse(caption.IsFinal);

        Assert.IsTrue(await events.MoveNextAsync());
        TranslationSessionEvent.TranslatedCaption translated =
            (TranslationSessionEvent.TranslatedCaption)events.Current;
        Assert.AreEqual("你好", translated.Text);
        Assert.IsFalse(translated.IsFinal);

        Assert.IsTrue(await events.MoveNextAsync());
        TranslationSessionEvent.AudioDelta audio =
            (TranslationSessionEvent.AudioDelta)events.Current;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, audio.Pcm16.ToArray());
        Assert.AreEqual(0, pool.ReturnCount);
        audio.Dispose();
        audio.Dispose();
        Assert.AreEqual(1, pool.ReturnCount);
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

        transport.Enqueue(Event("session.input_transcript.delta", delta: "fills-channel"));
        transport.Enqueue(Event(
            "session.output_audio.delta",
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
        Assert.AreEqual(0, transport.ActiveReceiveCount);
        Assert.IsFalse(transport.DisposedWhileReceiveActive);
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
            "session.output_audio.delta",
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
    public async Task ConnectedTransportCloseBecomesRetryableNetworkFailure()
    {
        FakeTranslationTransport transport = new();
        TranslationSession session = CreateSession(transport);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;
        await using IAsyncEnumerator<TranslationSessionEvent> events =
            session.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();

        transport.Enqueue(TranslationReceiveResult.Closed());
        TranslationSessionException failure =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => events.MoveNextAsync().AsTask());

        Assert.AreEqual(TranslationSessionState.Failed, session.State);
        Assert.AreEqual(ErrorCategory.Network, failure.Error.Category);
        Assert.AreEqual(
            "translationSession.unexpectedSocketClose",
            failure.Error.Code);
        Assert.AreEqual(RecoveryAction.Retry, failure.Error.RecoveryAction);
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
            "session.output_audio.delta",
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
        TranslationSessionException stopped =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => audio);
        Assert.AreEqual("translationSession.audioStopped", stopped.Error.Code);
        Assert.AreEqual(ErrorCategory.CloseTimeout, session.LastError!.Category);
        Assert.AreEqual("translationSession.closeTimeout", session.LastError.Code);
        Assert.AreEqual(TranslationSessionState.Failed, session.State);
        Assert.AreEqual(1, transport.DisposeCount);
        await WaitUntilAsync(
            () => transport.ActiveReceiveCount == 0,
            "receive shutdown");
        Assert.AreEqual(0, transport.ActiveReceiveCount);
        Assert.IsFalse(transport.DisposedWhileReceiveActive);
    }

    [TestMethod]
    public async Task RealSocketAudioCancellationDoesNotCompleteRemoteCloseOrReplaceTimeout()
    {
        CancelAwareClientWebSocket adapter = new();
        TranslationSocket socket = new(adapter, receiveLimit: 1024);
        ManualClock clock = new();
        TranslationSession session = CreateSession(socket, clock: clock);
        Task connect = session.ConnectAsync(CancellationToken.None);
        adapter.EnqueueText("""{"type":"session.created"}""");
        await WaitUntilAsync(() => adapter.SessionUpdateCount == 1, "session update");
        adapter.EnqueueText("""{"type":"session.updated"}""");
        await connect;

        Task audio = session.SendPcmAsync(
            new byte[PcmFrameBatcher.FrameBytes],
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => adapter.AudioSendCount == 1, "audio send");
        Task close = session.CloseAsync(CancellationToken.None);
        await adapter.AudioCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => adapter.CloseCount == 1, "close send");

        Assert.IsFalse(close.IsCompleted);
        clock.AdvanceTo(SessionCloseCoordinator.DeadlineMilliseconds);
        TranslationSessionException timeout =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => close);
        TranslationSessionException stopped =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => audio);

        Assert.AreEqual(ErrorCategory.CloseTimeout, timeout.Error.Category);
        Assert.AreEqual("translationSession.closeTimeout", timeout.Error.Code);
        Assert.AreEqual("translationSession.audioStopped", stopped.Error.Code);
        Assert.AreEqual("translationSession.closeTimeout", session.LastError!.Code);
        Assert.AreEqual(TranslationSessionState.Failed, session.State);
        Assert.AreEqual(1, adapter.MaxConcurrentSendCount);
    }

    [TestMethod]
    public async Task NonCooperativeAudioSendCannotDelayPublicClosePastDeadline()
    {
        CancelAwareClientWebSocket adapter = new()
        {
            IgnoreAudioCancellation = true,
        };
        TranslationSocket socket = new(adapter, receiveLimit: 1024);
        ManualClock clock = new();
        TranslationSession session = CreateSession(socket, clock: clock);
        Task connect = session.ConnectAsync(CancellationToken.None);
        adapter.EnqueueText("""{"type":"session.created"}""");
        await WaitUntilAsync(() => adapter.SessionUpdateCount == 1, "session update");
        adapter.EnqueueText("""{"type":"session.updated"}""");
        await connect;
        Task audio = session.SendPcmAsync(
            new byte[PcmFrameBatcher.FrameBytes],
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => adapter.AudioSendCount == 1, "audio send");
        Task close = session.CloseAsync(CancellationToken.None);

        try
        {
            clock.AdvanceTo(SessionCloseCoordinator.DeadlineMilliseconds);
            TranslationSessionException timeout =
                await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                    () => close.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.AreEqual(ErrorCategory.CloseTimeout, timeout.Error.Category);
            Assert.AreEqual("translationSession.closeTimeout", session.LastError!.Code);
            Assert.IsFalse(audio.IsCompleted);
            Assert.IsFalse(session.ShutdownTaskForTest!.IsCompleted);
            Assert.AreEqual(0, adapter.CloseCount);
            Assert.AreEqual(1, adapter.MaxConcurrentSendCount);
            Assert.AreEqual(1, adapter.DisposeCount);
        }
        finally
        {
            adapter.ReleaseAudioSend();
        }

        TranslationSessionException stopped =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => audio);
        Assert.AreEqual("translationSession.audioStopped", stopped.Error.Code);
        await session.ShutdownTaskForTest!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(session.ShutdownTaskForTest.IsFaulted);
    }

    [TestMethod]
    public async Task CallerCanceledRealSocketSendDoesNotFailSessionOrRepeatAttemptedFrame()
    {
        CancelAwareClientWebSocket adapter = new();
        TranslationSocket socket = new(adapter, receiveLimit: 1024);
        TranslationSession session = CreateSession(socket);
        Task connect = session.ConnectAsync(CancellationToken.None);
        adapter.EnqueueText("""{"type":"session.created"}""");
        await WaitUntilAsync(() => adapter.SessionUpdateCount == 1, "session update");
        adapter.EnqueueText("""{"type":"session.updated"}""");
        await connect;
        using CancellationTokenSource callerCancellation = new();

        Task canceledSend = session.SendPcmAsync(
            Enumerable.Repeat((byte)0x11, PcmFrameBatcher.FrameBytes).ToArray(),
            callerCancellation.Token).AsTask();
        await WaitUntilAsync(() => adapter.AudioSendCount == 1, "caller send");
        await callerCancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => canceledSend);
        Assert.AreEqual(TranslationSessionState.Connected, session.State);
        Assert.IsNull(session.LastError);
        Assert.AreEqual(0, session.RetainedPcmByteCountForTest);

        adapter.BlockAudioSends = false;
        await session.SendPcmAsync(
            Enumerable.Repeat((byte)0x22, PcmFrameBatcher.FrameBytes).ToArray(),
            CancellationToken.None);
        Assert.AreEqual(2, adapter.AudioSendCount);
        Assert.AreEqual(0, session.RetainedPcmByteCountForTest);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task NonCooperativeReceiveCannotDelayPublicClosePastDeadline()
    {
        FakeTranslationTransport transport = new()
        {
            IgnoreReceiveCancellation = true,
        };
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        ManualClock clock = new();
        TranslationSession session = CreateSession(transport, clock: clock);
        await session.ConnectAsync(CancellationToken.None);
        await WaitUntilAsync(() => transport.ActiveReceiveCount == 1, "blocked receive");
        Task close = session.CloseAsync(CancellationToken.None);
        await WaitUntilAsync(() => transport.CloseCount == 1, "close send");

        try
        {
            clock.AdvanceTo(SessionCloseCoordinator.DeadlineMilliseconds);
            TranslationSessionException timeout =
                await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                    () => close.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.AreEqual(ErrorCategory.CloseTimeout, timeout.Error.Category);
            Assert.AreEqual("translationSession.closeTimeout", session.LastError!.Code);
            Assert.AreEqual(1, transport.DisposeCount);
            Assert.AreEqual(1, transport.ActiveReceiveCount);
            Assert.IsFalse(session.ShutdownTaskForTest!.IsCompleted);
        }
        finally
        {
            transport.ReleaseReceive();
        }

        await session.ShutdownTaskForTest!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, transport.ActiveReceiveCount);
        Assert.IsFalse(session.ShutdownTaskForTest.IsFaulted);
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
    public async Task PartialPcmIsDiscardedOnCloseFailureAndDispose()
    {
        await AssertPartialDiscardAsync(StopPath.Close);
        await AssertPartialDiscardAsync(StopPath.Failure);
        await AssertPartialDiscardAsync(StopPath.Dispose);
    }

    [TestMethod]
    public async Task SendQueuedBeforeCloseCannotAppendAfterTheSessionStartsClosing()
    {
        FakeTranslationTransport transport = new()
        {
            BlockAudioSend = true,
        };
        TranslationSession session = CreateSession(transport);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;

        Task first = session.SendPcmAsync(
            new byte[PcmFrameBatcher.FrameBytes],
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => transport.AudioSendCount == 1, "first send");
        Task queued = session.SendPcmAsync(
            new byte[PcmFrameBatcher.FrameBytes],
            CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => session.PendingSendCountForTest == 2,
            "queued send passed connected check");

        Task close = session.CloseAsync(CancellationToken.None);
        transport.Enqueue(Event("session.closed"));
        await close;

        TranslationSessionException firstStopped =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => first);
        TranslationSessionException queuedStopped =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(() => queued);
        Assert.AreEqual("translationSession.audioStopped", firstStopped.Error.Code);
        Assert.AreEqual("translationSession.audioStopped", queuedStopped.Error.Code);
        Assert.AreEqual(1, transport.AudioSendCount);
        Assert.AreEqual(0, session.RetainedPcmByteCountForTest);
    }

    [TestMethod]
    public async Task DisposeDrainsQueuedAudioLeaseAndAwaitsReceiveBeforeTransportRelease()
    {
        FakeTranslationTransport transport = new();
        TrackingArrayPool pool = new();
        TranslationSession session = CreateSession(transport, pool: pool);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;
        transport.Enqueue(Event(
            "session.output_audio.delta",
            pcm16: new byte[] { 1, 2 }));
        await WaitUntilAsync(() => pool.RentCount == 1, "queued audio");

        await session.DisposeAsync();

        Assert.AreEqual(1, pool.ReturnCount);
        Assert.AreEqual(0, transport.ActiveReceiveCount);
        Assert.IsFalse(transport.DisposedWhileReceiveActive);
        Assert.AreEqual(1, transport.DisposeCount);
    }

    [TestMethod]
    public async Task GracefulCloseLeavesQueuedAudioOwnedByReaderUntilDisposeDrainsIt()
    {
        FakeTranslationTransport transport = new();
        TrackingArrayPool pool = new();
        TranslationSession session = CreateSession(transport, pool: pool);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;
        transport.Enqueue(Event(
            "session.output_audio.delta",
            pcm16: new byte[] { 1, 2 }));
        await WaitUntilAsync(() => pool.RentCount == 1, "queued audio");

        Task close = session.CloseAsync(CancellationToken.None);
        transport.Enqueue(Event("session.closed"));
        await close;

        Assert.AreEqual(0, pool.ReturnCount);
        await session.DisposeAsync();
        Assert.AreEqual(1, pool.ReturnCount);
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
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.AreEqual(1, transport.ConnectCount);
        Assert.AreEqual(1, transport.CloseCount);
        Assert.AreEqual(1, transport.DisposeCount);
    }

    private static async Task AssertPartialDiscardAsync(StopPath path)
    {
        FakeTranslationTransport transport = new();
        TranslationSession session = CreateSession(transport);
        Task connect = session.ConnectAsync(CancellationToken.None);
        transport.Enqueue(Event("session.created"));
        transport.Enqueue(Event("session.updated"));
        await connect;
        await session.SendPcmAsync(new byte[2400], CancellationToken.None);
        Assert.AreEqual(2400, session.RetainedPcmByteCountForTest, path.ToString());

        switch (path)
        {
            case StopPath.Close:
                Task close = session.CloseAsync(CancellationToken.None);
                transport.Enqueue(Event("session.closed"));
                await close;
                break;
            case StopPath.Failure:
                transport.Enqueue(TranslationReceiveResult.Failed(Error(
                    ErrorCategory.Network,
                    "translationSocket.receiveFailed")));
                await WaitUntilAsync(
                    () => transport.DisposeCount == 1,
                    "failure shutdown");
                break;
            case StopPath.Dispose:
                await session.DisposeAsync();
                break;
            default:
                Assert.Fail("Unknown stop path.");
                break;
        }

        Assert.AreEqual(0, session.RetainedPcmByteCountForTest, path.ToString());
        Assert.AreEqual(0, transport.ActiveReceiveCount, path.ToString());
        Assert.IsFalse(transport.DisposedWhileReceiveActive, path.ToString());
        Assert.AreEqual(1, transport.DisposeCount, path.ToString());
    }

    private static TranslationSession CreateSession(
        ITranslationTransport transport,
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
        private int _activeReceiveCount;

        public int ConnectCount { get; private set; }

        public int SessionUpdateCount { get; private set; }

        public int CloseCount { get; private set; }

        public int AudioSendCount { get; private set; }

        public int ActiveReceiveCount => Volatile.Read(ref _activeReceiveCount);

        public bool DisposedWhileReceiveActive { get; private set; }

        public int DisposeCount => Volatile.Read(ref _disposed);

        public LanguageCode? LastUpdateLanguage { get; private set; }

        public RuntimeError? SendAudioError { get; set; }

        public RuntimeError? SessionUpdateError { get; set; }

        public bool BlockSessionUpdate { get; init; }

        public bool BlockAudioSend { get; init; }

        public bool IgnoreAudioCancellation { get; init; }

        public bool IgnoreReceiveCancellation { get; init; }

        private TaskCompletionSource SessionUpdateRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource AudioSendRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource ReceiveRelease { get; } =
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
                if (IgnoreAudioCancellation)
                {
                    await AudioSendRelease.Task;
                }
                else
                {
                    await AudioSendRelease.Task.WaitAsync(cancellationToken);
                }
            }

            return SendAudioError;
        }

        public ValueTask<RuntimeError?> SendSessionCloseAsync(
            CancellationToken cancellationToken)
        {
            CloseCount++;
            return ValueTask.FromResult<RuntimeError?>(null);
        }

        public async ValueTask<TranslationReceiveResult> ReceiveEventAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activeReceiveCount);
            try
            {
                if (_receives.Reader.TryRead(out TranslationReceiveResult? received))
                {
                    return received;
                }

                if (IgnoreReceiveCancellation)
                {
                    await ReceiveRelease.Task;
                    return TranslationReceiveResult.Closed();
                }

                return await _receives.Reader.ReadAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReceiveCount);
            }
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

        public void ReleaseReceive()
        {
            ReceiveRelease.TrySetResult();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposedWhileReceiveActive = ActiveReceiveCount != 0;
                _receives.Writer.TryComplete();
            }
        }
    }

    private sealed class CancelAwareClientWebSocket : IClientWebSocket
    {
        private readonly Channel<byte[]> _receives = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource _audioSendRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeSendCount;
        private int _disposed;
        private int _maxConcurrentSendCount;

        public int SessionUpdateCount { get; private set; }

        public int AudioSendCount { get; private set; }

        public int CloseCount { get; private set; }

        public bool BlockAudioSends { get; set; } = true;

        public bool IgnoreAudioCancellation { get; init; }

        public int DisposeCount => Volatile.Read(ref _disposed);

        public int MaxConcurrentSendCount => Volatile.Read(ref _maxConcurrentSendCount);

        public TaskCompletionSource AudioCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeSendCount);
            int observed;
            while (active > (observed = Volatile.Read(ref _maxConcurrentSendCount)))
            {
                if (Interlocked.CompareExchange(
                        ref _maxConcurrentSendCount,
                        active,
                        observed) == observed)
                {
                    break;
                }
            }

            try
            {
                string json = Encoding.UTF8.GetString(payload.Span);
                if (json.Contains("\"type\":\"session.update\"", StringComparison.Ordinal))
                {
                    SessionUpdateCount++;
                    return;
                }

                if (json.Contains("\"type\":\"session.close\"", StringComparison.Ordinal))
                {
                    CloseCount++;
                    return;
                }

                AudioSendCount++;
                if (!BlockAudioSends)
                {
                    return;
                }

                try
                {
                    if (IgnoreAudioCancellation)
                    {
                        await _audioSendRelease.Task;
                    }
                    else
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    AudioCancellationObserved.TrySetResult();
                    throw;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeSendCount);
            }
        }

        public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            byte[] frame = await _receives.Reader.ReadAsync(cancellationToken);
            frame.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(
                frame.Length,
                WebSocketMessageType.Text,
                endOfMessage: true);
        }

        public void EnqueueText(string json)
        {
            Assert.IsTrue(_receives.Writer.TryWrite(Encoding.UTF8.GetBytes(json)));
        }

        public void ReleaseAudioSend()
        {
            _audioSendRelease.TrySetResult();
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
        private int _rentCount;
        private int _returnCount;

        public int RentCount => Volatile.Read(ref _rentCount);

        public int ReturnCount => Volatile.Read(ref _returnCount);

        public override byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref _rentCount);
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

    private enum StopPath
    {
        Close,
        Failure,
        Dispose,
    }
}
