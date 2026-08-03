namespace EMKE.Windows.App.Presentation;

internal enum PresentationSeverity
{
    Normal,
    Progress,
    Warning,
    Critical,
}

internal enum PresentationActionKind
{
    Start,
    Stop,
    Dismiss,
    EditSettings,
    OpenPrivacySettings,
    InstallOrRepairDriver,
    SelectDevice,
    EditApiKey,
    Retry,
    ReportCompatibility,
}

internal sealed record PresentationAction(
    PresentationActionKind Kind,
    string Label,
    bool IsVisible,
    bool IsEnabled);

internal sealed record ChannelPresentation(
    bool IsVisible,
    string StatusLabel,
    string RouteLabel,
    PresentationSeverity Severity,
    string? SafetyMessage,
    bool IsBypassActive,
    string? BypassBadge);

internal sealed record ErrorPresentation(
    string Message,
    PresentationSeverity Severity,
    PresentationAction Action);

internal sealed record AppPresentation(
    ulong SnapshotVersion,
    string RuntimeLabel,
    PresentationSeverity Severity,
    bool IsProgressVisible,
    PresentationAction StartAction,
    PresentationAction StopAction,
    ChannelPresentation InboundChannel,
    ChannelPresentation OutboundChannel,
    double InboundLevel,
    double OutboundLevel,
    string SourceCaption,
    string TranslatedCaption,
    ErrorPresentation? Error);
