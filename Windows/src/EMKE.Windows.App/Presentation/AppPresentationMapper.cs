using EMKE.Core;
using EMKE.Windows.App.Localization;

namespace EMKE.Windows.App.Presentation;

internal sealed class AppPresentationMapper
{
    private readonly LocalizationService _localization;

    public AppPresentationMapper(LocalizationService localization)
    {
        _localization = localization
            ?? throw new ArgumentNullException(nameof(localization));
    }

    public AppPresentation Map(
        AppSnapshot snapshot,
        AppInterfaceLanguage language)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AppPresentation(
            snapshot.Version,
            RuntimeLabel(snapshot.RuntimeState, language),
            RuntimeSeverity(snapshot.RuntimeState),
            snapshot.RuntimeState
                is RuntimeState.Starting or RuntimeState.Stopping,
            StartAction(snapshot.RuntimeState, language),
            StopAction(snapshot.RuntimeState, language),
            InboundChannel(snapshot, language),
            OutboundChannel(snapshot, language),
            snapshot.InboundLevel,
            snapshot.OutboundLevel,
            snapshot.SourceCaption,
            snapshot.TranslatedCaption,
            Error(snapshot.Error, language));
    }

    private string RuntimeLabel(
        RuntimeState state,
        AppInterfaceLanguage language)
    {
        LocalizedString key = state switch
        {
            RuntimeState.Stopped => LocalizedString.RuntimeStopped,
            RuntimeState.Starting => LocalizedString.RuntimeStarting,
            RuntimeState.Running => LocalizedString.RuntimeRunning,
            RuntimeState.Stopping => LocalizedString.RuntimeStopping,
            RuntimeState.Degraded => LocalizedString.RuntimeDegraded,
            RuntimeState.Failed => LocalizedString.RuntimeFailed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Undefined runtime state."),
        };
        return _localization.Get(key, language);
    }

    private static PresentationSeverity RuntimeSeverity(RuntimeState state)
    {
        return state switch
        {
            RuntimeState.Starting or RuntimeState.Stopping =>
                PresentationSeverity.Progress,
            RuntimeState.Degraded => PresentationSeverity.Warning,
            RuntimeState.Failed => PresentationSeverity.Critical,
            RuntimeState.Stopped or RuntimeState.Running =>
                PresentationSeverity.Normal,
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Undefined runtime state."),
        };
    }

    private PresentationAction StartAction(
        RuntimeState state,
        AppInterfaceLanguage language)
    {
        return state switch
        {
            RuntimeState.Stopped or RuntimeState.Failed =>
                Action(
                    PresentationActionKind.Start,
                    LocalizedString.ActionStart,
                    isVisible: true,
                    isEnabled: true,
                    language),
            RuntimeState.Starting =>
                Action(
                    PresentationActionKind.Start,
                    LocalizedString.ActionStart,
                    isVisible: true,
                    isEnabled: false,
                    language),
            RuntimeState.Running
                or RuntimeState.Stopping
                or RuntimeState.Degraded =>
                Action(
                    PresentationActionKind.Start,
                    LocalizedString.ActionStart,
                    isVisible: false,
                    isEnabled: false,
                    language),
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Undefined runtime state."),
        };
    }

    private PresentationAction StopAction(
        RuntimeState state,
        AppInterfaceLanguage language)
    {
        return state switch
        {
            RuntimeState.Starting
                or RuntimeState.Running
                or RuntimeState.Degraded =>
                Action(
                    PresentationActionKind.Stop,
                    LocalizedString.ActionStop,
                    isVisible: true,
                    isEnabled: true,
                    language),
            RuntimeState.Stopping =>
                Action(
                    PresentationActionKind.Stop,
                    LocalizedString.ActionStop,
                    isVisible: true,
                    isEnabled: false,
                    language),
            RuntimeState.Stopped or RuntimeState.Failed =>
                Action(
                    PresentationActionKind.Stop,
                    LocalizedString.ActionStop,
                    isVisible: false,
                    isEnabled: false,
                    language),
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Undefined runtime state."),
        };
    }

    private ChannelPresentation InboundChannel(
        AppSnapshot snapshot,
        AppInterfaceLanguage language)
    {
        bool bypass = snapshot.InboundRoute == InboundRoute.OriginalBypass
            || snapshot.InboundChannelState == ChannelState.Bypassed;
        string? safetyMessage =
            snapshot.InboundRoute == InboundRoute.OriginalFailOpen
                ? _localization.Get(
                    LocalizedString.InboundFailOpenExplanation,
                    language)
                : null;

        return new ChannelPresentation(
            IsVisible: true,
            ChannelLabel(snapshot.InboundChannelState, language),
            InboundRouteLabel(snapshot.InboundRoute, language),
            ChannelSeverity(
                snapshot.InboundChannelState,
                safetyMessage is not null),
            safetyMessage,
            bypass,
            bypass
                ? _localization.Get(LocalizedString.BypassBadge, language)
                : null);
    }

    private ChannelPresentation OutboundChannel(
        AppSnapshot snapshot,
        AppInterfaceLanguage language)
    {
        bool bypass = snapshot.OutboundRoute == OutboundRoute.OriginalBypass
            || snapshot.OutboundChannelState == ChannelState.Bypassed;
        string? safetyMessage =
            snapshot.OutboundRoute == OutboundRoute.MutedFailClosed
                ? _localization.Get(
                    LocalizedString.OutboundFailClosedWarning,
                    language)
                : null;

        return new ChannelPresentation(
            IsVisible: true,
            ChannelLabel(snapshot.OutboundChannelState, language),
            OutboundRouteLabel(snapshot.OutboundRoute, language),
            ChannelSeverity(
                snapshot.OutboundChannelState,
                safetyMessage is not null),
            safetyMessage,
            bypass,
            bypass
                ? _localization.Get(LocalizedString.BypassBadge, language)
                : null);
    }

    private string ChannelLabel(
        ChannelState state,
        AppInterfaceLanguage language)
    {
        LocalizedString key = state switch
        {
            ChannelState.Inactive => LocalizedString.ChannelInactive,
            ChannelState.Connecting => LocalizedString.ChannelConnecting,
            ChannelState.Connected => LocalizedString.ChannelConnected,
            ChannelState.Reconnecting => LocalizedString.ChannelReconnecting,
            ChannelState.Bypassed => LocalizedString.ChannelBypassed,
            ChannelState.Degraded => LocalizedString.ChannelDegraded,
            ChannelState.Failed => LocalizedString.ChannelFailed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Undefined channel state."),
        };
        return _localization.Get(key, language);
    }

    private static PresentationSeverity ChannelSeverity(
        ChannelState state,
        bool hasSafetyMessage)
    {
        if (state == ChannelState.Failed)
        {
            return PresentationSeverity.Critical;
        }

        if (hasSafetyMessage)
        {
            return PresentationSeverity.Warning;
        }

        return state switch
        {
            ChannelState.Connecting => PresentationSeverity.Progress,
            ChannelState.Reconnecting or ChannelState.Degraded =>
                PresentationSeverity.Warning,
            ChannelState.Inactive
                or ChannelState.Connected
                or ChannelState.Bypassed => PresentationSeverity.Normal,
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Undefined channel state."),
        };
    }

    private string InboundRouteLabel(
        InboundRoute route,
        AppInterfaceLanguage language)
    {
        LocalizedString key = route switch
        {
            InboundRoute.Stopped => LocalizedString.RouteStopped,
            InboundRoute.Translated => LocalizedString.RouteTranslated,
            InboundRoute.OriginalFailOpen =>
                LocalizedString.RouteOriginalFailOpen,
            InboundRoute.OriginalBypass =>
                LocalizedString.RouteOriginalBypass,
            _ => throw new ArgumentOutOfRangeException(
                nameof(route),
                route,
                "Undefined inbound route."),
        };
        return _localization.Get(key, language);
    }

    private string OutboundRouteLabel(
        OutboundRoute route,
        AppInterfaceLanguage language)
    {
        LocalizedString key = route switch
        {
            OutboundRoute.Stopped => LocalizedString.RouteStopped,
            OutboundRoute.Translated => LocalizedString.RouteTranslated,
            OutboundRoute.MutedFailClosed =>
                LocalizedString.RouteMutedFailClosed,
            OutboundRoute.OriginalBypass =>
                LocalizedString.RouteOriginalBypass,
            _ => throw new ArgumentOutOfRangeException(
                nameof(route),
                route,
                "Undefined outbound route."),
        };
        return _localization.Get(key, language);
    }

    private ErrorPresentation? Error(
        RuntimeError? error,
        AppInterfaceLanguage language)
    {
        if (error is null)
        {
            return null;
        }

        LocalizedString messageKey = ErrorMessageKey(error);
        PresentationSeverity severity = error.Category switch
        {
            ErrorCategory.Network
                or ErrorCategory.Backpressure
                or ErrorCategory.CloseTimeout =>
                PresentationSeverity.Warning,
            ErrorCategory.Configuration
                or ErrorCategory.Permission
                or ErrorCategory.Driver
                or ErrorCategory.Device
                or ErrorCategory.Authentication
                or ErrorCategory.EndpointModel
                or ErrorCategory.Protocol =>
                PresentationSeverity.Critical,
            _ => throw new ArgumentOutOfRangeException(
                nameof(error),
                error.Category,
                "Undefined error category."),
        };

        return new ErrorPresentation(
            _localization.Get(messageKey, language),
            severity,
            RecoveryAction(error.RecoveryAction, language));
    }

    private static LocalizedString ErrorMessageKey(RuntimeError error)
    {
        return error.Code switch
        {
            "translationRuntime.settingsMissing" =>
                LocalizedString.ErrorSettingsMissing,
            "translationRuntime.secretMissing" =>
                LocalizedString.ErrorSecretMissing,
            "translationRuntime.driverIncompatible" =>
                LocalizedString.ErrorDriverIncompatible,
            "translationRuntime.defaultPhysicalDeviceMissing" =>
                LocalizedString.ErrorDefaultDeviceMissing,
            _ => error.Category switch
            {
                ErrorCategory.Configuration =>
                    LocalizedString.ErrorConfiguration,
                ErrorCategory.Permission => LocalizedString.ErrorPermission,
                ErrorCategory.Driver => LocalizedString.ErrorDriver,
                ErrorCategory.Device => LocalizedString.ErrorDevice,
                ErrorCategory.Authentication =>
                    LocalizedString.ErrorAuthentication,
                ErrorCategory.EndpointModel =>
                    LocalizedString.ErrorEndpointModel,
                ErrorCategory.Protocol => LocalizedString.ErrorProtocol,
                ErrorCategory.Network => LocalizedString.ErrorNetwork,
                ErrorCategory.Backpressure =>
                    LocalizedString.ErrorBackpressure,
                ErrorCategory.CloseTimeout =>
                    LocalizedString.ErrorCloseTimeout,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(error),
                    error.Category,
                    "Undefined error category."),
            },
        };
    }

    private PresentationAction RecoveryAction(
        EMKE.Core.RecoveryAction recovery,
        AppInterfaceLanguage language)
    {
        return recovery switch
        {
            EMKE.Core.RecoveryAction.None =>
                Action(
                    PresentationActionKind.Dismiss,
                    LocalizedString.ActionDismiss,
                    isVisible: true,
                    isEnabled: true,
                    language),
            EMKE.Core.RecoveryAction.EditSettings =>
                Action(
                    PresentationActionKind.EditSettings,
                    LocalizedString.ActionEditSettings,
                    isVisible: true,
                    isEnabled: true,
                    language),
            EMKE.Core.RecoveryAction.OpenPrivacySettings =>
                Action(
                    PresentationActionKind.OpenPrivacySettings,
                    LocalizedString.ActionOpenPrivacySettings,
                    isVisible: true,
                    isEnabled: true,
                    language),
            EMKE.Core.RecoveryAction.InstallDriver =>
                Action(
                    PresentationActionKind.InstallOrRepairDriver,
                    LocalizedString.ActionInstallOrRepairDriver,
                    isVisible: true,
                    isEnabled: true,
                    language),
            EMKE.Core.RecoveryAction.SelectDevice =>
                Action(
                    PresentationActionKind.SelectDevice,
                    LocalizedString.ActionSelectDevice,
                    isVisible: true,
                    isEnabled: true,
                    language),
            EMKE.Core.RecoveryAction.UpdateApiKey =>
                Action(
                    PresentationActionKind.EditApiKey,
                    LocalizedString.ActionEditApiKey,
                    isVisible: true,
                    isEnabled: true,
                    language),
            EMKE.Core.RecoveryAction.Retry =>
                Action(
                    PresentationActionKind.Retry,
                    LocalizedString.ActionRetry,
                    isVisible: true,
                    isEnabled: true,
                    language),
            EMKE.Core.RecoveryAction.ReportCompatibility =>
                Action(
                    PresentationActionKind.ReportCompatibility,
                    LocalizedString.ActionReportCompatibility,
                    isVisible: true,
                    isEnabled: true,
                    language),
            _ => throw new ArgumentOutOfRangeException(
                nameof(recovery),
                recovery,
                "Undefined recovery action."),
        };
    }

    private PresentationAction Action(
        PresentationActionKind kind,
        LocalizedString label,
        bool isVisible,
        bool isEnabled,
        AppInterfaceLanguage language)
    {
        return new PresentationAction(
            kind,
            _localization.Get(label, language),
            isVisible,
            isEnabled);
    }
}
