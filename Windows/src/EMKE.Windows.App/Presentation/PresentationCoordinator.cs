using EMKE.Core;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.State;

namespace EMKE.Windows.App.Presentation;

internal sealed class PresentationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly LocalizationService _localization;
    private readonly AppPresentationMapper _mapper;
    private readonly IDisposable _snapshotSubscription;
    private readonly Dictionary<long, Action<AppPresentation>> _observers = [];
    private AppSnapshot? _currentSnapshot;
    private AppPresentation? _currentPresentation;
    private long _nextSubscriptionId;
    private bool _disposed;

    public PresentationCoordinator(
        AppSnapshotStore snapshots,
        LocalizationService localization,
        AppPresentationMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        _localization = localization
            ?? throw new ArgumentNullException(nameof(localization));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _snapshotSubscription = snapshots.Subscribe(OnSnapshot);
        _localization.LanguageChanged += OnLanguageChanged;

        if (snapshots.Current is { } current)
        {
            OnSnapshot(current);
        }
    }

    public AppPresentation? Current
    {
        get
        {
            lock (_sync)
            {
                return _currentPresentation;
            }
        }
    }

    public IDisposable Subscribe(Action<AppPresentation> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        long subscriptionId;
        AppPresentation? current;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscriptionId = checked(++_nextSubscriptionId);
            _observers.Add(subscriptionId, observer);
            current = _currentPresentation;
        }

        Subscription subscription = new(this, subscriptionId);
        if (current is not null)
        {
            try
            {
                observer(current);
            }
            catch
            {
                subscription.Dispose();
                throw;
            }
        }

        return subscription;
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
            _observers.Clear();
            _currentSnapshot = null;
            _currentPresentation = null;
        }

        _localization.LanguageChanged -= OnLanguageChanged;
        _snapshotSubscription.Dispose();
    }

    private void OnSnapshot(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        AppPresentation presentation;
        Action<AppPresentation>[] observers;
        lock (_sync)
        {
            if (_disposed
                || (_currentSnapshot is not null
                    && snapshot.Version <= _currentSnapshot.Version))
            {
                return;
            }

            presentation = _mapper.Map(
                snapshot,
                _localization.CurrentLanguage);
            _currentSnapshot = snapshot;
            _currentPresentation = presentation;
            observers = [.. _observers.Values];
        }

        Publish(presentation, observers);
    }

    private void OnLanguageChanged(
        object? sender,
        AppInterfaceLanguageChangedEventArgs eventArgs)
    {
        AppPresentation presentation;
        Action<AppPresentation>[] observers;
        lock (_sync)
        {
            if (_disposed || _currentSnapshot is null)
            {
                return;
            }

            presentation = _mapper.Map(
                _currentSnapshot,
                eventArgs.Language);
            _currentPresentation = presentation;
            observers = [.. _observers.Values];
        }

        Publish(presentation, observers);
    }

    private static void Publish(
        AppPresentation presentation,
        IEnumerable<Action<AppPresentation>> observers)
    {
        foreach (Action<AppPresentation> observer in observers)
        {
            observer(presentation);
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
        private PresentationCoordinator? _owner;
        private readonly long _subscriptionId;

        public Subscription(
            PresentationCoordinator owner,
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
