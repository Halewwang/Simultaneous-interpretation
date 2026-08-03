using System.Runtime.CompilerServices;
using EMKE.Core;
using EMKE.Windows.App.State;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class AppSnapshotStoreTests
{
    [TestMethod]
    public void OlderVersionCannotReplaceNewestPendingSnapshot()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore store = new(dispatcher);
        List<AppSnapshot> observed = [];
        using IDisposable subscription = store.Subscribe(observed.Add);
        AppSnapshot newest = Snapshot(10);

        store.Publish(newest);
        store.Publish(Snapshot(9));
        dispatcher.Drain();

        CollectionAssert.AreEqual(new[] { newest }, observed);
        Assert.AreSame(newest, store.Current);
    }

    [TestMethod]
    public void NewerVersionReplacesPublishedSnapshot()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore store = new(dispatcher);
        List<AppSnapshot> observed = [];
        using IDisposable subscription = store.Subscribe(observed.Add);
        AppSnapshot version10 = Snapshot(10);
        AppSnapshot version11 = Snapshot(11);

        store.Publish(version10);
        dispatcher.Drain();
        store.Publish(version11);
        dispatcher.Drain();

        CollectionAssert.AreEqual(
            new[] { version10, version11 },
            observed);
        Assert.AreSame(version11, store.Current);
    }

    [TestMethod]
    public void SubscribersObserveSameSnapshotObjectAndVersion()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore store = new(dispatcher);
        AppSnapshot? first = null;
        AppSnapshot? second = null;
        using IDisposable firstSubscription =
            store.Subscribe(snapshot => first = snapshot);
        using IDisposable secondSubscription =
            store.Subscribe(snapshot => second = snapshot);
        AppSnapshot snapshot = Snapshot(42);

        store.Publish(snapshot);
        dispatcher.Drain();

        Assert.AreSame(snapshot, first);
        Assert.AreSame(first, second);
        Assert.AreEqual(42UL, second?.Version);
    }

    [TestMethod]
    public void RapidUpdatesCoalesceToLatestPendingSnapshot()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore store = new(dispatcher);
        List<AppSnapshot> observed = [];
        using IDisposable subscription = store.Subscribe(observed.Add);
        AppSnapshot latest = Snapshot(12);

        store.Publish(Snapshot(10));
        store.Publish(Snapshot(11));
        store.Publish(latest);

        Assert.AreEqual(1, dispatcher.PendingCount);
        dispatcher.Drain();
        CollectionAssert.AreEqual(new[] { latest }, observed);
    }

    [TestMethod]
    public void UnsubscribedWindowIsNotRetained()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore store = new(dispatcher);

        WeakReference window = SubscribeThenReleaseWindow(store);
        ForceGarbageCollection();

        Assert.IsFalse(window.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SubscribeThenReleaseWindow(
        AppSnapshotStore store)
    {
        WindowObserver window = new();
        WeakReference reference = new(window);
        IDisposable subscription = store.Subscribe(window.Observe);
        subscription.Dispose();
        return reference;
    }

    private static void ForceGarbageCollection()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static AppSnapshot Snapshot(ulong version)
    {
        return new AppSnapshot(
            contractVersion: 1,
            version,
            RuntimeState.Stopped,
            ChannelState.Inactive,
            ChannelState.Inactive,
            InboundRoute.Stopped,
            OutboundRoute.Stopped,
            inboundLevel: 0,
            outboundLevel: 0,
            sourceCaption: string.Empty,
            translatedCaption: string.Empty,
            new AudioSelection("input", "output"),
            new DriverCompatibility(true, "compatible"),
            connectionReport: null,
            new AudioDiagnostics(true, 0),
            new UpdateAvailability(false, string.Empty),
            error: null);
    }

    private sealed class FakeDispatcher : IAppDispatcher
    {
        private readonly Queue<Action> _pending = new();

        public int PendingCount => _pending.Count;

        public void Post(Action callback)
        {
            _pending.Enqueue(callback);
        }

        public void Drain()
        {
            while (_pending.TryDequeue(out Action? callback))
            {
                callback();
            }
        }
    }

    private sealed class WindowObserver
    {
        public AppSnapshot? LastSnapshot { get; private set; }

        public void Observe(AppSnapshot snapshot)
        {
            LastSnapshot = snapshot;
        }
    }
}
