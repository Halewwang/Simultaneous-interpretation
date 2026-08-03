using System.Security.Cryptography;
using System.Text;
using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Diagnostics;
using EMKE.Windows.App.Diagnostics;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.
#pragma warning disable CA2000 // View-model lifetime is explicitly ended by the test.
#pragma warning disable CA1001 // The fake token source lives for the bounded test process.

[TestClass]
public sealed class DiagnosticsViewModelTests
{
    private static readonly TranslationSessionRequest Inbound = new(
        new Uri("https://translation.example.test/v1", UriKind.Absolute),
        new TranslationSessionConfiguration(
            LanguageCode.En,
            LanguageCode.Zh,
            "translation-model"));

    private static readonly TranslationSessionRequest Outbound = new(
        new Uri("https://translation.example.test/v1", UriKind.Absolute),
        new TranslationSessionConfiguration(
            LanguageCode.Zh,
            LanguageCode.En,
            "translation-model"));

    [TestMethod]
    public async Task EndpointRowsExposeFriendlyNameFormatRoleAndOnlyEightCharacterHash()
    {
        const string secretEndpointId =
            "{0.0.1.00000000}.private-device-identifier";
        WindowsAudioDiagnosticSnapshot snapshot = new(
        [
            new WindowsAudioEndpointDiagnostic(
                secretEndpointId,
                "Studio Microphone",
                WindowsAudioEndpointRole.PhysicalInput,
                "48000 Hz, 1 channel, PCM16",
                IsAvailable: true),
        ],
        new WindowsAudioDiagnosticCounters(
            "deviceUnavailable",
            Underruns: 7,
            Overflows: 8,
            DroppedFrames: 9));
        FakeAudioDiagnostics audio = new(snapshot);
        DiagnosticsViewModel viewModel = new(
            audio,
            new FakeConnectionProbe(CompatibleReport()),
            Inbound,
            Outbound);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.HasCount(1, viewModel.Endpoints);
        DiagnosticEndpointRow row = viewModel.Endpoints[0];
        Assert.AreEqual("Studio Microphone", row.FriendlyName);
        Assert.AreEqual("48000 Hz, 1 channel, PCM16", row.CurrentFormat);
        Assert.AreEqual(WindowsAudioEndpointRole.PhysicalInput, row.Role);
        Assert.AreEqual(ExpectedHash(secretEndpointId), row.EndpointHash);
        Assert.HasCount(8, row.EndpointHash);
        Assert.IsFalse(
            row.DisplayName.Contains(secretEndpointId, StringComparison.Ordinal));
        Assert.AreEqual("deviceUnavailable", viewModel.LastHResultCategory);
        Assert.AreEqual(7UL, viewModel.Underruns);
        Assert.AreEqual(8UL, viewModel.Overflows);
        Assert.AreEqual(9UL, viewModel.DroppedFrames);
    }

    [TestMethod]
    public async Task ConnectionCheckPublishesSevenStageReportFromProbe()
    {
        TranslationCompatibilityReport expected = CompatibleReport();
        FakeConnectionProbe probe = new(expected);
        DiagnosticsViewModel viewModel = new(
            new FakeAudioDiagnostics(WindowsAudioDiagnosticSnapshot.Empty),
            probe,
            Inbound,
            Outbound);

        await viewModel.TestConnectionAsync(CancellationToken.None);

        Assert.AreSame(expected, viewModel.ConnectionReport);
        Assert.AreSame(Inbound, probe.Inbound);
        Assert.AreSame(Outbound, probe.Outbound);
        Assert.AreEqual(1, probe.CallCount);
    }

    [TestMethod]
    public async Task ClosingCancelsAndJoinsDiagnosticBeforeReturning()
    {
        FakeAudioDiagnostics audio = new(
            WindowsAudioDiagnosticSnapshot.Empty)
        {
            BlockInput = true,
        };
        DiagnosticsViewModel viewModel = new(
            audio,
            new FakeConnectionProbe(CompatibleReport()),
            Inbound,
            Outbound);
        Task run = viewModel.RunInputTestAsync(
            "input-secret",
            CancellationToken.None);
        await audio.InputEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.CloseAsync(CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await run);
        Assert.AreEqual(1, audio.StopCount);
        Assert.AreEqual(1, audio.InputExited);
        Assert.IsFalse(viewModel.IsDiagnosticRunning);
    }

    [TestMethod]
    public async Task NavigationStopCancelsAndJoinsButAllowsAnotherDiagnostic()
    {
        FakeAudioDiagnostics audio = new(
            WindowsAudioDiagnosticSnapshot.Empty)
        {
            BlockInput = true,
        };
        DiagnosticsViewModel viewModel = new(
            audio,
            new FakeConnectionProbe(CompatibleReport()),
            Inbound,
            Outbound);
        Task first = viewModel.RunInputTestAsync(
            "first-private-input",
            CancellationToken.None);
        await audio.InputEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await first);
        audio.BlockInput = false;
        await viewModel.RunInputTestAsync(
            "second-private-input",
            CancellationToken.None);
        Assert.AreEqual(2, audio.InputCallCount);
        Assert.IsFalse(viewModel.IsDiagnosticRunning);
    }

    private static TranslationCompatibilityReport CompatibleReport()
    {
        return new TranslationCompatibilityReport(
        [
            Stage("authentication", TranslationCapabilityOutcome.Passed),
            Stage("translationWebSocketHandshake", TranslationCapabilityOutcome.Passed),
            Stage("targetLanguageUpdate", TranslationCapabilityOutcome.Passed),
            Stage("dualSessionConcurrency", TranslationCapabilityOutcome.Passed),
            Stage("sourceTranscript", TranslationCapabilityOutcome.RequiresInteractiveAudio),
            Stage("translatedAudio", TranslationCapabilityOutcome.RequiresInteractiveAudio),
            Stage("safeClose", TranslationCapabilityOutcome.Passed),
        ]);
    }

    private static TranslationCompatibilityStageResult Stage(
        string name,
        TranslationCapabilityOutcome outcome) =>
        new(name, outcome);

    private static string ExpectedHash(string endpointId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(endpointId));
        return Convert.ToHexString(digest.AsSpan(0, 4));
    }

    private sealed class FakeConnectionProbe(
        TranslationCompatibilityReport report) : ITranslationConnectionProbe
    {
        public int CallCount { get; private set; }

        public TranslationSessionRequest? Inbound { get; private set; }

        public TranslationSessionRequest? Outbound { get; private set; }

        public Task<TranslationCompatibilityReport> RunAsync(
            TranslationSessionRequest inbound,
            TranslationSessionRequest outbound,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Inbound = inbound;
            Outbound = outbound;
            return Task.FromResult(report);
        }
    }

    private sealed class FakeAudioDiagnostics(
        WindowsAudioDiagnosticSnapshot snapshot) : IWindowsAudioDiagnostics
    {
        private CancellationTokenSource _stop = new();

        public bool BlockInput { get; set; }

        public bool IsRunning { get; private set; }

        public string? LastErrorCode => null;

        public int StopCount { get; private set; }

        public int InputExited { get; private set; }

        public int InputCallCount { get; private set; }

        public TaskCompletionSource InputEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WindowsAudioDiagnosticResult> RunInputTestAsync(
            string endpointId,
            CancellationToken cancellationToken)
        {
            InputCallCount++;
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _stop.Token);
            IsRunning = true;
            InputEntered.TrySetResult();
            try
            {
                if (BlockInput)
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        linked.Token);
                }

                return new WindowsAudioDiagnosticResult(
                    WindowsAudioDiagnosticKind.InputLevel,
                    IsSuccessful: true,
                    Level: 0.5,
                    Snapshot: null);
            }
            finally
            {
                IsRunning = false;
                InputExited++;
            }
        }

        public Task<WindowsAudioDiagnosticResult> RunOutputTestAsync(
            string endpointId,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Not used by this test.");
        }

        public Task<WindowsAudioDiagnosticResult> InspectVirtualEndpointsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new WindowsAudioDiagnosticResult(
                    WindowsAudioDiagnosticKind.VirtualEndpoints,
                    IsSuccessful: true,
                    Level: null,
                    snapshot));
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            CancellationTokenSource stop = _stop;
            await stop.CancelAsync();
            while (IsRunning)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _stop = new CancellationTokenSource();
            stop.Dispose();
        }
    }
}

#pragma warning restore CA2000
#pragma warning restore CA1001
#pragma warning restore CA2007
#pragma warning restore CA1515
