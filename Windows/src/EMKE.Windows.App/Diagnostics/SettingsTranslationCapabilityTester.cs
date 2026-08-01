using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Settings;
using EMKE.Windows.App.Settings;

namespace EMKE.Windows.App.Diagnostics;

internal sealed class SettingsTranslationCapabilityTester
    : ISettingsCapabilityTester
{
    public const string IncompatibleCode =
        "translationCapability.protocolIncompatible";

    private readonly IWindowsProductSettingsStore _settings;
    private readonly ITranslationConnectionProbe _probe;

    public SettingsTranslationCapabilityTester(
        IWindowsProductSettingsStore settings,
        ITranslationConnectionProbe probe)
    {
        _settings = settings
            ?? throw new ArgumentNullException(nameof(settings));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task TestConnectionAsync(
        CancellationToken cancellationToken)
    {
        WindowsProductSettings settings =
            await _settings.LoadProductSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
        TranslationCompatibilityReport report =
            await _probe.RunAsync(
                    Inbound(settings),
                    Outbound(settings),
                    cancellationToken)
                .ConfigureAwait(false);
        if (report.Overall
            != TranslationCompatibilityOverall.ProtocolCompatibleRequiresAudio)
        {
            throw new InvalidOperationException(IncompatibleCode);
        }
    }

    internal static TranslationSessionConfiguration Inbound(
        WindowsProductSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TranslationSessionConfiguration(
            settings.MeetingLanguage,
            settings.NativeLanguage,
            settings.ModelId);
    }

    internal static TranslationSessionConfiguration Outbound(
        WindowsProductSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TranslationSessionConfiguration(
            settings.NativeLanguage,
            settings.MeetingLanguage,
            settings.ModelId);
    }
}
