using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Driver;
using EMKE.Platform.Native;
using EMKE.Platform.Security;
using EMKE.Platform.Settings;
using EMKE.Realtime;
using EMKE.Routing;
using EMKE.Windows.App.Bootstrap;
using EMKE.Windows.App.Commands;
using EMKE.Windows.App.Presentation;
using EMKE.Windows.App.State;
using EMKE.Windows.App.Tray;
using System.Reflection;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.
#pragma warning disable CA2000 // Composition-root creation takes ownership of coordinator inputs.

[TestClass]
[DoNotParallelize]
public sealed class ProductionCompositionTests
{
    [TestMethod]
    public async Task StartupFactoryComposesOneRuntimeAndSharesItsUiContext()
    {
        int coreCreations = 0;
        int uiCreations = 0;
        AppUiCompositionContext? capturedContext = null;
        CapturingTray? tray = null;
        CapturingViews? views = null;
        IAppAdapterFactory factory = AppStartupFactory.Create(
            _ =>
            {
                coreCreations++;
                return ValueTask.FromResult(
                    new AppCoreAdapterBundle(
                        Dependencies(),
                        new NoOpDiagnostics(),
                        new NoOpAsyncDisposable()));
            },
            (context, _) =>
            {
                uiCreations++;
                capturedContext = context;
                tray = new CapturingTray(context);
                views = new CapturingViews(context);
                return ValueTask.FromResult(
                    new AppUiAdapterBundle(
                        tray,
                        views,
                        new NoOpAsyncDisposable()));
            });

        AppCompositionRoot root =
            await AppCompositionRoot.CreateForProcessAsync(
                factory,
                new NoOpAsyncDisposable(),
                TimeSpan.FromSeconds(1),
                static callback => callback(),
                static () => { },
                CancellationToken.None);

        Assert.AreEqual(1, coreCreations);
        Assert.AreEqual(1, uiCreations);
        Assert.IsNotNull(capturedContext);
        Assert.IsNotNull(tray);
        Assert.IsNotNull(views);
        Assert.IsTrue(tray.Started);
        Assert.AreSame(
            capturedContext.RuntimeCommands,
            tray.RuntimeCommands);
        Assert.AreSame(
            capturedContext.RuntimeCommands,
            views.RuntimeCommands);
        Assert.AreSame(capturedContext.Snapshots, views.Snapshots);
        Assert.AreSame(capturedContext.Presentation, tray.Presentation);
        Assert.AreSame(capturedContext.Presentation, views.Presentation);

        capturedContext.Snapshots.Publish(Snapshot(version: 900));
        Assert.AreEqual(
            900UL,
            capturedContext.Presentation.Current?.SnapshotVersion);
        await root.ShowInitialSurfaceAsync();
        await root.ShowDashboardAsync();
        Assert.AreEqual(1, views.InitialSurfaceCount);
        Assert.AreEqual(1, views.DashboardCount);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
                await AppCompositionRoot.CreateForProcessAsync(
                    factory,
                    new NoOpAsyncDisposable(),
                    TimeSpan.FromSeconds(1),
                    static callback => callback(),
                    static () => { },
                    CancellationToken.None));
        Assert.AreEqual(1, coreCreations);

        AppExitReport exit = await root.ExitAsync();
        Assert.IsEmpty(exit.Errors);
    }

    [TestMethod]
    public void ProductionStartupFactoryIsAlwaysAvailable()
    {
        IAppAdapterFactory factory = AppStartupFactory.CreateProduction(
            new InlineUiDispatcher(),
            static () => Task.CompletedTask);

        Assert.IsInstanceOfType<ProductionAppAdapterFactory>(factory);
    }

    [TestMethod]
    public async Task ProductionCoreUsesRealReadOnlyDriverPreflight()
    {
        AppCoreAdapterBundle bundle =
            await ProductionCoreAdapters.CreateAsync(CancellationToken.None);
        try
        {
            Assert.IsInstanceOfType<WindowsDriverManager>(
                bundle.RuntimeDependencies.DriverManager);
        }
        finally
        {
            await bundle.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ProductionCoreComposesMetadataBackedWindowsHostGate()
    {
        AppCoreAdapterBundle bundle =
            await ProductionCoreAdapters.CreateAsync(CancellationToken.None);
        try
        {
            Assert.AreEqual(
                "EMKE.Platform.Compatibility.WindowsHostBuildGate",
                bundle.RuntimeDependencies.WindowsBuildGate.GetType().FullName);
        }
        finally
        {
            await bundle.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ProductionCoreSharesRealSettingsAndCredentialAdaptersWithUi()
    {
        AppCoreAdapterBundle bundle =
            await ProductionCoreAdapters.CreateAsync(CancellationToken.None);
        try
        {
            Assert.IsInstanceOfType<WindowsSettingsStore>(
                bundle.RuntimeDependencies.SettingsStore);
            Assert.IsInstanceOfType<CredentialManagerSecretStore>(
                bundle.SecretStore);
            Assert.AreSame(
                (object)bundle.RuntimeDependencies.SettingsStore,
                (object?)bundle.ProductSettings);
        }
        finally
        {
            await bundle.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ProductionCoreComposesConcreteRuntimeAdapters()
    {
        AppCoreAdapterBundle bundle =
            await ProductionCoreAdapters.CreateAsync(CancellationToken.None);
        try
        {
            Assert.IsInstanceOfType<WindowsAudioDeviceCatalog>(
                bundle.RuntimeDependencies.DeviceCatalog);
            Assert.IsInstanceOfType<TranslationSessionFactory>(
                bundle.RuntimeDependencies.SessionFactory);
            Assert.IsInstanceOfType<OfflineLanguageClassifier>(
                bundle.RuntimeDependencies.LanguageClassifier);
            Assert.IsInstanceOfType<NativeAudioEngine>(
                bundle.RuntimeDependencies.AudioEngine);
            Assert.IsInstanceOfType<WindowsDriverManager>(
                bundle.RuntimeDependencies.DriverManager);
            Assert.IsInstanceOfType<WindowsSettingsStore>(
                bundle.RuntimeDependencies.SettingsStore);
            Assert.IsInstanceOfType<CredentialManagerSecretStore>(
                bundle.SecretStore);
            Assert.AreSame(
                (object)bundle.RuntimeDependencies.SettingsStore,
                (object?)bundle.ProductSettings);
        }
        finally
        {
            await bundle.DisposeAsync();
        }
    }

    [TestMethod]
    public void ProductionCompositionContainsNoPendingRuntimeAdapters()
    {
        string source = File.ReadAllText(TestSourceLocator.Find(
            Path.Combine(
                "Bootstrap",
                "ProductionAppAdapterFactory.cs")));

        Assert.IsFalse(source.Contains(
            "PendingAudioDeviceCatalog",
            StringComparison.Ordinal));
        Assert.IsFalse(source.Contains(
            "PendingTranslationSessionFactory",
            StringComparison.Ordinal));
        Assert.IsFalse(source.Contains(
            "PendingLanguageClassifier",
            StringComparison.Ordinal));
        Assert.IsFalse(source.Contains(
            "composition is not available",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProductionCoreSessionFactoryUsesTheOwnedSecretStore()
    {
        AppCoreAdapterBundle bundle =
            await ProductionCoreAdapters.CreateAsync(CancellationToken.None);
        try
        {
            TranslationSessionFactory factory =
                Assert.IsInstanceOfType<TranslationSessionFactory>(
                    bundle.RuntimeDependencies.SessionFactory);
            PropertyInfo? secretStore = typeof(TranslationSessionFactory)
                .GetProperty(
                    "SecretStore",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(secretStore);

            Assert.AreSame(bundle.SecretStore, secretStore.GetValue(factory));
        }
        finally
        {
            await bundle.DisposeAsync();
        }
    }

    [TestMethod]
    public void ProductionDiagnosticsProbeUsesTheRuntimeSessionFactory()
    {
        ITranslationSessionFactory runtimeFactory =
            new UnreachableSessionFactory();
        MethodInfo? createProbe = typeof(ProductionUiAdapters).GetMethod(
            "CreateTranslationConnectionProbe",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(createProbe);

        TranslationConnectionProbe probe =
            Assert.IsInstanceOfType<TranslationConnectionProbe>(
                createProbe.Invoke(null, [runtimeFactory]));
        FieldInfo? sessionFactory = typeof(TranslationConnectionProbe)
            .GetField("_sessionFactory", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(sessionFactory);

        Assert.AreSame(runtimeFactory, sessionFactory.GetValue(probe));
    }

    [TestMethod]
    public async Task StartupPreflightControlsStartWithoutOpeningNetworkOrAudio()
    {
        await AssertStartupPreflightAsync(
            new DriverCompatibility(
                isCompatible: false,
                statusLabel: "driverMissing",
                updateRecommended: true,
                repairAvailable: false),
            expectedStartEnabled: false);
        await AssertStartupPreflightAsync(
            new DriverCompatibility(
                isCompatible: true,
                statusLabel: "compatible",
                updateRecommended: false,
                repairAvailable: false),
            expectedStartEnabled: true);
    }

    [TestMethod]
    public async Task UiDiagnosticsLifetimeIsStoppedByApplicationExit()
    {
        RecordingDiagnostics coreDiagnostics = new();
        RecordingDiagnostics uiDiagnostics = new();
        IAppAdapterFactory factory = AppStartupFactory.Create(
            _ => ValueTask.FromResult(
                new AppCoreAdapterBundle(
                    Dependencies(),
                    coreDiagnostics,
                    new NoOpAsyncDisposable())),
            (context, _) => ValueTask.FromResult(
                new AppUiAdapterBundle(
                    new CapturingTray(context),
                    new CapturingViews(context),
                    new NoOpAsyncDisposable(),
                    uiDiagnostics)));
        AppCompositionRoot root =
            await AppCompositionRoot.CreateForProcessAsync(
                factory,
                new NoOpAsyncDisposable(),
                TimeSpan.FromSeconds(1),
                static callback => callback(),
                static () => { },
                CancellationToken.None);

        _ = await root.ExitAsync();

        Assert.AreEqual(1, uiDiagnostics.StopCount);
        Assert.AreEqual(0, coreDiagnostics.StopCount);
    }

    private static async Task AssertStartupPreflightAsync(
        DriverCompatibility compatibility,
        bool expectedStartEnabled)
    {
        RecordingDriverManager driver = new(compatibility);
        RecordingAudioEngine audio = new();
        RecordingSessionFactory sessions = new();
        AppPresentation? presentationSeenBeforeUiCreation = null;
        IAppAdapterFactory factory = AppStartupFactory.Create(
            _ => ValueTask.FromResult(
                new AppCoreAdapterBundle(
                    Dependencies(driver, audio, sessions),
                    new NoOpDiagnostics(),
                    new NoOpAsyncDisposable())),
            (context, _) =>
            {
                presentationSeenBeforeUiCreation =
                    context.Presentation.Current;
                return ValueTask.FromResult(
                    new AppUiAdapterBundle(
                        new CapturingTray(context),
                        new CapturingViews(context),
                        new NoOpAsyncDisposable()));
            });

        AppCompositionRoot root =
            await AppCompositionRoot.CreateForProcessAsync(
                factory,
                new NoOpAsyncDisposable(),
                TimeSpan.FromSeconds(1),
                static callback => callback(),
                static () => { },
                CancellationToken.None);
        try
        {
            Assert.IsNotNull(presentationSeenBeforeUiCreation);
            Assert.AreEqual(
                expectedStartEnabled,
                presentationSeenBeforeUiCreation.StartAction.IsEnabled);
            Assert.AreEqual(1, driver.ReadCount);
            Assert.AreEqual(0, audio.StartCount);
            Assert.AreEqual(0, sessions.CreateCount);
        }
        finally
        {
            _ = await root.ExitAsync();
        }
    }

    private static TranslationRuntimeDependencies Dependencies()
    {
        return new TranslationRuntimeDependencies(
            new PassingBuildGate(),
            new MissingSettingsStore(),
            new CompatibleDriverManager(),
            new EmptyDeviceCatalog(),
            new NoOpAudioEngine(),
            new UnreachableSessionFactory(),
            new UniformLanguageClassifier(),
            new ImmediateClock(),
            new NoOpRuntimeLog());
    }

    private static TranslationRuntimeDependencies Dependencies(
        IDriverManager driverManager,
        ITranslationAudioEngine audioEngine,
        ITranslationSessionFactory sessionFactory)
    {
        return new TranslationRuntimeDependencies(
            new PassingBuildGate(),
            new MissingSettingsStore(),
            driverManager,
            new EmptyDeviceCatalog(),
            audioEngine,
            sessionFactory,
            new UniformLanguageClassifier(),
            new ImmediateClock(),
            new NoOpRuntimeLog());
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

    private sealed class CapturingTray : IAppTrayLifetime
    {
        public CapturingTray(AppUiCompositionContext context)
        {
            RuntimeCommands = context.RuntimeCommands;
            Presentation = context.Presentation;
        }

        public IRuntimeCommandSink RuntimeCommands { get; }

        public PresentationCoordinator Presentation { get; }

        public bool Started { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingViews : IAppViewLifetime
    {
        public CapturingViews(AppUiCompositionContext context)
        {
            RuntimeCommands = context.RuntimeCommands;
            Snapshots = context.Snapshots;
            Presentation = context.Presentation;
        }

        public IRuntimeCommandSink RuntimeCommands { get; }

        public AppSnapshotStore Snapshots { get; }

        public PresentationCoordinator Presentation { get; }

        public int InitialSurfaceCount { get; private set; }

        public int DashboardCount { get; private set; }

        public ValueTask ShowInitialSurfaceAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitialSurfaceCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowDashboardAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DashboardCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public ValueTask InvokeAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpDiagnostics : IAppDiagnosticsLifetime
    {
        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDiagnostics : IAppDiagnosticsLifetime
    {
        public int StopCount { get; private set; }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassingBuildGate : IWindowsBuildGate
    {
        public ValueTask<RuntimeError?> CheckAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RuntimeError?>(null);
        }
    }

    private sealed class MissingSettingsStore : ISettingsStore
    {
        public ValueTask<RuntimeSettings?> LoadAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RuntimeSettings?>(null);
        }

        public ValueTask SaveAsync(
            RuntimeSettings settings,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompatibleDriverManager : IDriverManager
    {
        public Task<DriverCompatibility> CheckCompatibilityAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new DriverCompatibility(true, "compatible"));
        }
    }

    private sealed class RecordingDriverManager(
        DriverCompatibility compatibility)
        : IDriverManager
    {
        public int ReadCount { get; private set; }

        public Task<DriverCompatibility> CheckCompatibilityAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(compatibility);
        }
    }

    private sealed class EmptyDeviceCatalog : IAudioDeviceCatalog
    {
        public Task<AudioDeviceSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new AudioDeviceSnapshot([]));
        }
    }

    private sealed class NoOpAudioEngine : ITranslationAudioEngine
    {
        public Task StartAsync(
            AudioEngineConfiguration configuration,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public ValueTask<AudioEngineEvent?> PollEventAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AudioEngineEvent?>(null);
        }

        public ValueTask EnqueueInboundTranslationAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask EnqueueOutboundTranslationAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask SetInboundRouteAsync(
            InboundRoute route,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask SetOutboundRouteAsync(
            OutboundRoute route,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAudioEngine : ITranslationAudioEngine
    {
        public int StartCount { get; private set; }

        public Task StartAsync(
            AudioEngineConfiguration configuration,
            CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public ValueTask<AudioEngineEvent?> PollEventAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AudioEngineEvent?>(null);
        }

        public ValueTask EnqueueInboundTranslationAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask EnqueueOutboundTranslationAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask SetInboundRouteAsync(
            InboundRoute route,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask SetOutboundRouteAsync(
            OutboundRoute route,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnreachableSessionFactory :
        ITranslationSessionFactory
    {
        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionRequest request,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromException<ITranslationSession>(
                new InvalidOperationException(
                    "The test runtime must not create a session."));
        }
    }

    private sealed class RecordingSessionFactory :
        ITranslationSessionFactory
    {
        public int CreateCount { get; private set; }

        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionRequest request,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return ValueTask.FromException<ITranslationSession>(
                new InvalidOperationException(
                    "Startup preflight must not create a Translation session."));
        }
    }

    private sealed class UniformLanguageClassifier : ILanguageClassifier
    {
        public ValueTask<LanguageProbabilities> ClassifyAsync(
            string text,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(
                new LanguageProbabilities(
                    1d / 3,
                    1d / 3,
                    1d / 3));
        }
    }

    private sealed class ImmediateClock : IClock
    {
        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpRuntimeLog : IRuntimeLog
    {
        public void Write(
            RuntimeLogLevel level,
            string eventName,
            IReadOnlyDictionary<string, string> safeFields)
        {
        }
    }
}
