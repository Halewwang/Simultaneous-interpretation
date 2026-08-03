using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using EMKE.Windows.App.Bootstrap;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2000 // Test fakes transfer async-disposable ownership to the coordinator.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.

[TestClass]
public sealed class SingleInstanceCoordinatorTests
{
    [TestMethod]
    public async Task FirstInstanceRetainsNamedMutexUntilDisposed()
    {
        FakeMutexFactory mutexes = new();
        FakeCommandTransport commands = new();
        await using SingleInstanceCoordinator coordinator = new(
            WindowsPackageChannel.Internal,
            mutexes,
            commands);

        SingleInstanceStartResult result =
            await coordinator.StartAsync(static _ => ValueTask.CompletedTask);

        Assert.AreEqual(SingleInstanceStartResult.Primary, result);
        Assert.IsTrue(
            mutexes.IsOwned("Local\\EMKE.Translation.Internal.Instance"));
    }

    [TestMethod]
    public async Task SecondInstanceSendsShowDashboardAndDoesNotOwnMutex()
    {
        FakeMutexFactory mutexes = new();
        FakeCommandTransport commands = new();
        int showDashboardCount = 0;
        await using SingleInstanceCoordinator first = new(
            WindowsPackageChannel.Beta,
            mutexes,
            commands);
        await using SingleInstanceCoordinator second = new(
            WindowsPackageChannel.Beta,
            mutexes,
            commands);
        await first.StartAsync(
            _ =>
            {
                showDashboardCount++;
                return ValueTask.CompletedTask;
            });

        SingleInstanceStartResult result =
            await second.StartAsync(static _ => ValueTask.CompletedTask);

        Assert.AreEqual(SingleInstanceStartResult.Secondary, result);
        Assert.AreEqual(1, showDashboardCount);
        Assert.AreEqual(
            SingleInstanceCoordinator.ShowDashboardCommand,
            commands.LastCommand);
    }

    [TestMethod]
    public async Task StalePipeDoesNotPreventFreshMutexOwnerFromStarting()
    {
        FakeMutexFactory mutexes = new();
        FakeCommandTransport commands = new()
        {
            ThrowIfClientSendIsAttempted = true,
        };
        await using SingleInstanceCoordinator coordinator = new(
            WindowsPackageChannel.Stable,
            mutexes,
            commands);

        SingleInstanceStartResult result =
            await coordinator.StartAsync(static _ => ValueTask.CompletedTask);

        Assert.AreEqual(SingleInstanceStartResult.Primary, result);
        Assert.AreEqual(0, commands.SendAttemptCount);
    }

    [TestMethod]
    public async Task RealBusyPipeFailsBoundedListenerStartAndRecoversAfterRelease()
    {
        string pipeName =
            $"e{Guid.NewGuid():N}"[..13];
        NamedPipeServerStream blocker = new(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        IAsyncDisposable? unexpectedListener = null;
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            IOException failure =
                await Assert.ThrowsExactlyAsync<IOException>(
                    async () =>
                    {
                        unexpectedListener =
                            await SingleInstanceCoordinator
                                .NamedPipeSingleInstanceCommandTransport
                                .Instance
                                .ListenAsync(
                                    pipeName,
                                    static (_, _) => ValueTask.CompletedTask,
                                    CancellationToken.None);
                    });

            Assert.Contains(
                "bounded startup window",
                failure.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            if (unexpectedListener is not null)
            {
                await unexpectedListener.DisposeAsync();
            }

            await blocker.DisposeAsync();
        }

        stopwatch.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(3));

        await using IAsyncDisposable recovered =
            await SingleInstanceCoordinator
                .NamedPipeSingleInstanceCommandTransport
                .Instance
                .ListenAsync(
                    pipeName,
                    static (_, _) => ValueTask.CompletedTask,
                    CancellationToken.None);
    }

    [TestMethod]
    [DataRow(
        "Internal",
        "Local\\EMKE.Translation.Internal.Instance",
        "EMKE.Translation.Internal.Commands")]
    [DataRow(
        "Beta",
        "Local\\EMKE.Translation.Beta.Instance",
        "EMKE.Translation.Beta.Commands")]
    [DataRow(
        "Stable",
        "Local\\EMKE.Translation.Stable.Instance",
        "EMKE.Translation.Stable.Commands")]
    public void NamesAreIsolatedByWindowsPackageChannel(
        string channelName,
        string expectedMutexName,
        string expectedPipeName)
    {
        WindowsPackageChannel channel =
            Enum.Parse<WindowsPackageChannel>(channelName);
        SingleInstanceNames names = SingleInstanceNames.For(channel);

        Assert.AreEqual(expectedMutexName, names.MutexName);
        Assert.AreEqual(expectedPipeName, names.PipeName);
    }

    private sealed class FakeMutexFactory : ISingleInstanceMutexFactory
    {
        private readonly HashSet<string> _ownedNames = new(StringComparer.Ordinal);

        public ValueTask<ISingleInstanceMutex> CreateAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool createdNew = _ownedNames.Add(name);
            return ValueTask.FromResult<ISingleInstanceMutex>(
                new FakeMutex(this, name, createdNew));
        }

        public bool IsOwned(string name)
        {
            return _ownedNames.Contains(name);
        }

        private void Release(string name)
        {
            _ownedNames.Remove(name);
        }

        private sealed class FakeMutex : ISingleInstanceMutex
        {
            private readonly FakeMutexFactory _owner;
            private readonly string _name;
            private int _disposed;

            public FakeMutex(
                FakeMutexFactory owner,
                string name,
                bool createdNew)
            {
                _owner = owner;
                _name = name;
                CreatedNew = createdNew;
            }

            public bool CreatedNew { get; }

            public ValueTask DisposeAsync()
            {
                if (CreatedNew && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _owner.Release(_name);
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeCommandTransport : ISingleInstanceCommandTransport
    {
        private readonly Dictionary<
            string,
            Func<string, CancellationToken, ValueTask>> _listeners =
                new(StringComparer.Ordinal);

        public bool ThrowIfClientSendIsAttempted { get; init; }

        public int SendAttemptCount { get; private set; }

        public string? LastCommand { get; private set; }

        public ValueTask<IAsyncDisposable> ListenAsync(
            string pipeName,
            Func<string, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _listeners.Add(pipeName, handler);
            return ValueTask.FromResult<IAsyncDisposable>(
                new Listener(_listeners, pipeName));
        }

        public async ValueTask SendAsync(
            string pipeName,
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            SendAttemptCount++;
            if (ThrowIfClientSendIsAttempted)
            {
                throw new AssertFailedException("A fresh owner contacted a stale pipe.");
            }

            Assert.IsTrue(timeout > TimeSpan.Zero);
            LastCommand = command;
            await _listeners[pipeName](command, cancellationToken);
        }

        private sealed class Listener : IAsyncDisposable
        {
            private readonly Dictionary<
                string,
                Func<string, CancellationToken, ValueTask>> _listeners;
            private readonly string _pipeName;

            public Listener(
                Dictionary<string, Func<string, CancellationToken, ValueTask>> listeners,
                string pipeName)
            {
                _listeners = listeners;
                _pipeName = pipeName;
            }

            public ValueTask DisposeAsync()
            {
                _listeners.Remove(_pipeName);
                return ValueTask.CompletedTask;
            }
        }
    }
}
