using EMKE.Application;
using EMKE.Core;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class ProductionFailureMatrixTests
{
    private const int RequiredIterations = 100;

    [TestMethod]
    public async Task InboundSessionLossKeepsOriginalAudioFailOpenForOneHundredIterations()
    {
        SafetyAudit audit = new();
        for (int iteration = 0; iteration < RequiredIterations; iteration++)
        {
            ControlledClock clock = new();
            await using MockTranslationServer server =
                await MockTranslationServer.StartAsync().ConfigureAwait(false);
            TestAudioEngine audio = new();
            await using TranslationRuntime runtime =
                TranslationRuntimeIntegrationTests.CreateRuntime(
                    server,
                    audio,
                    clock: clock);
            SnapshotWatcher snapshots = new(runtime.CurrentSnapshot);
            using IDisposable subscription = runtime.Snapshots.Subscribe(snapshots);

            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            Task<AppSnapshot> failed = snapshots.WaitForAsync(
                static snapshot => snapshot.InboundRoute == InboundRoute.OriginalFailOpen
                    && snapshot.Error?.Category == ErrorCategory.Network);
            await server.DisconnectAsync(LanguageCode.Zh).ConfigureAwait(false);
            AppSnapshot observed = await failed.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.AreEqual("translationSocket.receiveFailed", observed.Error?.Code);
            Assert.AreEqual(RecoveryAction.Retry, observed.Error?.RecoveryAction);
            Assert.AreEqual(OutboundRoute.Translated, observed.OutboundRoute);
            AssertSafe(observed.Error);
            byte[] original = [1, 2, 3, 4];
            audio.ClearMeetingSpeaker();
            audio.RenderMeetingSpeaker(original);
            CollectionAssert.AreEqual(original, audio.MeetingSpeakerOutput);
            Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
            Assert.AreEqual(1, audio.StartCount);
            Assert.AreEqual(1, audio.StopCount);
            await AssertReleasedAsync(server, audio).ConfigureAwait(false);
            audit.RecordInboundFailOpen();
        }

        Assert.AreEqual(RequiredIterations, audit.InboundFailOpenCount);
    }

    [TestMethod]
    public async Task OutboundSessionLossKeepsVirtualMicrophoneFailClosedForOneHundredIterations()
    {
        SafetyAudit audit = new();
        for (int iteration = 0; iteration < RequiredIterations; iteration++)
        {
            await using MockTranslationServer server =
                await MockTranslationServer.StartAsync().ConfigureAwait(false);
            TestAudioEngine audio = new();
            await using TranslationRuntime runtime =
                TranslationRuntimeIntegrationTests.CreateRuntime(server, audio);
            SnapshotWatcher snapshots = new(runtime.CurrentSnapshot);
            using IDisposable subscription = runtime.Snapshots.Subscribe(snapshots);

            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            Task<AppSnapshot> failed = snapshots.WaitForAsync(
                static snapshot => snapshot.OutboundRoute == OutboundRoute.MutedFailClosed
                    && snapshot.Error?.Category == ErrorCategory.Protocol);
            await server.SendServerErrorAsync(LanguageCode.En).ConfigureAwait(false);
            AppSnapshot observed = await failed.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.AreEqual("translationSession.remoteError", observed.Error?.Code);
            Assert.AreEqual(RecoveryAction.Retry, observed.Error?.RecoveryAction);
            Assert.AreEqual(InboundRoute.Translated, observed.InboundRoute);
            AssertSafe(observed.Error);
            byte[] original = [5, 6, 7, 8];
            audio.ClearVirtualMicrophone();
            audio.RenderVirtualMicrophone(original);
            Assert.IsTrue(audio.VirtualMicrophoneOutput.All(static sample => sample == 0));
            Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
            Assert.AreEqual(1, audio.StartCount);
            Assert.AreEqual(1, audio.StopCount);
            await AssertReleasedAsync(server, audio).ConfigureAwait(false);
            audit.RecordOutboundFailClosed();
        }

        Assert.AreEqual(RequiredIterations, audit.OutboundFailClosedCount);
    }

    [TestMethod]
    public async Task ExplicitBypassRemainsExplicitAfterInboundFaultForOneHundredIterations()
    {
        SafetyAudit audit = new();
        RuntimeSettings settings = new(
            new Uri("https://translation.example.test/v1", UriKind.Absolute),
            LanguageCode.Zh,
            LanguageCode.En,
            "gpt-realtime-translate",
            inboundBypass: false,
            outboundBypass: true);
        for (int iteration = 0; iteration < RequiredIterations; iteration++)
        {
            await using MockTranslationServer server =
                await MockTranslationServer.StartAsync().ConfigureAwait(false);
            TestAudioEngine audio = new();
            await using TranslationRuntime runtime =
                TranslationRuntimeIntegrationTests.CreateRuntime(server, audio, settings);
            SnapshotWatcher snapshots = new(runtime.CurrentSnapshot);
            using IDisposable subscription = runtime.Snapshots.Subscribe(snapshots);

            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            Task<AppSnapshot> failed = snapshots.WaitForAsync(
                static snapshot => snapshot.InboundRoute == InboundRoute.OriginalFailOpen
                    && snapshot.OutboundRoute == OutboundRoute.OriginalBypass);
            await server.SendServerErrorAsync(LanguageCode.Zh).ConfigureAwait(false);
            AppSnapshot observed = await failed.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.AreEqual(ErrorCategory.Protocol, observed.Error?.Category);
            Assert.AreEqual("translationSession.remoteError", observed.Error?.Code);
            Assert.AreEqual(RecoveryAction.Retry, observed.Error?.RecoveryAction);
            AssertSafe(observed.Error);
            byte[] original = [9, 10, 11, 12];
            audio.ClearVirtualMicrophone();
            audio.RenderVirtualMicrophone(original);
            CollectionAssert.AreEqual(original, audio.VirtualMicrophoneOutput);
            Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
            Assert.AreEqual(1, audio.StartCount);
            Assert.AreEqual(1, audio.StopCount);
            await AssertReleasedAsync(server, audio).ConfigureAwait(false);
            audit.RecordExplicitBypass();
        }

        Assert.AreEqual(RequiredIterations, audit.ExplicitBypassCount);
    }

    [TestMethod]
    public async Task ReconnectResumesTranslatedRouteOnlyAfterNewHandshakeForOneHundredIterations()
    {
        SafetyAudit audit = new();
        for (int iteration = 0; iteration < RequiredIterations; iteration++)
        {
            ControlledClock clock = new();
            await using MockTranslationServer server =
                await MockTranslationServer.StartAsync().ConfigureAwait(false);
            TestAudioEngine audio = new();
            await using TranslationRuntime runtime =
                TranslationRuntimeIntegrationTests.CreateRuntime(
                    server,
                    audio,
                    clock: clock);
            SnapshotWatcher snapshots = new(runtime.CurrentSnapshot);
            using IDisposable subscription = runtime.Snapshots.Subscribe(snapshots);

            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            Task<AppSnapshot> reconnecting = snapshots.WaitForAsync(
                static snapshot => snapshot.OutboundChannelState == ChannelState.Reconnecting
                    && snapshot.OutboundRoute == OutboundRoute.MutedFailClosed);
            Task<AppSnapshot> recovered = snapshots.WaitForAsync(
                snapshot => server.TotalConnectionCount == 3
                    && snapshot.OutboundChannelState == ChannelState.Connected
                    && snapshot.OutboundRoute == OutboundRoute.Translated);
            await server.DisconnectAsync(LanguageCode.En).ConfigureAwait(false);
            AppSnapshot disconnected = await reconnecting.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            ControlledClock.DelayCall retryDelay = await clock.NextDelayAsync()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual("translationSocket.receiveFailed", disconnected.Error?.Code);
            Assert.AreEqual(RecoveryAction.Retry, disconnected.Error?.RecoveryAction);
            AssertSafe(disconnected.Error);
            byte[] whileReconnecting = [17, 18, 19, 20];
            audio.ClearVirtualMicrophone();
            audio.RenderVirtualMicrophone(whileReconnecting);
            Assert.IsTrue(audio.VirtualMicrophoneOutput.All(static sample => sample == 0));
            retryDelay.Release();
            AppSnapshot resumed = await recovered.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.IsNull(resumed.Error);
            Assert.AreEqual(3, server.TotalConnectionCount);
            Assert.AreEqual(InboundRoute.Translated, resumed.InboundRoute);
            byte[] translated = [13, 14, 15, 16];
            audio.ClearVirtualMicrophone();
            await server.SendAudioDeltaAsync(LanguageCode.En, translated).ConfigureAwait(false);
            await audio.VirtualMicrophoneTranslated
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            CollectionAssert.AreEqual(translated, audio.VirtualMicrophoneOutput);
            Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
            Assert.AreEqual(1, audio.StartCount);
            Assert.AreEqual(1, audio.StopCount);
            await AssertReleasedAsync(server, audio).ConfigureAwait(false);
            audit.RecordReconnectAfterHandshake();
        }

        Assert.AreEqual(RequiredIterations, audit.ReconnectAfterHandshakeCount);
    }

    [TestMethod]
    public async Task ShutdownCompletesBeforeControlledDeadlineForOneHundredIterations()
    {
        SafetyAudit audit = new();
        for (int iteration = 0; iteration < RequiredIterations; iteration++)
        {
            ControlledClock clock = new();
            await using MockTranslationServer server =
                await MockTranslationServer.StartAsync().ConfigureAwait(false);
            TestAudioEngine audio = new();
            await using TranslationRuntime runtime =
                TranslationRuntimeIntegrationTests.CreateRuntime(
                    server,
                    audio,
                    clock: clock);

            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            Task<RuntimeError?> stopping = runtime.StopAsync();
            _ = await clock.NextDelayAsync().WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            RuntimeError? stopError = await stopping.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.IsNull(stopError);
            Assert.AreEqual(RuntimeState.Stopped, runtime.CurrentSnapshot.RuntimeState);
            Assert.AreEqual(InboundRoute.Stopped, runtime.CurrentSnapshot.InboundRoute);
            Assert.AreEqual(OutboundRoute.Stopped, runtime.CurrentSnapshot.OutboundRoute);
            Assert.AreEqual(1, audio.StartCount);
            Assert.AreEqual(1, audio.StopCount);
            await AssertReleasedAsync(server, audio).ConfigureAwait(false);
            audit.RecordShutdown();
        }

        Assert.AreEqual(RequiredIterations, audit.ShutdownCount);
    }

    private static void AssertSafe(RuntimeError? error)
    {
        Assert.IsNotNull(error);
        Assert.IsEmpty(error.Parameters);
        Assert.IsFalse(error.Code.Contains('?', StringComparison.Ordinal));
        Assert.IsFalse(error.Code.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertReleasedAsync(
        MockTranslationServer server,
        TestAudioEngine audio)
    {
        await Task.WhenAll(
            server.WaitForConnectionClosedAsync(LanguageCode.Zh),
            server.WaitForConnectionClosedAsync(LanguageCode.En),
            audio.PollQuiesced).WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Assert.AreEqual(0, server.ActiveConnectionCount);
        Assert.AreEqual(0, audio.ActivePollCount);
        Assert.AreEqual(0, audio.PendingEventCount);
        Assert.AreEqual(0, audio.PendingOutboundTranslationCount);
        Assert.AreEqual(0, audio.ActivePcmLeaseCount);
    }

    private sealed class SafetyAudit
    {
        private int _inboundFailOpen;
        private int _outboundFailClosed;
        private int _explicitBypass;
        private int _reconnectAfterHandshake;
        private int _shutdown;

        public int InboundFailOpenCount => Volatile.Read(ref _inboundFailOpen);
        public int OutboundFailClosedCount => Volatile.Read(ref _outboundFailClosed);
        public int ExplicitBypassCount => Volatile.Read(ref _explicitBypass);
        public int ReconnectAfterHandshakeCount => Volatile.Read(ref _reconnectAfterHandshake);
        public int ShutdownCount => Volatile.Read(ref _shutdown);
        public void RecordInboundFailOpen() => Interlocked.Increment(ref _inboundFailOpen);
        public void RecordOutboundFailClosed() => Interlocked.Increment(ref _outboundFailClosed);
        public void RecordExplicitBypass() => Interlocked.Increment(ref _explicitBypass);
        public void RecordReconnectAfterHandshake() => Interlocked.Increment(ref _reconnectAfterHandshake);
        public void RecordShutdown() => Interlocked.Increment(ref _shutdown);
    }

    private sealed class ControlledClock : IClock
    {
        private readonly System.Threading.Channels.Channel<DelayCall> _delays =
            System.Threading.Channels.Channel.CreateUnbounded<DelayCall>(
                new System.Threading.Channels.UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayCall call = new(delay);
            if (!_delays.Writer.TryWrite(call))
            {
                throw new InvalidOperationException("The controlled clock is closed.");
            }

            return new ValueTask(call.WaitAsync(cancellationToken));
        }

        public async Task<DelayCall> NextDelayAsync()
        {
            return await _delays.Reader.ReadAsync().ConfigureAwait(false);
        }

        internal sealed class DelayCall(TimeSpan delay)
        {
            private readonly TaskCompletionSource _released = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public TimeSpan Delay { get; } = delay;

            public Task WaitAsync(CancellationToken cancellationToken)
            {
                return _released.Task.WaitAsync(cancellationToken);
            }

            public void Release()
            {
                _released.TrySetResult();
            }
        }
    }

    private sealed class SnapshotWatcher(AppSnapshot initial) : IObserver<AppSnapshot>
    {
        private readonly object _sync = new();
        private readonly List<Watch> _watches = [];
        private AppSnapshot _current = initial;

        public Task<AppSnapshot> WaitForAsync(Func<AppSnapshot, bool> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            lock (_sync)
            {
                if (predicate(_current))
                {
                    return Task.FromResult(_current);
                }

                Watch watch = new(predicate);
                _watches.Add(watch);
                return watch.Completion.Task;
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(AppSnapshot value)
        {
            lock (_sync)
            {
                _current = value;
                for (int index = _watches.Count - 1; index >= 0; index--)
                {
                    Watch watch = _watches[index];
                    if (!watch.Predicate(value))
                    {
                        continue;
                    }

                    _watches.RemoveAt(index);
                    watch.Completion.TrySetResult(value);
                }
            }
        }

        private sealed class Watch(Func<AppSnapshot, bool> predicate)
        {
            public Func<AppSnapshot, bool> Predicate { get; } = predicate;
            public TaskCompletionSource<AppSnapshot> Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

#pragma warning restore CA2007
#pragma warning restore CA1515
