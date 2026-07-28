using EMKE.Core;

namespace EMKE.Windows.App.State;

internal interface IAppDispatcher
{
    void Post(Action callback);
}

internal sealed class AppSnapshotStore :
    IObserver<AppSnapshot>,
    IDisposable
{
    private readonly object _sync = new();
    private readonly IAppDispatcher _dispatcher;
    private readonly Dictionary<long, Action<AppSnapshot>> _observers = [];
    private AppSnapshot? _current;
    private AppSnapshot? _pending;
    private long _nextSubscriptionId;
    private bool _dispatchPending;
    private bool _disposed;

    public AppSnapshotStore(IAppDispatcher dispatcher)
    {
        _dispatcher =
            dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public AppSnapshot? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Publish(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool postDispatch = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ulong newestVersion = _pending?.Version
                ?? _current?.Version
                ?? 0;
            bool hasSnapshot = _pending is not null || _current is not null;
            if (hasSnapshot && snapshot.Version <= newestVersion)
            {
                return;
            }

            _pending = snapshot;
            if (!_dispatchPending)
            {
                _dispatchPending = true;
                postDispatch = true;
            }
        }

        if (postDispatch)
        {
            try
            {
                _dispatcher.Post(DrainPending);
            }
            catch
            {
                lock (_sync)
                {
                    _dispatchPending = false;
                }

                throw;
            }
        }
    }

    public IDisposable Subscribe(Action<AppSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        long subscriptionId;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscriptionId = checked(++_nextSubscriptionId);
            _observers.Add(subscriptionId, observer);
        }

        return new Subscription(this, subscriptionId);
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
    }

    public void OnNext(AppSnapshot value)
    {
        Publish(value);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dispatchPending = false;
            _pending = null;
            _current = null;
            _observers.Clear();
        }
    }

    private void DrainPending()
    {
        AppSnapshot? snapshot;
        Action<AppSnapshot>[] observers;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            snapshot = _pending;
            _pending = null;
            _dispatchPending = false;
            if (snapshot is null)
            {
                return;
            }

            _current = snapshot;
            observers = [.. _observers.Values];
        }

        foreach (Action<AppSnapshot> observer in observers)
        {
            observer(snapshot);
        }
    }

    private void Unsubscribe(long subscriptionId)
    {
        lock (_sync)
        {
            _observers.Remove(subscriptionId);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private AppSnapshotStore? _owner;
        private readonly long _subscriptionId;

        public Subscription(
            AppSnapshotStore owner,
            long subscriptionId)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _subscriptionId = subscriptionId;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)
                ?.Unsubscribe(_subscriptionId);
        }
    }
}
