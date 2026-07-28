using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace EMKE.Windows.App.Bootstrap;

#pragma warning disable CA2000 // Factories transfer async-disposable ownership to the coordinator.

internal enum WindowsPackageChannel
{
    Internal,
    Beta,
    Stable,
}

internal enum SingleInstanceStartResult
{
    Primary,
    Secondary,
}

internal sealed record SingleInstanceNames(
    string MutexName,
    string PipeName)
{
    public static SingleInstanceNames For(WindowsPackageChannel channel)
    {
        string label = channel switch
        {
            WindowsPackageChannel.Internal => "Internal",
            WindowsPackageChannel.Beta => "Beta",
            WindowsPackageChannel.Stable => "Stable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(channel),
                channel,
                "The Windows package channel is not supported."),
        };
        return new SingleInstanceNames(
            $"Local\\EMKE.Translation.{label}.Instance",
            $"EMKE.Translation.{label}.Commands");
    }
}

internal interface ISingleInstanceMutex : IAsyncDisposable
{
    bool CreatedNew { get; }
}

internal interface ISingleInstanceMutexFactory
{
    ValueTask<ISingleInstanceMutex> CreateAsync(
        string name,
        CancellationToken cancellationToken);
}

internal interface ISingleInstanceCommandTransport
{
    ValueTask<IAsyncDisposable> ListenAsync(
        string pipeName,
        Func<string, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken);

    ValueTask SendAsync(
        string pipeName,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    public const string ShowDashboardCommand = "show dashboard";

    private static readonly TimeSpan DefaultCommandTimeout =
        TimeSpan.FromSeconds(2);

    private readonly ISingleInstanceMutexFactory _mutexFactory;
    private readonly ISingleInstanceCommandTransport _commandTransport;
    private readonly TimeSpan _commandTimeout;
    private ISingleInstanceMutex? _mutex;
    private IAsyncDisposable? _listener;
    private int _started;
    private int _disposed;

    public SingleInstanceCoordinator(WindowsPackageChannel channel)
        : this(
            channel,
            SystemSingleInstanceMutexFactory.Instance,
            NamedPipeSingleInstanceCommandTransport.Instance)
    {
    }

    internal SingleInstanceCoordinator(
        WindowsPackageChannel channel,
        ISingleInstanceMutexFactory mutexFactory,
        ISingleInstanceCommandTransport commandTransport)
        : this(
            channel,
            mutexFactory,
            commandTransport,
            DefaultCommandTimeout)
    {
    }

    internal SingleInstanceCoordinator(
        WindowsPackageChannel channel,
        ISingleInstanceMutexFactory mutexFactory,
        ISingleInstanceCommandTransport commandTransport,
        TimeSpan commandTimeout)
    {
        _mutexFactory =
            mutexFactory ?? throw new ArgumentNullException(nameof(mutexFactory));
        _commandTransport =
            commandTransport ?? throw new ArgumentNullException(nameof(commandTransport));
        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                commandTimeout,
                "The command timeout must be positive.");
        }

        _commandTimeout = commandTimeout;
        Names = SingleInstanceNames.For(channel);
    }

    public SingleInstanceNames Names { get; }

    public async ValueTask<SingleInstanceStartResult> StartAsync(
        Func<CancellationToken, ValueTask> showDashboard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(showDashboard);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "The single-instance coordinator has already started.");
        }

        ISingleInstanceMutex mutex = await _mutexFactory.CreateAsync(
            Names.MutexName,
            cancellationToken).ConfigureAwait(false);
        if (!mutex.CreatedNew)
        {
            await mutex.DisposeAsync().ConfigureAwait(false);
            await SendShowDashboardBestEffortAsync(cancellationToken)
                .ConfigureAwait(false);
            return SingleInstanceStartResult.Secondary;
        }

        try
        {
            IAsyncDisposable listener = await _commandTransport.ListenAsync(
                Names.PipeName,
                HandleCommandAsync,
                cancellationToken).ConfigureAwait(false);
            _mutex = mutex;
            _listener = listener;
            return SingleInstanceStartResult.Primary;
        }
        catch
        {
            await mutex.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        ValueTask HandleCommandAsync(
            string command,
            CancellationToken commandCancellationToken)
        {
            return string.Equals(
                command,
                ShowDashboardCommand,
                StringComparison.Ordinal)
                ? showDashboard(commandCancellationToken)
                : ValueTask.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IAsyncDisposable? listener = Interlocked.Exchange(
            ref _listener,
            null);
        ISingleInstanceMutex? mutex = Interlocked.Exchange(ref _mutex, null);
        try
        {
            if (listener is not null)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (mutex is not null)
            {
                await mutex.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask SendShowDashboardBestEffortAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _commandTransport.SendAsync(
                Names.PipeName,
                ShowDashboardCommand,
                _commandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private sealed class SystemSingleInstanceMutexFactory
        : ISingleInstanceMutexFactory
    {
        public static SystemSingleInstanceMutexFactory Instance { get; } = new();

        private SystemSingleInstanceMutexFactory()
        {
        }

        public ValueTask<ISingleInstanceMutex> CreateAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mutex mutex = new(
                initiallyOwned: false,
                name,
                out bool createdNew);
            return ValueTask.FromResult<ISingleInstanceMutex>(
                new SystemSingleInstanceMutex(mutex, createdNew));
        }
    }

    private sealed class SystemSingleInstanceMutex : ISingleInstanceMutex
    {
        private Mutex? _mutex;

        public SystemSingleInstanceMutex(Mutex mutex, bool createdNew)
        {
            _mutex = mutex ?? throw new ArgumentNullException(nameof(mutex));
            CreatedNew = createdNew;
        }

        public bool CreatedNew { get; }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _mutex, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class NamedPipeSingleInstanceCommandTransport
        : ISingleInstanceCommandTransport
    {
        private static readonly TimeSpan DefaultListenerStartTimeout =
            TimeSpan.FromMilliseconds(500);

        private static readonly TimeSpan DefaultInitialRetryDelay =
            TimeSpan.FromMilliseconds(25);

        private static readonly TimeSpan DefaultMaximumRetryDelay =
            TimeSpan.FromMilliseconds(100);

        private readonly TimeSpan _listenerStartTimeout;
        private readonly TimeSpan _initialRetryDelay;
        private readonly TimeSpan _maximumRetryDelay;

        public static NamedPipeSingleInstanceCommandTransport Instance { get; } =
            new();

        private NamedPipeSingleInstanceCommandTransport()
            : this(
                DefaultListenerStartTimeout,
                DefaultInitialRetryDelay,
                DefaultMaximumRetryDelay)
        {
        }

        internal NamedPipeSingleInstanceCommandTransport(
            TimeSpan listenerStartTimeout,
            TimeSpan initialRetryDelay,
            TimeSpan maximumRetryDelay)
        {
            if (listenerStartTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(listenerStartTimeout),
                    listenerStartTimeout,
                    "The listener startup timeout must be positive.");
            }

            if (initialRetryDelay <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialRetryDelay),
                    initialRetryDelay,
                    "The initial retry delay must be positive.");
            }

            if (maximumRetryDelay < initialRetryDelay)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumRetryDelay),
                    maximumRetryDelay,
                    "The maximum retry delay cannot be shorter than the initial delay.");
            }

            _listenerStartTimeout = listenerStartTimeout;
            _initialRetryDelay = initialRetryDelay;
            _maximumRetryDelay = maximumRetryDelay;
        }

        public async ValueTask<IAsyncDisposable> ListenAsync(
            string pipeName,
            Func<string, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(pipeName);
            ArgumentNullException.ThrowIfNull(handler);
            cancellationToken.ThrowIfCancellationRequested();
            NamedPipeServerStream initialServer =
                await CreateServerWithBoundedRetryAsync(
                    pipeName,
                    cancellationToken).ConfigureAwait(false);
            try
            {
                return new NamedPipeCommandListener(
                    initialServer,
                    handler,
                    token => CreateServerWithBoundedRetryAsync(
                        pipeName,
                        token));
            }
            catch
            {
                await initialServer.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask SendAsync(
            string pipeName,
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            using NamedPipeClientStream client = new(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(timeoutCancellation.Token)
                    .ConfigureAwait(false);
                using StreamWriter writer = new(
                    client,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 256,
                    leaveOpen: true);
                await writer.WriteLineAsync(
                    command.AsMemory(),
                    timeoutCancellation.Token).ConfigureAwait(false);
                await writer.FlushAsync(timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The primary instance did not accept the command in time.");
            }
        }

        private async ValueTask<NamedPipeServerStream>
            CreateServerWithBoundedRetryAsync(
                string pipeName,
                CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            TimeSpan retryDelay = _initialRetryDelay;
            IOException? lastFailure = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lastFailure is not null
                    && stopwatch.Elapsed >= _listenerStartTimeout)
                {
                    throw new IOException(
                        $"Named pipe listener '{pipeName}' could not start within its bounded startup window.",
                        lastFailure);
                }

                try
                {
                    return new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                            | PipeOptions.CurrentUserOnly);
                }
                catch (IOException exception)
                {
                    lastFailure = exception;
                }

                TimeSpan remaining =
                    _listenerStartTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    continue;
                }

                TimeSpan boundedDelay = retryDelay <= remaining
                    ? retryDelay
                    : remaining;
                await Task.Delay(
                    boundedDelay,
                    cancellationToken).ConfigureAwait(false);
                retryDelay = retryDelay.Ticks
                    >= _maximumRetryDelay.Ticks / 2
                    ? _maximumRetryDelay
                    : TimeSpan.FromTicks(retryDelay.Ticks * 2);
            }
        }
    }

    private sealed class NamedPipeCommandListener : IAsyncDisposable
    {
        private readonly Func<string, CancellationToken, ValueTask> _handler;
        private readonly Func<
            CancellationToken,
            ValueTask<NamedPipeServerStream>> _serverFactory;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _listenTask;
        private int _disposed;

        public NamedPipeCommandListener(
            NamedPipeServerStream initialServer,
            Func<string, CancellationToken, ValueTask> handler,
            Func<
                CancellationToken,
                ValueTask<NamedPipeServerStream>> serverFactory)
        {
            ArgumentNullException.ThrowIfNull(initialServer);
            _handler = handler
                ?? throw new ArgumentNullException(nameof(handler));
            _serverFactory = serverFactory
                ?? throw new ArgumentNullException(nameof(serverFactory));
            _listenTask = ListenLoopAsync(
                initialServer,
                _cancellation.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cancellation.Dispose();
            }
        }

        private async Task ListenLoopAsync(
            NamedPipeServerStream initialServer,
            CancellationToken cancellationToken)
        {
            NamedPipeServerStream server = initialServer;
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream currentServer = server;
                await using (currentServer.ConfigureAwait(false))
                {
                    try
                    {
                        await currentServer
                            .WaitForConnectionAsync(cancellationToken)
                            .ConfigureAwait(false);
                        using StreamReader reader = new(
                            currentServer,
                            Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: true,
                            bufferSize: 256,
                            leaveOpen: true);
                        string? command =
                            await reader.ReadLineAsync(cancellationToken)
                                .ConfigureAwait(false);
                        if (command is not null)
                        {
                            await _handler(command, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (IOException)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(50),
                            cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    server = await _serverFactory(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }
}
