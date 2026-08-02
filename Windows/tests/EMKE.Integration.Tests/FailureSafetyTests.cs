using System.Runtime.CompilerServices;
using EMKE.Application;
using EMKE.Core;
using EMKE.Realtime;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class FailureSafetyTests
{
    [TestMethod]
    public async Task OneHundredDeterministicSeedsKeepVirtualMicrophoneZero()
    {
        List<string> failures = [];
        HashSet<SafetyInjection> observed = [];
        SafetyIterationAudit audit = new();
        SafetyInjection[] injections = Enum.GetValues<SafetyInjection>();
        for (int seed = 0; seed < 100; seed++)
        {
            SafetyInjection injection = injections[seed % injections.Length];
            observed.Add(injection);
            try
            {
                await RunSeedAsync(seed, injection, audit)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Collect every deterministic seed failure in one diagnostic.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failures.Add(
                    $"seed={seed} injection={injection} failed: "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        CollectionAssert.AreEquivalent(
            injections,
            observed.ToArray());
        Assert.AreEqual(100, audit.RuntimeCount);
        Assert.HasCount(
            0,
            failures,
            string.Join(Environment.NewLine, failures));
    }

    private static async Task RunSeedAsync(
        int seed,
        SafetyInjection injection,
        SafetyIterationAudit audit)
    {
        MockTranslationScenario scenario =
            injection == SafetyInjection.CloseTimeout
                ? MockTranslationScenario.BlockedClose
                : MockTranslationScenario.Normal;
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(scenario)
                .ConfigureAwait(false);
        TestAudioEngine audio = new();
        FaultPlan faults = new();
        FaultInjectingSessionFactory factory = new(server, faults);
        await using TranslationRuntime runtime =
            TranslationRuntimeIntegrationTests.CreateRuntime(
                server,
                audio,
                sessionFactory: factory,
                clock: injection == SafetyInjection.CloseTimeout
                    ? null
                    : new HeldReconnectClock());
        audit.RecordRuntime();
        RuntimeError? startError =
            await runtime.StartAsync().ConfigureAwait(false);
        if (startError is not null)
        {
            throw new InvalidOperationException(
                $"{injection} could not start: {startError.Code}");
        }

        Require(
            runtime.CurrentSnapshot.OutboundRoute
                != OutboundRoute.OriginalBypass,
            "pre-failure runtime route unexpectedly used OriginalBypass");
        Require(
            audio.CurrentOutboundRoute != OutboundRoute.OriginalBypass,
            "pre-failure audio route unexpectedly used OriginalBypass");
        byte[] preFailureProbe = CreateProbe(seed, 16);
        audio.ClearVirtualMicrophone();
        await server.SendAudioDeltaAsync(
            LanguageCode.En,
            preFailureProbe).ConfigureAwait(false);
        await WaitUntilAsync(
            () => audio.VirtualMicrophoneOutput.Length
                == preFailureProbe.Length).ConfigureAwait(false);
        byte[] preFailureOutput = audio.VirtualMicrophoneOutput;
        Require(
            preFailureOutput.Length == preFailureProbe.Length,
            "pre-failure translated output length was not exact");
        Require(
            preFailureOutput.SequenceEqual(preFailureProbe),
            "pre-failure translated output did not match the service probe");
        Require(
            preFailureOutput.Any(static sample => sample != 0),
            "pre-failure translated control was unexpectedly all-zero");
        audio.ClearVirtualMicrophone();

        switch (injection)
        {
            case SafetyInjection.OutboundDisconnect:
                await server.DisconnectAsync(LanguageCode.En)
                    .ConfigureAwait(false);
                break;
            case SafetyInjection.ServerError:
                await server.SendServerErrorAsync(LanguageCode.En)
                    .ConfigureAwait(false);
                break;
            case SafetyInjection.SendFailure:
                faults.FailNextSend();
                audio.EmitCaptured(
                    AudioDirection.Outbound,
                    CreateProbe(seed, 9_600));
                break;
            case SafetyInjection.ReceiveFailure:
                faults.ArmReceiveFailure();
                await server.SendTranscriptAsync(
                    LanguageCode.En,
                    $"receive-boundary-{seed}").ConfigureAwait(false);
                await WaitForTaskAsync(
                    faults.ReceiveFailureReady,
                    "receive failure boundary was not armed")
                    .ConfigureAwait(false);
                faults.FailReceive();
                break;
            case SafetyInjection.QueueFull:
                await server.SendAudioDeltaAsync(
                    LanguageCode.En,
                    CreateProbe(seed, 4)).ConfigureAwait(false);
                break;
            case SafetyInjection.TranslatedAudioUnderrun:
#pragma warning disable CA2000 // Ownership transfers to the test audio engine event queue.
                AudioEngineEvent underrun = AudioEngineEvent.CreateControl(
                    AudioEngineEventKind.Backpressure,
                    AudioEngineStatus.QueueFull,
                    AudioEngineRoute.Translated,
                    (ulong)seed + 1);
#pragma warning restore CA2000
                audio.EmitControl(underrun);
                break;
            case SafetyInjection.CloseTimeout:
                RuntimeError? closeError =
                    await runtime.StopAsync().ConfigureAwait(false);
                if (closeError?.Category != ErrorCategory.CloseTimeout
                    || !string.Equals(
                        closeError.Code,
                        "translationRuntime.localCloseTimeout",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "CloseTimeout did not produce the stable runtime error: "
                        + $"category={closeError?.Category.ToString() ?? "none"} "
                        + $"code={closeError?.Code ?? "none"}.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(injection));
        }

        try
        {
            await WaitForTaskAsync(
                audio.FailClosedOrStopped,
                "runtime did not apply fail-closed or stopped route")
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            AppSnapshot snapshot = runtime.CurrentSnapshot;
            throw new InvalidOperationException(
                $"{exception.Message}; state={snapshot.RuntimeState}; "
                + $"route={snapshot.OutboundRoute}; "
                + $"error={snapshot.Error?.Code ?? "none"}; "
                + $"receiveConsumed={faults.ReceiveFailureConsumed}",
                exception);
        }
        Require(
            audio.CurrentOutboundRoute != OutboundRoute.OriginalBypass,
            "post-failure route unexpectedly used OriginalBypass");
        byte[] postFailureProbe = CreateProbe(seed + 100, 16);
        audio.ClearVirtualMicrophone();
        audio.RenderVirtualMicrophone(postFailureProbe);
        byte[] postFailureOutput = audio.VirtualMicrophoneOutput;
        Require(
            postFailureOutput.Length == postFailureProbe.Length,
            "post-failure virtual microphone output length was not exact");
        Require(
            postFailureOutput.All(static sample => sample == 0),
            "post-failure virtual microphone output was not all-zero");

        if (runtime.CurrentSnapshot.RuntimeState != RuntimeState.Stopped)
        {
            _ = await runtime.StopAsync().ConfigureAwait(false);
        }
    }

    private static byte[] CreateProbe(int seed, int length)
    {
        byte[] probe = new byte[length];
        for (int index = 0; index < probe.Length; index++)
        {
            probe[index] = (byte)(((seed + 1) * 31 + index) % 255 + 1);
        }

        return probe;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Timed out waiting for deterministic safety state.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task WaitForTaskAsync(Task task, string timeoutMessage)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                timeoutMessage,
                exception);
        }
    }

    private enum SafetyInjection
    {
        OutboundDisconnect,
        ServerError,
        SendFailure,
        ReceiveFailure,
        QueueFull,
        TranslatedAudioUnderrun,
        CloseTimeout,
    }

    private sealed class SafetyIterationAudit
    {
        private int _runtimeCount;

        public int RuntimeCount => Volatile.Read(ref _runtimeCount);

        public void RecordRuntime()
        {
            Interlocked.Increment(ref _runtimeCount);
        }
    }

    private sealed class HeldReconnectClock : IClock
    {
        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FaultPlan
    {
        private readonly TaskCompletionSource _receiveFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _receiveFailureReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _receiveFailureArmed;
        private int _sendFailure;
        private int _receiveFailureConsumed;

        public Task ReceiveFailure => _receiveFailure.Task;

        public Task ReceiveFailureReady => _receiveFailureReady.Task;

        public bool ReceiveFailureArmed =>
            Volatile.Read(ref _receiveFailureArmed) != 0;

        public bool ReceiveFailureConsumed =>
            Volatile.Read(ref _receiveFailureConsumed) != 0;

        public void FailNextSend()
        {
            Interlocked.Exchange(ref _sendFailure, 1);
        }

        public bool ConsumeSendFailure()
        {
            return Interlocked.Exchange(ref _sendFailure, 0) != 0;
        }

        public void FailReceive()
        {
            _receiveFailure.TrySetResult();
        }

        public void ArmReceiveFailure()
        {
            Interlocked.Exchange(ref _receiveFailureArmed, 1);
        }

        public void MarkReceiveFailureReady()
        {
            _receiveFailureReady.TrySetResult();
        }

        public bool ConsumeReceiveFailure()
        {
            return Interlocked.Exchange(
                ref _receiveFailureConsumed,
                1) == 0;
        }

    }

    private sealed class FaultInjectingSessionFactory(
        MockTranslationServer server,
        FaultPlan faults) : ITranslationSessionFactory
    {
        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            TranslationSessionConfiguration configuration = request.Configuration;
#pragma warning disable CA2000 // Ownership transfers to ChannelSupervisor.
            TranslationSession inner = new(
                server.ResolveUri(configuration.Model),
                configuration);
            ITranslationSession session =
                configuration.TargetLanguage == LanguageCode.En
                    ? new FaultInjectingSession(inner, faults)
                    : inner;
#pragma warning restore CA2000
            return ValueTask.FromResult(session);
        }
    }

    private sealed class FaultInjectingSession(
        TranslationSession inner,
        FaultPlan faults) :
        ITranslationSession,
        IAsyncDisposable
    {
        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return inner.ConnectAsync(cancellationToken);
        }

        public ValueTask SendPcmAsync(
            ReadOnlyMemory<byte> pcm,
            CancellationToken cancellationToken)
        {
            if (faults.ConsumeSendFailure())
            {
                throw new TranslationSessionException(new RuntimeError(
                    ErrorCategory.Network,
                    "translationSocket.sendFailed",
                    new Dictionary<string, string>(),
                    RecoveryAction.Retry));
            }

            return inner.SendPcmAsync(pcm, cancellationToken);
        }

        public IAsyncEnumerable<TranslationSessionEvent> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            return ReceiveCoreAsync(cancellationToken);
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            return inner.CloseAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
        }

        private async IAsyncEnumerable<TranslationSessionEvent> ReceiveCoreAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using IAsyncEnumerator<TranslationSessionEvent> events =
                inner.ReceiveAsync(cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                if (faults.ReceiveFailureArmed
                    && !faults.ReceiveFailureConsumed)
                {
                    faults.MarkReceiveFailureReady();
                    await faults.ReceiveFailure
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (faults.ConsumeReceiveFailure())
                    {
                        throw new IOException(
                            "Deterministic receive failure.");
                    }
                }

                Task<bool> move = events.MoveNextAsync().AsTask();
                if (!await move.ConfigureAwait(false))
                {
                    yield break;
                }

                yield return events.Current;
            }
        }
    }
}

#pragma warning restore CA2007
#pragma warning restore CA1515
