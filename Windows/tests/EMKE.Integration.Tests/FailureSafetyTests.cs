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
        foreach (SafetyInjection injection in Enum.GetValues<SafetyInjection>())
        {
            int[] seeds = Enumerable.Range(0, 100)
                .Where(seed =>
                    seed % Enum.GetValues<SafetyInjection>().Length
                        == (int)injection)
                .ToArray();
            foreach (int seed in seeds)
            {
                observed.Add(injection);
            }

            try
            {
                await RunInjectionAsync(injection, seeds, failures)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Collect every deterministic seed failure in one diagnostic.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                foreach (int seed in seeds)
                {
                    failures.Add(
                        $"seed={seed} injection={injection} failed: {exception.GetType().Name}");
                }
            }
        }

        CollectionAssert.AreEquivalent(
            Enum.GetValues<SafetyInjection>(),
            observed.ToArray());
        Assert.HasCount(
            0,
            failures,
            string.Join(Environment.NewLine, failures));
    }

    private static async Task RunInjectionAsync(
        SafetyInjection injection,
        int[] seeds,
        List<string> failures)
    {
        MockTranslationScenario scenario =
            injection == SafetyInjection.CloseTimeout
                ? MockTranslationScenario.BlockedClose
                : MockTranslationScenario.Normal;
        await using MockTranslationServer server =
            await MockTranslationServer.StartAsync(scenario)
                .ConfigureAwait(false);
        TestAudioEngine audio = new();
        using FaultPlan faults = new();
        FaultInjectingSessionFactory factory = new(server, faults);
        await using TranslationRuntime runtime =
            TranslationRuntimeIntegrationTests.CreateRuntime(
                server,
                audio,
                sessionFactory: factory);
        RuntimeError? startError =
            await runtime.StartAsync().ConfigureAwait(false);
        if (startError is not null)
        {
            throw new InvalidOperationException(
                $"{injection} could not start: {startError.Code}");
        }

        int representativeSeed = seeds[0];
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
                    CreateProbe(representativeSeed, 9_600));
                break;
            case SafetyInjection.ReceiveFailure:
                faults.FailReceive();
                break;
            case SafetyInjection.QueueFull:
                audio.OutboundEnqueueException =
                    new RuntimeOperationException(new RuntimeError(
                        ErrorCategory.Backpressure,
                        "test.queueFull",
                        new Dictionary<string, string>(),
                        RecoveryAction.Retry));
                await server.SendAudioDeltaAsync(
                    LanguageCode.En,
                    CreateProbe(representativeSeed, 4)).ConfigureAwait(false);
                break;
            case SafetyInjection.TranslatedAudioUnderrun:
#pragma warning disable CA2000 // Ownership transfers to the test audio engine event queue.
                AudioEngineEvent underrun = AudioEngineEvent.CreateControl(
                    AudioEngineEventKind.Backpressure,
                    AudioEngineStatus.QueueFull,
                    AudioEngineRoute.Translated,
                    (ulong)representativeSeed + 1);
#pragma warning restore CA2000
                audio.EmitControl(underrun);
                break;
            case SafetyInjection.CloseTimeout:
                _ = await runtime.StopAsync().ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(injection));
        }

        await audio.FailClosedOrStopped.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        foreach (int seed in seeds)
        {
            audio.ClearVirtualMicrophone();
            audio.RenderVirtualMicrophone(CreateProbe(seed, 16));
            byte[] output = audio.VirtualMicrophoneOutput;
            if (output.Any(static sample => sample != 0))
            {
                failures.Add(
                    $"seed={seed} injection={injection} emitted non-zero virtual microphone audio");
            }
        }

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

    private sealed class FaultPlan : IDisposable
    {
        private readonly TaskCompletionSource _receiveFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _receiveCancellation = new();
        private int _sendFailure;
        private int _receiveFailureConsumed;

        public Task ReceiveFailure => _receiveFailure.Task;

        public CancellationToken ReceiveFailureToken =>
            _receiveCancellation.Token;

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
            _receiveCancellation.Cancel();
            _receiveFailure.TrySetResult();
        }

        public bool ConsumeReceiveFailure()
        {
            return Interlocked.Exchange(
                ref _receiveFailureConsumed,
                1) == 0;
        }

        public void Dispose()
        {
            _receiveCancellation.Dispose();
        }
    }

    private sealed class FaultInjectingSessionFactory(
        MockTranslationServer server,
        FaultPlan faults) : ITranslationSessionFactory
    {
        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionConfiguration configuration,
            CancellationToken cancellationToken)
        {
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
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    faults.ReceiveFailureToken);
            await using IAsyncEnumerator<TranslationSessionEvent> events =
                inner.ReceiveAsync(linked.Token)
                    .GetAsyncEnumerator(linked.Token);
            while (true)
            {
                Task<bool> move = events.MoveNextAsync().AsTask();
                if (!faults.ReceiveFailureConsumed)
                {
                    Task first = await Task.WhenAny(
                            move,
                            faults.ReceiveFailure)
                        .ConfigureAwait(false);
                    if ((first == faults.ReceiveFailure
                            || faults.ReceiveFailure.IsCompleted)
                        && faults.ConsumeReceiveFailure())
                    {
                        throw new IOException(
                            "Deterministic receive failure.");
                    }
                }

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
