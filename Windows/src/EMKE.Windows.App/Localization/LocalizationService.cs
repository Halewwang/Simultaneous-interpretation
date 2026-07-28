using System.Globalization;
using System.Resources;

namespace EMKE.Windows.App.Localization;

internal enum LocalizedString
{
    AppName,
    ActionDismiss,
    ActionEditApiKey,
    ActionEditSettings,
    ActionInstallOrRepairDriver,
    ActionOpenPrivacySettings,
    ActionReportCompatibility,
    ActionRetry,
    ActionSelectDevice,
    ActionStart,
    ActionStop,
    DashboardDiagnostics,
    DashboardInbound,
    DashboardInboundBypass,
    DashboardLevel,
    DashboardMeetingLanguage,
    DashboardNativeLanguage,
    DashboardOutbound,
    DashboardOutboundBypass,
    DashboardSettings,
    DashboardSourceCaption,
    DashboardTranslatedCaption,
    DashboardTitle,
    LanguageEnglish,
    LanguageSimplifiedChinese,
    PlaceholderClose,
    PlaceholderDiagnosticsBody,
    PlaceholderDiagnosticsTitle,
    PlaceholderOnboardingBody,
    PlaceholderOnboardingTitle,
    PlaceholderSettingsBody,
    PlaceholderSettingsTitle,
    PlaceholderUpdateBody,
    PlaceholderUpdateTitle,
    BypassBadge,
    ChannelBypassed,
    ChannelConnected,
    ChannelConnecting,
    ChannelDegraded,
    ChannelFailed,
    ChannelInactive,
    ChannelReconnecting,
    ErrorAuthentication,
    ErrorBackpressure,
    ErrorCloseTimeout,
    ErrorConfiguration,
    ErrorDefaultDeviceMissing,
    ErrorDevice,
    ErrorDriver,
    ErrorDriverIncompatible,
    ErrorEndpointModel,
    ErrorNetwork,
    ErrorPermission,
    ErrorProtocol,
    ErrorSecretMissing,
    ErrorSettingsMissing,
    InboundFailOpenExplanation,
    OutboundFailClosedWarning,
    RouteOriginalBypass,
    RouteOriginalFailOpen,
    RouteStopped,
    RouteTranslated,
    RouteMutedFailClosed,
    RuntimeDegraded,
    RuntimeFailed,
    RuntimeRunning,
    RuntimeStarting,
    RuntimeStopped,
    RuntimeStopping,
    TrayCheckForUpdates,
    TrayExit,
    TrayOpenDashboard,
    TrayOpenOnboarding,
    TrayOpenSettings,
}

internal sealed class AppInterfaceLanguageChangedEventArgs : EventArgs
{
    public AppInterfaceLanguageChangedEventArgs(AppInterfaceLanguage language)
    {
        Language = language;
    }

    public AppInterfaceLanguage Language { get; }
}

internal sealed class LocalizationService
{
    private static readonly CultureInfo SimplifiedChineseCulture =
        CultureInfo.GetCultureInfo("zh-CN");
    private static readonly ResourceManager Resources =
        new(
            "EMKE.Windows.App.Localization.Strings",
            typeof(LocalizationService).Assembly);

    private readonly object _sync = new();
    private readonly Func<CultureInfo> _systemCulture;
    private AppInterfaceLanguage _currentLanguage;

    public LocalizationService(Func<CultureInfo>? systemCulture = null)
    {
        _systemCulture = systemCulture ?? (() => CultureInfo.CurrentUICulture);
        _currentLanguage = AppInterfaceLanguage.System;
    }

    public event EventHandler<AppInterfaceLanguageChangedEventArgs>?
        LanguageChanged;

    public AppInterfaceLanguage CurrentLanguage
    {
        get
        {
            lock (_sync)
            {
                return _currentLanguage;
            }
        }
    }

    public static CultureInfo ResolveResourceCulture(
        AppInterfaceLanguage language,
        CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);

        return language switch
        {
            AppInterfaceLanguage.ZhHans => SimplifiedChineseCulture,
            AppInterfaceLanguage.English => CultureInfo.InvariantCulture,
            AppInterfaceLanguage.System
                when systemCulture.Name.Equals(
                    "zh-Hans",
                    StringComparison.OrdinalIgnoreCase)
                    || systemCulture.Name.Equals(
                        "zh-CN",
                        StringComparison.OrdinalIgnoreCase)
                    || systemCulture.Name.Equals(
                        "zh-SG",
                        StringComparison.OrdinalIgnoreCase)
                => SimplifiedChineseCulture,
            AppInterfaceLanguage.System => CultureInfo.InvariantCulture,
            _ => throw new ArgumentOutOfRangeException(
                nameof(language),
                language,
                "Undefined interface language."),
        };
    }

    public string Get(
        LocalizedString key,
        AppInterfaceLanguage language)
    {
        if (!Enum.IsDefined(key))
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "Undefined localized resource key.");
        }

        CultureInfo culture = ResolveResourceCulture(
            language,
            _systemCulture());
        return Resources.GetString(key.ToString(), culture)
            ?? throw new MissingManifestResourceException(
                $"Missing localized resource '{key}' for '{culture.Name}'.");
    }

    public void ChangeLanguage(AppInterfaceLanguage language)
    {
        _ = language.ToStableValue();

        bool changed;
        lock (_sync)
        {
            changed = _currentLanguage != language;
            _currentLanguage = language;
        }

        if (changed)
        {
            LanguageChanged?.Invoke(
                this,
                new AppInterfaceLanguageChangedEventArgs(language));
        }
    }
}
