using System.Globalization;
using System.Runtime.CompilerServices;
using EMKE.Core;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Presentation;
using EMKE.Windows.App.State;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class PresentationCoordinatorTests
{
    [TestMethod]
    public void LanguageChangeRemapsCurrentErrorForEverySubscriberWithoutRuntimeCommand()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore snapshots = new(dispatcher);
        LocalizationService localization = new(
            () => CultureInfo.GetCultureInfo("en-US"));
        AppPresentationMapper mapper = new(localization);
        using PresentationCoordinator coordinator = new(
            snapshots,
            localization,
            mapper);
        CountingRuntime runtime = new(snapshots);
        List<AppPresentation> firstWindow = [];
        List<AppPresentation> secondWindow = [];
        using IDisposable firstSubscription =
            coordinator.Subscribe(firstWindow.Add);
        using IDisposable secondSubscription =
            coordinator.Subscribe(secondWindow.Add);

        runtime.PublishSnapshot(Snapshot());
        dispatcher.Drain();
        AppPresentation initial = firstWindow.Single();
        Assert.AreSame(initial, secondWindow.Single());
        Assert.AreEqual(
            "The EMKE virtual audio driver is missing or incompatible.",
            initial.Error?.Message);

        localization.ChangeLanguage(AppInterfaceLanguage.ZhHans);

        Assert.HasCount(2, firstWindow);
        Assert.HasCount(2, secondWindow);
        AppPresentation remapped = firstWindow[1];
        Assert.AreSame(remapped, secondWindow[1]);
        Assert.AreNotSame(initial, remapped);
        Assert.AreEqual(initial.SnapshotVersion, remapped.SnapshotVersion);
        Assert.AreEqual(
            "EMKE 虚拟音频驱动缺失或不兼容。",
            remapped.Error?.Message);
        Assert.AreEqual(0, runtime.CommandCount);
        Assert.AreSame(SnapshotError, snapshots.Current?.Error);
    }

    [TestMethod]
    public void LateSubscriberReceivesCurrentPresentationObjectWithoutNewMapping()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore snapshots = new(dispatcher);
        LocalizationService localization = new(
            () => CultureInfo.GetCultureInfo("en-US"));
        using PresentationCoordinator coordinator = new(
            snapshots,
            localization,
            new AppPresentationMapper(localization));
        List<AppPresentation> firstWindow = [];
        using IDisposable firstSubscription =
            coordinator.Subscribe(firstWindow.Add);
        snapshots.Publish(Snapshot());
        dispatcher.Drain();

        AppPresentation? lateWindow = null;
        using IDisposable lateSubscription =
            coordinator.Subscribe(presentation => lateWindow = presentation);

        Assert.AreSame(firstWindow.Single(), lateWindow);
    }

    [TestMethod]
    public void DisposedSubscriberIsNotRetainedByCoordinator()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore snapshots = new(dispatcher);
        LocalizationService localization = new(
            () => CultureInfo.GetCultureInfo("en-US"));
        using PresentationCoordinator coordinator = new(
            snapshots,
            localization,
            new AppPresentationMapper(localization));

        WeakReference window = SubscribeThenReleaseWindow(coordinator);
        ForceGarbageCollection();

        Assert.IsFalse(window.IsAlive);
    }

    [TestMethod]
    public void DisposedCoordinatorStopsSnapshotAndLanguagePublishing()
    {
        FakeDispatcher dispatcher = new();
        using AppSnapshotStore snapshots = new(dispatcher);
        LocalizationService localization = new(
            () => CultureInfo.GetCultureInfo("en-US"));
        PresentationCoordinator coordinator = new(
            snapshots,
            localization,
            new AppPresentationMapper(localization));
        List<AppPresentation> observed = [];
        using IDisposable subscription = coordinator.Subscribe(observed.Add);
        snapshots.Publish(Snapshot());
        dispatcher.Drain();

        coordinator.Dispose();
        localization.ChangeLanguage(AppInterfaceLanguage.ZhHans);
        snapshots.Publish(Snapshot(version: 100));
        dispatcher.Drain();

        Assert.HasCount(1, observed);
    }

    private static readonly RuntimeError SnapshotError = new(
        ErrorCategory.Driver,
        "translationRuntime.driverIncompatible",
        new Dictionary<string, string>(),
        RecoveryAction.InstallDriver);

    private static AppSnapshot Snapshot(ulong version = 99)
    {
        return new AppSnapshot(
            contractVersion: 1,
            version,
            RuntimeState.Degraded,
            ChannelState.Degraded,
            ChannelState.Connected,
            InboundRoute.OriginalFailOpen,
            OutboundRoute.Translated,
            inboundLevel: 0.25,
            outboundLevel: 0.75,
            sourceCaption: "source",
            translatedCaption: "translated",
            new AudioSelection("input", "output"),
            new DriverCompatibility(true, "compatible"),
            connectionReport: null,
            new AudioDiagnostics(true, 0),
            new UpdateAvailability(false, string.Empty),
            SnapshotError);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SubscribeThenReleaseWindow(
        PresentationCoordinator coordinator)
    {
        WindowObserver window = new();
        WeakReference reference = new(window);
        IDisposable subscription = coordinator.Subscribe(window.Observe);
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

    private sealed class FakeDispatcher : IAppDispatcher
    {
        private readonly Queue<Action> _pending = new();

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

    private sealed class CountingRuntime
    {
        private readonly AppSnapshotStore _snapshots;

        public CountingRuntime(AppSnapshotStore snapshots)
        {
            _snapshots = snapshots;
        }

        public int CommandCount { get; private set; }

        public void PublishSnapshot(AppSnapshot snapshot)
        {
            _snapshots.Publish(snapshot);
        }

        public void SubmitCommand()
        {
            CommandCount++;
        }
    }

    private sealed class WindowObserver
    {
        public AppPresentation? LastPresentation { get; private set; }

        public void Observe(AppPresentation presentation)
        {
            LastPresentation = presentation;
        }
    }
}
