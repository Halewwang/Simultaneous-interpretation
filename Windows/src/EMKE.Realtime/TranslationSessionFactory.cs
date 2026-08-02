using EMKE.Core;

namespace EMKE.Realtime;

public sealed class TranslationSessionFactory : ITranslationSessionFactory
{
    private const string CredentialName = "translationApiKey";

    private readonly ISecretStore _secretStore;
    private readonly Func<IClientWebSocket> _socketFactory;

    public TranslationSessionFactory(ISecretStore secretStore)
        : this(secretStore, static () => new ClientWebSocketAdapter())
    {
    }

    internal TranslationSessionFactory(
        ISecretStore secretStore,
        Func<IClientWebSocket> socketFactory)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
    }

    public async ValueTask<ITranslationSession> CreateAsync(
        TranslationSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        TranslationEndpointResult endpointResult = TranslationEndpoint.Create(
            request.BaseAddress.AbsoluteUri,
            request.Configuration.Model);
        if (!endpointResult.IsSuccess)
        {
            throw new TranslationSessionException(endpointResult.Error!);
        }

        TranslationSocket? socket = null;
        ISecretBuffer? lease = null;
        try
        {
            lease = await _secretStore.LoadAsync(CredentialName, cancellationToken)
                .ConfigureAwait(false);
            if (lease is null || !IsValidSecret(lease.Memory.Span))
            {
                throw new TranslationSessionException(AuthenticationError(
                    "translationSessionFactory.invalidApiKey"));
            }

            socket = CreateConfiguredSocket(lease.Memory.Span);
        }
        catch (TranslationSessionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or ObjectDisposedException)
        {
            throw new TranslationSessionException(AuthenticationError(
                "translationSessionFactory.authenticationConfigurationFailed"));
        }
        finally
        {
            lease?.Dispose();
        }

        try
        {
            TranslationSession session = new(
                socket!,
                endpointResult.Endpoint!,
                request.Configuration);
            socket = null;
            return session;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private TranslationSocket CreateConfiguredSocket(ReadOnlySpan<char> secret)
    {
        TranslationSocket socket = new(
            _socketFactory(),
            TranslationSocket.DefaultReceiveLimit);
        try
        {
            socket.ConfigureAuthorizationHeader(secret);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsValidSecret(ReadOnlySpan<char> value)
    {
        return !value.IsEmpty && value.IndexOfAny('\r', '\n') < 0;
    }

    private static RuntimeError AuthenticationError(string code)
    {
        return new RuntimeError(
            ErrorCategory.Authentication,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.UpdateApiKey);
    }
}
