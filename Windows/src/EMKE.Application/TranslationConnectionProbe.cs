using EMKE.Core;

namespace EMKE.Application;

public interface ITranslationConnectionProbe
{
    Task<TranslationCompatibilityReport> RunAsync(
        TranslationSessionConfiguration inbound,
        TranslationSessionConfiguration outbound,
        CancellationToken cancellationToken);
}

public sealed class TranslationConnectionProbe : ITranslationConnectionProbe
{
    private readonly ITranslationSessionFactory _sessionFactory;
    private readonly TimeSpan _closeTimeout;

    public TranslationConnectionProbe(
        ITranslationSessionFactory sessionFactory,
        TimeSpan? closeTimeout = null)
    {
        _sessionFactory =
            sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(3);
        if (_closeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(closeTimeout));
        }
    }

    public async Task<TranslationCompatibilityReport> RunAsync(
        TranslationSessionConfiguration inbound,
        TranslationSessionConfiguration outbound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(outbound);

        ITranslationSession? inboundSession = null;
        ITranslationSession? outboundSession = null;
        bool bothConnected = false;
        bool safeClose = false;
        string? connectionFailure = null;
        try
        {
            try
            {
                ValueTask<ITranslationSession> inboundCreation =
                    _sessionFactory.CreateAsync(inbound, cancellationToken);
                ValueTask<ITranslationSession> outboundCreation =
                    _sessionFactory.CreateAsync(outbound, cancellationToken);
                inboundSession = await inboundCreation.ConfigureAwait(false);
                outboundSession = await outboundCreation.ConfigureAwait(false);

                Task inboundConnect =
                    inboundSession.ConnectAsync(cancellationToken);
                Task outboundConnect =
                    outboundSession.ConnectAsync(cancellationToken);
                await Task.WhenAll(inboundConnect, outboundConnect)
                    .ConfigureAwait(false);
                bothConnected = true;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // A diagnostic probe converts provider failures into a stable, secret-free stage.
            catch (Exception exception)
            {
                connectionFailure = SafeFailureCode(exception);
            }
#pragma warning restore CA1031
        }
        finally
        {
            safeClose = await CloseAllAsync(
                inboundSession,
                outboundSession).ConfigureAwait(false);
        }

        TranslationProtocolEvidence? inboundEvidence =
            (inboundSession as ITranslationProtocolSessionEvidence)
                ?.ProtocolEvidence;
        TranslationProtocolEvidence? outboundEvidence =
            (outboundSession as ITranslationProtocolSessionEvidence)
                ?.ProtocolEvidence;
        bool authentication = bothConnected
            || (inboundEvidence?.AuthenticationSucceeded == true
                && outboundEvidence?.AuthenticationSucceeded == true);
        bool translationHandshake = bothConnected
            && inboundEvidence?.TranslationHandshakeSucceeded == true
            && outboundEvidence?.TranslationHandshakeSucceeded == true;
        bool targetLanguageUpdate = translationHandshake
            && inboundEvidence?.TargetLanguageUpdateSucceeded == true
            && outboundEvidence?.TargetLanguageUpdateSucceeded == true;

        TranslationCapabilityOutcome audioOutcome =
            authentication && translationHandshake && targetLanguageUpdate
                ? TranslationCapabilityOutcome.RequiresInteractiveAudio
                : TranslationCapabilityOutcome.NotRun;

        return new TranslationCompatibilityReport(
        [
            Stage(
                "authentication",
                authentication
                    ? TranslationCapabilityOutcome.Passed
                    : TranslationCapabilityOutcome.Failed,
                authentication ? null : connectionFailure),
            Stage(
                "translationWebSocketHandshake",
                translationHandshake
                    ? TranslationCapabilityOutcome.Passed
                    : authentication
                        ? TranslationCapabilityOutcome.Failed
                        : TranslationCapabilityOutcome.NotRun,
                translationHandshake ? null : connectionFailure),
            Stage(
                "targetLanguageUpdate",
                targetLanguageUpdate
                    ? TranslationCapabilityOutcome.Passed
                    : translationHandshake
                        ? TranslationCapabilityOutcome.Failed
                        : TranslationCapabilityOutcome.NotRun,
                targetLanguageUpdate ? null : connectionFailure),
            Stage(
                "dualSessionConcurrency",
                bothConnected
                    ? TranslationCapabilityOutcome.Passed
                    : TranslationCapabilityOutcome.Failed,
                bothConnected ? null : connectionFailure),
            Stage("sourceTranscript", audioOutcome),
            Stage("translatedAudio", audioOutcome),
            Stage(
                "safeClose",
                safeClose
                    ? TranslationCapabilityOutcome.Passed
                    : TranslationCapabilityOutcome.Failed,
                safeClose ? null : "translationProbe.safeCloseFailed"),
        ]);
    }

    private async Task<bool> CloseAllAsync(
        ITranslationSession? inbound,
        ITranslationSession? outbound)
    {
        Task<bool>[] closeTasks =
        [
            CloseOneAsync(inbound),
            CloseOneAsync(outbound),
        ];
        bool[] results = await Task.WhenAll(closeTasks).ConfigureAwait(false);
        return results.All(static result => result);
    }

    private async Task<bool> CloseOneAsync(ITranslationSession? session)
    {
        if (session is null)
        {
            return true;
        }

        bool closeSucceeded = false;
        using CancellationTokenSource deadline = new(_closeTimeout);
        try
        {
            Task close = session.CloseAsync(deadline.Token);
            await close.WaitAsync(_closeTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            closeSucceeded = true;
        }
#pragma warning disable CA1031 // Provider cleanup faults become a stable safeClose result.
        catch (Exception)
        {
            closeSucceeded = false;
        }
#pragma warning restore CA1031
        finally
        {
            await DisposeBoundedAsync(session).ConfigureAwait(false);
        }

        return closeSucceeded;
    }

    private async Task DisposeBoundedAsync(ITranslationSession session)
    {
        try
        {
            Task? dispose = session switch
            {
                IAsyncDisposable asyncDisposable =>
                    asyncDisposable.DisposeAsync().AsTask(),
                IDisposable disposable => Task.Run(disposable.Dispose),
                _ => null,
            };
            if (dispose is not null)
            {
                await dispose.WaitAsync(_closeTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Cleanup is bounded and provider failures must not escape the probe.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private static TranslationCompatibilityStageResult Stage(
        string name,
        TranslationCapabilityOutcome outcome,
        string? failureCode = null)
    {
        return new TranslationCompatibilityStageResult(
            name,
            outcome,
            failureCode);
    }

    private static string SafeFailureCode(Exception exception)
    {
        return exception switch
        {
            RuntimeOperationException operation => operation.Error.Code,
            _ => "translationProbe.connectionFailed",
        };
    }
}
