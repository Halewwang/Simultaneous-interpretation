using System.Threading.Channels;
using EMKE.Core;

namespace EMKE.Application;

internal sealed class RuntimeSnapshotPublisher : IObservable<AppSnapshot>, IDisposable
{
    private readonly object _sync = new();
    private readonly List<Subscription> _subscriptions = [];
    private bool _disposed;

    public IDisposable Subscribe(IObserver<AppSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        Subscription subscription;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscription = new Subscription(this, observer);
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Publish(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Subscription[] subscriptions;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscriptions = _subscriptions.ToArray();
        }

        foreach (Subscription subscription in subscriptions)
        {
            subscription.Offer(snapshot);
        }
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (Subscription subscription in subscriptions)
        {
            subscription.DisposeFromPublisher();
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_sync)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly RuntimeSnapshotPublisher _owner;
        private readonly IObserver<AppSnapshot> _observer;
        private readonly Channel<AppSnapshot> _pending;
        private readonly Task _delivery;
        private int _disposed;

        public Subscription(
            RuntimeSnapshotPublisher owner,
            IObserver<AppSnapshot> observer)
        {
            _owner = owner;
            _observer = observer;
            _pending = Channel.CreateBounded<AppSnapshot>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false,
                });
            _delivery = Task.Run(DeliverAsync);
        }

        public void Offer(AppSnapshot snapshot)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _pending.Writer.TryWrite(snapshot);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Remove(this);
            Cancel();
        }

        public void DisposeFromPublisher()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Cancel();
            }
        }

        private void Cancel()
        {
            _pending.Writer.TryComplete();
            _ = _delivery.Exception;
        }

        private async Task DeliverAsync()
        {
            await foreach (AppSnapshot snapshot in _pending.Reader.ReadAllAsync()
                               .ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                try
                {
                    _observer.OnNext(snapshot);
                }
#pragma warning disable CA1031 // Arbitrary UI observers must be isolated from the actor.
                catch (Exception)
#pragma warning restore CA1031
                {
                    // Observers are isolated from the runtime actor.
                }
            }
        }
    }
}
