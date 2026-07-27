using System.Net.WebSockets;
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
    private bool _disposed;

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

    public async Task<RuntimeError?> ConnectAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

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
                or ArgumentException
                or InvalidOperationException)
        {
            return NetworkError("translationSocket.connectFailed");
        }
    }

    public ValueTask<RuntimeError?> SendSessionUpdateAsync(
        LanguageCode targetLanguage,
        CancellationToken cancellationToken)
    {
        return SendTextAsync(
            TranslationEventCodec.EncodeSessionUpdate(targetLanguage),
            cancellationToken);
    }

    public ValueTask<RuntimeError?> SendAudioAppendAsync(
        ReadOnlySpan<byte> pcm16,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return SendTextAsync(
                TranslationEventCodec.EncodeAudioAppend(pcm16),
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
        return SendTextAsync(
            TranslationEventCodec.EncodeSessionClose(),
            cancellationToken);
    }

    public async ValueTask<TranslationReceiveResult> ReceiveEventAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_receiveBuffer);
        _adapter.Dispose();
    }

    private async ValueTask<RuntimeError?> SendTextAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RuntimeError? frameError =
            TranslationClientFramePolicy.Validate(WebSocketMessageType.Text);
        if (frameError is not null)
        {
            return frameError;
        }

        try
        {
            await _adapter.SendAsync(
                payload,
                WebSocketMessageType.Text,
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
    }

    private static RuntimeError ProtocolError(string code)
    {
        return Error(ErrorCategory.Protocol, code);
    }

    private static RuntimeError NetworkError(string code)
    {
        return Error(ErrorCategory.Network, code);
    }

    private static RuntimeError Error(ErrorCategory category, string code)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.Retry);
    }

    private sealed class ClientWebSocketAdapter : IClientWebSocket
    {
        private readonly ClientWebSocket _socket = new();

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
}
