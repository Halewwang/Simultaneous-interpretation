using EMKE.Platform.Diagnostics;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.
#pragma warning disable CA2000 // Test method lifetime owns each async-disposable diagnostic service.

[TestClass]
public sealed class WindowsAudioDiagnosticsTests
{
    [TestMethod]
    public async Task InputTestPublishesOnlyABoundedLevel()
    {
        FakeDiagnosticBackend backend = new()
        {
            InputLevel = 1.7,
        };
        WindowsAudioDiagnostics diagnostics = new(backend, () => false);

        WindowsAudioDiagnosticResult result =
            await diagnostics.RunInputTestAsync(
                "physical-input-secret-id",
                CancellationToken.None);

        Assert.AreEqual(WindowsAudioDiagnosticKind.InputLevel, result.Kind);
        Assert.AreEqual(1d, result.Level);
        Assert.AreEqual(0, backend.PlayCount);
        Assert.AreEqual(0, backend.ServiceCallCount);
        Assert.IsNull(result.Pcm16);
    }

    [TestMethod]
    public async Task OutputTestPlaysGeneratedToneLocallyWithoutServiceTraffic()
    {
        FakeDiagnosticBackend backend = new();
        WindowsAudioDiagnostics diagnostics = new(backend, () => false);

        WindowsAudioDiagnosticResult result =
            await diagnostics.RunOutputTestAsync(
                "physical-output-secret-id",
                CancellationToken.None);

        Assert.AreEqual(WindowsAudioDiagnosticKind.LocalOutputTone, result.Kind);
        Assert.IsTrue(result.IsSuccessful);
        Assert.AreEqual(1, backend.PlayCount);
        Assert.AreEqual("physical-output-secret-id", backend.LastOutputEndpointId);
        Assert.HasCount(48_000 / 4, backend.LastTone);
        Assert.IsTrue(backend.LastTone.Any(static sample => sample != 0));
        Assert.AreEqual(0, backend.ServiceCallCount);
    }

    [TestMethod]
    public async Task VirtualEndpointTestInspectsRolesWithoutStartingAudioOrService()
    {
        FakeDiagnosticBackend backend = new()
        {
            Snapshot = Snapshot(),
        };
        WindowsAudioDiagnostics diagnostics = new(backend, () => false);

        WindowsAudioDiagnosticResult result =
            await diagnostics.InspectVirtualEndpointsAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccessful);
        Assert.HasCount(6, result.Snapshot!.Endpoints);
        Assert.AreEqual(1, backend.InspectCount);
        Assert.AreEqual(0, backend.MeasureCount);
        Assert.AreEqual(0, backend.PlayCount);
        Assert.AreEqual(0, backend.ServiceCallCount);
    }

    [TestMethod]
    public async Task StartingSecondDiagnosticCancelsAndJoinsFirst()
    {
        FakeDiagnosticBackend backend = new()
        {
            BlockInput = true,
        };
        WindowsAudioDiagnostics diagnostics = new(backend, () => false);
        Task<WindowsAudioDiagnosticResult> first =
            diagnostics.RunInputTestAsync("input", CancellationToken.None);
        await backend.InputEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        WindowsAudioDiagnosticResult second =
            await diagnostics.RunOutputTestAsync("output", CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await first);
        Assert.AreEqual(1, backend.InputCancellationObserved);
        Assert.AreEqual(1, backend.InputExited);
        Assert.AreEqual(WindowsAudioDiagnosticKind.LocalOutputTone, second.Kind);
    }

    [TestMethod]
    public async Task StartingSecondDiagnosticContinuesAfterCanceledProviderFault()
    {
        FakeDiagnosticBackend backend = new()
        {
            BlockInput = true,
            ThrowProviderFailureOnCancellation = true,
        };
        WindowsAudioDiagnostics diagnostics = new(backend, () => false);
        Task<WindowsAudioDiagnosticResult> first =
            diagnostics.RunInputTestAsync("input", CancellationToken.None);
        await backend.InputEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        WindowsAudioDiagnosticResult second =
            await diagnostics.RunOutputTestAsync("output", CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await first);
        Assert.AreEqual(WindowsAudioDiagnosticKind.LocalOutputTone, second.Kind);
        Assert.AreEqual(1, backend.InputExited);
        Assert.AreEqual(
            WindowsAudioDiagnostics.ProviderCleanupFailureCode,
            diagnostics.LastErrorCode);
    }

    [TestMethod]
    public async Task StopCancelsAndJoinsRunningDiagnostic()
    {
        FakeDiagnosticBackend backend = new()
        {
            BlockInput = true,
        };
        WindowsAudioDiagnostics diagnostics = new(backend, () => false);
        Task<WindowsAudioDiagnosticResult> running =
            diagnostics.RunInputTestAsync("input", CancellationToken.None);
        await backend.InputEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await diagnostics.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await running);
        Assert.AreEqual(1, backend.InputExited);
        Assert.IsFalse(diagnostics.IsRunning);
    }

    [TestMethod]
    public async Task TranslationActivityBlocksEveryDiagnostic()
    {
        FakeDiagnosticBackend backend = new();
        WindowsAudioDiagnostics diagnostics = new(backend, () => true);

        InvalidOperationException input =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => diagnostics.RunInputTestAsync(
                    "input",
                    CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => diagnostics.RunOutputTestAsync(
                "output",
                CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => diagnostics.InspectVirtualEndpointsAsync(
                CancellationToken.None));

        Assert.AreEqual(
            WindowsAudioDiagnostics.TranslationActiveCode,
            input.Message);
        Assert.AreEqual(0, backend.CallCount);
    }

    private static WindowsAudioDiagnosticSnapshot Snapshot()
    {
        return new WindowsAudioDiagnosticSnapshot(
        [
            Endpoint("input", WindowsAudioEndpointRole.PhysicalInput),
            Endpoint("output", WindowsAudioEndpointRole.PhysicalOutput),
            Endpoint("speaker-render", WindowsAudioEndpointRole.MeetingSpeakerRender),
            Endpoint("speaker-capture", WindowsAudioEndpointRole.AppSpeakerCapture),
            Endpoint("microphone-render", WindowsAudioEndpointRole.AppMicrophoneRender),
            Endpoint("microphone-capture", WindowsAudioEndpointRole.MeetingMicrophoneCapture),
        ],
        new WindowsAudioDiagnosticCounters(
            LastHResultCategory: "none",
            Underruns: 2,
            Overflows: 3,
            DroppedFrames: 4));
    }

    private static WindowsAudioEndpointDiagnostic Endpoint(
        string id,
        WindowsAudioEndpointRole role)
    {
        return new WindowsAudioEndpointDiagnostic(
            id,
            $"Friendly {role}",
            role,
            "48000 Hz, 2 channel, float32",
            IsAvailable: true);
    }

    private sealed class FakeDiagnosticBackend : IWindowsAudioDiagnosticBackend
    {
        public double InputLevel { get; set; } = 0.5;

        public bool BlockInput { get; set; }

        public bool ThrowProviderFailureOnCancellation { get; set; }

        public WindowsAudioDiagnosticSnapshot Snapshot { get; set; } =
            WindowsAudioDiagnosticSnapshot.Empty;

        public int InspectCount { get; private set; }

        public int MeasureCount { get; private set; }

        public int PlayCount { get; private set; }

        public int ServiceCallCount { get; private set; }

        public int InputCancellationObserved { get; private set; }

        public int InputExited { get; private set; }

        public int CallCount => InspectCount + MeasureCount + PlayCount;

        public TaskCompletionSource InputEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastOutputEndpointId { get; private set; }

        public short[] LastTone { get; private set; } = [];

        public async Task<double> MeasureInputLevelAsync(
            string endpointId,
            CancellationToken cancellationToken)
        {
            MeasureCount++;
            InputEntered.TrySetResult();
            try
            {
                if (BlockInput)
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }

                return InputLevel;
            }
            catch (OperationCanceledException)
            {
                InputCancellationObserved++;
                if (ThrowProviderFailureOnCancellation)
                {
                    throw new InvalidOperationException(
                        "provider cancellation cleanup failed");
                }

                throw;
            }
            finally
            {
                InputExited++;
            }
        }

        public Task PlayLocalPcm16Async(
            string endpointId,
            ReadOnlyMemory<short> pcm16,
            int sampleRate,
            int channelCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlayCount++;
            LastOutputEndpointId = endpointId;
            LastTone = pcm16.ToArray();
            Assert.AreEqual(48_000, sampleRate);
            Assert.AreEqual(1, channelCount);
            return Task.CompletedTask;
        }

        public Task<WindowsAudioDiagnosticSnapshot> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCount++;
            return Task.FromResult(Snapshot);
        }
    }
}

#pragma warning restore CA2007
#pragma warning restore CA2000
#pragma warning restore CA1515
