using System.Net.WebSockets;
using System.Net;
using System.Text;
using EMKE.Core;

namespace EMKE.Realtime.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // TranslationSocket takes ownership of the fake adapter in these tests.
#pragma warning disable CA2007 // MSTest test methods intentionally resume on the test context.

[TestClass]
public sealed class TranslationSocketTests
{
    [TestMethod]
    public async Task ClientEventsAreSingleTextFramesWithEndOfMessage()
    {
        FakeClientWebSocket adapter = new();
        TranslationSocket socket = new(adapter, receiveLimit: 256);

        Assert.IsNull(await socket.SendSessionUpdateAsync(LanguageCode.En, CancellationToken.None));
        Assert.IsNull(await socket.SendAudioAppendAsync([1, 2, 3, 4], CancellationToken.None));
        Assert.IsNull(await socket.SendSessionCloseAsync(CancellationToken.None));

        Assert.HasCount(3, adapter.Sends);
        Assert.IsTrue(adapter.Sends.All(static send => send.Type == WebSocketMessageType.Text));
        Assert.IsTrue(adapter.Sends.All(static send => send.EndOfMessage));
        StringAssert.Contains(
            adapter.Sends[0].Json,
            "\"type\":\"session.update\"",
            StringComparison.Ordinal);
        StringAssert.Contains(
            adapter.Sends[1].Json,
            "\"type\":\"session.input_audio_buffer.append\"",
            StringComparison.Ordinal);
        StringAssert.Contains(
            adapter.Sends[2].Json,
            "\"type\":\"session.close\"",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task BinaryClientEventIsRejectedByTheSocketBeforeAdapterSend()
    {
        FakeClientWebSocket adapter = new();
        TranslationSocket socket = new(adapter, receiveLimit: 256);

        RuntimeError? error = await socket.SendClientEventAsync(
            TranslationEventCodec.EncodeSessionUpdate(LanguageCode.Zh),
            WebSocketMessageType.Binary,
            CancellationToken.None);

        Assert.AreEqual(ErrorCategory.Protocol, error!.Category);
        Assert.AreEqual("binaryTranslationEvent", error.Code);
        Assert.HasCount(0, adapter.Sends);
    }

    [TestMethod]
    public async Task ClientSendsAreSerializedAndWaitingSendHonorsCancellation()
    {
        BlockingClientWebSocket adapter = new();
        TranslationSocket socket = new(adapter, receiveLimit: 256);
        using CancellationTokenSource audioCancellation = new();
        using CancellationTokenSource closeCancellation = new();

        Task<RuntimeError?> audio = socket.SendAudioAppendAsync(
            [1, 2, 3, 4],
            audioCancellation.Token).AsTask();
        await adapter.FirstSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<RuntimeError?> close = socket.SendSessionCloseAsync(
            closeCancellation.Token).AsTask();
        await closeCancellation.CancelAsync();

        RuntimeError? closeError = await close;
        Assert.AreEqual("translationSocket.sendCanceled", closeError!.Code);
        Assert.AreEqual(1, adapter.SendCount);
        Assert.AreEqual(1, adapter.MaxConcurrentSendCount);

        await audioCancellation.CancelAsync();
        RuntimeError? audioError = await audio;
        Assert.AreEqual("translationSocket.sendCanceled", audioError!.Code);
        Assert.AreEqual(1, adapter.MaxConcurrentSendCount);
    }

    [TestMethod]
    public async Task ReceiveAssemblesFragmentedTextAndClearsUsedBuffer()
    {
        FakeClientWebSocket adapter = new(
            Frame.Text("""{"type":"session.""", endOfMessage: false),
            Frame.Text("""created"}""", endOfMessage: true));
        TranslationSocket socket = new(adapter, receiveLimit: 64);

        TranslationReceiveResult result = await socket.ReceiveEventAsync(CancellationToken.None);

        Assert.AreEqual(TranslationReceiveStatus.Event, result.Status);
        Assert.AreEqual("session.created", result.Event!.Type);
        Assert.IsNull(result.Error);
        Assert.IsTrue(adapter.ReceiveBuffer.Span[..adapter.LastCompletedMessageBytes].ToArray()
            .All(static value => value == 0));
    }

    [TestMethod]
    public async Task ReceiveAcceptsExactLimitAndRejectsLimitPlusOne()
    {
        const string exactJson = """{"type":"session.created"}""";
        int limit = Encoding.UTF8.GetByteCount(exactJson);
        TranslationSocket exactSocket = new(
            new FakeClientWebSocket(Frame.Text(exactJson, endOfMessage: true)),
            limit);
        TranslationSocket tooLargeSocket = new(
            new FakeClientWebSocket(
                Frame.Text(exactJson, endOfMessage: false),
                Frame.Text("x", endOfMessage: true)),
            limit);

        TranslationReceiveResult exact = await exactSocket.ReceiveEventAsync(CancellationToken.None);
        TranslationReceiveResult tooLarge = await tooLargeSocket.ReceiveEventAsync(CancellationToken.None);

        Assert.AreEqual(TranslationReceiveStatus.Event, exact.Status);
        Assert.AreEqual(TranslationReceiveStatus.Failed, tooLarge.Status);
        Assert.AreEqual("translationSocket.messageTooLarge", tooLarge.Error!.Code);
    }

    [TestMethod]
    public async Task ReceiveRejectsBinaryServerFramesWithStableProtocolError()
    {
        FakeClientWebSocket adapter = new(
            Frame.Binary("""{"type":"session.created"}""", endOfMessage: true));
        TranslationSocket socket = new(adapter, receiveLimit: 128);

        TranslationReceiveResult result = await socket.ReceiveEventAsync(CancellationToken.None);

        Assert.AreEqual(TranslationReceiveStatus.Failed, result.Status);
        Assert.AreEqual(ErrorCategory.Protocol, result.Error!.Category);
        Assert.AreEqual("binaryTranslationEvent", result.Error.Code);
        Assert.IsTrue(adapter.ReceiveBuffer.Span.ToArray()
            .All(static value => value == 0));
    }

    [TestMethod]
    public async Task CloseFrameIsReportedWithoutAttemptingJsonDecode()
    {
        FakeClientWebSocket adapter = new(Frame.Close());
        TranslationSocket socket = new(adapter, receiveLimit: 128);

        TranslationReceiveResult result = await socket.ReceiveEventAsync(CancellationToken.None);

        Assert.AreEqual(TranslationReceiveStatus.Closed, result.Status);
        Assert.IsNull(result.Event);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task ShortMessageAfterLongMessageCannotObserveOldBufferBytes()
    {
        FakeClientWebSocket adapter = new(
            Frame.Text(
                """{"type":"error","error":{"code":"a-long-safe-code","message":"a-long-safe-message"}}""",
                endOfMessage: true),
            Frame.Text(
                """{"type":"session.created","session":{"model":"gpt-realtime-translate"}}""",
                endOfMessage: true));
        TranslationSocket socket = new(adapter, receiveLimit: 128);

        TranslationReceiveResult first = await socket.ReceiveEventAsync(CancellationToken.None);
        TranslationReceiveResult second = await socket.ReceiveEventAsync(CancellationToken.None);

        Assert.AreEqual("error", first.Event!.Type);
        Assert.AreEqual("session.created", second.Event!.Type);
        Assert.IsNull(second.Event.Code);
        Assert.IsNull(second.Event.Message);
    }

    [TestMethod]
    public async Task ConnectAndAdapterFailuresReturnStableSecretFreeErrors()
    {
        const string secret = "sk-1234567890abcdef";
        FakeClientWebSocket adapter = new()
        {
            ConnectException = new InvalidOperationException($"Authorization: Bearer {secret}"),
        };
        TranslationSocket socket = new(adapter, receiveLimit: 128);

        RuntimeError? error = await socket.ConnectAsync(
            new Uri("wss://api.example.test/realtime/translations?model=x"),
            CancellationToken.None);

        Assert.AreEqual("translationSocket.connectFailed", error!.Code);
        Assert.HasCount(0, error.Parameters);
        Assert.IsFalse(error.ToString()!.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, ErrorCategory.Authentication, "translationSocket.authenticationRejected", RecoveryAction.UpdateApiKey)]
    [DataRow(HttpStatusCode.Forbidden, ErrorCategory.Authentication, "translationSocket.authenticationRejected", RecoveryAction.UpdateApiKey)]
    [DataRow(HttpStatusCode.NotFound, ErrorCategory.EndpointModel, "translationSocket.endpointModelRejected", RecoveryAction.EditSettings)]
    public async Task ConnectHttpStatusUsesStableAuthenticationOrEndpointModelError(
        HttpStatusCode statusCode,
        ErrorCategory category,
        string code,
        RecoveryAction recoveryAction)
    {
        FakeClientWebSocket adapter = new()
        {
            ConnectException = new WebSocketException("opaque provider response"),
            HttpStatusCode = statusCode,
        };

        RuntimeError? error = await new TranslationSocket(adapter, 128)
            .ConnectAsync(new Uri("wss://api.example.test/realtime/translations?model=x"), CancellationToken.None);

        Assert.AreEqual(category, error?.Category);
        Assert.AreEqual(code, error?.Code);
        Assert.AreEqual(recoveryAction, error?.RecoveryAction);
        Assert.HasCount(0, error!.Parameters);
    }

    [TestMethod]
    public void AuthorizationConfigurationRejectsEmptyAndLineBreakSecretsBeforeAdapterInjection()
    {
        FakeClientWebSocket adapter = new();
        TranslationSocket socket = new(adapter, receiveLimit: 128);

        Assert.ThrowsExactly<ArgumentException>(
            () => socket.ConfigureAuthorizationHeader(ReadOnlySpan<char>.Empty));
        Assert.ThrowsExactly<ArgumentException>(
            () => socket.ConfigureAuthorizationHeader("line\r\nbreak"));

        Assert.AreEqual(0, adapter.HeaderSetCount);
    }

    [TestMethod]
    public async Task SendAndReceiveCancellationReturnStableErrors()
    {
        FakeClientWebSocket sendAdapter = new()
        {
            SendException = new OperationCanceledException("adapter detail"),
        };
        FakeClientWebSocket receiveAdapter = new()
        {
            ReceiveException = new OperationCanceledException("adapter detail"),
        };

        RuntimeError? sendError = await new TranslationSocket(sendAdapter, 128)
            .SendSessionCloseAsync(CancellationToken.None);
        TranslationReceiveResult receiveError = await new TranslationSocket(receiveAdapter, 128)
            .ReceiveEventAsync(CancellationToken.None);

        Assert.AreEqual("translationSocket.sendCanceled", sendError!.Code);
        Assert.AreEqual("translationSocket.receiveCanceled", receiveError.Error!.Code);
        Assert.HasCount(0, sendError.Parameters);
        Assert.HasCount(0, receiveError.Error.Parameters);
    }

    [TestMethod]
    public async Task ConnectCancellationAndSendExceptionReturnStableErrors()
    {
        FakeClientWebSocket connectAdapter = new()
        {
            ConnectException = new OperationCanceledException("adapter detail"),
        };
        FakeClientWebSocket sendAdapter = new()
        {
            SendException = new WebSocketException("adapter detail"),
        };

        RuntimeError? connectError = await new TranslationSocket(connectAdapter, 128)
            .ConnectAsync(
                new Uri("wss://api.example.test/realtime/translations?model=x"),
                CancellationToken.None);
        RuntimeError? sendError = await new TranslationSocket(sendAdapter, 128)
            .SendSessionCloseAsync(CancellationToken.None);

        Assert.AreEqual("translationSocket.connectCanceled", connectError!.Code);
        Assert.AreEqual("translationSocket.sendFailed", sendError!.Code);
        Assert.HasCount(0, connectError.Parameters);
        Assert.HasCount(0, sendError.Parameters);
    }

    [TestMethod]
    public async Task ReceiveAdapterExceptionReturnsStableNetworkError()
    {
        FakeClientWebSocket adapter = new()
        {
            ReceiveException = new WebSocketException("local path and secret must stay private"),
        };

        TranslationReceiveResult result = await new TranslationSocket(adapter, 128)
            .ReceiveEventAsync(CancellationToken.None);

        Assert.AreEqual(TranslationReceiveStatus.Failed, result.Status);
        Assert.AreEqual(ErrorCategory.Network, result.Error!.Category);
        Assert.AreEqual("translationSocket.receiveFailed", result.Error.Code);
        Assert.HasCount(0, result.Error.Parameters);
    }

    private sealed class FakeClientWebSocket(params Frame[] frames) : IClientWebSocket
    {
        private readonly Queue<Frame> _frames = new(frames);

        internal List<SendRecord> Sends { get; } = [];

        internal Exception? ConnectException { get; init; }

        public HttpStatusCode? HttpStatusCode { get; init; }

        internal Exception? SendException { get; init; }

        internal Exception? ReceiveException { get; init; }

        internal Memory<byte> ReceiveBuffer { get; private set; }

        internal int LastCompletedMessageBytes { get; private set; }

        internal int HeaderSetCount { get; private set; }

        public void SetRequestHeader(string name, string value)
        {
            HeaderSetCount++;
        }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            return ConnectException is null
                ? Task.CompletedTask
                : Task.FromException(ConnectException);
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (SendException is not null)
            {
                return ValueTask.FromException(SendException);
            }

            Sends.Add(new SendRecord(
                Encoding.UTF8.GetString(payload.Span),
                messageType,
                endOfMessage));
            return ValueTask.CompletedTask;
        }

        public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (ReceiveException is not null)
            {
                return ValueTask.FromException<ValueWebSocketReceiveResult>(ReceiveException);
            }

            Frame frame = _frames.Dequeue();
            frame.Payload.CopyTo(buffer);
            if (ReceiveBuffer.IsEmpty)
            {
                ReceiveBuffer = buffer;
            }
            LastCompletedMessageBytes += frame.Payload.Length;
            return ValueTask.FromResult(
                new ValueWebSocketReceiveResult(
                    frame.Payload.Length,
                    frame.Type,
                    frame.EndOfMessage));
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingClientWebSocket : IClientWebSocket
    {
        private int _activeSendCount;
        private int _maxConcurrentSendCount;
        private int _sendCount;

        public TaskCompletionSource FirstSendEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SendCount => Volatile.Read(ref _sendCount);

        public int MaxConcurrentSendCount => Volatile.Read(ref _maxConcurrentSendCount);

        public void SetRequestHeader(string name, string value)
        {
        }

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
            Interlocked.Increment(ref _sendCount);
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

            FirstSendEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSendCount);
            }
        }

        public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromException<ValueWebSocketReceiveResult>(
                new NotSupportedException());
        }

        public void Dispose()
        {
        }
    }

    private sealed record SendRecord(
        string Json,
        WebSocketMessageType Type,
        bool EndOfMessage);

    private sealed record Frame(
        byte[] Payload,
        WebSocketMessageType Type,
        bool EndOfMessage)
    {
        internal static Frame Text(string text, bool endOfMessage)
        {
            return new Frame(
                Encoding.UTF8.GetBytes(text),
                WebSocketMessageType.Text,
                endOfMessage);
        }

        internal static Frame Binary(string text, bool endOfMessage)
        {
            return new Frame(
                Encoding.UTF8.GetBytes(text),
                WebSocketMessageType.Binary,
                endOfMessage);
        }

        internal static Frame Close()
        {
            return new Frame([], WebSocketMessageType.Close, EndOfMessage: true);
        }
    }
}

#pragma warning restore CA2007
#pragma warning restore CA2000
#pragma warning restore CA1515
