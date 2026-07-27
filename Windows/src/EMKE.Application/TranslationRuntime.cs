using EMKE.Core;
using EMKE.Realtime;
using EMKE.Routing;
using System.Collections.Concurrent;

namespace EMKE.Application;

public interface IWindowsBuildGate
{
    ValueTask<RuntimeError?> CheckAsync(CancellationToken cancellationToken);
}

public sealed class TranslationRuntimeDependencies
{
    public TranslationRuntimeDependencies(
        IWindowsBuildGate windowsBuildGate,
        ISettingsStore settingsStore,
        ISecretStore secretStore,
        IDriverManager driverManager,
        IAudioDeviceCatalog deviceCatalog,
        ITranslationAudioEngine audioEngine,
        ITranslationSessionFactory sessionFactory,
        ILanguageClassifier languageClassifier,
        IClock clock,
        IRuntimeLog log)
    {
        WindowsBuildGate =
            windowsBuildGate ?? throw new ArgumentNullException(nameof(windowsBuildGate));
        SettingsStore =
            settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        SecretStore =
            secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        DriverManager =
            driverManager ?? throw new ArgumentNullException(nameof(driverManager));
        DeviceCatalog =
            deviceCatalog ?? throw new ArgumentNullException(nameof(deviceCatalog));
        AudioEngine =
            audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        SessionFactory =
            sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        LanguageClassifier =
            languageClassifier ?? throw new ArgumentNullException(nameof(languageClassifier));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IWindowsBuildGate WindowsBuildGate { get; }

    public ISettingsStore SettingsStore { get; }

    public ISecretStore SecretStore { get; }

    public IDriverManager DriverManager { get; }

    public IAudioDeviceCatalog DeviceCatalog { get; }

    public ITranslationAudioEngine AudioEngine { get; }

    public ITranslationSessionFactory SessionFactory { get; }

    public ILanguageClassifier LanguageClassifier { get; }

    public IClock Clock { get; }

    public IRuntimeLog Log { get; }
}

#pragma warning disable CA1032 // Domain failures expose a stable RuntimeError.

public sealed class RuntimeOperationException : Exception
{
    public RuntimeOperationException(RuntimeError error)
        : base(error?.Code)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RuntimeError Error { get; }
}

#pragma warning restore CA1032

public sealed class TranslationRuntime :
    IAsyncDisposable,
    IDisposable
{
    private const int CommandCapacity = 64;
    private static readonly TimeSpan LocalStopDeadline =
        TimeSpan.FromSeconds(1);

    private readonly object _submissionSync = new();
    private readonly object _audioWorkSync = new();
    private readonly TranslationRuntimeDependencies _dependencies;
    private readonly RuntimeStateReducer _reducer = new();
    private readonly RuntimeSnapshotPublisher _publisher = new();
    private readonly RuntimeCommandMailbox<RuntimeMessage> _mailbox;
    private readonly CancellationTokenSource _actorCancellation = new();
    private readonly RoutingPolicy _routingPolicy = new();
    private readonly PcmLevelMeter _inboundLevel = new();
    private readonly PcmLevelMeter _outboundLevel = new();
    private readonly PcmVoiceActivityDetector _voiceActivity = new();
    private readonly ConcurrentDictionary<Task, byte> _inFlightAudioWork = new();
    private readonly Task _actorStartBarrier;
    private readonly Task _actor;
    private AppSnapshot _currentSnapshot;
    private Task<RuntimeError?>? _activeStart;
    private Task<RuntimeError?>? _activeStop;
    private Task<RuntimeError?>? _activeExit;
    private TaskCompletionSource<RuntimeError?>? _startCompletion;
    private TaskCompletionSource<RuntimeError?>? _stopCompletion;
    private TaskCompletionSource<RuntimeError?>? _exitCompletion;
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _pollCancellation;
    private CancellationTokenSource? _audioWorkCancellation;
    private Task? _pollTask;
    private Task? _startTask;
    private Task? _stopTask;
    private Task? _stopDeadlineTask;
    private ChannelSupervisor? _inbound;
    private ChannelSupervisor? _outbound;
    private RuntimeSettings? _settings;
    private InboundUtteranceBuffer? _inboundBuffer;
    private bool _acceptCapturedAudio;
    private bool _acceptEngineWrites;
    private bool _audioStarted;
    private bool _priorityWakeQueued;
    private bool _stopRequested;
    private bool _exitRequested;
    private bool _exitTerminal;
    private bool _waitingForStartBeforeStop;
    private bool _actorShouldExit;
    private bool _cleanupPending;
    private long _drainingGeneration;
    private long _droppedAudioFrames;
    private RoutingPolicySnapshot _routingSnapshot;
    private int _disposed;

    public TranslationRuntime(TranslationRuntimeDependencies dependencies)
        : this(dependencies, Task.CompletedTask)
    {
    }

    internal TranslationRuntime(
        TranslationRuntimeDependencies dependencies,
        Task actorStartBarrier)
    {
        _dependencies =
            dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _actorStartBarrier =
            actorStartBarrier ?? throw new ArgumentNullException(nameof(actorStartBarrier));
        _routingSnapshot = _routingPolicy.Snapshot;
        _currentSnapshot = _reducer.Current;
        _mailbox = new RuntimeCommandMailbox<RuntimeMessage>(
            CommandCapacity,
            DropMessage);
        _actor = Task.Run(ActorLoopAsync);
    }

    public AppSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    public IObservable<AppSnapshot> Snapshots => _publisher;

    public Task<RuntimeError?> StartAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<RuntimeError?>(cancellationToken);
        }

        lock (_submissionSync)
        {
            ObjectDisposedException.ThrowIf(_exitTerminal, this);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_cleanupPending)
            {
                return Task.FromResult<RuntimeError?>(Error(
                    ErrorCategory.CloseTimeout,
                    "translationRuntime.stopCleanupPending",
                    RecoveryAction.Retry));
            }

            if (_activeStart is { IsCompleted: false })
            {
                return _activeStart;
            }

            if (CurrentSnapshot.RuntimeState is (
                RuntimeState.Running or RuntimeState.Degraded))
            {
                return Task.FromResult<RuntimeError?>(null);
            }

            TaskCompletionSource<RuntimeError?> completion =
                NewCompletion();
            _startCompletion = completion;
            _activeStart = completion.Task;
            _mailbox.TryWrite(new StartRequestedMessage(completion));
            return _activeStart;
        }
    }

    public Task<RuntimeError?> StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<RuntimeError?>(cancellationToken);
        }

        lock (_submissionSync)
        {
            ObjectDisposedException.ThrowIf(_exitTerminal, this);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_activeStop is { IsCompleted: false })
            {
                return _activeStop;
            }

            if (CurrentSnapshot.RuntimeState == RuntimeState.Stopped
                && _startCompletion is null)
            {
                return Task.FromResult<RuntimeError?>(null);
            }

            _stopCompletion = NewCompletion();
            _activeStop = _stopCompletion.Task;
            _stopRequested = true;
            EnsurePriorityWakeLocked();
            return _activeStop;
        }
    }

    public Task<RuntimeError?> ExitAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<RuntimeError?>(cancellationToken);
        }

        lock (_submissionSync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_activeExit is { IsCompleted: false })
            {
                return _activeExit;
            }

            ObjectDisposedException.ThrowIf(_exitTerminal, this);
            _exitCompletion = NewCompletion();
            _activeExit = _exitCompletion.Task;
            _exitRequested = true;
            _exitTerminal = true;
            if (CurrentSnapshot.RuntimeState != RuntimeState.Stopped
                || _startCompletion is not null)
            {
                _stopRequested = true;
            }

            EnsurePriorityWakeLocked();
            return _activeExit;
        }
    }

    public Task<RuntimeError?> SubmitAsync(
        RuntimeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            RuntimeCommand.Start => StartAsync(cancellationToken),
            RuntimeCommand.Stop => StopAsync(cancellationToken),
            RuntimeCommand.Exit => ExitAsync(cancellationToken),
            _ => SubmitOrdinary(command, cancellationToken),
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _startCancellation?.Cancel();
        _pollCancellation?.Cancel();
        _audioWorkCancellation?.Cancel();
        _actorCancellation.Cancel();
        _inbound?.Dispose();
        _outbound?.Dispose();
        _mailbox.Dispose();
        _publisher.Dispose();
        _startCancellation?.Dispose();
        _pollCancellation?.Dispose();
        _audioWorkCancellation?.Dispose();
        _actorCancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (CurrentSnapshot.RuntimeState != RuntimeState.Stopped)
            {
                await StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await _actorCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await _actor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Dispose();
        }
    }

    private Task<RuntimeError?> SubmitOrdinary(
        RuntimeCommand command,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<RuntimeError?>(cancellationToken);
        }

        lock (_submissionSync)
        {
            ObjectDisposedException.ThrowIf(_exitTerminal, this);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            TaskCompletionSource<RuntimeError?> completion = NewCompletion();
            _mailbox.TryWrite(new OrdinaryCommandMessage(command, completion));
            return completion.Task;
        }
    }

    private void EnsurePriorityWakeLocked()
    {
        if (_priorityWakeQueued)
        {
            return;
        }

        _priorityWakeQueued = true;
        _mailbox.TryWritePriority(PriorityWakeMessage.Instance);
    }

    private async Task ActorLoopAsync()
    {
        try
        {
            await _actorStartBarrier.WaitAsync(_actorCancellation.Token)
                .ConfigureAwait(false);
            while (!_actorShouldExit)
            {
                RuntimeMailboxRead<RuntimeMessage> read =
                    await _mailbox.ReadAsync(_actorCancellation.Token)
                        .ConfigureAwait(false);
                if (read.IsPriority)
                {
                    HandlePriorityWake();
                }
                else
                {
                    HandleMessage(read.Item);
                }
            }
        }
        catch (OperationCanceledException) when (
            _actorCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _mailbox.Dispose();
        }
    }

    private void HandlePriorityWake()
    {
        bool stop;
        bool exit;
        lock (_submissionSync)
        {
            _priorityWakeQueued = false;
            stop = _stopRequested;
            exit = _exitRequested;
            if (stop
                && CurrentSnapshot.RuntimeState == RuntimeState.Stopped
                && _startCompletion is not null)
            {
                _startCompletion.TrySetResult(Error(
                    ErrorCategory.Protocol,
                    "translationRuntime.startCanceled",
                    RecoveryAction.None));
                _startCompletion = null;
            }
        }

        if (!stop && exit && CurrentSnapshot.RuntimeState == RuntimeState.Stopped)
        {
            CompleteExit(null);
            _actorShouldExit = true;
            return;
        }

        if (!stop || CurrentSnapshot.RuntimeState == RuntimeState.Stopping)
        {
            return;
        }

        RuntimeState priorState = CurrentSnapshot.RuntimeState;
        _drainingGeneration = _reducer.Generation;
        long stopGeneration = _reducer.BeginStop();
        Publish(_reducer.Current);
        _acceptCapturedAudio = false;
        _ = _pollCancellation?.CancelAsync();
        Task deadline = BeginStopDeadline();
        _stopDeadlineTask = deadline;
        if (priorState == RuntimeState.Starting)
        {
            _waitingForStartBeforeStop = true;
            _ = _startCancellation?.CancelAsync();
            ObserveDetached(WatchStartingStopDeadlineAsync(
                stopGeneration,
                deadline));
            return;
        }

        StartStopPipeline(stopGeneration, deadline);
    }

    private void HandleMessage(RuntimeMessage message)
    {
        switch (message)
        {
            case StartRequestedMessage start:
                HandleStartRequested(start);
                break;
            case StartCompletedMessage completed:
                HandleStartCompleted(completed);
                break;
            case StopCompletedMessage stopped:
                HandleStopCompleted(stopped);
                break;
            case StopDeadlineElapsedMessage deadline:
                HandleStartingStopDeadline(deadline);
                break;
            case SupervisorMessage supervisor:
                HandleSupervisorMessage(supervisor);
                break;
            case AudioCapturedMessage audio:
                HandleAudioCaptured(audio);
                break;
            case OrdinaryCommandMessage ordinary:
                HandleOrdinaryCommand(ordinary);
                break;
            case DevicesRefreshedMessage devices:
                HandleDevicesRefreshed(devices);
                break;
            case ClassifiedMessage classified:
                HandleClassified(classified);
                break;
        }
    }

    private void HandleStartRequested(StartRequestedMessage message)
    {
        if (!ReferenceEquals(message.Completion, _startCompletion)
            || CurrentSnapshot.RuntimeState is not (
                RuntimeState.Stopped or RuntimeState.Failed))
        {
            message.Completion.TrySetResult(Error(
                ErrorCategory.Protocol,
                "translationRuntime.invalidStartState",
                RecoveryAction.None));
            return;
        }

        long generation = _reducer.BeginStart();
        Publish(_reducer.Current);
        _startCancellation?.Dispose();
        _startCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _actorCancellation.Token);
        _startTask = ExecuteStartAsync(
            generation,
            message.Completion,
            _startCancellation.Token);
    }

    private async Task ExecuteStartAsync(
        long generation,
        TaskCompletionSource<RuntimeError?> completion,
        CancellationToken cancellationToken)
    {
        StartOutcome outcome;
        try
        {
            outcome = await BuildStartOutcomeAsync(
                generation,
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The start worker must always return a stable outcome to the actor.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            RuntimeError error = MapException(exception);
            SafeLogFailure(error);
            outcome = StartOutcome.Failed(error);
        }

        await PostReliableAsync(
            new StartCompletedMessage(
                generation,
                outcome,
                completion,
                DropStartOutcome),
            _actorCancellation.Token).ConfigureAwait(false);
    }

    private async Task<StartOutcome> BuildStartOutcomeAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        ChannelSupervisor? inbound = null;
        ChannelSupervisor? outbound = null;
        bool audioStarted = false;
        try
        {
            RuntimeError? osError =
                await _dependencies.WindowsBuildGate.CheckAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (osError is not null)
            {
                return StartOutcome.Failed(osError);
            }

            RuntimeSettings? settings =
                await _dependencies.SettingsStore.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (settings is null)
            {
                return StartOutcome.Failed(Error(
                    ErrorCategory.Configuration,
                    "translationRuntime.settingsMissing",
                    RecoveryAction.EditSettings));
            }

            using (ISecretBuffer? secret =
                   await _dependencies.SecretStore.LoadAsync(
                       "translationApiKey",
                       cancellationToken).ConfigureAwait(false))
            {
                if (secret is null || IsWhiteSpace(secret.Memory.Span))
                {
                    return StartOutcome.Failed(Error(
                        ErrorCategory.Authentication,
                        "translationRuntime.secretMissing",
                        RecoveryAction.UpdateApiKey));
                }
            }

            DriverCompatibility driver =
                await _dependencies.DriverManager.CheckCompatibilityAsync(
                    cancellationToken).ConfigureAwait(false);
            if (!driver.IsCompatible)
            {
                return StartOutcome.Failed(Error(
                    ErrorCategory.Driver,
                    "translationRuntime.driverIncompatible",
                    RecoveryAction.InstallDriver));
            }

            AudioDeviceSnapshot devices =
                await _dependencies.DeviceCatalog.GetSnapshotAsync(
                    cancellationToken).ConfigureAwait(false);
            DeviceSelection? selection = SelectDevices(devices);
            if (selection is null)
            {
                return StartOutcome.Failed(Error(
                    ErrorCategory.Device,
                    "translationRuntime.defaultPhysicalDeviceMissing",
                    RecoveryAction.SelectDevice));
            }

            await _dependencies.AudioEngine.StartAsync(
                new AudioEngineConfiguration(
                    selection.Input.Id,
                    selection.Output.Id,
                    sampleRate: 24_000,
                    channelCount: 1),
                cancellationToken).ConfigureAwait(false);
            audioStarted = true;
            cancellationToken.ThrowIfCancellationRequested();

#pragma warning disable CA2000 // Ownership transfers into StartOutcome or rollback.
            inbound = CreateSupervisor(
                AudioDirection.Inbound,
                generation,
                new TranslationSessionConfiguration(
                    settings.TargetLanguage,
                    settings.SourceLanguage,
                    settings.Model));
#pragma warning restore CA2000
            RuntimeError? inboundError =
                await inbound.ConnectAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (inboundError is not null)
            {
                _ = await RollBackStartAsync(
                    inbound,
                    outbound: null,
                    audioStarted).ConfigureAwait(false);
                inbound = null;
                audioStarted = false;
                return StartOutcome.Failed(inboundError);
            }

            bool sameLanguage =
                settings.SourceLanguage == settings.TargetLanguage;
            RuntimeError? outboundError = null;
            if (!sameLanguage && !settings.OutboundBypass)
            {
#pragma warning disable CA2000 // Ownership transfers into StartOutcome or rollback.
                outbound = CreateSupervisor(
                    AudioDirection.Outbound,
                    generation,
                    new TranslationSessionConfiguration(
                        settings.SourceLanguage,
                        settings.TargetLanguage,
                        settings.Model));
#pragma warning restore CA2000
                outboundError = await outbound.ConnectAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (outboundError is not null)
                {
                    _ = await DisposeSupervisorAsync(
                        outbound,
                        current: null).ConfigureAwait(false);
                    outbound = null;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new StartOutcome(
                settings,
                driver,
                selection,
                inbound,
                outbound,
                audioStarted,
                sameLanguage || settings.OutboundBypass,
                outboundError);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _ = await RollBackStartAsync(inbound, outbound, audioStarted)
                .ConfigureAwait(false);
            return StartOutcome.Failed(Error(
                ErrorCategory.Protocol,
                "translationRuntime.startCanceled",
                RecoveryAction.None));
        }
#pragma warning disable CA1031 // Platform adapters are mapped to stable secret-free runtime errors.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _ = await RollBackStartAsync(inbound, outbound, audioStarted)
                .ConfigureAwait(false);
            RuntimeError error = MapException(exception);
            SafeLogFailure(error);
            return StartOutcome.Failed(error);
        }
    }

    private void HandleStartCompleted(StartCompletedMessage message)
    {
        bool stopping = CurrentSnapshot.RuntimeState == RuntimeState.Stopping
            && _waitingForStartBeforeStop;
        if (message.Generation != _reducer.Generation && !stopping)
        {
            Task cleanup = CleanupOutcomeAsync(message.Outcome);
            if (IsCleanupPending())
            {
                TrackDeferredCleanup(cleanup);
            }
            else
            {
                ObserveDetached(cleanup);
            }

            CompleteStart(message.Outcome.Error);
            return;
        }

        if (stopping)
        {
            _inbound = message.Outcome.Inbound;
            _outbound = message.Outcome.Outbound;
            _audioStarted = message.Outcome.AudioStarted;
            CompleteStart(message.Outcome.Error);
            _waitingForStartBeforeStop = false;
            StartStopPipeline(
                _reducer.Generation,
                _stopDeadlineTask ?? BeginStopDeadline());
            return;
        }

        StartOutcome outcome = message.Outcome;
        if (outcome.Settings is null
            || outcome.Driver is null
            || outcome.Selection is null
            || outcome.Inbound is null)
        {
            Publish(_reducer.FailStart(
                message.Generation,
                outcome.Error ?? Error(
                    ErrorCategory.Protocol,
                    "translationRuntime.startFailed",
                    RecoveryAction.Retry)));
            CompleteStart(outcome.Error);
            return;
        }

        _settings = outcome.Settings;
        _inbound = outcome.Inbound;
        _outbound = outcome.Outbound;
        _audioStarted = outcome.AudioStarted;
        _inboundBuffer = new InboundUtteranceBuffer(
            outcome.Settings.SourceLanguage,
            _dependencies.Clock);
        _inboundBuffer.Begin();
        RoutingPolicySnapshot routing =
            _routingPolicy.Start(outcome.OutboundBypassed);
        if (outcome.Error is not null)
        {
            routing = _routingPolicy.FailOutbound(outcome.Error.Category);
        }
        _routingSnapshot = routing;
        _audioWorkCancellation?.Dispose();
        _audioWorkCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _actorCancellation.Token);
        lock (_audioWorkSync)
        {
            _acceptEngineWrites = true;
        }

        Publish(_reducer.SetStartupEnvironment(
            message.Generation,
            new AudioSelection(
                outcome.Selection.Input.Label,
                outcome.Selection.Output.Label),
            outcome.Driver));
        _ = _reducer.TryCompleteStart(
            message.Generation,
            routing,
            outcome.Error,
            out AppSnapshot started);
        Publish(started);
        _acceptCapturedAudio = true;
        StartPolling(message.Generation);
        TrackRouteMutation(routing);
        CompleteStart(outcome.Error);
    }

    private void StartPolling(long generation)
    {
        _pollCancellation?.Dispose();
        _pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _actorCancellation.Token);
        _pollTask = PollAudioAsync(
            generation,
            _pollCancellation.Token);
    }

    private async Task PollAudioAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                AudioEngineEvent? audio =
                    await _dependencies.AudioEngine.PollEventAsync(
                        cancellationToken).ConfigureAwait(false);
                if (audio is null)
                {
                    continue;
                }

                _mailbox.TryWrite(new AudioCapturedMessage(
                    generation,
                    audio,
                    OnDroppedAudio));
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Poll failures become safe actor notifications.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            RuntimeError error = MapException(exception);
            await PostReliableAsync(
                new SupervisorMessage(
                    new ChannelSupervisorNotification(
                        generation,
                        AudioDirection.Inbound,
                        ChannelState.Failed,
                        null,
                        error),
                    NewCompletion()),
                _actorCancellation.Token).ConfigureAwait(false);
        }
    }

    private Task BeginStopDeadline()
    {
        try
        {
            return _dependencies.Clock.DelayAsync(
                LocalStopDeadline,
                _actorCancellation.Token).AsTask();
        }
#pragma warning disable CA1031 // A broken clock fails closed as an immediate local timeout.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            SafeLogFailure(MapException(exception));
            return Task.CompletedTask;
        }
    }

    private async Task WatchStartingStopDeadlineAsync(
        long stopGeneration,
        Task deadline)
    {
        try
        {
            await deadline.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _actorCancellation.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // A failed clock task is treated as elapsed.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            SafeLogFailure(MapException(exception));
        }

        await PostReliableAsync(
            new StopDeadlineElapsedMessage(stopGeneration),
            _actorCancellation.Token).ConfigureAwait(false);
    }

    private void HandleStartingStopDeadline(
        StopDeadlineElapsedMessage message)
    {
        if (message.Generation != _reducer.Generation
            || CurrentSnapshot.RuntimeState != RuntimeState.Stopping
            || !_waitingForStartBeforeStop)
        {
            return;
        }

        _waitingForStartBeforeStop = false;
        MarkCleanupPending();
        CompleteStart(Error(
            ErrorCategory.Protocol,
            "translationRuntime.startCanceled",
            RecoveryAction.None));
        CompleteStoppedState(
            message.Generation,
            Error(
                ErrorCategory.CloseTimeout,
                "translationRuntime.localCloseTimeout",
                RecoveryAction.Retry));
    }

    private void StartStopPipeline(
        long stopGeneration,
        Task deadline)
    {
        if (_stopTask is { IsCompleted: false })
        {
            return;
        }

        ChannelSupervisor? inbound = _inbound;
        ChannelSupervisor? outbound = _outbound;
        Task? poll = _pollTask;
        bool audioStarted = _audioStarted;
        _inbound = null;
        _outbound = null;
        _pollTask = null;
        _audioStarted = false;
        TaskCompletionSource<RuntimeError?>? completion;
        lock (_submissionSync)
        {
            completion = _stopCompletion;
        }

        _stopTask = ExecuteStopAsync(
            stopGeneration,
            inbound,
            outbound,
            poll,
            deadline,
            audioStarted,
            completion);
    }

    private async Task ExecuteStopAsync(
        long stopGeneration,
        ChannelSupervisor? inbound,
        ChannelSupervisor? outbound,
        Task? poll,
        Task deadline,
        bool audioStarted,
        TaskCompletionSource<RuntimeError?>? completion)
    {
        object audioStopSync = new();
        Task<RuntimeError?>? audioStopTask = null;
        Task<RuntimeError?> StopAudioOnce()
        {
            lock (audioStopSync)
            {
                audioStopTask ??= audioStarted
                    ? StopAudioSafelyAsync()
                    : Task.FromResult<RuntimeError?>(null);
                return audioStopTask;
            }
        }

        RuntimeError? error;
        try
        {
            Task<RuntimeError?> graceful = CompleteGracefulStopAsync(
                inbound,
                outbound,
                poll,
                StopAudioOnce);
            Task winner =
                await Task.WhenAny(graceful, deadline).ConfigureAwait(false);
            if (ReferenceEquals(winner, graceful))
            {
                error = await graceful.ConfigureAwait(false);
            }
            else
            {
                error = Error(
                    ErrorCategory.CloseTimeout,
                    "translationRuntime.localCloseTimeout",
                    RecoveryAction.Retry);
                CloseAudioWorkAdmission(cancel: true);
                DisposeSupervisor(inbound);
                DisposeSupervisor(outbound);
                ObserveDetached(graceful);
                Task<RuntimeError?> deferredStop =
                    FinishTimedOutStopAsync(StopAudioOnce);
                TrackDeferredCleanup(Task.WhenAll(graceful, deferredStop));
                if (deferredStop.IsCompleted)
                {
                    _ = await deferredStop.ConfigureAwait(false);
                }
                else
                {
                    ObserveDetached(deferredStop);
                }
            }
        }
#pragma warning disable CA1031 // The stop worker must always return a stable outcome to the actor.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            error = MapException(exception);
            SafeLogFailure(error);
            CloseAudioWorkAdmission(cancel: true);
            DisposeSupervisor(inbound);
            DisposeSupervisor(outbound);
            Task<RuntimeError?> emergencyStop =
                FinishTimedOutStopAsync(StopAudioOnce);
            TrackDeferredCleanup(emergencyStop);
            ObserveDetached(emergencyStop);
        }

        await PostReliableAsync(
            new StopCompletedMessage(stopGeneration, error, completion),
            _actorCancellation.Token).ConfigureAwait(false);
    }

    private async Task<RuntimeError?> CompleteGracefulStopAsync(
        ChannelSupervisor? inbound,
        ChannelSupervisor? outbound,
        Task? poll,
        Func<Task<RuntimeError?>> stopAudio)
    {
        RuntimeError? error = null;
        Task inboundClose =
            inbound?.CloseAsync(CancellationToken.None) ?? Task.CompletedTask;
        Task outboundClose =
            outbound?.CloseAsync(CancellationToken.None) ?? Task.CompletedTask;
        error = await CaptureFailureAsync(
            Task.WhenAll(inboundClose, outboundClose),
            error).ConfigureAwait(false);
        if (poll is not null)
        {
            error = await CaptureFailureAsync(poll, error)
                .ConfigureAwait(false);
        }

        CloseAudioWorkAdmission(cancel: false);
        error = await DrainAudioWorkAsync(error).ConfigureAwait(false);
        RuntimeError? stopError = await stopAudio().ConfigureAwait(false);
        error ??= stopError;
        error = await DisposeSupervisorAsync(inbound, error)
            .ConfigureAwait(false);
        error = await DisposeSupervisorAsync(outbound, error)
            .ConfigureAwait(false);
        return error;
    }

    private async Task<RuntimeError?> FinishTimedOutStopAsync(
        Func<Task<RuntimeError?>> stopAudio)
    {
        RuntimeError? error = await DrainAudioWorkAsync(error: null)
            .ConfigureAwait(false);
        RuntimeError? stopError = await stopAudio().ConfigureAwait(false);
        return error ?? stopError;
    }

    private async Task<RuntimeError?> DrainAudioWorkAsync(
        RuntimeError? error)
    {
        while (true)
        {
            Task[] pending = _inFlightAudioWork.Keys.ToArray();
            if (pending.Length == 0)
            {
                return error;
            }

            error = await CaptureFailureAsync(Task.WhenAll(pending), error)
                .ConfigureAwait(false);
        }
    }

    private static async Task<RuntimeError?> CaptureFailureAsync(
        Task operation,
        RuntimeError? current)
    {
        try
        {
            await operation.ConfigureAwait(false);
            return current;
        }
        catch (OperationCanceledException)
        {
            return current;
        }
#pragma warning disable CA1031 // Cleanup failures are reduced to a stable runtime error.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return current ?? MapException(exception);
        }
    }

    private async Task<RuntimeError?> StopAudioSafelyAsync()
    {
        try
        {
            await _dependencies.AudioEngine.StopAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return null;
        }
#pragma warning disable CA1031 // Native stop failures are reduced to a stable runtime error.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return MapException(exception);
        }
    }

    private static async Task<RuntimeError?> DisposeSupervisorAsync(
        ChannelSupervisor? supervisor,
        RuntimeError? current)
    {
        if (supervisor is null)
        {
            return current;
        }

        try
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
            return current;
        }
#pragma warning disable CA1031 // Cleanup failures are reduced to a stable runtime error.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return current ?? MapException(exception);
        }
    }

    private static void DisposeSupervisor(ChannelSupervisor? supervisor)
    {
        try
        {
            supervisor?.Dispose();
        }
#pragma warning disable CA1031 // Timeout cleanup cannot escape the stop completion path.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void CloseAudioWorkAdmission(bool cancel)
    {
        CancellationTokenSource? cancellation;
        lock (_audioWorkSync)
        {
            _acceptEngineWrites = false;
            cancellation = _audioWorkCancellation;
        }

        if (cancel && cancellation is not null)
        {
            try
            {
                ObserveDetached(cancellation.CancelAsync());
            }
#pragma warning disable CA1031 // A disposed cancellation source is already closed.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private static void ObserveDetached(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void MarkCleanupPending()
    {
        lock (_submissionSync)
        {
            _cleanupPending = true;
        }
    }

    private bool IsCleanupPending()
    {
        lock (_submissionSync)
        {
            return _cleanupPending;
        }
    }

    private void TrackDeferredCleanup(Task cleanup)
    {
        MarkCleanupPending();
        _ = cleanup.ContinueWith(
            static (completed, state) =>
            {
                TranslationRuntime runtime = (TranslationRuntime)state!;
                _ = completed.Exception;
                lock (runtime._submissionSync)
                {
                    runtime._cleanupPending = false;
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleStopCompleted(StopCompletedMessage message)
    {
        if (message.Generation != _reducer.Generation)
        {
            return;
        }

        CompleteStoppedState(message.Generation, message.Error);
    }

    private void CompleteStoppedState(
        long generation,
        RuntimeError? error)
    {
        _routingPolicy.Stop();
        _routingSnapshot = _routingPolicy.Snapshot;
        _inboundBuffer?.Stop();
        _inboundBuffer = null;
        _inboundLevel.Reset();
        _outboundLevel.Reset();
        _voiceActivity.Reset();
        Interlocked.Exchange(ref _droppedAudioFrames, 0);
        _settings = null;
        _stopDeadlineTask = null;
        Publish(_reducer.CompleteStop(generation));
        CompleteStop(error);
        if (_exitRequested)
        {
            CompleteExit(error);
            _actorShouldExit = true;
        }
    }

    private void HandleSupervisorMessage(SupervisorMessage message)
    {
        ChannelSupervisorNotification notification = message.Notification;
        bool drainingTail =
            CurrentSnapshot.RuntimeState == RuntimeState.Stopping
            && notification.Generation == _drainingGeneration
            && notification.Event is not null;
        if (notification.Generation != _reducer.Generation && !drainingTail)
        {
            message.Drop();
            return;
        }

        if (notification.Event is TranslationSessionEvent.AudioDelta audio)
        {
            if (!TryTrackAudioWork(
                    cancellationToken => EnqueueTranslatedAsync(
                        notification.Direction,
                        audio,
                        drainingTail,
                        message.Completion,
                        cancellationToken)))
            {
                message.Drop();
            }

            return;
        }

        if (notification.Event is TranslationSessionEvent.SourceCaption source)
        {
            string translated = CurrentSnapshot.TranslatedCaption;
            Publish(_reducer.UpdateCaptions(
                _reducer.Generation,
                source.Text,
                translated));
            _inboundBuffer?.AppendTranscript(source.Text);
            _ = ClassifyAsync(
                notification.Generation,
                source.Text);
        }
        else if (notification.Event is TranslationSessionEvent.TranslatedCaption translated)
        {
            Publish(_reducer.UpdateCaptions(
                _reducer.Generation,
                CurrentSnapshot.SourceCaption,
                translated.Text));
        }
        else if (notification.Event is TranslationSessionEvent.Completed
                 && notification.Direction == AudioDirection.Inbound)
        {
            IReadOnlyList<byte[]> selected =
                _inboundBuffer?.Finish(voiced: true) ?? [];
            _inboundBuffer?.Begin();
            RoutingPolicySnapshot routing =
                _routingPolicy.CompleteInboundUtterance();
            _routingSnapshot = routing;
            Publish(_reducer.ApplyRouting(
                _reducer.Generation,
                routing,
                error: null));
            TrackRouteMutation(routing);
            if (!TryTrackAudioWork(
                    cancellationToken => EnqueueChunksAsync(
                        AudioDirection.Inbound,
                        selected,
                        message.Completion,
                        cancellationToken)))
            {
                message.Completion.TrySetResult(Error(
                    ErrorCategory.CloseTimeout,
                    "translationRuntime.audioWorkClosed",
                    RecoveryAction.Retry));
            }

            return;
        }

        if (notification.Error is not null)
        {
            ApplyChannelFailure(notification);
        }
        else if (notification.Event is null
                 && notification.State == ChannelState.Connected
                 && CurrentSnapshot.RuntimeState is (
                     RuntimeState.Running or RuntimeState.Degraded))
        {
            ApplyChannelRecovery(notification.Direction);
        }

        message.Completion.TrySetResult(null);
    }

    private async Task EnqueueTranslatedAsync(
        AudioDirection direction,
        TranslationSessionEvent.AudioDelta audio,
        bool drainingTail,
        TaskCompletionSource<RuntimeError?> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            if (direction == AudioDirection.Inbound)
            {
                IReadOnlyList<byte[]> selected =
                    _inboundBuffer?.AppendTranslation(audio.Pcm16.Span)
                    ?? [audio.Pcm16.ToArray()];
                if (drainingTail && selected.Count == 0)
                {
                    selected = _inboundBuffer?.Finish(voiced: true)
                        ?? [audio.Pcm16.ToArray()];
                }

                foreach (byte[] chunk in selected)
                {
                    await _dependencies.AudioEngine
                        .EnqueueInboundTranslationAsync(
                            chunk,
                            cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await _dependencies.AudioEngine
                    .EnqueueOutboundTranslationAsync(
                        audio.Pcm16,
                        cancellationToken).ConfigureAwait(false);
            }

            completion.TrySetResult(null);
        }
#pragma warning disable CA1031 // PCM ownership must be released on every adapter failure.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            completion.TrySetResult(MapException(exception));
        }
        finally
        {
            audio.Dispose();
        }
    }

    private async Task EnqueueChunksAsync(
        AudioDirection direction,
        IReadOnlyList<byte[]> chunks,
        TaskCompletionSource<RuntimeError?> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (byte[] chunk in chunks)
            {
                if (direction == AudioDirection.Inbound)
                {
                    await _dependencies.AudioEngine
                        .EnqueueInboundTranslationAsync(
                            chunk,
                            cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _dependencies.AudioEngine
                        .EnqueueOutboundTranslationAsync(
                            chunk,
                            cancellationToken).ConfigureAwait(false);
                }
            }

            completion.TrySetResult(null);
        }
#pragma warning disable CA1031 // Adapter failures are returned through the actor completion.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            completion.TrySetResult(MapException(exception));
        }
    }

    private void ApplyChannelFailure(
        ChannelSupervisorNotification notification)
    {
        RuntimeError error = notification.Error!;
        RoutingPolicySnapshot routing = notification.Direction switch
        {
            AudioDirection.Inbound =>
                _routingPolicy.FailInbound(error.Category),
            AudioDirection.Outbound
                when notification.State == ChannelState.Reconnecting =>
                _routingPolicy.DisconnectOutbound(),
            AudioDirection.Outbound =>
                _routingPolicy.FailOutbound(error.Category),
            _ => _routingPolicy.Snapshot,
        };
        _routingSnapshot = routing;
        Publish(_reducer.ApplyRouting(
            _reducer.Generation,
            routing,
            error));
        TrackRouteMutation(routing);
    }

    private void ApplyChannelRecovery(AudioDirection direction)
    {
        RoutingPolicySnapshot routing = direction switch
        {
            AudioDirection.Inbound => _routingPolicy.RecoverInbound(),
            AudioDirection.Outbound => _routingPolicy.ReconnectOutbound(),
            _ => _routingSnapshot,
        };
        _routingSnapshot = routing;
        Publish(_reducer.ApplyRouting(
            _reducer.Generation,
            routing,
            error: null));
        TrackRouteMutation(routing);
    }

    private void HandleAudioCaptured(AudioCapturedMessage message)
    {
        if (!_acceptCapturedAudio
            || message.Generation != _reducer.Generation)
        {
            message.Drop();
            return;
        }

        AudioEngineEvent audio = message.Audio;
        AudioDirection? direction = audio.Direction;
        if (direction is null)
        {
            audio.Dispose();
            return;
        }

        if (direction == AudioDirection.Inbound)
        {
            double level = _inboundLevel.Observe(audio.Pcm16.Span);
            PcmVoiceActivityEvent activity =
                _voiceActivity.Observe(audio.Pcm16.Span);
            IReadOnlyList<byte[]> selected =
                _inboundBuffer?.AppendOriginal(audio.Pcm16.Span) ?? [];
            if (activity == PcmVoiceActivityEvent.SpeechEnded
                && _inboundBuffer is not null)
            {
                List<byte[]> completed = [.. selected];
                completed.AddRange(_inboundBuffer.Finish(voiced: true));
                _inboundBuffer.Begin();
                selected = completed;
                RoutingPolicySnapshot routing =
                    _routingPolicy.CompleteInboundUtterance();
                _routingSnapshot = routing;
                Publish(_reducer.ApplyRouting(
                    message.Generation,
                    routing,
                    error: null));
                TrackRouteMutation(routing);
            }

            if (selected.Count > 0)
            {
                _ = TryTrackAudioWork(
                    cancellationToken => EnqueueChunksAsync(
                        AudioDirection.Inbound,
                        selected,
                        NewCompletion(),
                        cancellationToken));
            }

            Publish(_reducer.UpdateLevels(
                message.Generation,
                level,
                CurrentSnapshot.OutboundLevel));
            if (_inbound is not null)
            {
#pragma warning disable CA2025 // Stop snapshots and awaits every tracked PCM task before supervisor disposal.
                if (!TryTrackAudioWork(
                        cancellationToken => SendCapturedAsync(
                            _inbound,
                            audio,
                            cancellationToken)))
                {
                    audio.Dispose();
                }
#pragma warning restore CA2025
                return;
            }
        }
        else
        {
            double level = _outboundLevel.Observe(audio.Pcm16.Span);
            Publish(_reducer.UpdateLevels(
                message.Generation,
                CurrentSnapshot.InboundLevel,
                level));
            if (_outbound is not null)
            {
#pragma warning disable CA2025 // Stop snapshots and awaits every tracked PCM task before supervisor disposal.
                if (!TryTrackAudioWork(
                        cancellationToken => SendCapturedAsync(
                            _outbound,
                            audio,
                            cancellationToken)))
                {
                    audio.Dispose();
                }
#pragma warning restore CA2025
                return;
            }
        }

        audio.Dispose();
    }

    private static async Task SendCapturedAsync(
        ChannelSupervisor supervisor,
        AudioEngineEvent audio,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await supervisor.SendPcmAsync(
                audio.Pcm16,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            audio.Dispose();
        }
    }

    private bool TryTrackAudioWork(
        Func<CancellationToken, Task> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        Task task;
        lock (_audioWorkSync)
        {
            if (!_acceptEngineWrites
                || _audioWorkCancellation is null)
            {
                return false;
            }

            task = start(_audioWorkCancellation.Token);
            _inFlightAudioWork.TryAdd(task, 0);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                ConcurrentDictionary<Task, byte> tracked =
                    (ConcurrentDictionary<Task, byte>)state!;
                tracked.TryRemove(completed, out _);
                _ = completed.Exception;
            },
            _inFlightAudioWork,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    private void HandleOrdinaryCommand(OrdinaryCommandMessage message)
    {
        switch (message.Command)
        {
            case RuntimeCommand.RefreshDevices:
                _ = RefreshDevicesAsync(
                    _reducer.Generation,
                    message.Completion);
                break;
            case RuntimeCommand.SetOutboundBypass outbound:
                RoutingPolicySnapshot routing = outbound.Enabled
                    ? _routingPolicy.EnableOutboundBypass()
                    : _routingSnapshot;
                _routingSnapshot = routing;
                Publish(_reducer.ApplyRouting(
                    _reducer.Generation,
                    routing,
                    error: null));
                TrackRouteMutation(routing);
                message.Completion.TrySetResult(null);
                break;
            default:
                message.Completion.TrySetResult(Error(
                    ErrorCategory.Configuration,
                    "translationRuntime.commandUnsupported",
                    RecoveryAction.None));
                break;
        }
    }

    private async Task RefreshDevicesAsync(
        long generation,
        TaskCompletionSource<RuntimeError?> completion)
    {
        try
        {
            AudioDeviceSnapshot devices =
                await _dependencies.DeviceCatalog.GetSnapshotAsync(
                    _actorCancellation.Token).ConfigureAwait(false);
            await PostReliableAsync(
                new DevicesRefreshedMessage(
                    generation,
                    devices,
                    completion,
                    null),
                _actorCancellation.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Device adapter failures map to stable runtime errors.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            await PostReliableAsync(
                new DevicesRefreshedMessage(
                    generation,
                    null,
                    completion,
                    MapException(exception)),
                _actorCancellation.Token).ConfigureAwait(false);
        }
    }

    private void HandleDevicesRefreshed(DevicesRefreshedMessage message)
    {
        if (message.Generation != _reducer.Generation)
        {
            message.Completion.TrySetResult(null);
            return;
        }

        if (message.Error is not null || message.Devices is null)
        {
            message.Completion.TrySetResult(message.Error);
            return;
        }

        DeviceSelection? selected = SelectDevices(message.Devices);
        if (selected is null)
        {
            message.Completion.TrySetResult(Error(
                ErrorCategory.Device,
                "translationRuntime.defaultPhysicalDeviceMissing",
                RecoveryAction.SelectDevice));
            return;
        }

        Publish(_reducer.UpdateAudioSelection(
            message.Generation,
            new AudioSelection(
                selected.Input.Label,
                selected.Output.Label)));
        message.Completion.TrySetResult(null);
    }

    private async Task ClassifyAsync(long generation, string text)
    {
        try
        {
            LanguageProbabilities probabilities =
                await _dependencies.LanguageClassifier.ClassifyAsync(
                    text,
                    _actorCancellation.Token).ConfigureAwait(false);
            await PostReliableAsync(
                new ClassifiedMessage(generation, probabilities),
                _actorCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _actorCancellation.IsCancellationRequested)
        {
        }
    }

    private void HandleClassified(ClassifiedMessage message)
    {
        if (message.Generation != _reducer.Generation
            || _inboundBuffer is null)
        {
            return;
        }

        IReadOnlyDictionary<string, double> probabilities =
            new Dictionary<string, double>
            {
                ["zh"] = message.Probabilities.Zh,
                ["en"] = message.Probabilities.En,
                ["de"] = message.Probabilities.De,
            };
        IReadOnlyList<byte[]> selected =
            _inboundBuffer.Observe(probabilities);
        if (selected.Count > 0)
        {
            _ = TryTrackAudioWork(
                cancellationToken => EnqueueChunksAsync(
                    AudioDirection.Inbound,
                    selected,
                    NewCompletion(),
                    cancellationToken));
        }
    }

    private ChannelSupervisor CreateSupervisor(
        AudioDirection direction,
        long generation,
        TranslationSessionConfiguration configuration)
    {
        return new ChannelSupervisor(
            direction,
            generation,
            _dependencies.SessionFactory,
            configuration,
            _dependencies.Clock,
            PostSupervisorAsync);
    }

    private async ValueTask PostSupervisorAsync(
        ChannelSupervisorNotification notification)
    {
        TaskCompletionSource<RuntimeError?> completion = NewCompletion();
        SupervisorMessage message = new(notification, completion);
        await PostReliableAsync(message, _actorCancellation.Token)
            .ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    private async ValueTask PostReliableAsync(
        RuntimeMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await _mailbox.WriteReliableAsync(message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            message.Drop();
        }
    }

    private void TrackRouteMutation(RoutingPolicySnapshot routing)
    {
        _ = TryTrackAudioWork(
            cancellationToken => ApplyRoutesAsync(
                routing,
                cancellationToken));
    }

    private async Task ApplyRoutesAsync(
        RoutingPolicySnapshot routing,
        CancellationToken cancellationToken)
    {
        try
        {
            await _dependencies.AudioEngine.SetInboundRouteAsync(
                routing.InboundRoute,
                cancellationToken).ConfigureAwait(false);
            await _dependencies.AudioEngine.SetOutboundRouteAsync(
                routing.OutboundRoute,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<RuntimeError?> RollBackStartAsync(
        ChannelSupervisor? inbound,
        ChannelSupervisor? outbound,
        bool audioStarted)
    {
        RuntimeError? error = null;
        Task inboundClose = StartSupervisorClose(inbound);
        Task outboundClose = StartSupervisorClose(outbound);
        error = await CaptureFailureAsync(
            Task.WhenAll(inboundClose, outboundClose),
            error).ConfigureAwait(false);
        error = await DisposeSupervisorAsync(inbound, error)
            .ConfigureAwait(false);
        error = await DisposeSupervisorAsync(outbound, error)
            .ConfigureAwait(false);
        if (audioStarted)
        {
            RuntimeError? stopError = await StopAudioSafelyAsync()
                .ConfigureAwait(false);
            error ??= stopError;
        }

        return error;
    }

    private async Task CleanupOutcomeAsync(StartOutcome outcome)
    {
        _ = await RollBackStartAsync(
            outcome.Inbound,
            outcome.Outbound,
            outcome.AudioStarted).ConfigureAwait(false);
    }

    private static Task StartSupervisorClose(
        ChannelSupervisor? supervisor)
    {
        try
        {
            return supervisor?.CloseAsync(CancellationToken.None)
                ?? Task.CompletedTask;
        }
#pragma warning disable CA1031 // Synchronous close failures join the same safe cleanup path.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return Task.FromException(exception);
        }
    }

    private void CompleteStart(RuntimeError? error)
    {
        lock (_submissionSync)
        {
            _startCompletion?.TrySetResult(error);
            _startCompletion = null;
        }
    }

    private void CompleteStop(RuntimeError? error)
    {
        lock (_submissionSync)
        {
            _stopCompletion?.TrySetResult(error);
            _stopCompletion = null;
            _stopRequested = false;
        }
    }

    private void CompleteExit(RuntimeError? error)
    {
        lock (_submissionSync)
        {
            _exitCompletion?.TrySetResult(error);
            _exitCompletion = null;
            _exitRequested = false;
        }
    }

    private void Publish(AppSnapshot snapshot)
    {
        long dropped = Interlocked.Read(ref _droppedAudioFrames);
        if (dropped >= 0
            && snapshot.RuntimeState != RuntimeState.Stopped
            && snapshot.AudioDiagnostics.DroppedFrameCount != (ulong)dropped)
        {
            snapshot = _reducer.UpdateAudioDiagnostics(
                _reducer.Generation,
                (ulong)dropped);
        }

        Volatile.Write(ref _currentSnapshot, snapshot);
        _publisher.Publish(snapshot);
    }

    private void OnDroppedAudio(uint frameCount)
    {
        Interlocked.Add(ref _droppedAudioFrames, frameCount);
    }

    private void DropStartOutcome(StartOutcome outcome)
    {
        DisposeSupervisor(outcome.Inbound);
        DisposeSupervisor(outcome.Outbound);
        if (outcome.AudioStarted)
        {
            ObserveDetached(StopAudioAfterDropAsync());
        }
    }

    private async Task StopAudioAfterDropAsync()
    {
        try
        {
            await _dependencies.AudioEngine.StopAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Drop cleanup cannot report through a disposed actor.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void SafeLogFailure(RuntimeError error)
    {
        try
        {
            _dependencies.Log.Write(
                RuntimeLogLevel.Error,
                "translationRuntime.failure",
                new Dictionary<string, string>
                {
                    ["category"] = error.Category.ToString(),
                    ["code"] = error.Code,
                });
        }
#pragma warning disable CA1031 // Logging is never allowed to break runtime completion.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static bool IsWhiteSpace(ReadOnlySpan<char> secret)
    {
        if (secret.IsEmpty)
        {
            return true;
        }

        foreach (char character in secret)
        {
            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    private static DeviceSelection? SelectDevices(
        AudioDeviceSnapshot snapshot)
    {
        AudioDeviceDescriptor? input = snapshot.Devices.FirstOrDefault(
            static device =>
                device.Direction == AudioDeviceDirection.Input
                && device.IsDefault
                && device.IsAvailable);
        AudioDeviceDescriptor? output = snapshot.Devices.FirstOrDefault(
            static device =>
                device.Direction == AudioDeviceDirection.Output
                && device.IsDefault
                && device.IsAvailable);
        return input is null || output is null
            ? null
            : new DeviceSelection(input, output);
    }

    private static RuntimeError MapException(Exception exception)
    {
        return exception switch
        {
            RuntimeOperationException operation => operation.Error,
            TranslationSessionException session => session.Error,
            IOException => Error(
                ErrorCategory.Network,
                "translationRuntime.networkFailure",
                RecoveryAction.Retry),
            UnauthorizedAccessException => Error(
                ErrorCategory.Permission,
                "translationRuntime.permissionDenied",
                RecoveryAction.OpenPrivacySettings),
            _ => Error(
                ErrorCategory.Protocol,
                "translationRuntime.operationFailed",
                RecoveryAction.ReportCompatibility),
        };
    }

    private static RuntimeError Error(
        ErrorCategory category,
        string code,
        RecoveryAction recovery)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            recovery);
    }

    private static TaskCompletionSource<RuntimeError?> NewCompletion()
    {
        return new TaskCompletionSource<RuntimeError?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void DropMessage(RuntimeMessage message)
    {
        message.Drop();
    }

    private sealed record DeviceSelection(
        AudioDeviceDescriptor Input,
        AudioDeviceDescriptor Output);

    private sealed record StartOutcome(
        RuntimeSettings? Settings,
        DriverCompatibility? Driver,
        DeviceSelection? Selection,
        ChannelSupervisor? Inbound,
        ChannelSupervisor? Outbound,
        bool AudioStarted,
        bool OutboundBypassed,
        RuntimeError? Error)
    {
        public static StartOutcome Failed(RuntimeError error)
        {
            return new StartOutcome(
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                error);
        }
    }

    private abstract class RuntimeMessage
    {
        public virtual void Drop()
        {
        }
    }

    private sealed class PriorityWakeMessage : RuntimeMessage
    {
        public static PriorityWakeMessage Instance { get; } = new();

        private PriorityWakeMessage()
        {
        }
    }

    private sealed class StartRequestedMessage(
        TaskCompletionSource<RuntimeError?> completion) : RuntimeMessage
    {
        public TaskCompletionSource<RuntimeError?> Completion { get; } =
            completion;

        public override void Drop()
        {
            Completion.TrySetResult(Error(
                ErrorCategory.Backpressure,
                "translationRuntime.commandQueueFull",
                RecoveryAction.Retry));
        }
    }

    private sealed class StartCompletedMessage(
        long generation,
        StartOutcome outcome,
        TaskCompletionSource<RuntimeError?> completion,
        Action<StartOutcome> onDrop) : RuntimeMessage
    {
        public long Generation { get; } = generation;

        public StartOutcome Outcome { get; } = outcome;

        public override void Drop()
        {
            onDrop(Outcome);
            completion.TrySetResult(Outcome.Error ?? Error(
                ErrorCategory.Protocol,
                "translationRuntime.startCanceled",
                RecoveryAction.None));
        }
    }

    private sealed class StopCompletedMessage(
        long generation,
        RuntimeError? error,
        TaskCompletionSource<RuntimeError?>? completion) : RuntimeMessage
    {
        public long Generation { get; } = generation;

        public RuntimeError? Error { get; } = error;

        public override void Drop()
        {
            completion?.TrySetResult(Error ?? TranslationRuntime.Error(
                ErrorCategory.Protocol,
                "translationRuntime.stopCanceled",
                RecoveryAction.None));
        }
    }

    private sealed class StopDeadlineElapsedMessage(
        long generation) : RuntimeMessage
    {
        public long Generation { get; } = generation;
    }

    private sealed class SupervisorMessage(
        ChannelSupervisorNotification notification,
        TaskCompletionSource<RuntimeError?> completion) : RuntimeMessage
    {
        public ChannelSupervisorNotification Notification { get; } =
            notification;

        public TaskCompletionSource<RuntimeError?> Completion { get; } =
            completion;

        public override void Drop()
        {
            if (Notification.Event is IDisposable disposable)
            {
                disposable.Dispose();
            }

            Completion.TrySetResult(Error(
                ErrorCategory.Backpressure,
                "translationRuntime.commandQueueFull",
                RecoveryAction.Retry));
        }
    }

    private sealed class AudioCapturedMessage(
        long generation,
        AudioEngineEvent audio,
        Action<uint> onDrop) : RuntimeMessage
    {
        private AudioEngineEvent? _audio = audio;

        public long Generation { get; } = generation;

        public AudioEngineEvent Audio =>
            Volatile.Read(ref _audio)
            ?? throw new ObjectDisposedException(nameof(AudioCapturedMessage));

        public override void Drop()
        {
            AudioEngineEvent? dropped =
                Interlocked.Exchange(ref _audio, null);
            if (dropped is not null)
            {
                uint frameCount = dropped.FrameCount;
                dropped.Dispose();
                onDrop(frameCount);
            }
        }
    }

    private sealed class OrdinaryCommandMessage(
        RuntimeCommand command,
        TaskCompletionSource<RuntimeError?> completion) : RuntimeMessage
    {
        public RuntimeCommand Command { get; } = command;

        public TaskCompletionSource<RuntimeError?> Completion { get; } =
            completion;

        public override void Drop()
        {
            Completion.TrySetResult(Error(
                ErrorCategory.Backpressure,
                "translationRuntime.commandQueueFull",
                RecoveryAction.Retry));
        }
    }

    private sealed class DevicesRefreshedMessage(
        long generation,
        AudioDeviceSnapshot? devices,
        TaskCompletionSource<RuntimeError?> completion,
        RuntimeError? error) : RuntimeMessage
    {
        public long Generation { get; } = generation;

        public AudioDeviceSnapshot? Devices { get; } = devices;

        public TaskCompletionSource<RuntimeError?> Completion { get; } =
            completion;

        public RuntimeError? Error { get; } = error;

        public override void Drop()
        {
            Completion.TrySetResult(Error ?? TranslationRuntime.Error(
                ErrorCategory.Backpressure,
                "translationRuntime.commandQueueFull",
                RecoveryAction.Retry));
        }
    }

    private sealed class ClassifiedMessage(
        long generation,
        LanguageProbabilities probabilities) : RuntimeMessage
    {
        public long Generation { get; } = generation;

        public LanguageProbabilities Probabilities { get; } = probabilities;
    }
}
