using System.Net.WebSockets;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using EMKE.Core;

namespace EMKE.Realtime;

public enum TranslationReceiveStatus
{
    Event,
    Closed,
    Failed,
}

public sealed record TranslationReceiveResult
{
    private TranslationReceiveResult(
        TranslationReceiveStatus status,
        TranslationProtocolEvent? protocolEvent,
        RuntimeError? error)
    {
        Status = status;
        Event = protocolEvent;
        Error = error;
    }

    public TranslationReceiveStatus Status { get; }

    public TranslationProtocolEvent? Event { get; }

    public RuntimeError? Error { get; }

    internal static TranslationReceiveResult Received(TranslationProtocolEvent protocolEvent)
    {
        return new TranslationReceiveResult(
            TranslationReceiveStatus.Event,
            protocolEvent,
            null);
    }

    internal static TranslationReceiveResult Closed()
    {
        return new TranslationReceiveResult(
            TranslationReceiveStatus.Closed,
            null,
            null);
    }

    internal static TranslationReceiveResult Failed(RuntimeError error)
    {
        return new TranslationReceiveResult(
            TranslationReceiveStatus.Failed,
            null,
            error);
    }
}

internal interface IClientWebSocket : IDisposable
{
    HttpStatusCode? HttpStatusCode => null;

    void SetRequestHeader(string name, string value);

    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);
}

internal interface ITranslationTransport : IDisposable
{
    Task<RuntimeError?> ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    ValueTask<RuntimeError?> SendSessionUpdateAsync(
        LanguageCode targetLanguage,
        CancellationToken cancellationToken);

    ValueTask<RuntimeError?> SendAudioAppendAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken);

    ValueTask<RuntimeError?> SendSessionCloseAsync(CancellationToken cancellationToken);

    ValueTask<TranslationReceiveResult> ReceiveEventAsync(CancellationToken cancellationToken);
}

internal static class TranslationClientFramePolicy
{
    public static RuntimeError? Validate(WebSocketMessageType messageType)
    {
        return messageType == WebSocketMessageType.Text
            ? null
            : new RuntimeError(
                ErrorCategory.Protocol,
                "binaryTranslationEvent",
                new Dictionary<string, string>(),
                RecoveryAction.Retry);
    }
}

public sealed class TranslationSocket : ITranslationTransport
{
    public const int DefaultReceiveLimit = 64 * 1024;

    private readonly IClientWebSocket _adapter;
    private readonly byte[] _receiveBuffer;
#pragma warning disable CA2213 // The gate must outlive detached in-flight sends during synchronous transport disposal.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
#pragma warning restore CA2213
    private int _disposed;

    public TranslationSocket()
        : this(new ClientWebSocketAdapter(), DefaultReceiveLimit)
    {
    }

    internal TranslationSocket(IClientWebSocket adapter, int receiveLimit)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveLimit);

        _adapter = adapter;
        _receiveBuffer = GC.AllocateUninitializedArray<byte>(receiveLimit);
    }

    internal void ConfigureAuthorizationHeader(ReadOnlySpan<char> secret)
    {
        if (secret.IsEmpty || secret.IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException(
                "Translation credentials must not be empty or contain line breaks.",
                nameof(secret));
        }

        char[] copy = secret.ToArray();
        try
        {
            string header = string.Create(
                "Bearer ".Length + copy.Length,
                copy,
                static (destination, value) =>
                {
                    "Bearer ".AsSpan().CopyTo(destination);
                    value.AsSpan().CopyTo(destination["Bearer ".Length..]);
                });
            _adapter.SetRequestHeader("Authorization", header);
        }
        finally
        {
            Array.Clear(copy);
        }
    }

    public async Task<RuntimeError?> ConnectAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ThrowIfDisposed();

        try
        {
            await _adapter.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return NetworkError("translationSocket.connectCanceled");
        }
        catch (Exception exception) when (
            exception is WebSocketException
                or HttpRequestException
                or IOException
                or AuthenticationException
                or ArgumentException
                or InvalidOperationException)
        {
            return ConnectError(_adapter.HttpStatusCode);
        }
    }

    public ValueTask<RuntimeError?> SendSessionUpdateAsync(
        LanguageCode targetLanguage,
        CancellationToken cancellationToken)
    {
        return SendClientEventAsync(
            TranslationEventCodec.EncodeSessionUpdate(targetLanguage),
            WebSocketMessageType.Text,
            cancellationToken);
    }

    public ValueTask<RuntimeError?> SendAudioAppendAsync(
        ReadOnlySpan<byte> pcm16,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            return SendClientEventAsync(
                TranslationEventCodec.EncodeAudioAppend(pcm16),
                WebSocketMessageType.Text,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult<RuntimeError?>(
                ProtocolError("translationEvent.invalidPcm16"));
        }
    }

    ValueTask<RuntimeError?> ITranslationTransport.SendAudioAppendAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken)
    {
        return SendAudioAppendAsync(pcm16.Span, cancellationToken);
    }

    public ValueTask<RuntimeError?> SendSessionCloseAsync(
        CancellationToken cancellationToken)
    {
        return SendClientEventAsync(
            TranslationEventCodec.EncodeSessionClose(),
            WebSocketMessageType.Text,
            cancellationToken);
    }

    public async ValueTask<TranslationReceiveResult> ReceiveEventAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        int used = 0;
        try
        {
            while (true)
            {
                if (used == _receiveBuffer.Length)
                {
                    return TranslationReceiveResult.Failed(
                        ProtocolError("translationSocket.messageTooLarge"));
                }

                ValueWebSocketReceiveResult receiveResult;
                try
                {
                    receiveResult = await _adapter.ReceiveAsync(
                        _receiveBuffer.AsMemory(used),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return TranslationReceiveResult.Failed(
                        NetworkError("translationSocket.receiveCanceled"));
                }
                catch (Exception exception) when (
                    exception is WebSocketException
                        or HttpRequestException
                        or ArgumentException
                        or InvalidOperationException
                        or IOException)
                {
                    return TranslationReceiveResult.Failed(
                        NetworkError("translationSocket.receiveFailed"));
                }

                if (receiveResult.Count < 0
                    || receiveResult.Count > _receiveBuffer.Length - used)
                {
                    return TranslationReceiveResult.Failed(
                        ProtocolError("translationSocket.invalidReceiveCount"));
                }

                if (receiveResult.MessageType == WebSocketMessageType.Binary)
                {
                    return TranslationReceiveResult.Failed(
                        ProtocolError("binaryTranslationEvent"));
                }

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    return TranslationReceiveResult.Closed();
                }

                if (receiveResult.MessageType != WebSocketMessageType.Text)
                {
                    return TranslationReceiveResult.Failed(
                        ProtocolError("translationSocket.invalidFrameType"));
                }

                if (receiveResult.Count == 0 && !receiveResult.EndOfMessage)
                {
                    return TranslationReceiveResult.Failed(
                        ProtocolError("translationSocket.emptyFragment"));
                }

                used += receiveResult.Count;
                if (!receiveResult.EndOfMessage)
                {
                    continue;
                }

                TranslationDecodeResult decoded =
                    TranslationEventCodec.Decode(_receiveBuffer.AsMemory(0, used));
                return decoded.IsSuccess
                    ? TranslationReceiveResult.Received(decoded.Event!)
                    : TranslationReceiveResult.Failed(decoded.Error!);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_receiveBuffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_receiveBuffer);
        _adapter.Dispose();
    }

    internal async ValueTask<RuntimeError?> SendClientEventAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        RuntimeError? frameError = TranslationClientFramePolicy.Validate(messageType);
        if (frameError is not null)
        {
            return frameError;
        }

        bool entered = false;
        try
        {
            ThrowIfDisposed();
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ThrowIfDisposed();
            await _adapter.SendAsync(
                payload,
                messageType,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return NetworkError("translationSocket.sendCanceled");
        }
        catch (Exception exception) when (
            exception is WebSocketException
                or HttpRequestException
                or ArgumentException
                or InvalidOperationException
                or IOException)
        {
            return NetworkError("translationSocket.sendFailed");
        }
        finally
        {
            if (entered)
            {
                _sendGate.Release();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private static RuntimeError ProtocolError(string code)
    {
        return Error(ErrorCategory.Protocol, code);
    }

    private static RuntimeError NetworkError(string code)
    {
        return Error(ErrorCategory.Network, code);
    }

    private static RuntimeError ConnectError(HttpStatusCode? statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new RuntimeError(
                    ErrorCategory.Authentication,
                    "translationSocket.authenticationRejected",
                    new Dictionary<string, string>(),
                    RecoveryAction.UpdateApiKey),
            HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity =>
                new RuntimeError(
                    ErrorCategory.EndpointModel,
                    "translationSocket.endpointModelRejected",
                    new Dictionary<string, string>(),
                    RecoveryAction.EditSettings),
            _ => NetworkError("translationSocket.connectFailed"),
        };
    }

    private static RuntimeError Error(ErrorCategory category, string code)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.Retry);
    }

}

internal sealed class ClientWebSocketAdapter : IClientWebSocket
{
    private readonly ClientWebSocket _socket = new();

    public ClientWebSocketAdapter()
    {
        _socket.Options.CollectHttpResponseDetails = true;
    }

    public HttpStatusCode? HttpStatusCode => _socket.HttpStatusCode;

    public void SetRequestHeader(string name, string value)
    {
        _socket.Options.SetRequestHeader(name, value);
    }

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        return _socket.ConnectAsync(endpoint, cancellationToken);
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        return _socket.SendAsync(
            payload,
            messageType,
            endOfMessage,
            cancellationToken);
    }

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        return _socket.ReceiveAsync(buffer, cancellationToken);
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
