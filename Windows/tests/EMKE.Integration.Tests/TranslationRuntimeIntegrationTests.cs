using System.Diagnostics;
using System.Net.WebSockets;
using EMKE.Application;
using EMKE.Core;
using EMKE.Realtime;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class TranslationRuntimeIntegrationTests
{
    [TestMethod]
    public async Task ExplicitOutboundBypassRendersNonZeroAdapterProbe()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        TestAudioEngine audio = new();
        RuntimeSettings bypassSettings = new(
            new Uri("https://translation.example.test/v1", UriKind.Absolute),
            LanguageCode.Zh,
            LanguageCode.En,
            "gpt-realtime-translate",
            inboundBypass: false,
            outboundBypass: true);
        await using TranslationRuntime runtime =
            CreateRuntime(server, audio, bypassSettings);
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        byte[] probe = [7, 6, 5, 4, 3, 2, 1, 9];

        audio.RenderVirtualMicrophone(probe);

        Assert.AreEqual(
            OutboundRoute.OriginalBypass,
            runtime.CurrentSnapshot.OutboundRoute);
        Assert.AreEqual(
            OutboundRoute.OriginalBypass,
            audio.CurrentOutboundRoute);
        Assert.HasCount(probe.Length, audio.VirtualMicrophoneOutput);
        CollectionAssert.AreEqual(probe, audio.VirtualMicrophoneOutput);
        Assert.IsTrue(
            audio.VirtualMicrophoneOutput.Any(static sample => sample != 0));
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TestAudioEngineOutboundQueueReportsActualFullCondition()
    {
        TestAudioEngine audio = new();
        await audio.EnqueueOutboundTranslationAsync(
            new byte[] { 1, 0 },
            CancellationToken.None).ConfigureAwait(false);

        RuntimeOperationException failure =
            await Assert.ThrowsExactlyAsync<RuntimeOperationException>(
                () => audio.EnqueueOutboundTranslationAsync(
                    new byte[] { 2, 0 },
                    CancellationToken.None).AsTask());

        Assert.AreEqual(ErrorCategory.Backpressure, failure.Error.Category);
        Assert.AreEqual("testAudioEngine.outboundQueueFull", failure.Error.Code);
    }

    [TestMethod]
    public void TestAudioEngineEventQueueRejectsWritePastCapacity()
    {
        TestAudioEngine audio = new();
        for (ulong sequence = 1; sequence <= 8; sequence++)
        {
#pragma warning disable CA2000 // Ownership transfers to the bounded test event queue.
            audio.EmitControl(AudioEngineEvent.CreateControl(
                AudioEngineEventKind.Backpressure,
                AudioEngineStatus.QueueFull,
                AudioEngineRoute.Translated,
                sequence));
#pragma warning restore CA2000
        }

#pragma warning disable CA2000 // EmitControl consumes the rejected event.
        AudioEngineEvent overflow = AudioEngineEvent.CreateControl(
            AudioEngineEventKind.Backpressure,
            AudioEngineStatus.QueueFull,
            AudioEngineRoute.Translated,
            sequence: 9);
#pragma warning restore CA2000
        InvalidOperationException failure =
            Assert.ThrowsExactly<InvalidOperationException>(
                () => audio.EmitControl(overflow));

        Assert.AreEqual(
            "The test audio event queue is full.",
            failure.Message);
    }

    [TestMethod]
    public async Task MockCaptureQueueAppliesBoundedBackpressure()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        TestAudioEngine audio = new();
        await using TranslationRuntime runtime = CreateRuntime(server, audio);
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        byte[] first = Enumerable.Repeat((byte)1, 9_600).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 9_600).ToArray();

        audio.EmitCaptured(AudioDirection.Outbound, first);
        audio.EmitCaptured(AudioDirection.Outbound, second);
        await WaitUntilAsync(
            () => server.ClientAudioBackpressureCount >= 1)
            .ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(
            1,
            server.ClientAudioBackpressureCount);
        MockClientAudioMessage firstObserved =
            await server.WaitForClientAudioAsync(LanguageCode.En)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        MockClientAudioMessage secondObserved =
            await server.WaitForClientAudioAsync(LanguageCode.En)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        CollectionAssert.AreEqual(first, firstObserved.Pcm16);
        CollectionAssert.AreEqual(second, secondObserved.Pcm16);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReconnectWindowWaiterReturnsReplacementConnection()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        await using TranslationRuntime runtime = CreateRuntime(
            server,
            new TestAudioEngine());
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        await server.DisconnectAsync(LanguageCode.En).ConfigureAwait(false);
        Task sendDuringReconnect = server.SendTranscriptAsync(
            LanguageCode.En,
            "replacement-connection-caption");
        await sendDuringReconnect.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.TranslatedCaption
                == "replacement-connection-caption").ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(3, server.TotalConnectionCount);
        Assert.AreEqual(
            "replacement-connection-caption",
            runtime.CurrentSnapshot.TranslatedCaption);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TwoLanguageStartUsesTwoSessionsAndReachesRunning()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        await using TranslationRuntime runtime = CreateRuntime(
            server,
            new TestAudioEngine());

        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        Assert.AreEqual(RuntimeState.Running, runtime.CurrentSnapshot.RuntimeState);
        Assert.AreEqual(ChannelState.Connected, runtime.CurrentSnapshot.InboundChannelState);
        Assert.AreEqual(ChannelState.Connected, runtime.CurrentSnapshot.OutboundChannelState);
        Assert.AreEqual(2, server.TotalConnectionCount);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SameLanguageSkipsOutboundSocketAndEnablesOriginalBypass()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        RuntimeSettings settings = new(
            new Uri("https://translation.example.test/v1", UriKind.Absolute),
            LanguageCode.Zh,
            LanguageCode.Zh,
            "gpt-realtime-translate",
            inboundBypass: false,
            outboundBypass: false);
        await using TranslationRuntime runtime = CreateRuntime(
            server,
            new TestAudioEngine(),
            settings);

        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        Assert.AreEqual(1, server.TotalConnectionCount);
        Assert.AreEqual(ChannelState.Bypassed, runtime.CurrentSnapshot.OutboundChannelState);
        Assert.AreEqual(OutboundRoute.OriginalBypass, runtime.CurrentSnapshot.OutboundRoute);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InputPcmUsesOne9600ByteTextJsonMessage()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        TestAudioEngine audio = new();
        await using TranslationRuntime runtime = CreateRuntime(server, audio);
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        byte[] pcm16 = Enumerable.Range(0, 9_600)
            .Select(static value => (byte)(value % 251))
            .ToArray();

        audio.EmitCaptured(AudioDirection.Outbound, pcm16);
        MockClientAudioMessage observed =
            await server.WaitForClientAudioAsync(LanguageCode.En)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(WebSocketMessageType.Text, observed.MessageType);
        Assert.HasCount(9_600, observed.Pcm16);
        CollectionAssert.AreEqual(pcm16, observed.Pcm16);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LoopbackUsesFrozenTranslationEventsAndRejectsUnregisteredAliases()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        TestAudioEngine audio = new();
        await using TranslationRuntime runtime = CreateRuntime(server, audio);
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        byte[] capturedPcm16 = Enumerable.Repeat((byte)7, 9_600).ToArray();
        byte[] inboundPcm16 = [1, 0, 2, 0];

        audio.EmitCaptured(AudioDirection.Outbound, capturedPcm16);
        await server.WaitForClientAudioAsync(LanguageCode.En)
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await server.SendAudioDeltaAsync(LanguageCode.Zh, inboundPcm16)
            .ConfigureAwait(false);
        byte[] voicedFrame = [0x00, 0x40];
        audio.EmitCaptured(AudioDirection.Inbound, voicedFrame);
        byte[] silentFrame = [0x00, 0x00];
        for (int index = 0; index < 30; index++)
        {
            audio.EmitCaptured(AudioDirection.Inbound, silentFrame);
            await Task.Delay(2).ConfigureAwait(false);
        }

        await WaitUntilAsync(() => audio.InboundTranslations.Count == 1)
            .ConfigureAwait(false);
        await server.SendTranscriptAsync(LanguageCode.Zh, "source-caption")
            .ConfigureAwait(false);
        await server.SendTranslatedTranscriptAsync(
            LanguageCode.Zh,
            "translated-caption").ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.SourceCaption == "source-caption")
            .ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.TranslatedCaption == "translated-caption")
            .ConfigureAwait(false);

        Assert.AreEqual(2, server.TotalConnectionCount);
        CollectionAssert.Contains(server.HandshakeTargets, LanguageCode.Zh);
        CollectionAssert.Contains(server.HandshakeTargets, LanguageCode.En);
        CollectionAssert.Contains(
            server.ClientEventTypes(LanguageCode.En),
            "session.input_audio_buffer.append");
        CollectionAssert.AreEqual(inboundPcm16, audio.InboundTranslations[0]);
        TranslationDecodeResult completed = TranslationEventCodec.Decode(
            """{"type":"session.completed"}"""u8.ToArray());
        TranslationDecodeResult invented = TranslationEventCodec.Decode(
            """{"type":"session.audio.delta"}"""u8.ToArray());
        Assert.IsFalse(completed.IsSuccess);
        Assert.IsNotNull(completed.Error);
        RuntimeError completedError = completed.Error;
        Assert.AreEqual("translationEvent.unknownType", completedError.Code);
        Assert.IsFalse(invented.IsSuccess);
        Assert.IsNotNull(invented.Error);
        RuntimeError inventedError = invented.Error;
        Assert.AreEqual("translationEvent.unknownType", inventedError.Code);

        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
        CollectionAssert.Contains(server.ClientEventTypes(LanguageCode.Zh), "session.close");
        CollectionAssert.Contains(server.ClientEventTypes(LanguageCode.En), "session.close");
    }

    [TestMethod]
    public async Task TranslatedOutputReachesDirectionSpecificNativeQueues()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        TestAudioEngine audio = new();
        await using TranslationRuntime runtime = CreateRuntime(server, audio);
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        byte[] inbound = [1, 0, 2, 0];
        byte[] outbound = [3, 0, 4, 0];

        await server.SendAudioDeltaAsync(LanguageCode.Zh, inbound)
            .ConfigureAwait(false);
        await server.SendAudioDeltaAsync(LanguageCode.En, outbound)
            .ConfigureAwait(false);
        byte[] voicedFrame = [0x00, 0x40];
        audio.EmitCaptured(AudioDirection.Inbound, voicedFrame);
        byte[] silentFrame = [0x00, 0x00];
        for (int index = 0; index < 30; index++)
        {
            audio.EmitCaptured(AudioDirection.Inbound, silentFrame);
            await Task.Delay(2).ConfigureAwait(false);
        }

        await WaitUntilAsync(
            () => audio.InboundTranslations.Count == 1
                && audio.OutboundTranslations.Count == 1)
            .ConfigureAwait(false);

        CollectionAssert.AreEqual(inbound, audio.InboundTranslations[0]);
        CollectionAssert.AreEqual(outbound, audio.OutboundTranslations[0]);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task OutboundServerErrorMutesVirtualMicWithoutStoppingInbound()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        await using TranslationRuntime runtime = CreateRuntime(
            server,
            new TestAudioEngine());
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        await server.SendServerErrorAsync(LanguageCode.En)
            .ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.OutboundRoute
                == OutboundRoute.MutedFailClosed).ConfigureAwait(false);

        Assert.AreEqual(ChannelState.Connected, runtime.CurrentSnapshot.InboundChannelState);
        Assert.AreEqual(InboundRoute.Translated, runtime.CurrentSnapshot.InboundRoute);
        Assert.AreEqual(RuntimeState.Degraded, runtime.CurrentSnapshot.RuntimeState);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task OutboundDisconnectReconnectsWithoutStoppingInbound()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        await using TranslationRuntime runtime = CreateRuntime(
            server,
            new TestAudioEngine());
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        await server.DisconnectAsync(LanguageCode.En).ConfigureAwait(false);
        await WaitUntilAsync(
            () => server.TotalConnectionCount >= 3
                && runtime.CurrentSnapshot.OutboundChannelState
                    == ChannelState.Connected).ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(3, server.TotalConnectionCount);
        Assert.AreEqual(ChannelState.Connected, runtime.CurrentSnapshot.OutboundChannelState);
        Assert.AreEqual(ChannelState.Connected, runtime.CurrentSnapshot.InboundChannelState);
        Assert.AreEqual(RuntimeState.Running, runtime.CurrentSnapshot.RuntimeState);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(MockTranslationScenario.Normal, 0)]
    [DataRow(MockTranslationScenario.FragmentedText, 2)]
    public async Task MockServerCompletesNormalAndFragmentedTextHandshakes(
        MockTranslationScenario scenario,
        int minimumFragmentedFrames)
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(scenario)
                .ConfigureAwait(false);
        await using TranslationSession session = CreateSession(
            server,
            LanguageCode.En,
            LanguageCode.Zh);

        await session.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(
            TranslationSessionState.Connected,
            session.State);
        Assert.IsGreaterThanOrEqualTo(
            minimumFragmentedFrames,
            server.FragmentedTextFrameCount);
        await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(
        MockTranslationScenario.Unauthorized,
        ErrorCategory.Authentication,
        "translationSocket.authenticationRejected",
        RecoveryAction.UpdateApiKey)]
    [DataRow(
        MockTranslationScenario.Forbidden,
        ErrorCategory.Authentication,
        "translationSocket.authenticationRejected",
        RecoveryAction.UpdateApiKey)]
    [DataRow(
        MockTranslationScenario.UnknownModel,
        ErrorCategory.EndpointModel,
        "translationSocket.endpointModelRejected",
        RecoveryAction.EditSettings)]
    public async Task MockServerRejectsConfiguredHandshake(
        MockTranslationScenario scenario,
        ErrorCategory expectedCategory,
        string expectedCode,
        RecoveryAction expectedRecovery)
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(scenario)
                .ConfigureAwait(false);
        await using TranslationSession session = CreateSession(
            server,
            LanguageCode.En,
            LanguageCode.Zh,
            model: scenario == MockTranslationScenario.UnknownModel
                ? "unknown-model"
                : "gpt-realtime-translate");

        TranslationSessionException failure =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => session.ConnectAsync(CancellationToken.None));

        Assert.AreEqual(expectedCategory, failure.Error.Category);
        Assert.AreEqual(expectedCode, failure.Error.Code);
        Assert.AreEqual(expectedRecovery, failure.Error.RecoveryAction);
        Assert.IsEmpty(failure.Error.Parameters);
    }

    [TestMethod]
    [DataRow(
        MockTranslationScenario.Unauthorized,
        ErrorCategory.Authentication,
        "translationSocket.authenticationRejected",
        RecoveryAction.UpdateApiKey)]
    [DataRow(
        MockTranslationScenario.Forbidden,
        ErrorCategory.Authentication,
        "translationSocket.authenticationRejected",
        RecoveryAction.UpdateApiKey)]
    [DataRow(
        MockTranslationScenario.UnknownModel,
        ErrorCategory.EndpointModel,
        "translationSocket.endpointModelRejected",
        RecoveryAction.EditSettings)]
    public async Task RejectedProductionHandshakeStopsRuntimeWithStableSafeRoute(
        MockTranslationScenario scenario,
        ErrorCategory expectedCategory,
        string expectedCode,
        RecoveryAction expectedRecovery)
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(scenario)
                .ConfigureAwait(false);
        TestAudioEngine audio = new();
        await using TranslationRuntime runtime = CreateRuntime(server, audio);

        RuntimeError? failure = await runtime.StartAsync().ConfigureAwait(false);

        Assert.AreEqual(expectedCategory, failure?.Category);
        Assert.AreEqual(expectedCode, failure?.Code);
        Assert.AreEqual(expectedRecovery, failure?.RecoveryAction);
        Assert.IsEmpty(failure!.Parameters);
        Assert.AreEqual(RuntimeState.Failed, runtime.CurrentSnapshot.RuntimeState);
        Assert.AreEqual(InboundRoute.Stopped, runtime.CurrentSnapshot.InboundRoute);
        Assert.AreEqual(OutboundRoute.Stopped, runtime.CurrentSnapshot.OutboundRoute);
        Assert.AreEqual(1, audio.StartCount);
        Assert.AreEqual(1, audio.StopCount);
    }

    [TestMethod]
    public async Task MalformedProductionBinaryHandshakeStopsRuntimeAndDrainsAudioOwnership()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(MockTranslationScenario.BinaryEvent)
                .ConfigureAwait(false);
        TestAudioEngine audio = new();
        await using TranslationRuntime runtime = CreateRuntime(server, audio);

        RuntimeError? failure = await runtime.StartAsync().ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Protocol, failure?.Category);
        Assert.AreEqual("binaryTranslationEvent", failure?.Code);
        Assert.AreEqual(RecoveryAction.Retry, failure?.RecoveryAction);
        Assert.IsEmpty(failure!.Parameters);
        Assert.AreEqual(RuntimeState.Failed, runtime.CurrentSnapshot.RuntimeState);
        Assert.AreEqual(InboundRoute.Stopped, runtime.CurrentSnapshot.InboundRoute);
        Assert.AreEqual(OutboundRoute.Stopped, runtime.CurrentSnapshot.OutboundRoute);
        Assert.AreEqual(1, audio.StartCount);
        Assert.AreEqual(1, audio.StopCount);
        Assert.AreEqual(0, audio.ActivePollCount);
        Assert.AreEqual(0, audio.PendingEventCount);
        Assert.AreEqual(0, audio.PendingOutboundTranslationCount);
        Assert.AreEqual(0, audio.ActivePcmLeaseCount);
    }

    [TestMethod]
    public async Task MockServerBinaryEventIsRejectedAsProtocolFailure()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(
                MockTranslationScenario.BinaryEvent).ConfigureAwait(false);
        await using TranslationSession session = CreateSession(
            server,
            LanguageCode.En,
            LanguageCode.Zh);

        TranslationSessionException failure =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => session.ConnectAsync(CancellationToken.None));

        Assert.AreEqual(ErrorCategory.Protocol, failure.Error.Category);
        Assert.AreEqual("binaryTranslationEvent", failure.Error.Code);
    }

    [TestMethod]
    public async Task MockServerDelaysCloseUntilExplicitRelease()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(
                MockTranslationScenario.DelayedClose).ConfigureAwait(false);
        await using TranslationSession session = CreateSession(
            server,
            LanguageCode.En,
            LanguageCode.Zh);
        await session.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false);

        Task close = session.CloseAsync(CancellationToken.None);
        await server.CloseRequestReceived.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        Assert.IsFalse(close.IsCompleted);
        server.ReleaseDelayedClose();
        await close.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MockServerBlockedCloseReachesStableCloseTimeout()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(
                MockTranslationScenario.BlockedClose).ConfigureAwait(false);
        await using TranslationSession session = CreateSession(
            server,
            LanguageCode.En,
            LanguageCode.Zh);
        await session.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false);

        TranslationSessionException failure =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => session.CloseAsync(CancellationToken.None));

        Assert.AreEqual(ErrorCategory.CloseTimeout, failure.Error.Category);
        Assert.AreEqual("translationSession.closeTimeout", failure.Error.Code);
    }

    [TestMethod]
    public async Task MockServerLateTranscriptAndAudioRemainObservableBeforeClose()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(
                MockTranslationScenario.LateDeltas).ConfigureAwait(false);
        await using TranslationSession session = CreateSession(
            server,
            LanguageCode.En,
            LanguageCode.Zh);
        await session.ConnectAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await using IAsyncEnumerator<TranslationSessionEvent> events =
            session.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();

        Task close = session.CloseAsync(CancellationToken.None);
        Assert.IsTrue(
            await events.MoveNextAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
        TranslationSessionEvent.SourceCaption transcript =
            (TranslationSessionEvent.SourceCaption)events.Current;
        Assert.AreEqual("late-transcript", transcript.Text);
        Assert.IsTrue(
            await events.MoveNextAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
        TranslationSessionEvent.AudioDelta audio =
            (TranslationSessionEvent.AudioDelta)events.Current;
        CollectionAssert.AreEqual(
            new byte[] { 1, 0, 2, 0 },
            audio.Pcm16.ToArray());
        audio.Dispose();
        await close.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LoopbackSessionsKeepInboundAndOutboundCaptionsIsolated()
    {
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync().ConfigureAwait(false);
        TestAudioEngine audio = new();
        await using TranslationRuntime runtime =
            CreateRuntime(server, audio);

        RuntimeError? startError =
            await runtime.StartAsync().ConfigureAwait(false);
        Assert.IsNull(startError);
        Assert.AreEqual(RuntimeState.Running, runtime.CurrentSnapshot.RuntimeState);

        const string inboundTranscript = "meeting-source-caption";
        await server.SendTranscriptAsync(
            LanguageCode.Zh,
            inboundTranscript).ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.SourceCaption == inboundTranscript)
            .ConfigureAwait(false);

        const string outboundTranscript = "local-outbound-caption";
        await server.SendTranscriptAsync(
            LanguageCode.En,
            outboundTranscript).ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.TranslatedCaption == outboundTranscript
                || runtime.CurrentSnapshot.SourceCaption == outboundTranscript)
            .ConfigureAwait(false);

        Assert.AreEqual(
            inboundTranscript,
            runtime.CurrentSnapshot.SourceCaption,
            "The outbound session transcript must not overwrite the inbound source caption.");
        Assert.AreEqual(
            outboundTranscript,
            runtime.CurrentSnapshot.TranslatedCaption,
            "The outbound session transcript must update only the translated caption.");
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    internal static TranslationRuntime CreateRuntime(
        MockTranslationServer server,
        TestAudioEngine audio,
        RuntimeSettings? settings = null,
        ITranslationSessionFactory? sessionFactory = null,
        IClock? clock = null)
    {
        TranslationRuntimeDependencies dependencies = new(
            new PassingWindowsBuildGate(),
            new FixedSettingsStore(settings),
            new CompatibleDriverManager(),
            new PhysicalDeviceCatalog(),
            audio,
            sessionFactory ?? new LoopbackSessionFactory(server),
            new FixedLanguageClassifier(),
            clock ?? new SystemClock(),
            new NullRuntimeLog());
        return new TranslationRuntime(dependencies);
    }

    private static TranslationSession CreateSession(
        MockTranslationServer server,
        LanguageCode sourceLanguage,
        LanguageCode targetLanguage,
        string model = "gpt-realtime-translate")
    {
        TranslationSessionConfiguration configuration = new(
            sourceLanguage,
            targetLanguage,
            model);
#pragma warning disable CA2000 // Ownership transfers to the caller.
        return new TranslationSession(
            server.ResolveUri(model),
            configuration);
#pragma warning restore CA2000
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail("Timed out waiting for the runtime observation.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private sealed class PassingWindowsBuildGate : IWindowsBuildGate
    {
        public ValueTask<RuntimeError?> CheckAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RuntimeError?>(null);
        }
    }

    private sealed class FixedSettingsStore : ISettingsStore
    {
        private readonly RuntimeSettings _settings;

        public FixedSettingsStore(RuntimeSettings? settings)
        {
            _settings = settings ?? new RuntimeSettings(
                new Uri("https://translation.example.test/v1", UriKind.Absolute),
                LanguageCode.Zh,
                LanguageCode.En,
                "gpt-realtime-translate",
                inboundBypass: false,
                outboundBypass: false);
        }

        public ValueTask<RuntimeSettings?> LoadAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RuntimeSettings?>(_settings);
        }

        public ValueTask SaveAsync(
            RuntimeSettings settings,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompatibleDriverManager : IDriverManager
    {
        public Task<DriverCompatibility> CheckCompatibilityAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DriverCompatibility(
                isCompatible: true,
                "test-compatible"));
        }
    }

    private sealed class PhysicalDeviceCatalog : IAudioDeviceCatalog
    {
        public Task<AudioDeviceSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken)
        {
            AudioDeviceSnapshot snapshot = new(
            [
                new AudioDeviceDescriptor(
                    "local-input",
                    "Local test input",
                    AudioDeviceDirection.Input,
                    isDefault: true,
                    isAvailable: true),
                new AudioDeviceDescriptor(
                    "local-output",
                    "Local test output",
                    AudioDeviceDirection.Output,
                    isDefault: true,
                    isAvailable: true),
            ]);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class LoopbackSessionFactory(MockTranslationServer server)
        : ITranslationSessionFactory
    {
        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            TranslationSessionConfiguration configuration = request.Configuration;
#pragma warning disable CA2000 // Ownership transfers to ChannelSupervisor.
            ITranslationSession session = new TranslationSession(
                server.ResolveUri(configuration.Model),
                configuration);
#pragma warning restore CA2000
            return ValueTask.FromResult(session);
        }
    }

    private sealed class FixedLanguageClassifier : ILanguageClassifier
    {
        public ValueTask<LanguageProbabilities> ClassifyAsync(
            string text,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(
                new LanguageProbabilities(1, 0, 0));
        }
    }

    private sealed class SystemClock : IClock
    {
        private static readonly double TickToSeconds =
            1.0 / Stopwatch.Frequency;

        public TimeSpan MonotonicNow =>
            TimeSpan.FromSeconds(
                Stopwatch.GetTimestamp() * TickToSeconds);

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class NullRuntimeLog : IRuntimeLog
    {
        public void Write(
            RuntimeLogLevel level,
            string eventName,
            IReadOnlyDictionary<string, string> safeFields)
        {
        }
    }
}

#pragma warning restore CA2007
#pragma warning restore CA1515
