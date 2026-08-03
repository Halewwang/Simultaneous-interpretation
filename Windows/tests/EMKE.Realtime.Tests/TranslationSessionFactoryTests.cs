using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using EMKE.Core;

namespace EMKE.Realtime.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // Sessions take ownership of their configured sockets.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class TranslationSessionFactoryTests
{
    private static readonly string[] ExpectedSecretNames =
        ["translationApiKey", "translationApiKey"];

    [TestMethod]
    public async Task FactoryUsesFreshDisposedLeaseAndConfiguredSocketForEachSession()
    {
        TrackingSecretBuffer firstLease = new("owned-test-credential-one");
        TrackingSecretBuffer secondLease = new("owned-test-credential-two");
        TrackingSecretStore secretStore = new(firstLease, secondLease);
        RecordingClientWebSocket firstSocket = new();
        RecordingClientWebSocket secondSocket = new();
        Queue<RecordingClientWebSocket> sockets = new(
            new[] { firstSocket, secondSocket });
        TranslationSessionFactory factory = new(secretStore, () => sockets.Dequeue());

        ITranslationSession inbound = await factory.CreateAsync(Request(LanguageCode.Zh), CancellationToken.None);
        ITranslationSession outbound = await factory.CreateAsync(Request(LanguageCode.En), CancellationToken.None);

        Assert.AreNotSame(inbound, outbound);
        Assert.AreEqual(2, secretStore.LoadCount);
        CollectionAssert.AreEqual(ExpectedSecretNames, secretStore.Names);
        Assert.AreEqual(1, firstLease.DisposeCount);
        Assert.AreEqual(1, secondLease.DisposeCount);
        Assert.AreEqual("Bearer owned-test-credential-one", firstSocket.Authorization);
        Assert.AreEqual("Bearer owned-test-credential-two", secondSocket.Authorization);
        Assert.AreEqual(0, firstSocket.ConnectCount);
        Assert.AreEqual(0, secondSocket.ConnectCount);

        await inbound.ConnectAsync(CancellationToken.None);
        await outbound.ConnectAsync(CancellationToken.None);

        Assert.AreEqual(
            "wss://api.example.test/realtime/translations?model=gpt-realtime-translate",
            firstSocket.ConnectedEndpoint!.AbsoluteUri);
        Assert.AreEqual(
            "wss://api.example.test/realtime/translations?model=gpt-realtime-translate",
            secondSocket.ConnectedEndpoint!.AbsoluteUri);
        Assert.IsTrue(firstSocket.HeaderConfiguredBeforeConnect);
        Assert.IsTrue(secondSocket.HeaderConfiguredBeforeConnect);

        await ((IAsyncDisposable)inbound).DisposeAsync();
        await ((IAsyncDisposable)outbound).DisposeAsync();
    }

    [TestMethod]
    public async Task FactoryRejectsInvalidEndpointBeforeSecretOrSocketAllocation()
    {
        TrackingSecretStore secretStore = new(new TrackingSecretBuffer("unused-test-credential"));
        int socketFactoryCalls = 0;
        TranslationSessionFactory factory = new(
            secretStore,
            () =>
            {
                socketFactoryCalls++;
                return new RecordingClientWebSocket();
            });
        TranslationSessionRequest request = new(
            new Uri("http://api.example.test", UriKind.Absolute),
            Configuration(LanguageCode.Zh));

        TranslationSessionException exception =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => factory.CreateAsync(request, CancellationToken.None).AsTask());

        Assert.AreEqual(ErrorCategory.Configuration, exception.Error.Category);
        Assert.AreEqual("translationEndpoint.invalidBaseUrl", exception.Error.Code);
        Assert.AreEqual(0, secretStore.LoadCount);
        Assert.AreEqual(0, socketFactoryCalls);
    }

    [TestMethod]
    public Task FactoryRejectsMissingSecretAsStableSecretFreeAuthenticationError()
    {
        return AssertInvalidSecretAsync(value: null);
    }

    [TestMethod]
    public Task FactoryRejectsEmptySecretAsStableSecretFreeAuthenticationError()
    {
        return AssertInvalidSecretAsync(value: string.Empty);
    }

    [TestMethod]
    public Task FactoryRejectsLineBreakSecretAsStableSecretFreeAuthenticationError()
    {
        return AssertInvalidSecretAsync(value: "contains\r\nline-break");
    }

    private static async Task AssertInvalidSecretAsync(string? value)
    {
        TrackingSecretStore secretStore = value is null
            ? new TrackingSecretStore()
            : new TrackingSecretStore(new TrackingSecretBuffer(value));
        int socketFactoryCalls = 0;
        TranslationSessionFactory factory = new(
            secretStore,
            () =>
            {
                socketFactoryCalls++;
                return new RecordingClientWebSocket();
            });

        TranslationSessionException exception =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => factory.CreateAsync(Request(LanguageCode.Zh), CancellationToken.None).AsTask());

        Assert.AreEqual(ErrorCategory.Authentication, exception.Error.Category);
        Assert.AreEqual("translationSessionFactory.invalidApiKey", exception.Error.Code);
        Assert.HasCount(0, exception.Error.Parameters);
        if (!string.IsNullOrEmpty(value))
        {
            Assert.IsFalse(exception.ToString().Contains(value, StringComparison.Ordinal));
        }
        Assert.AreEqual(0, socketFactoryCalls);
        if (value is not null)
        {
            Assert.AreEqual(1, secretStore.Leases[0].DisposeCount);
        }
    }

    [TestMethod]
    public async Task FactoryDisposesPartialSocketAndLeaseWhenHeaderConfigurationFails()
    {
        const string fixtureCredential = "owned-test-credential";
        TrackingSecretBuffer lease = new(fixtureCredential);
        TrackingSecretStore secretStore = new(lease);
        RecordingClientWebSocket socket = new()
        {
            HeaderException = new InvalidOperationException("header fixture failure"),
        };
        TranslationSessionFactory factory = new(secretStore, () => socket);

        TranslationSessionException exception =
            await Assert.ThrowsExactlyAsync<TranslationSessionException>(
                () => factory.CreateAsync(Request(LanguageCode.Zh), CancellationToken.None).AsTask());

        Assert.AreEqual(ErrorCategory.Authentication, exception.Error.Category);
        Assert.AreEqual("translationSessionFactory.authenticationConfigurationFailed", exception.Error.Code);
        Assert.HasCount(0, exception.Error.Parameters);
        Assert.IsFalse(exception.ToString().Contains(fixtureCredential, StringComparison.Ordinal));
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(1, socket.DisposeCount);
        Assert.AreEqual(0, socket.ConnectCount);
    }

    private static TranslationSessionRequest Request(LanguageCode targetLanguage)
    {
        return new TranslationSessionRequest(
            new Uri("https://api.example.test", UriKind.Absolute),
            Configuration(targetLanguage));
    }

    private static TranslationSessionConfiguration Configuration(LanguageCode targetLanguage)
    {
        return new TranslationSessionConfiguration(
            LanguageCode.En,
            targetLanguage,
            "gpt-realtime-translate");
    }

    private sealed class TrackingSecretStore(params TrackingSecretBuffer[] leases) : ISecretStore
    {
        private readonly Queue<TrackingSecretBuffer> _leases = new(leases);
        private readonly List<string> _names = [];

        public int LoadCount { get; private set; }

        public string[] Names => _names.ToArray();

        public TrackingSecretBuffer[] Leases => _leases.Concat(ConsumedLeases).ToArray();

        private List<TrackingSecretBuffer> ConsumedLeases { get; } = [];

        public ValueTask<ISecretBuffer?> LoadAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            _names.Add(name);
            if (!_leases.TryDequeue(out TrackingSecretBuffer? lease))
            {
                return ValueTask.FromResult<ISecretBuffer?>(null);
            }

            ConsumedLeases.Add(lease);
            return ValueTask.FromResult<ISecretBuffer?>(lease);
        }

        public ValueTask SaveAsync(
            string name,
            ReadOnlyMemory<char> value,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromException(
                new NotSupportedException("This factory test only reads credential leases."));
        }

        public ValueTask DeleteAsync(string name, CancellationToken cancellationToken)
        {
            return ValueTask.FromException(
                new NotSupportedException("This factory test only reads credential leases."));
        }
    }

    private sealed class TrackingSecretBuffer(string value) : ISecretBuffer
    {
        private readonly char[] _characters = value.ToCharArray();
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ReadOnlyMemory<char> Memory => _characters;

        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                Array.Clear(_characters);
            }
        }
    }

    private sealed class RecordingClientWebSocket : IClientWebSocket
    {
        private readonly Channel<byte[]> _receives = Channel.CreateUnbounded<byte[]>();
        private bool _headerConfigured;
        private int _connectCount;
        private int _disposeCount;

        public string? Authorization { get; private set; }

        public Exception? HeaderException { get; init; }

        public Uri? ConnectedEndpoint { get; private set; }

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool HeaderConfiguredBeforeConnect { get; private set; }

        public void SetRequestHeader(string name, string value)
        {
            if (HeaderException is not null)
            {
                throw HeaderException;
            }

            Assert.AreEqual("Authorization", name);
            Authorization = value;
            _headerConfigured = true;
        }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            ConnectedEndpoint = endpoint;
            HeaderConfiguredBeforeConnect = _headerConfigured;
            _receives.Writer.TryWrite("{\"type\":\"session.created\"}"u8.ToArray());
            _receives.Writer.TryWrite("{\"type\":\"session.updated\"}"u8.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            byte[] payload = await _receives.Reader.ReadAsync(cancellationToken);
            payload.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(
                payload.Length,
                WebSocketMessageType.Text,
                endOfMessage: true);
        }

        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                _receives.Writer.TryComplete();
            }
        }
    }
}

#pragma warning restore CA2000
#pragma warning restore CA2007
#pragma warning restore CA1515
