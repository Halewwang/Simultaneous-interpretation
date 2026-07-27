using System.Collections.Concurrent;
using System.Threading.Channels;
using EMKE.Application;
using EMKE.Core;

namespace EMKE.Application.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA1861 // Scenario arrays are local immutable expectations.
#pragma warning disable CA2000 // Runtime/event constructors take explicit ownership in these tests.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class TranslationRuntimeTests
{
    private static readonly string[] InboundFailureTrace =
    [
        "os",
        "settings",
        "secret",
        "driver",
        "devices",
        "audio.start",
        "session.inbound.create",
        "session.inbound.connect",
        "session.inbound.close",
        "audio.stop",
    ];

    [TestMethod]
    public async Task StartUsesRequiredOrderAndInboundFailureRollsBackAudio()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.InboundSession.ConnectError = Error(
            ErrorCategory.Authentication,
            "test.inboundAuthentication");
        await using TranslationRuntime runtime = harness.CreateRuntime();

        RuntimeError? error = await runtime.StartAsync().ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Authentication, error?.Category);
        CollectionAssert.AreEqual(
            InboundFailureTrace,
            harness.Trace.ToArray());
        Assert.AreEqual(RuntimeState.Failed, runtime.CurrentSnapshot.RuntimeState);
    }

    [TestMethod]
    public async Task SecondStartWhileStartingSharesTaskAndRunsOneMutation()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.SettingsGate.Block();
        await using TranslationRuntime runtime = harness.CreateRuntime();

        Task<RuntimeError?> first = runtime.StartAsync();
        await harness.SettingsGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Task<RuntimeError?> second = runtime.StartAsync();

        Assert.AreSame(first, second);
        Assert.AreEqual(1, harness.SettingsLoadCount);
        harness.SettingsGate.Release();
        Assert.IsNull(await first.ConfigureAwait(false));
        Assert.AreEqual(RuntimeState.Running, runtime.CurrentSnapshot.RuntimeState);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task StopCancelsStartQueuedBeforeActorBeginsAndLateRequestCannotReviveIt()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TaskCompletionSource actorStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TranslationRuntime runtime =
            harness.CreateRuntime(actorStart.Task);

        Task<RuntimeError?> start = runtime.StartAsync();
        Task<RuntimeError?> stop = runtime.StopAsync();
        actorStart.TrySetResult();

        RuntimeError? startError =
            await start.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        RuntimeError? stopError =
            await stop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Protocol, startError?.Category);
        Assert.AreEqual("translationRuntime.startCanceled", startError?.Code);
        Assert.IsNull(stopError);
        Assert.AreEqual(RuntimeState.Stopped, runtime.CurrentSnapshot.RuntimeState);
        Assert.AreEqual(0, harness.SettingsLoadCount);
        Assert.AreEqual(0, harness.SessionCreateCount);
    }

    [TestMethod]
    public async Task ExitRejectsNewCommandsAndDrainsCommandsQueuedBeforeActorExit()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TaskCompletionSource actorStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TranslationRuntime runtime =
            harness.CreateRuntime(actorStart.Task);
        Task<RuntimeError?> pendingStart = runtime.StartAsync();
        Task<RuntimeError?> pendingRefresh =
            runtime.SubmitAsync(new RuntimeCommand.RefreshDevices());

        Task<RuntimeError?> exit = runtime.ExitAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(
            () => runtime.StartAsync());
        Assert.ThrowsExactly<ObjectDisposedException>(
            () => runtime.StopAsync());
        Assert.ThrowsExactly<ObjectDisposedException>(
            () => runtime.SubmitAsync(new RuntimeCommand.RefreshDevices()));
        actorStart.TrySetResult();
        Assert.IsNull(
            await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        Assert.AreEqual(
            ErrorCategory.Protocol,
            (await pendingStart.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false))?.Category);
        Assert.IsNotNull(
            await pendingRefresh.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false));
        Assert.ThrowsExactly<ObjectDisposedException>(
            () => runtime.ExitAsync());
        Assert.AreEqual(0, harness.SettingsLoadCount);
    }

    [TestMethod]
    public async Task StopRequestedDuringStartDoesNotOverlapAudioMutation()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.AudioStartGate.Block();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Task<RuntimeError?> start = runtime.StartAsync();
        await harness.AudioStartGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        Task<RuntimeError?> stop = runtime.StopAsync();

        Assert.AreEqual(0, harness.AudioStopCount);
        harness.AudioStartGate.Release();
        _ = await start.ConfigureAwait(false);
        Assert.IsNull(await stop.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false));
        Assert.AreEqual(1, harness.MaximumAudioMutationDepth);
    }

    [TestMethod]
    public async Task StopDeadlineStartsWhileCanceledStartRollbackIsStillBlocked()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.Audio.StartGate.Block();
        harness.Audio.StopGate.Block();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Task<RuntimeError?> start = runtime.StartAsync();
            await harness.Audio.StartGate.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Task<RuntimeError?> stop = runtime.StopAsync();
            await harness.Clock.DelayEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            harness.Clock.ReleaseAll();

            RuntimeError? stopError =
                await stop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            RuntimeError? startError =
                await start.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(ErrorCategory.CloseTimeout, stopError?.Category);
            Assert.AreEqual("translationRuntime.startCanceled", startError?.Code);
            Assert.AreEqual(RuntimeState.Stopped, runtime.CurrentSnapshot.RuntimeState);
            Assert.AreEqual(0, harness.AudioStopCount);
            RuntimeError? restartError =
                await runtime.StartAsync().ConfigureAwait(false);
            Assert.AreEqual(ErrorCategory.CloseTimeout, restartError?.Category);

            harness.Audio.StartGate.Release();
            await harness.Audio.StopGate.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            harness.Audio.StartGate.Release();
            harness.Audio.StopGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task OutboundConnectFailureKeepsInboundAndAudioInDegradedFailClosedState()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.OutboundSession.ConnectError = Error(
            ErrorCategory.Network,
            "test.outboundNetwork");
        await using TranslationRuntime runtime = harness.CreateRuntime();

        RuntimeError? error = await runtime.StartAsync().ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Network, error?.Category);
        Assert.AreEqual(0, harness.AudioStopCount);
        Assert.AreEqual(0, harness.InboundSession.CloseCount);
        Assert.AreEqual(RuntimeState.Degraded, runtime.CurrentSnapshot.RuntimeState);
        Assert.AreEqual(
            ChannelState.Connected,
            runtime.CurrentSnapshot.InboundChannelState);
        Assert.AreEqual(
            OutboundRoute.MutedFailClosed,
            runtime.CurrentSnapshot.OutboundRoute);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task FailedNativeFailClosedRouteNeverPublishesItAndForcesSafetyStop()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            await harness.Audio.FirstOutboundRouteApplied.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            harness.Audio.StopGate.Block();
            harness.Audio.OutboundRouteException =
                new NotSupportedException("route probe");

            harness.Audio.Emit(AudioEngineEvent.CreateControl(
                AudioEngineEventKind.Backpressure,
                AudioEngineStatus.QueueFull,
                AudioEngineRoute.Translated,
                sequence: 701));
            await harness.Audio.StopEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreNotEqual(
                OutboundRoute.MutedFailClosed,
                runtime.CurrentSnapshot.OutboundRoute);
            Assert.AreEqual(0, harness.OutboundSession.SendCount);
            Assert.IsFalse(harness.Trace.Contains("audio.enqueue.outbound"));
        }
        finally
        {
            harness.Audio.OutboundRouteException = null;
            harness.Audio.StopGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task FirstRouteFailureCancelsQueuedMutationBeforeNativeApplyOrCommit()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            harness.Audio.RouteGate.Block();
            harness.Audio.FailNextOutboundRoute(
                new NotSupportedException("first route probe"));
            harness.Audio.StopGate.Block();
            harness.Audio.Emit(AudioEngineEvent.CreateControl(
                AudioEngineEventKind.Backpressure,
                AudioEngineStatus.QueueFull,
                AudioEngineRoute.Translated,
                sequence: 751));
            await harness.Audio.RouteGate.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Task<RuntimeError?> queuedBypass = runtime.SubmitAsync(
                new RuntimeCommand.SetOutboundBypass(true));

            harness.Audio.RouteGate.Release();
            await harness.Audio.StopEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            RuntimeError? queuedError = await queuedBypass
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.IsNotNull(queuedError);
            Assert.AreEqual(2, harness.Audio.OutboundRouteCallCount);
            Assert.AreEqual(
                OutboundRoute.Translated,
                runtime.CurrentSnapshot.OutboundRoute);
        }
        finally
        {
            harness.Audio.RouteGate.Release();
            harness.Audio.StopGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task DeviceChangedControlEventStopsAndRetainsStableError()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        harness.Audio.Emit(AudioEngineEvent.CreateControl(
            AudioEngineEventKind.DeviceChanged,
            AudioEngineStatus.DeviceMissing,
            AudioEngineRoute.Stopped,
            sequence: 801));
        await harness.Audio.StopEntered.Task
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.RuntimeState == RuntimeState.Stopped);

        Assert.AreEqual(
            "translationRuntime.deviceChanged",
            runtime.CurrentSnapshot.Error?.Code);
        Assert.AreEqual(ErrorCategory.Device, runtime.CurrentSnapshot.Error?.Category);
        harness.Factory.Queue(
            new FakeTranslationSession("restart-inbound"),
            new FakeTranslationSession("restart-outbound"));
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        Assert.AreEqual(2, harness.AudioStartCount);
    }

    [TestMethod]
    public async Task StreamErrorControlEventStopsAndRetainsStableError()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        harness.Audio.Emit(AudioEngineEvent.CreateControl(
            AudioEngineEventKind.StreamError,
            AudioEngineStatus.InternalError,
            AudioEngineRoute.Translated,
            sequence: 803));
        await harness.Audio.StopEntered.Task
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.RuntimeState == RuntimeState.Stopped);

        Assert.AreEqual(
            "translationRuntime.audioStreamError",
            runtime.CurrentSnapshot.Error?.Code);
        Assert.AreEqual(
            ErrorCategory.Protocol,
            runtime.CurrentSnapshot.Error?.Category);
    }

    [TestMethod]
    public async Task BackpressureControlEventPreservesExplicitOutboundBypass()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.Settings = new RuntimeSettings(
            LanguageCode.Zh,
            LanguageCode.Zh,
            "gpt-realtime-translate",
            inboundBypass: false,
            outboundBypass: false);
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        harness.Audio.Emit(AudioEngineEvent.CreateControl(
            AudioEngineEventKind.Backpressure,
            AudioEngineStatus.QueueFull,
            AudioEngineRoute.OriginalBypass,
            sequence: 802));
        await harness.Audio.PollRead.Task
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsNull(await runtime.SubmitAsync(
            new RuntimeCommand.RefreshDevices()).ConfigureAwait(false));

        Assert.AreEqual(
            OutboundRoute.OriginalBypass,
            runtime.CurrentSnapshot.OutboundRoute);
        Assert.AreEqual(0, harness.AudioStopCount);
    }

    [TestMethod]
    public async Task StartupFailClosedRouteFailureStopsInsteadOfPublishingDegraded()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.OutboundSession.ConnectError = Error(
            ErrorCategory.Network,
            "test.outboundNetwork");
        harness.Audio.OutboundRouteException =
            new NotSupportedException("startup route probe");
        await using TranslationRuntime runtime = harness.CreateRuntime();

        RuntimeError? error = await runtime.StartAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Audio.StopEntered.Task
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.RuntimeState == RuntimeState.Stopped);

        Assert.IsNotNull(error);
        Assert.AreEqual(1, harness.AudioStopCount);
        Assert.AreNotEqual(
            OutboundRoute.MutedFailClosed,
            runtime.CurrentSnapshot.OutboundRoute);
        Assert.AreNotEqual(
            RuntimeState.Degraded,
            runtime.CurrentSnapshot.RuntimeState);
    }

    [TestMethod]
    public async Task OutboundEnqueueFailureCommitsFailClosedRouteThroughActor()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        await harness.Audio.FirstOutboundRouteApplied.Task
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        harness.Audio.OutboundEnqueueException = new RuntimeOperationException(
            Error(ErrorCategory.Backpressure, "test.outboundQueueFull"));
        TrackingPcmLease lease = new([1, 0]);

        harness.OutboundSession.Emit(new TranslationSessionEvent.AudioDelta(lease));
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.OutboundRoute
                == OutboundRoute.MutedFailClosed);

        Assert.AreEqual(
            ErrorCategory.Backpressure,
            runtime.CurrentSnapshot.Error?.Category);
    }

    [TestMethod]
    public async Task InboundEnqueueFailureCommitsFailOpenRouteThroughActor()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        await harness.Audio.FirstOutboundRouteApplied.Task
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        harness.Audio.InboundEnqueueException = new RuntimeOperationException(
            Error(ErrorCategory.Backpressure, "test.inboundQueueFull"));
        TrackingPcmLease lease = new([1, 0]);

        harness.InboundSession.Emit(new TranslationSessionEvent.AudioDelta(lease));
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        harness.InboundSession.Emit(new TranslationSessionEvent.Completed());
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot.InboundRoute
                == InboundRoute.OriginalFailOpen);

        Assert.AreEqual(
            ErrorCategory.Backpressure,
            runtime.CurrentSnapshot.Error?.Category);
    }

    [TestMethod]
    public async Task SameLanguageCreatesOneSessionAndUsesOutboundOriginalBypass()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.Settings = new RuntimeSettings(
            LanguageCode.Zh,
            LanguageCode.Zh,
            "gpt-realtime-translate",
            inboundBypass: false,
            outboundBypass: false);
        await using TranslationRuntime runtime = harness.CreateRuntime();

        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

        Assert.AreEqual(1, harness.SessionCreateCount);
        Assert.AreEqual(
            ChannelState.Bypassed,
            runtime.CurrentSnapshot.OutboundChannelState);
        Assert.AreEqual(
            OutboundRoute.OriginalBypass,
            runtime.CurrentSnapshot.OutboundRoute);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task StopClosesBothSessionsConcurrentlyAndCoalescesRepeatedRequests()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        harness.InboundSession.CloseGate.Block();
        harness.OutboundSession.CloseGate.Block();

        Task<RuntimeError?> firstStop = runtime.StopAsync();
        Task<RuntimeError?> secondStop = runtime.StopAsync();
        Task<RuntimeError?> firstExit = runtime.ExitAsync();
        Task<RuntimeError?> secondExit = runtime.ExitAsync();
        await Task.WhenAll(
            harness.InboundSession.CloseGate.Entered.Task,
            harness.OutboundSession.CloseGate.Entered.Task)
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreSame(firstStop, secondStop);
        Assert.AreSame(firstExit, secondExit);
        Assert.IsFalse(firstStop.IsCompleted);
        harness.InboundSession.CloseGate.Release();
        harness.OutboundSession.CloseGate.Release();
        Assert.IsNull(await firstStop.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false));
        Assert.IsNull(await firstExit.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false));
    }

    [TestMethod]
    public async Task StopUsesControllableOneSecondLocalDeadline()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        harness.InboundSession.CloseGate.Block();
        harness.OutboundSession.CloseGate.Block();
        TrackingPcmLease lateLease = new([7, 0]);
        harness.InboundSession.TailEvent =
            new TranslationSessionEvent.AudioDelta(lateLease);
        harness.InboundSession.EmitTailAfterCloseGate = true;

        Task<RuntimeError?> stop = runtime.StopAsync();
        await harness.Clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Assert.AreEqual(TimeSpan.FromSeconds(1), harness.Clock.Delays.Last());
        harness.Clock.ReleaseAll();

        RuntimeError? error = await stop.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Assert.AreEqual(ErrorCategory.CloseTimeout, error?.Category);
        Assert.AreEqual(1, harness.AudioStopCount);
        Assert.AreEqual(RuntimeState.Stopped, runtime.CurrentSnapshot.RuntimeState);
        harness.InboundSession.CloseGate.Release();
        harness.OutboundSession.CloseGate.Release();
        await lateLease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Assert.IsFalse(harness.Trace.Contains("audio.enqueue.inbound"));
    }

    [TestMethod]
    public async Task StopDeadlineCoversNonCooperativePollAndAudioStop()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            harness.Audio.PollReturnGate.Block();
            harness.Audio.StopGate.Block();
            TrackingPcmLease lease = new([1, 0]);
            harness.Audio.Emit(AudioEngineEvent.CreatePcm(
                lease,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                frameCount: 1,
                sequence: 701));
            await harness.Audio.PollRead.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Task<RuntimeError?> stop = runtime.StopAsync();
            await harness.Clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            harness.Clock.ReleaseAll();

            RuntimeError? error =
                await stop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(ErrorCategory.CloseTimeout, error?.Category);
            Assert.AreEqual(RuntimeState.Stopped, runtime.CurrentSnapshot.RuntimeState);
            await harness.Audio.StopGate.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            harness.Audio.PollReturnGate.Release();
            harness.Audio.StopGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task StopDeadlineCompletesLocallyButDefersEngineStopUntilAcceptedSendDrains()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            harness.InboundSession.SendGate.Block();
            TrackingPcmLease lease = new([1, 0]);
            harness.Audio.Emit(AudioEngineEvent.CreatePcm(
                lease,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                frameCount: 1,
                sequence: 702));
            await harness.InboundSession.SendGate.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Task<RuntimeError?> stop = runtime.StopAsync();
            await harness.Clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            harness.Clock.ReleaseAll();

            RuntimeError? error =
                await stop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(ErrorCategory.CloseTimeout, error?.Category);
            Assert.AreEqual(0, harness.AudioStopCount);
            harness.InboundSession.SendGate.Release();
            await harness.Audio.StopEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(1, lease.DisposeCount);
        }
        finally
        {
            harness.InboundSession.SendGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task RestartIsRejectedWhileTimedOutStopStillDrainsAcceptedSend()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            harness.InboundSession.SendGate.Block();
            TrackingPcmLease lease = new([1, 0]);
            harness.Audio.Emit(AudioEngineEvent.CreatePcm(
                lease,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                frameCount: 1,
                sequence: 703));
            await harness.InboundSession.SendGate.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Task<RuntimeError?> stop = runtime.StopAsync();
            await harness.Clock.DelayEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            harness.Clock.ReleaseAll();
            Assert.AreEqual(
                ErrorCategory.CloseTimeout,
                (await stop.WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false))?.Category);

            RuntimeError? restartError =
                await runtime.StartAsync().ConfigureAwait(false);

            Assert.AreEqual(ErrorCategory.CloseTimeout, restartError?.Category);
            Assert.AreEqual(1, harness.AudioStartCount);
            Assert.AreEqual(0, harness.AudioStopCount);
        }
        finally
        {
            harness.InboundSession.SendGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task TimedOutPollKeepsRestartClosedUntilEntireOldCleanupFinishes()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        FakeTranslationSession restartedInbound = new("inbound");
        FakeTranslationSession restartedOutbound = new("outbound");
        harness.Factory.Queue(restartedInbound, restartedOutbound);
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            harness.Audio.PollReturnGate.Block();
            TrackingPcmLease oldLease = new([1, 0]);
            harness.Audio.Emit(AudioEngineEvent.CreatePcm(
                oldLease,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                frameCount: 1,
                sequence: 704));
            await harness.Audio.PollRead.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Task<RuntimeError?> stop = runtime.StopAsync();
            await harness.Clock.DelayEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            harness.Clock.ReleaseAll();
            Assert.AreEqual(
                ErrorCategory.CloseTimeout,
                (await stop.WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false))?.Category);
            await harness.Audio.StopEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            RuntimeError? earlyRestart =
                await runtime.StartAsync().ConfigureAwait(false);

            Assert.AreEqual(ErrorCategory.CloseTimeout, earlyRestart?.Category);
            Assert.AreEqual(1, harness.AudioStartCount);
            harness.Audio.PollReturnGate.Release();
            await oldLease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            RuntimeError? restart = await StartWhenCleanupFinishesAsync(runtime)
                .ConfigureAwait(false);

            Assert.IsNull(restart);
            Assert.AreEqual(2, harness.AudioStartCount);
            TrackingPcmLease captured = new([2, 0]);
            harness.Audio.Emit(AudioEngineEvent.CreatePcm(
                captured,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                frameCount: 1,
                sequence: 705));
            await restartedInbound.SendEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(1, restartedInbound.SendCount);
            TrackingPcmLease translatedLease = new([3, 0]);
            restartedOutbound.Emit(
                new TranslationSessionEvent.AudioDelta(translatedLease));
            await harness.Audio.OutboundEnqueueEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await translatedLease.Disposed.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(1, translatedLease.DisposeCount);
            Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
        }
        finally
        {
            harness.Audio.PollReturnGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task StopDeadlineCompletesLocallyButDefersEngineStopUntilTailEnqueueDrains()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            harness.Audio.InboundEnqueueGate.Block();
            TrackingPcmLease tailLease = new([1, 0]);
            harness.InboundSession.TailEvent =
                new TranslationSessionEvent.AudioDelta(tailLease);

            Task<RuntimeError?> stop = runtime.StopAsync();
            await harness.Audio.InboundEnqueueGate.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await harness.Clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            harness.Clock.ReleaseAll();

            RuntimeError? error =
                await stop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(ErrorCategory.CloseTimeout, error?.Category);
            Assert.AreEqual(0, harness.AudioStopCount);
            harness.Audio.InboundEnqueueGate.Release();
            await harness.Audio.StopEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            string[] trace = harness.Trace.ToArray();
            Assert.IsGreaterThan(
                Array.IndexOf(trace, "audio.enqueue.inbound.complete"),
                Array.IndexOf(trace, "audio.stop"));
        }
        finally
        {
            harness.Audio.InboundEnqueueGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task StopDeadlineTracksRouteMutationBeforeEngineStop()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
            harness.Audio.RouteGate.Block();
            harness.Audio.Emit(AudioEngineEvent.CreateControl(
                AudioEngineEventKind.Backpressure,
                AudioEngineStatus.QueueFull,
                AudioEngineRoute.Translated,
                sequence: 901));
            await harness.Audio.RouteGate.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Task<RuntimeError?> stop = runtime.StopAsync();
            await harness.Clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            harness.Clock.ReleaseAll();

            RuntimeError? error =
                await stop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(ErrorCategory.CloseTimeout, error?.Category);
            Assert.AreEqual(0, harness.AudioStopCount);
            harness.Audio.RouteGate.Release();
            await harness.Audio.StopEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            harness.Audio.RouteGate.Release();
            harness.Clock.ReleaseAll();
            await runtime.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task StopRejectsPolledAudioBeforeItCanReachEitherSession()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        harness.Audio.PollReturnGate.Block();
        TrackingPcmLease lease = new([1, 0]);
        harness.Audio.Emit(AudioEngineEvent.CreatePcm(
            lease,
            AudioDirection.Outbound,
            AudioEngineRoute.Translated,
            AudioEngineStatus.Ok,
            frameCount: 1,
            sequence: 99));
        await harness.Audio.PollRead.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        Task<RuntimeError?> stop = runtime.StopAsync();
        harness.Audio.PollReturnGate.Release();
        Assert.IsNull(await stop.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false));
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        Assert.AreEqual(0, harness.InboundSession.SendCount);
        Assert.AreEqual(0, harness.OutboundSession.SendCount);
        Assert.AreEqual(1, lease.DisposeCount);
    }

    [TestMethod]
    public async Task StaleDeviceRefreshCannotOverwriteStoppedGeneration()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        harness.DeviceRefreshGate.Block();

        Task<RuntimeError?> refresh =
            runtime.SubmitAsync(new RuntimeCommand.RefreshDevices());
        await harness.DeviceRefreshGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
        AppSnapshot stopped = runtime.CurrentSnapshot;
        harness.DeviceRefreshGate.Release();
        Assert.IsNull(await refresh.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false));

        Assert.AreSame(stopped, runtime.CurrentSnapshot);
        Assert.AreEqual(RuntimeState.Stopped, runtime.CurrentSnapshot.RuntimeState);
    }

    [TestMethod]
    public async Task StartGatesFailAtTheirBoundaryWithoutRunningLaterSteps()
    {
        await AssertStartFailureAsync(
            harness => harness.OsError = Error(
                ErrorCategory.Configuration,
                "test.os"),
            ErrorCategory.Configuration,
            ["os"]).ConfigureAwait(false);
        await AssertStartFailureAsync(
            harness => harness.Settings = null,
            ErrorCategory.Configuration,
            ["os", "settings"]).ConfigureAwait(false);
        await AssertStartFailureAsync(
            harness => harness.SecretAvailable = false,
            ErrorCategory.Authentication,
            ["os", "settings", "secret"]).ConfigureAwait(false);
        await AssertStartFailureAsync(
            harness => harness.DriverCompatibility =
                new DriverCompatibility(false, "incompatible"),
            ErrorCategory.Driver,
            ["os", "settings", "secret", "driver"]).ConfigureAwait(false);
        await AssertStartFailureAsync(
            harness => harness.DevicesAvailable = false,
            ErrorCategory.Device,
            ["os", "settings", "secret", "driver", "devices"])
            .ConfigureAwait(false);
        await AssertStartFailureAsync(
            harness => harness.AudioStartError = Error(
                ErrorCategory.Device,
                "test.audioStart"),
            ErrorCategory.Device,
            ["os", "settings", "secret", "driver", "devices", "audio.start"])
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StartCompletesWithPrimaryErrorWhenRollbackCleanupThrows()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.InboundSession.ConnectError = Error(
            ErrorCategory.Authentication,
            "test.inboundAuthentication");
        harness.InboundSession.CloseException =
            new NotSupportedException("close probe");
        harness.Audio.StopException =
            new NotSupportedException("rollback audio stop probe");
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            RuntimeError? error = await runtime.StartAsync()
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Assert.AreEqual(ErrorCategory.Authentication, error?.Category);
            Assert.AreEqual(RuntimeState.Failed, runtime.CurrentSnapshot.RuntimeState);
            Assert.AreEqual(1, harness.AudioStopCount);
        }
        finally
        {
#pragma warning disable CA1849 // Failure-path probe must not await a deliberately hung runtime.
            runtime.Dispose();
#pragma warning restore CA1849
        }
    }

    [TestMethod]
    public async Task StartCompletesWhenFailureLoggerThrows()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.AudioStartError = Error(
            ErrorCategory.Device,
            "test.audioStart");
        harness.RuntimeLog = new RuntimeHarness.ThrowingRuntimeLog();
        TranslationRuntime runtime = harness.CreateRuntime();
        try
        {
            RuntimeError? error = await runtime.StartAsync()
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Assert.AreEqual(ErrorCategory.Device, error?.Category);
            Assert.AreEqual(RuntimeState.Failed, runtime.CurrentSnapshot.RuntimeState);
        }
        finally
        {
#pragma warning disable CA1849 // Failure-path probe must not await a deliberately hung runtime.
            runtime.Dispose();
#pragma warning restore CA1849
        }
    }

    [TestMethod]
    public async Task AudioStopFailureQuarantinesRuntimeAndRejectsRestart()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        harness.Audio.StopException =
            new NotSupportedException("audio stop probe");

        RuntimeError? error = await runtime.StopAsync()
            .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Protocol, error?.Category);
        Assert.AreEqual(RuntimeState.Failed, runtime.CurrentSnapshot.RuntimeState);
        RuntimeError? restart = await runtime.StartAsync().ConfigureAwait(false);
        Assert.AreEqual(
            "translationRuntime.nativeCleanupQuarantined",
            restart?.Code);
        Assert.AreEqual(1, harness.AudioStartCount);
    }

    [TestMethod]
    public async Task RollbackAudioStopFailureQuarantinesRetryWithoutAnotherNativeStart()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.InboundSession.ConnectError = Error(
            ErrorCategory.Authentication,
            "test.inboundAuthentication");
        harness.Audio.StopException =
            new NotSupportedException("rollback audio stop probe");
        await using TranslationRuntime runtime = harness.CreateRuntime();

        RuntimeError? first = await runtime.StartAsync().ConfigureAwait(false);
        RuntimeError? retry = await runtime.StartAsync().ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Authentication, first?.Category);
        Assert.AreEqual(
            "translationRuntime.nativeCleanupQuarantined",
            retry?.Code);
        Assert.AreEqual(1, harness.AudioStartCount);
    }

    [TestMethod]
    public async Task StopCompletesWithStableErrorWhenSessionCloseThrows()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        harness.InboundSession.CloseException =
            new NotSupportedException("inbound close probe");
        harness.OutboundSession.CloseException =
            new NotSupportedException("outbound close probe");

        RuntimeError? error = await runtime.StopAsync()
            .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.AreEqual(ErrorCategory.Protocol, error?.Category);
        Assert.AreEqual(RuntimeState.Stopped, runtime.CurrentSnapshot.RuntimeState);
        Assert.AreEqual(1, harness.AudioStopCount);
    }

    [TestMethod]
    public async Task SecretIsDisposedBeforeDriverAndCapturedPcmOwnersReleaseAfterSend()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();

        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        Assert.AreEqual(1, harness.SecretDisposeCount);
        Assert.IsGreaterThan(
            Array.IndexOf(harness.Trace.ToArray(), "secret"),
            Array.IndexOf(harness.Trace.ToArray(), "driver"));
        TrackingPcmLease inboundLease = new([1, 0, 2, 0]);
        TrackingPcmLease outboundLease = new([3, 0, 4, 0]);
        harness.Audio.Emit(AudioEngineEvent.CreatePcm(
            inboundLease,
            AudioDirection.Inbound,
            AudioEngineRoute.Translated,
            AudioEngineStatus.Ok,
            frameCount: 2,
            sequence: 1));
        harness.Audio.Emit(AudioEngineEvent.CreatePcm(
            outboundLease,
            AudioDirection.Outbound,
            AudioEngineRoute.Translated,
            AudioEngineStatus.Ok,
            frameCount: 2,
            sequence: 2));
        await Task.WhenAll(
            harness.InboundSession.SendEntered.Task,
            harness.OutboundSession.SendEntered.Task)
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, inboundLease.DisposeCount);
        Assert.AreEqual(1, outboundLease.DisposeCount);
        Assert.AreEqual(1, harness.InboundSession.SendCount);
        Assert.AreEqual(1, harness.OutboundSession.SendCount);
        Assert.AreEqual(1, harness.Audio.MaximumPollDepth);
        Assert.IsNull(await runtime.StopAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task StopDeliversTailAudioBeforeStoppingNativeEngine()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        await using TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));
        harness.Trace.Clear();
        TrackingPcmLease tailLease = new([1, 0, 2, 0]);
        harness.InboundSession.TailEvent =
            new TranslationSessionEvent.AudioDelta(tailLease);

        RuntimeError? error = await runtime.StopAsync().ConfigureAwait(false);

        Assert.IsNull(error);
        string[] trace = harness.Trace.ToArray();
        int tail = Array.IndexOf(trace, "audio.enqueue.inbound");
        int stop = Array.IndexOf(trace, "audio.stop");
        Assert.IsGreaterThanOrEqualTo(0, tail, "Expected translated tail audio to be enqueued.");
        Assert.IsGreaterThan(tail, stop, "Native audio stopped before tail audio was enqueued.");
        Assert.AreEqual(1, tailLease.DisposeCount);
        Assert.AreEqual(RuntimeState.Stopped, runtime.CurrentSnapshot.RuntimeState);
        Assert.AreEqual(string.Empty, runtime.CurrentSnapshot.SourceCaption);
        Assert.AreEqual(0, runtime.CurrentSnapshot.InboundLevel);
    }

    [TestMethod]
    public async Task PublisherSendsSameImmutableObjectAndSurvivesObserverFailure()
    {
        using RuntimeSnapshotPublisher publisher = new();
        RecordingObserver first = new();
        ThrowingObserver throwing = new();
        RecordingObserver second = new();
        using IDisposable firstSubscription = publisher.Subscribe(first);
        using IDisposable throwingSubscription = publisher.Subscribe(throwing);
        using IDisposable secondSubscription = publisher.Subscribe(second);
        AppSnapshot snapshot = RuntimeStateReducer.CreateInitialSnapshot()
            .WithNextVersion();

        publisher.Publish(snapshot);
        AppSnapshot firstValue =
            await first.Next.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        AppSnapshot secondValue =
            await second.Next.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

        Assert.AreSame(snapshot, firstValue);
        Assert.AreSame(firstValue, secondValue);
        Assert.AreEqual(snapshot.Version, secondValue.Version);
    }

    [TestMethod]
    public async Task SlowPublisherSubscriberReceivesOnlyLatestPendingSnapshot()
    {
        using RuntimeSnapshotPublisher publisher = new();
        BlockingObserver observer = new();
        using IDisposable subscription = publisher.Subscribe(observer);
        AppSnapshot initial = RuntimeStateReducer.CreateInitialSnapshot();
        AppSnapshot first = initial.WithNextVersion();
        AppSnapshot second = first.WithNextVersion();
        AppSnapshot latest = second.WithNextVersion();

        publisher.Publish(first);
        await observer.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        publisher.Publish(second);
        publisher.Publish(latest);
        observer.ReleaseFirst.TrySetResult();

        AppSnapshot deliveredLatest =
            await observer.Latest.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

        Assert.AreSame(latest, deliveredLatest);
        CollectionAssert.DoesNotContain(observer.Seen.ToArray(), second);
    }

    [TestMethod]
    public async Task UnsubscribeReturnsWhileObserverIsBlockedAndRetainsNoLaterSnapshots()
    {
        using RuntimeSnapshotPublisher publisher = new();
        BlockingObserver observer = new();
        IDisposable subscription = publisher.Subscribe(observer);
        AppSnapshot first =
            RuntimeStateReducer.CreateInitialSnapshot().WithNextVersion();
        AppSnapshot later = first.WithNextVersion();
        publisher.Publish(first);
        await observer.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        subscription.Dispose();
        publisher.Publish(later);
        observer.ReleaseFirst.TrySetResult();

        Assert.HasCount(1, observer.Seen);
        CollectionAssert.DoesNotContain(observer.Seen.ToArray(), later);
    }

    [TestMethod]
    public async Task SynchronousDisposeCompletesQueuedLifecycleWaiters()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TaskCompletionSource actorStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TranslationRuntime runtime = harness.CreateRuntime(actorStart.Task);
        Task<RuntimeError?> start = runtime.StartAsync();
        Task<RuntimeError?> refresh = runtime.SubmitAsync(
            new RuntimeCommand.RefreshDevices());
        Task<RuntimeError?> stop = runtime.StopAsync();
        Task<RuntimeError?> exit = runtime.ExitAsync();

#pragma warning disable CA1849 // This test verifies the synchronous IDisposable contract.
        runtime.Dispose();
#pragma warning restore CA1849

        Assert.AreEqual(
            "translationRuntime.disposed",
            (await start.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false))?.Code);
        Assert.AreEqual(
            "translationRuntime.disposed",
            (await refresh.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false))?.Code);
        Assert.AreEqual(
            "translationRuntime.disposed",
            (await stop.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false))?.Code);
        Assert.AreEqual(
            "translationRuntime.disposed",
            (await exit.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false))?.Code);
    }

    [TestMethod]
    public async Task SynchronousDisposeCompletesActiveStartBeforeWorkerUnwinds()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        harness.SettingsGate.Block();
        TranslationRuntime runtime = harness.CreateRuntime();
        Task<RuntimeError?> start = runtime.StartAsync();
        await harness.SettingsGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

#pragma warning disable CA1849 // This test verifies the synchronous IDisposable contract.
        runtime.Dispose();
#pragma warning restore CA1849

        RuntimeError? error = await start.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Assert.AreEqual("translationRuntime.disposed", error?.Code);
        harness.SettingsGate.Release();
    }

    [TestMethod]
    public async Task SynchronousDisposeDrainsAndStopsRunningNativeEngine()
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        TranslationRuntime runtime = harness.CreateRuntime();
        Assert.IsNull(await runtime.StartAsync().ConfigureAwait(false));

#pragma warning disable CA1849 // This test verifies the synchronous IDisposable contract.
        runtime.Dispose();
#pragma warning restore CA1849
        await runtime.DisposeAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, harness.AudioStopCount);
    }

    private static async Task AssertStartFailureAsync(
        Action<RuntimeHarness> configure,
        ErrorCategory expectedCategory,
        string[] expectedTrace)
    {
        RuntimeHarness harness = RuntimeHarness.Create();
        configure(harness);
        await using TranslationRuntime runtime = harness.CreateRuntime();

        RuntimeError? error = await runtime.StartAsync().ConfigureAwait(false);

        Assert.AreEqual(expectedCategory, error?.Category);
        CollectionAssert.AreEqual(expectedTrace, harness.Trace.ToArray());
        Assert.AreEqual(RuntimeState.Failed, runtime.CurrentSnapshot.RuntimeState);
    }

    private static RuntimeError Error(ErrorCategory category, string code)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.Retry);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 1_000; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(1).ConfigureAwait(false);
        }

        Assert.Fail("Condition was not observed.");
    }

    private static async Task<RuntimeError?> StartWhenCleanupFinishesAsync(
        TranslationRuntime runtime)
    {
        for (int attempt = 0; attempt < 1_000; attempt++)
        {
            RuntimeError? result =
                await runtime.StartAsync().ConfigureAwait(false);
            if (result?.Code != "translationRuntime.stopCleanupPending")
            {
                return result;
            }

            await Task.Yield();
        }

        Assert.Fail("Old runtime cleanup did not finish.");
        return null;
    }

    private sealed class RecordingObserver : IObserver<AppSnapshot>
    {
        public TaskCompletionSource<AppSnapshot> Next { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(AppSnapshot value)
        {
            Next.TrySetResult(value);
        }
    }

    private sealed class ThrowingObserver : IObserver<AppSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(AppSnapshot value)
        {
            throw new InvalidOperationException("observer failure");
        }
    }

    private sealed class BlockingObserver : IObserver<AppSnapshot>
    {
        private int _count;

        public ConcurrentQueue<AppSnapshot> Seen { get; } = new();

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<AppSnapshot> Latest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(AppSnapshot value)
        {
            Seen.Enqueue(value);
            if (Interlocked.Increment(ref _count) == 1)
            {
                FirstEntered.TrySetResult();
                ReleaseFirst.Task.GetAwaiter().GetResult();
                return;
            }

            Latest.TrySetResult(value);
        }
    }
}

internal sealed class RuntimeHarness
{
    private RuntimeHarness()
    {
        Audio = new FakeAudioEngine(this);
        Factory = new FakeSessionFactory(this);
        Clock = new ManualClock();
    }

    public ConcurrentQueue<string> Trace { get; } = new();

    public FakeTranslationSession InboundSession { get; } = new("inbound");

    public FakeTranslationSession OutboundSession { get; } = new("outbound");

    public ControlledAsyncGate SettingsGate { get; } = new();

    public ControlledAsyncGate AudioStartGate => Audio.StartGate;

    public ControlledAsyncGate DeviceRefreshGate { get; } = new();

    public RuntimeSettings? Settings { get; set; } = new(
        LanguageCode.Zh,
        LanguageCode.En,
        "gpt-realtime-translate",
        inboundBypass: false,
        outboundBypass: false);

    public RuntimeError? OsError { get; set; }

    public bool SecretAvailable { get; set; } = true;

    public DriverCompatibility DriverCompatibility { get; set; } =
        new(true, "compatible");

    public bool DevicesAvailable { get; set; } = true;

    public RuntimeError? AudioStartError
    {
        get => Audio.StartError;
        set => Audio.StartError = value;
    }

    public int SecretDisposeCount =>
        Volatile.Read(ref _secretDisposeCount);

    public int SettingsLoadCount { get; private set; }

    public int SessionCreateCount => Factory.CreateCount;

    public int AudioStopCount => Audio.StopCount;

    public int AudioStartCount => Audio.StartCount;

    public int MaximumAudioMutationDepth => Audio.MaximumMutationDepth;

    public FakeAudioEngine Audio { get; }

    public FakeSessionFactory Factory { get; }

    public ManualClock Clock { get; }

    public IRuntimeLog RuntimeLog { get; set; } = new NullRuntimeLog();

    private int _secretDisposeCount;

    public static RuntimeHarness Create()
    {
        return new RuntimeHarness();
    }

    public TranslationRuntime CreateRuntime(Task? actorStartBarrier = null)
    {
        TranslationRuntimeDependencies dependencies = new(
            new FakeWindowsBuildGate(this),
            new FakeSettingsStore(this),
            new FakeSecretStore(this),
            new FakeDriverManager(this),
            new FakeDeviceCatalog(this),
            Audio,
            Factory,
            new FakeLanguageClassifier(),
            Clock,
            RuntimeLog);
        return actorStartBarrier is null
            ? new TranslationRuntime(dependencies)
            : new TranslationRuntime(dependencies, actorStartBarrier);
    }

    internal sealed class FakeWindowsBuildGate(
        RuntimeHarness owner) : IWindowsBuildGate
    {
        public ValueTask<RuntimeError?> CheckAsync(
            CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("os");
            return ValueTask.FromResult(owner.OsError);
        }
    }

    internal sealed class FakeSettingsStore(
        RuntimeHarness owner) : ISettingsStore
    {
        public async ValueTask<RuntimeSettings?> LoadAsync(
            CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("settings");
            owner.SettingsLoadCount++;
            await owner.SettingsGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return owner.Settings;
        }

        public ValueTask SaveAsync(
            RuntimeSettings settings,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeSecretStore(
        RuntimeHarness owner) : ISecretStore
    {
        public ValueTask<ISecretBuffer?> LoadAsync(
            string name,
            CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("secret");
            if (!owner.SecretAvailable)
            {
                return ValueTask.FromResult<ISecretBuffer?>(null);
            }
#pragma warning disable CA2000 // Ownership transfers to the runtime through ISecretBuffer.
            FakeSecretBuffer secret = new(owner);
#pragma warning restore CA2000
            return ValueTask.FromResult<ISecretBuffer?>(secret);
        }

        public ValueTask SaveAsync(
            string name,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string name,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeSecretBuffer(RuntimeHarness owner) : ISecretBuffer
    {
        private char[]? _secret = "test-secret".ToCharArray();

        public ReadOnlyMemory<char> Memory =>
            _secret ?? throw new ObjectDisposedException(nameof(FakeSecretBuffer));

        public void Dispose()
        {
            char[]? secret = Interlocked.Exchange(ref _secret, null);
            if (secret is not null)
            {
                Array.Clear(secret);
                Interlocked.Increment(ref owner._secretDisposeCount);
            }
        }
    }

    internal sealed class FakeDriverManager(
        RuntimeHarness owner) : IDriverManager
    {
        public Task<DriverCompatibility> CheckCompatibilityAsync(
            CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("driver");
            return Task.FromResult(owner.DriverCompatibility);
        }
    }

    internal sealed class FakeDeviceCatalog(
        RuntimeHarness owner) : IAudioDeviceCatalog
    {
        private int _calls;

        public async Task<AudioDeviceSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("devices");
            if (Interlocked.Increment(ref _calls) > 1)
            {
                await owner.DeviceRefreshGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            List<AudioDeviceDescriptor> devices =
            [
                new AudioDeviceDescriptor(
                    "input-id",
                    "Default microphone",
                    AudioDeviceDirection.Input,
                    isDefault: true,
                    isAvailable: true),
            ];
            if (owner.DevicesAvailable)
            {
                devices.Add(new AudioDeviceDescriptor(
                    "output-id",
                    "Default headphones",
                    AudioDeviceDirection.Output,
                    isDefault: true,
                    isAvailable: true));
            }

            return new AudioDeviceSnapshot(devices);
        }
    }

    internal sealed class FakeAudioEngine(
        RuntimeHarness owner) : ITranslationAudioEngine
    {
        private readonly Channel<AudioEngineEvent> _events =
            Channel.CreateUnbounded<AudioEngineEvent>();
        private int _mutationDepth;
        private int _maximumMutationDepth;
        private int _pollDepth;
        private int _maximumPollDepth;
        private int _startCount;
        private int _stopCount;
        private int _outboundRouteCallCount;
        private readonly ConcurrentQueue<Exception> _outboundRouteFailures = new();

        public ControlledAsyncGate StartGate { get; } = new();

        public ControlledAsyncGate StopGate { get; } = new();

        public ControlledAsyncGate PollReturnGate { get; } = new();

        public ControlledAsyncGate InboundEnqueueGate { get; } = new();

        public ControlledAsyncGate RouteGate { get; } = new();

        public TaskCompletionSource PollRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InboundEnqueueEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OutboundEnqueueEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeError? StartError { get; set; }

        public Exception? StopException { get; set; }

        public Exception? OutboundRouteException { get; set; }

        public Exception? OutboundEnqueueException { get; set; }

        public Exception? InboundEnqueueException { get; set; }

        public TaskCompletionSource FirstOutboundRouteApplied { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount => Volatile.Read(ref _stopCount);

        public int StartCount => Volatile.Read(ref _startCount);

        public int OutboundRouteCallCount =>
            Volatile.Read(ref _outboundRouteCallCount);

        public int MaximumMutationDepth => Volatile.Read(ref _maximumMutationDepth);

        public int MaximumPollDepth => Volatile.Read(ref _maximumPollDepth);

        public async Task StartAsync(
            AudioEngineConfiguration configuration,
            CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("audio.start");
            Interlocked.Increment(ref _startCount);
            EnterMutation();
            try
            {
                await StartGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (StartError is not null)
                {
                    throw new RuntimeOperationException(StartError);
                }
            }
            finally
            {
                ExitMutation();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("audio.stop");
            EnterMutation();
            try
            {
                Interlocked.Increment(ref _stopCount);
                StopEntered.TrySetResult();
                await StopGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (StopException is not null)
                {
                    throw StopException;
                }
            }
            finally
            {
                ExitMutation();
            }
        }

        public async ValueTask<AudioEngineEvent?> PollEventAsync(
            CancellationToken cancellationToken)
        {
            int depth = Interlocked.Increment(ref _pollDepth);
            UpdateMaximum(ref _maximumPollDepth, depth);
            try
            {
                AudioEngineEvent audio =
                    await _events.Reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                PollRead.TrySetResult();
                await PollReturnGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return audio;
            }
            finally
            {
                Interlocked.Decrement(ref _pollDepth);
            }
        }

        public async ValueTask EnqueueInboundTranslationAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("audio.enqueue.inbound");
            InboundEnqueueEntered.TrySetResult();
            await InboundEnqueueGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (InboundEnqueueException is not null)
            {
                throw InboundEnqueueException;
            }

            owner.Trace.Enqueue("audio.enqueue.inbound.complete");
        }

        public ValueTask EnqueueOutboundTranslationAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken)
        {
            owner.Trace.Enqueue("audio.enqueue.outbound");
            OutboundEnqueueEntered.TrySetResult();
            if (OutboundEnqueueException is not null)
            {
                throw OutboundEnqueueException;
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask SetInboundRouteAsync(
            InboundRoute route,
            CancellationToken cancellationToken)
        {
            await RouteGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask SetOutboundRouteAsync(
            OutboundRoute route,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _outboundRouteCallCount);
            if (_outboundRouteFailures.TryDequeue(out Exception? failure))
            {
                throw failure;
            }

            if (OutboundRouteException is not null)
            {
                throw OutboundRouteException;
            }

            FirstOutboundRouteApplied.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void FailNextOutboundRoute(Exception exception)
        {
            _outboundRouteFailures.Enqueue(exception);
        }

        public void Emit(AudioEngineEvent audio)
        {
            if (!_events.Writer.TryWrite(audio))
            {
                audio.Dispose();
                throw new InvalidOperationException("Fake audio event queue is closed.");
            }
        }

        private void EnterMutation()
        {
            int depth = Interlocked.Increment(ref _mutationDepth);
            UpdateMaximum(ref _maximumMutationDepth, depth);
        }

        private static void UpdateMaximum(ref int maximum, int value)
        {
            int observed;
            while (value > (observed = Volatile.Read(ref maximum)))
            {
                if (Interlocked.CompareExchange(
                        ref maximum,
                        value,
                        observed) == observed)
                {
                    break;
                }
            }
        }

        private void ExitMutation()
        {
            Interlocked.Decrement(ref _mutationDepth);
        }
    }

    internal sealed class FakeSessionFactory(
        RuntimeHarness owner) : ITranslationSessionFactory
    {
        private readonly ConcurrentQueue<FakeTranslationSession> _sessions =
            new([owner.InboundSession, owner.OutboundSession]);
        private int _count;

        public int CreateCount => Volatile.Read(ref _count);

        public void Queue(params FakeTranslationSession[] sessions)
        {
            foreach (FakeTranslationSession session in sessions)
            {
                _sessions.Enqueue(session);
            }
        }

        public ValueTask<ITranslationSession> CreateAsync(
            TranslationSessionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            int count = Interlocked.Increment(ref _count);
            string direction = (count & 1) == 1 ? "inbound" : "outbound";
            owner.Trace.Enqueue($"session.{direction}.create");
#pragma warning disable CA2000 // Ownership transfers from the fake queue into the runtime supervisor.
            if (!_sessions.TryDequeue(out FakeTranslationSession? session))
#pragma warning restore CA2000
            {
                throw new InvalidOperationException("No fake session remains.");
            }

            session.Attach(owner.Trace);
            return ValueTask.FromResult<ITranslationSession>(session);
        }
    }

    internal sealed class FakeLanguageClassifier : ILanguageClassifier
    {
        public ValueTask<LanguageProbabilities> ClassifyAsync(
            string text,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new LanguageProbabilities(1, 0, 0));
        }
    }

    internal sealed class ManualClock : IClock
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _pending = new();

        public ConcurrentQueue<TimeSpan> Delays { get; } = new();

        public TaskCompletionSource DelayEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan MonotonicNow { get; private set; }

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Delays.Enqueue(delay);
            MonotonicNow += delay;
            TaskCompletionSource delaySource =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                delaySource);
            _pending.Enqueue(delaySource);
            DelayEntered.TrySetResult();
            return new ValueTask(delaySource.Task);
        }

        public void ReleaseAll()
        {
            while (_pending.TryDequeue(out TaskCompletionSource? completion))
            {
                completion.TrySetResult();
            }
        }
    }

    internal sealed class NullRuntimeLog : IRuntimeLog
    {
        public void Write(
            RuntimeLogLevel level,
            string eventName,
            IReadOnlyDictionary<string, string> safeFields)
        {
        }
    }

    internal sealed class ThrowingRuntimeLog : IRuntimeLog
    {
        public void Write(
            RuntimeLogLevel level,
            string eventName,
            IReadOnlyDictionary<string, string> safeFields)
        {
            throw new NotSupportedException("log probe");
        }
    }
}

internal sealed class ControlledAsyncGate
{
    private readonly object _sync = new();
    private TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _blocked;

    public TaskCompletionSource Entered
    {
        get
        {
            lock (_sync)
            {
                return _entered;
            }
        }
    }

    public void Block()
    {
        lock (_sync)
        {
            _blocked = true;
            _entered =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_blocked)
            {
                return Task.CompletedTask;
            }

            _entered.TrySetResult();
            return _release.Task;
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            _blocked = false;
            _release.TrySetResult();
        }
    }
}

internal sealed class FakeTranslationSession(string direction) :
    ITranslationSession,
    IDisposable
{
    private readonly Channel<TranslationSessionEvent> _events =
        Channel.CreateUnbounded<TranslationSessionEvent>();
    private ConcurrentQueue<string>? _trace;
    private int _closeCount;
    private int _sendCount;
    private int _disposed;

    public RuntimeError? ConnectError { get; set; }

    public TranslationSessionEvent.AudioDelta? TailEvent { get; set; }

    public Exception? CloseException { get; set; }

    public bool EmitTailAfterCloseGate { get; set; }

    public ControlledAsyncGate CloseGate { get; } = new();

    public int CloseCount => Volatile.Read(ref _closeCount);

    public int SendCount => Volatile.Read(ref _sendCount);

    public TaskCompletionSource SendEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ControlledAsyncGate SendGate { get; } = new();

    public void Attach(ConcurrentQueue<string> trace)
    {
        _trace = trace;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _trace!.Enqueue($"session.{direction}.connect");
        return ConnectError is null
            ? Task.CompletedTask
            : Task.FromException(new RuntimeOperationException(ConnectError));
    }

    public async ValueTask SendPcmAsync(
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sendCount);
        SendEntered.TrySetResult();
        await SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TranslationSessionEvent> ReceiveAsync(
        CancellationToken cancellationToken)
    {
        return _events.Reader.ReadAllAsync(cancellationToken);
    }

    public void Emit(TranslationSessionEvent sessionEvent)
    {
        if (!_events.Writer.TryWrite(sessionEvent))
        {
            if (sessionEvent is IDisposable disposable)
            {
                disposable.Dispose();
            }

            throw new InvalidOperationException("Fake session event queue is closed.");
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _closeCount);
        _trace!.Enqueue($"session.{direction}.close");
        if (!EmitTailAfterCloseGate)
        {
            EmitTail();
        }

        await CloseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (CloseException is not null)
        {
            throw CloseException;
        }

        EmitTail();
        _events.Writer.TryComplete();
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            TailEvent?.Dispose();
            TailEvent = null;
        }
    }

    private void EmitTail()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            TailEvent?.Dispose();
            TailEvent = null;
            return;
        }

        if (TailEvent is not null)
        {
            _events.Writer.TryWrite(TailEvent);
            TailEvent = null;
        }
    }
}

internal sealed class TrackingPcmLease(byte[] bytes) : IPcmBufferLease
{
    private byte[]? _bytes = bytes;

    public int DisposeCount { get; private set; }

    public TaskCompletionSource Disposed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReadOnlyMemory<byte> Memory =>
        _bytes ?? throw new ObjectDisposedException(nameof(TrackingPcmLease));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _bytes, null) is not null)
        {
            DisposeCount++;
            Disposed.TrySetResult();
        }
    }
}
