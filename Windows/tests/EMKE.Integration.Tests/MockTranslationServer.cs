using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EMKE.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EMKE.Integration.Tests;

#pragma warning disable CA2000 // WebApplication owns Kestrel and is disposed by the server.
#pragma warning disable CA1515 // DataRow values on public MSTest methods require a public enum.

public enum MockTranslationScenario
{
    Normal,
    FragmentedText,
    BinaryEvent,
    Unauthorized,
    Forbidden,
    UnknownModel,
    DelayedClose,
    BlockedClose,
    LateDeltas,
}

internal sealed record MockClientAudioMessage(
    LanguageCode TargetLanguage,
    WebSocketMessageType MessageType,
    byte[] Pcm16);

internal sealed class MockTranslationServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentDictionary<LanguageCode, Connection> _connections = new();
    private readonly ConcurrentDictionary<LanguageCode, TaskCompletionSource<Connection>>
        _connectionWaiters = new();
    private readonly ConcurrentDictionary<LanguageCode, DisconnectWaiter>
        _disconnectWaiters = new();
    private readonly ConcurrentDictionary<LanguageCode, Channel<MockClientAudioMessage>>
        _clientAudio = new();
    private readonly ConcurrentDictionary<LanguageCode, ConcurrentQueue<string>>
        _clientEventTypes = new();
    private readonly ConcurrentQueue<LanguageCode> _handshakeTargets = new();
    private readonly TaskCompletionSource _closeRequestReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _delayedCloseRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Uri? _baseAddress;
    private int _fragmentedTextFrameCount;
    private int _totalConnectionCount;
    private int _clientAudioBackpressureCount;

    private MockTranslationServer(
        WebApplication application,
        MockTranslationScenario scenario)
    {
        _application = application;
        Scenario = scenario;
    }

    public MockTranslationScenario Scenario { get; }

    public int FragmentedTextFrameCount =>
        Volatile.Read(ref _fragmentedTextFrameCount);

    public Task CloseRequestReceived => _closeRequestReceived.Task;

    public int TotalConnectionCount =>
        Volatile.Read(ref _totalConnectionCount);

    public int ClientAudioBackpressureCount =>
        Volatile.Read(ref _clientAudioBackpressureCount);

    public LanguageCode[] HandshakeTargets => _handshakeTargets.ToArray();

    public string[] ClientEventTypes(LanguageCode targetLanguage)
    {
        return _clientEventTypes.TryGetValue(
            targetLanguage,
            out ConcurrentQueue<string>? events)
            ? events.ToArray()
            : [];
    }

    public void ReleaseDelayedClose()
    {
        _delayedCloseRelease.TrySetResult();
    }

    public async Task<MockClientAudioMessage> WaitForClientAudioAsync(
        LanguageCode targetLanguage)
    {
        Channel<MockClientAudioMessage> channel = _clientAudio.GetOrAdd(
            targetLanguage,
            static _ => CreateClientAudioChannel());
        return await channel.Reader.ReadAsync().ConfigureAwait(false);
    }

    public async Task SendAudioDeltaAsync(
        LanguageCode targetLanguage,
        ReadOnlyMemory<byte> pcm16)
    {
        if (pcm16.IsEmpty || (pcm16.Length & 1) != 0)
        {
            throw new ArgumentException(
                "PCM16 must contain a non-empty even byte count.",
                nameof(pcm16));
        }

        Connection connection = await WaitForConnectionAsync(targetLanguage)
            .ConfigureAwait(false);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "session.output_audio.delta",
            delta = Convert.ToBase64String(pcm16.Span),
        });
        await connection.SendTextAsync(payload).ConfigureAwait(false);
    }

    public async Task SendServerErrorAsync(LanguageCode targetLanguage)
    {
        Connection connection = await WaitForConnectionAsync(targetLanguage)
            .ConfigureAwait(false);
        await connection.SendTextAsync(
            """
            {"type":"error","error":{"code":"mock_server_error","message":"deterministic test failure"}}
            """u8.ToArray()).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(LanguageCode targetLanguage)
    {
        while (true)
        {
            Connection connection = await WaitForConnectionAsync(targetLanguage)
                .ConfigureAwait(false);
            DisconnectWaiter waiter = new(connection);
            if (!_disconnectWaiters.TryAdd(targetLanguage, waiter))
            {
                throw new InvalidOperationException(
                    $"A disconnect is already pending for {targetLanguage}.");
            }

            if (!_connections.TryGetValue(targetLanguage, out Connection? active)
                || !ReferenceEquals(active, connection))
            {
                _disconnectWaiters.TryRemove(
                    new KeyValuePair<LanguageCode, DisconnectWaiter>(
                        targetLanguage,
                        waiter));
                continue;
            }

            connection.Abort();
            try
            {
                await waiter.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                return;
            }
            finally
            {
                _disconnectWaiters.TryRemove(
                    new KeyValuePair<LanguageCode, DisconnectWaiter>(
                        targetLanguage,
                        waiter));
            }
        }
    }

    public static async Task<MockTranslationServer> StartAsync(
        MockTranslationScenario scenario = MockTranslationScenario.Normal)
    {
        WebApplicationOptions applicationOptions = new()
        {
            Args = [],
            ApplicationName = typeof(MockTranslationServer).Assembly.FullName,
        };
        WebApplicationBuilder builder =
            WebApplication.CreateSlimBuilder(applicationOptions);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(static options =>
            options.Listen(IPAddress.Loopback, 0));

        WebApplication application = builder.Build();
        MockTranslationServer server = new(application, scenario);
        application.UseWebSockets();
        application.Map("/realtime/translations", server.HandleAsync);
        await application.StartAsync().ConfigureAwait(false);

        IServerAddressesFeature addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException(
                "Kestrel did not publish a resolved loopback address.");
        string resolved = addresses.Addresses.Single();
        Uri httpAddress = new(resolved);
        server._baseAddress = new UriBuilder(httpAddress)
        {
            Scheme = "ws",
            Path = "/realtime/translations",
        }.Uri;
        if (!IPAddress.IsLoopback(IPAddress.Parse(server._baseAddress.Host)))
        {
            throw new InvalidOperationException(
                "The mock Translation server must bind only to loopback.");
        }

        return server;
    }

    public Uri ResolveUri(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model must not be empty.", nameof(model));
        }

        Uri baseAddress = _baseAddress
            ?? throw new InvalidOperationException("The server has not started.");
        return new UriBuilder(baseAddress)
        {
            Query = $"model={Uri.EscapeDataString(model)}",
        }.Uri;
    }

    public async Task SendTranscriptAsync(
        LanguageCode targetLanguage,
        string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        Connection connection = await WaitForConnectionAsync(targetLanguage)
            .ConfigureAwait(false);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "session.input_transcript.delta",
            delta = transcript,
        });
        await connection.SendTextAsync(payload).ConfigureAwait(false);
    }

    public async Task SendTranslatedTranscriptAsync(
        LanguageCode targetLanguage,
        string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        Connection connection = await WaitForConnectionAsync(targetLanguage)
            .ConfigureAwait(false);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "session.output_transcript.delta",
            delta = transcript,
        });
        await connection.SendTextAsync(payload).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private Task<Connection> WaitForConnectionAsync(LanguageCode targetLanguage)
    {
        if (_connections.TryGetValue(
                targetLanguage,
                out Connection? existing))
        {
            return Task.FromResult(existing);
        }

        TaskCompletionSource<Connection> waiter =
            _connectionWaiters.GetOrAdd(
                targetLanguage,
                static _ => new TaskCompletionSource<Connection>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
        return waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Channel<MockClientAudioMessage> CreateClientAudioChannel()
    {
        return Channel.CreateBounded<MockClientAudioMessage>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    private void RecordClientEvent(LanguageCode targetLanguage, string type)
    {
        ConcurrentQueue<string> events = _clientEventTypes.GetOrAdd(
            targetLanguage,
            static _ => new ConcurrentQueue<string>());
        events.Enqueue(type);
    }

    private void ResetConnectionWaiter(
        LanguageCode targetLanguage,
        Connection connection)
    {
        while (_connectionWaiters.TryGetValue(
            targetLanguage,
            out TaskCompletionSource<Connection>? waiter))
        {
            if (!waiter.Task.IsCompletedSuccessfully
                || !ReferenceEquals(waiter.Task.Result, connection))
            {
                return;
            }

            TaskCompletionSource<Connection> replacement = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_connectionWaiters.TryUpdate(
                targetLanguage,
                replacement,
                waiter))
            {
                return;
            }
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (Scenario == MockTranslationScenario.Unauthorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (Scenario == MockTranslationScenario.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (Scenario == MockTranslationScenario.UnknownModel
            && !string.Equals(
                context.Request.Query["model"],
                "gpt-realtime-translate",
                StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync()
            .ConfigureAwait(false);
        Connection connection = new(socket, context.Abort);
        if (Scenario == MockTranslationScenario.BinaryEvent)
        {
            await connection.SendBinaryAsync(
                """{"type":"session.created","session":{"model":"gpt-realtime-translate"}}"""u8.ToArray())
                .ConfigureAwait(false);
            return;
        }

        await SendServerEventAsync(
            connection,
            """{"type":"session.created","session":{"model":"gpt-realtime-translate"}}"""u8.ToArray()).ConfigureAwait(false);

        string? update = await ReceiveTextAsync(socket, context.RequestAborted)
            .ConfigureAwait(false);
        if (update is null)
        {
            return;
        }

        LanguageCode targetLanguage = ReadTargetLanguage(update);
        _handshakeTargets.Enqueue(targetLanguage);
        RecordClientEvent(targetLanguage, "session.update");
        if (!_connections.TryAdd(targetLanguage, connection))
        {
            throw new InvalidOperationException(
                $"A {targetLanguage} session is already connected.");
        }

        Interlocked.Increment(ref _totalConnectionCount);
        _connectionWaiters.GetOrAdd(
                targetLanguage,
                static _ => new TaskCompletionSource<Connection>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(connection);
        await SendServerEventAsync(
            connection,
            """{"type":"session.updated"}"""u8.ToArray()).ConfigureAwait(false);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                string? message =
                    await ReceiveTextAsync(socket, context.RequestAborted)
                        .ConfigureAwait(false);
                if (message is null)
                {
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(message);
                string? type = document.RootElement
                    .GetProperty("type")
                    .GetString();
                if (type is not null)
                {
                    RecordClientEvent(targetLanguage, type);
                }
                if (string.Equals(
                        type,
                        "session.input_audio_buffer.append",
                        StringComparison.Ordinal))
                {
                    byte[] pcm16 = Convert.FromBase64String(
                        document.RootElement
                            .GetProperty("audio")
                            .GetString()!);
                    Channel<MockClientAudioMessage> audio =
                        _clientAudio.GetOrAdd(
                            targetLanguage,
                            static _ => CreateClientAudioChannel());
                    MockClientAudioMessage clientMessage = new(
                        targetLanguage,
                        WebSocketMessageType.Text,
                        pcm16);
                    if (!audio.Writer.TryWrite(clientMessage))
                    {
                        Interlocked.Increment(
                            ref _clientAudioBackpressureCount);
                        await audio.Writer.WriteAsync(
                            clientMessage,
                            context.RequestAborted).ConfigureAwait(false);
                    }

                    continue;
                }

                if (string.Equals(
                        type,
                        "session.close",
                        StringComparison.Ordinal))
                {
                    _closeRequestReceived.TrySetResult();
                    if (Scenario == MockTranslationScenario.DelayedClose)
                    {
                        await _delayedCloseRelease.Task
                            .WaitAsync(context.RequestAborted)
                            .ConfigureAwait(false);
                    }

                    if (Scenario == MockTranslationScenario.BlockedClose)
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            context.RequestAborted).ConfigureAwait(false);
                        return;
                    }

                    if (Scenario == MockTranslationScenario.LateDeltas)
                    {
                        await connection.SendTextAsync(
                            """
                            {"type":"session.input_transcript.delta","delta":"late-transcript"}
                            """u8.ToArray()).ConfigureAwait(false);
                        await connection.SendTextAsync(
                            """
                            {"type":"session.output_audio.delta","delta":"AQACAA=="}
                            """u8.ToArray()).ConfigureAwait(false);
                    }

                    await connection.SendTextAsync(
                        """{"type":"session.closed"}"""u8.ToArray())
                        .ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            context.RequestAborted.IsCancellationRequested)
        {
        }
        finally
        {
            ResetConnectionWaiter(targetLanguage, connection);
            _connections.TryRemove(
                new KeyValuePair<LanguageCode, Connection>(
                    targetLanguage,
                    connection));
            if (_disconnectWaiters.TryGetValue(
                    targetLanguage,
                    out DisconnectWaiter? waiter)
                && ReferenceEquals(waiter.Connection, connection)
                && _disconnectWaiters.TryRemove(
                    new KeyValuePair<LanguageCode, DisconnectWaiter>(
                        targetLanguage,
                        waiter)))
            {
                waiter.Completion.TrySetResult();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Timed out waiting for mock server state.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private async Task SendServerEventAsync(
        Connection connection,
        ReadOnlyMemory<byte> payload)
    {
        if (Scenario == MockTranslationScenario.FragmentedText)
        {
            await connection.SendFragmentedTextAsync(payload)
                .ConfigureAwait(false);
            Interlocked.Add(ref _fragmentedTextFrameCount, 2);
            return;
        }

        await connection.SendTextAsync(payload).ConfigureAwait(false);
    }

    private static LanguageCode ReadTargetLanguage(string update)
    {
        using JsonDocument document = JsonDocument.Parse(update);
        JsonElement root = document.RootElement;
        if (!string.Equals(
                root.GetProperty("type").GetString(),
                "session.update",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The first client event must be session.update.");
        }

        return root.GetProperty("session")
            .GetProperty("audio")
            .GetProperty("output")
            .GetProperty("language")
            .GetString() switch
        {
            "zh" => LanguageCode.Zh,
            "en" => LanguageCode.En,
            "de" => LanguageCode.De,
            _ => throw new InvalidOperationException(
                "The client sent an unknown target language."),
        };
    }

    private static async Task<string?> ReceiveTextAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        int used = 0;
        while (true)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(
                buffer.AsMemory(used),
                cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException(
                    "All client Translation events must be Text frames.");
            }

            used += result.Count;
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(buffer, 0, used);
            }

            if (used == buffer.Length)
            {
                throw new InvalidOperationException(
                    "The client event exceeded the mock server receive limit.");
            }
        }
    }

    private sealed class Connection
    {
        private readonly WebSocket _socket;
        private readonly Action _abortConnection;
        private readonly Channel<byte> _sendGate = Channel.CreateBounded<byte>(1);

        public Connection(WebSocket socket, Action abortConnection)
        {
            _socket = socket;
            _abortConnection = abortConnection;
            _ = _sendGate.Writer.TryWrite(0);
        }

        public async Task SendTextAsync(ReadOnlyMemory<byte> payload)
        {
            _ = await _sendGate.Reader.ReadAsync().ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = _sendGate.Writer.TryWrite(0);
            }
        }

        public async Task SendFragmentedTextAsync(
            ReadOnlyMemory<byte> payload)
        {
            int split = Math.Max(1, payload.Length / 2);
            _ = await _sendGate.Reader.ReadAsync().ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(
                    payload[..split],
                    WebSocketMessageType.Text,
                    endOfMessage: false,
                    CancellationToken.None).ConfigureAwait(false);
                await _socket.SendAsync(
                    payload[split..],
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = _sendGate.Writer.TryWrite(0);
            }
        }

        public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload)
        {
            _ = await _sendGate.Reader.ReadAsync().ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(
                    payload,
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = _sendGate.Writer.TryWrite(0);
            }
        }

        public void Abort()
        {
            _abortConnection();
        }
    }

    private sealed class DisconnectWaiter(Connection connection)
    {
        public Connection Connection { get; } = connection;

        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

#pragma warning restore CA2000
#pragma warning restore CA1515
