using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using EMKE.Core;
using EMKE.Realtime;

namespace EMKE.Application.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.
#pragma warning disable CA1861 // Expected stage vectors are intentionally local to the assertion.

[TestClass]
public sealed class TranslationConnectionProbeTests
{
    private static readonly TranslationSessionConfiguration Inbound =
        new(LanguageCode.En, LanguageCode.Zh, "translation-model");

    private static readonly TranslationSessionConfiguration Outbound =
        new(LanguageCode.Zh, LanguageCode.En, "translation-model");

    [TestMethod]
    public async Task ConnectionProbeReportsSevenStableStagesWithoutClaimingUntestedAudio()
    {
        EvidenceSession inbound = new();
        EvidenceSession outbound = new();
        TranslationConnectionProbe probe = new(
            new QueueSessionFactory(inbound, outbound),
            TimeSpan.FromMilliseconds(250));

        TranslationCompatibilityReport report =
            await probe.RunAsync(Request(Inbound), Request(Outbound), CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "authentication",
                "translationWebSocketHandshake",
                "targetLanguageUpdate",
                "dualSessionConcurrency",
                "sourceTranscript",
                "translatedAudio",
                "safeClose",
            },
            report.Stages.Select(static stage => stage.StableName).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                TranslationCapabilityOutcome.Passed,
                TranslationCapabilityOutcome.Passed,
                TranslationCapabilityOutcome.Passed,
                TranslationCapabilityOutcome.Passed,
                TranslationCapabilityOutcome.RequiresInteractiveAudio,
                TranslationCapabilityOutcome.RequiresInteractiveAudio,
                TranslationCapabilityOutcome.Passed,
            },
            report.Stages.Select(static stage => stage.Outcome).ToArray());
        Assert.AreEqual(
            TranslationCompatibilityOverall.ProtocolCompatibleRequiresAudio,
            report.Overall);
        Assert.AreEqual(1, inbound.CloseCount);
        Assert.AreEqual(1, outbound.CloseCount);
    }

    [TestMethod]
    public async Task GenericChatSessionSuccessCannotPassTranslationHandshake()
    {
        GenericChatSession inbound = new();
        GenericChatSession outbound = new();
        TranslationConnectionProbe probe = new(
            new QueueSessionFactory(inbound, outbound),
            TimeSpan.FromMilliseconds(250));

        TranslationCompatibilityReport report =
            await probe.RunAsync(Request(Inbound), Request(Outbound), CancellationToken.None);

        Assert.AreEqual(
            TranslationCapabilityOutcome.Passed,
            report.Stage("authentication").Outcome);
        Assert.AreEqual(
            TranslationCapabilityOutcome.Failed,
            report.Stage("translationWebSocketHandshake").Outcome);
        Assert.AreEqual(
            TranslationCapabilityOutcome.NotRun,
            report.Stage("targetLanguageUpdate").Outcome);
        Assert.AreEqual(
            TranslationCompatibilityOverall.Incompatible,
            report.Overall);
    }

    [TestMethod]
    public async Task ProbeConnectsBothDirectionsConcurrently()
    {
        ConcurrentConnectGate gate = new();
        EvidenceSession inbound = new(gate);
        EvidenceSession outbound = new(gate);
        TranslationConnectionProbe probe = new(
            new QueueSessionFactory(inbound, outbound),
            TimeSpan.FromMilliseconds(250));

        Task<TranslationCompatibilityReport> running =
            probe.RunAsync(Request(Inbound), Request(Outbound), CancellationToken.None);
        await gate.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Release.TrySetResult();
        TranslationCompatibilityReport report = await running;

        Assert.AreEqual(
            TranslationCapabilityOutcome.Passed,
            report.Stage("dualSessionConcurrency").Outcome);
        Assert.IsTrue(inbound.ConnectCompleted);
        Assert.IsTrue(outbound.ConnectCompleted);
    }

    [TestMethod]
    public async Task ProbeBoundsSafeCloseAndDisposesEveryCreatedSession()
    {
        EvidenceSession inbound = new(blockClose: true);
        EvidenceSession outbound = new(blockClose: true);
        TranslationConnectionProbe probe = new(
            new QueueSessionFactory(inbound, outbound),
            TimeSpan.FromMilliseconds(40));

        TranslationCompatibilityReport report =
            await probe.RunAsync(Request(Inbound), Request(Outbound), CancellationToken.None);

        Assert.AreEqual(
            TranslationCapabilityOutcome.Failed,
            report.Stage("safeClose").Outcome);
        Assert.AreEqual(1, inbound.DisposeCount);
        Assert.AreEqual(1, outbound.DisposeCount);
    }

    [TestMethod]
    public async Task ProviderCloseFailureBecomesSafeCloseStageInsteadOfEscaping()
    {
        EvidenceSession inbound = new()
        {
            CloseException = new NotSupportedException("provider detail"),
        };
        EvidenceSession outbound = new();
        TranslationConnectionProbe probe = new(
            new QueueSessionFactory(inbound, outbound),
            TimeSpan.FromMilliseconds(250));

        TranslationCompatibilityReport report =
            await probe.RunAsync(Request(Inbound), Request(Outbound), CancellationToken.None);

        Assert.AreEqual(
            TranslationCapabilityOutcome.Failed,
            report.Stage("safeClose").Outcome);
        Assert.AreEqual(
            "translationProbe.safeCloseFailed",
            report.Stage("safeClose").FailureCode);
    }

    [TestMethod]
    public async Task ProbeCreatesEachSessionFromItsOwnPublicRequest()
    {
        EvidenceSession inbound = new();
        EvidenceSession outbound = new();
        QueueSessionFactory factory = new(inbound, outbound);
        TranslationConnectionProbe probe = new(
            factory,
            TimeSpan.FromMilliseconds(250));
        TranslationSessionRequest inboundRequest = new(
            new Uri("https://translation.example.test/v1", UriKind.Absolute),
            Inbound);
        TranslationSessionRequest outboundRequest = new(
            new Uri("https://translation.example.test/v1", UriKind.Absolute),
            Outbound);

        TranslationCompatibilityReport report = await probe.RunAsync(
            inboundRequest,
            outboundRequest,
            CancellationToken.None);

        Assert.AreEqual(
            TranslationCapabilityOutcome.Passed,
            report.Stage("dualSessionConcurrency").Outcome);
        Assert.HasCount(2, factory.Requests);
        Assert.AreSame(inboundRequest, factory.Requests[0]);
        Assert.AreSame(outboundRequest, factory.Requests[1]);
    }

    [TestMethod]
    public async Task ProbePreservesTheStableTranslationSessionFailureCode()
    {
        RuntimeError expected = new(
            ErrorCategory.Authentication,
            "translationSession.authenticationFailed",
            new Dictionary<string, string>(),
            RecoveryAction.UpdateApiKey);
        TranslationConnectionProbe probe = new(
            new ThrowingSessionFactory(new TranslationSessionException(expected)));

        TranslationCompatibilityReport report =
            await probe.RunAsync(Request(Inbound), Request(Outbound), CancellationToken.None);

        Assert.AreEqual(
            expected.Code,
            report.Stage("authentication").FailureCode);
        AssertNoSensitiveFailureData(report);
    }

    [TestMethod]
    public async Task ProbeMapsIoFailuresToTheStableNetworkCode()
    {
        TranslationConnectionProbe probe = new(
            new ThrowingSessionFactory(new IOException(
                "socket https://example.test/?key=sk-test-secret-value")));

        TranslationCompatibilityReport report =
            await probe.RunAsync(Request(Inbound), Request(Outbound), CancellationToken.None);

        Assert.AreEqual(
            "translationProbe.networkFailed",
            report.Stage("authentication").FailureCode);
        AssertNoSensitiveFailureData(report);
    }

    [TestMethod]
    public async Task ProbeKeepsGenericFailuresOnTheStableConnectionCode()
    {
        TranslationConnectionProbe probe = new(
            new ThrowingSessionFactory(new InvalidOperationException(
                "provider https://example.test/?key=sk-test-secret-value")));

        TranslationCompatibilityReport report =
            await probe.RunAsync(Request(Inbound), Request(Outbound), CancellationToken.None);

        Assert.AreEqual(
            "translationProbe.connectionFailed",
            report.Stage("authentication").FailureCode);
        AssertNoSensitiveFailureData(report);
    }

    [TestMethod]
    public async Task ProbePropagatesCallerCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        TranslationConnectionProbe probe = new(
            new ThrowingSessionFactory(new InvalidOperationException()));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => probe.RunAsync(Request(Inbound), Request(Outbound), cancellation.Token));
    }

    private static TranslationSessionRequest Request(
        TranslationSessionConfiguration configuration)
    {
        return new TranslationSessionRequest(
            new Uri("https://translation.example.test/v1", UriKind.Absolute),
            configuration);
    }

    private static void AssertNoSensitiveFailureData(
        TranslationCompatibilityReport report)
    {
        Assert.IsFalse(report.Stages
            .Select(static stage => stage.FailureCode)
            .Where(static code => code is not null)
            .Any(static code => code!.Contains("sk-", StringComparison.Ordinal)));
    }

    private sealed class ThrowingSessionFactory(Exception exception)
        : ITranslationSessionFactory
    {
        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<ITranslationSession>(exception);
        }
    }

    private sealed class QueueSessionFactory(
        params ITranslationSession[] sessions) : ITranslationSessionFactory
    {
        private readonly ConcurrentQueue<ITranslationSession> _sessions =
            new(sessions);
        private readonly ConcurrentQueue<TranslationSessionRequest> _requests = new();

        public TranslationSessionRequest[] Requests => _requests.ToArray();

        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(request);
            if (!_sessions.TryDequeue(out ITranslationSession? session))
            {
                throw new InvalidOperationException("No probe session remains.");
            }

            return ValueTask.FromResult(session);
        }
    }

    private sealed class ConcurrentConnectGate
    {
        private int _entered;

        public TaskCompletionSource BothEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) == 2)
            {
                BothEntered.TrySetResult();
            }

            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class EvidenceSession :
        ITranslationSession,
        ITranslationProtocolSessionEvidence,
        IAsyncDisposable
    {
        private readonly ConcurrentConnectGate? _gate;
        private readonly bool _blockClose;
        private int _closeCount;
        private int _disposeCount;

        public EvidenceSession(
            ConcurrentConnectGate? gate = null,
            bool blockClose = false)
        {
            _gate = gate;
            _blockClose = blockClose;
        }

        public TranslationProtocolEvidence ProtocolEvidence { get; } =
            new(
                AuthenticationSucceeded: true,
                TranslationHandshakeSucceeded: true,
                TargetLanguageUpdateSucceeded: true);

        public int CloseCount => Volatile.Read(ref _closeCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool ConnectCompleted { get; private set; }

        public Exception? CloseException { get; init; }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (_gate is not null)
            {
                await _gate.EnterAsync(cancellationToken);
            }

            ConnectCompleted = true;
        }

        public ValueTask SendPcmAsync(
            ReadOnlyMemory<byte> pcm,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The protocol-only probe must not send audio.");
        }

        public async IAsyncEnumerable<TranslationSessionEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _closeCount);
            if (CloseException is not null)
            {
                throw CloseException;
            }

            if (_blockClose)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GenericChatSession : ITranslationSession
    {
        public Task ConnectAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask SendPcmAsync(
            ReadOnlyMemory<byte> pcm,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<TranslationSessionEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task CloseAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

#pragma warning restore CA1861
#pragma warning restore CA2007
#pragma warning restore CA1515
