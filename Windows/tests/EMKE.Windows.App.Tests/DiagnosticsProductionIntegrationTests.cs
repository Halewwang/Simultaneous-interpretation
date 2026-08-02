using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Settings;
using EMKE.Windows.App.Diagnostics;
using EMKE.Windows.App.Settings;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class DiagnosticsProductionIntegrationTests
{
    [TestMethod]
    public async Task SettingsCapabilityTesterLoadsLatestSettingsAndUsesProbe()
    {
        MutableSettingsStore settings = new(Settings(
            LanguageCode.En,
            LanguageCode.Zh,
            "first-model"));
        CapturingProbe probe = new(CompatibleReport());
        SettingsTranslationCapabilityTester tester = new(settings, probe);
        settings.Current = Settings(
            LanguageCode.De,
            LanguageCode.En,
            "latest-model");

        await tester.TestConnectionAsync(CancellationToken.None);

        Assert.AreEqual(1, probe.CallCount);
        Assert.AreEqual(
            "https://example.invalid/v1",
            probe.Inbound!.BaseAddress.AbsoluteUri);
        Assert.AreEqual(LanguageCode.En, probe.Inbound.Configuration.SourceLanguage);
        Assert.AreEqual(LanguageCode.De, probe.Inbound.Configuration.TargetLanguage);
        Assert.AreEqual("latest-model", probe.Inbound.Configuration.Model);
        Assert.AreEqual(LanguageCode.De, probe.Outbound!.Configuration.SourceLanguage);
        Assert.AreEqual(LanguageCode.En, probe.Outbound.Configuration.TargetLanguage);
        Assert.AreEqual("latest-model", probe.Outbound.Configuration.Model);
    }

    [TestMethod]
    public async Task ProtocolCompatibleRequiresAudioIsSuccessfulButIncompatibleFailsClosed()
    {
        MutableSettingsStore settings = new(Settings(
            LanguageCode.En,
            LanguageCode.Zh,
            "translation-model"));
        SettingsTranslationCapabilityTester compatible = new(
            settings,
            new CapturingProbe(CompatibleReport()));
        SettingsTranslationCapabilityTester incompatible = new(
            settings,
            new CapturingProbe(IncompatibleReport()));

        await compatible.TestConnectionAsync(CancellationToken.None);
        InvalidOperationException failure =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => incompatible.TestConnectionAsync(
                    CancellationToken.None));

        Assert.AreEqual(
            SettingsTranslationCapabilityTester.IncompatibleCode,
            failure.Message);
    }

    private static WindowsProductSettings Settings(
        LanguageCode native,
        LanguageCode meeting,
        string model)
    {
        return new WindowsProductSettings(
            new Uri("https://example.invalid/v1"),
            model,
            native,
            meeting,
            inputEndpointId: null,
            outputEndpointId: null,
            followDefaultInput: true,
            followDefaultOutput: true,
            interfaceLanguage: "english",
            onboardingPreferenceIdentifiers: []);
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

    private static TranslationCompatibilityReport IncompatibleReport()
    {
        return new TranslationCompatibilityReport(
        [
            Stage("authentication", TranslationCapabilityOutcome.Failed),
            Stage("translationWebSocketHandshake", TranslationCapabilityOutcome.NotRun),
            Stage("targetLanguageUpdate", TranslationCapabilityOutcome.NotRun),
            Stage("dualSessionConcurrency", TranslationCapabilityOutcome.Failed),
            Stage("sourceTranscript", TranslationCapabilityOutcome.NotRun),
            Stage("translatedAudio", TranslationCapabilityOutcome.NotRun),
            Stage("safeClose", TranslationCapabilityOutcome.Passed),
        ]);
    }

    private static TranslationCompatibilityStageResult Stage(
        string stableName,
        TranslationCapabilityOutcome outcome)
    {
        return new TranslationCompatibilityStageResult(
            stableName,
            outcome);
    }

    private sealed class MutableSettingsStore(WindowsProductSettings current)
        : IWindowsProductSettingsStore
    {
        public WindowsProductSettings Current { get; set; } = current;

        public ValueTask<WindowsProductSettings> LoadProductSettingsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current);
        }

        public ValueTask SaveProductSettingsAsync(
            WindowsProductSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = settings;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingProbe(TranslationCompatibilityReport report)
        : ITranslationConnectionProbe
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
}

#pragma warning restore CA2007
#pragma warning restore CA1515
