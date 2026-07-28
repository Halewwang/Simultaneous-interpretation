using System.Globalization;
using EMKE.Core;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Presentation;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class AppPresentationMapperTests
{
    private static readonly AppPresentationMapper Mapper =
        new(new LocalizationService(
            () => CultureInfo.GetCultureInfo("en-US")));

    [TestMethod]
    public void StoppedShowsEnabledStartAndHidesStop()
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(RuntimeState.Stopped),
            AppInterfaceLanguage.English);

        Assert.AreEqual("Translation stopped", presentation.RuntimeLabel);
        Assert.IsFalse(presentation.IsProgressVisible);
        Assert.IsTrue(presentation.StartAction.IsVisible);
        Assert.IsTrue(presentation.StartAction.IsEnabled);
        Assert.AreEqual("Start", presentation.StartAction.Label);
        Assert.IsFalse(presentation.StopAction.IsVisible);
    }

    [TestMethod]
    public void DriverMissingDisablesStart()
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Stopped,
                driverCompatibility: new DriverCompatibility(
                    isCompatible: false,
                    statusLabel: "driverMissing",
                    updateRecommended: true,
                    repairAvailable: false)),
            AppInterfaceLanguage.English);

        Assert.IsTrue(presentation.StartAction.IsVisible);
        Assert.IsFalse(presentation.StartAction.IsEnabled);
    }

    [DataRow(RuntimeState.Starting, "Translation starting", true, true)]
    [DataRow(RuntimeState.Stopping, "Translation stopping", true, false)]
    [TestMethod]
    public void TransitionalStateShowsProgressAndDisablesDuplicateCommand(
        RuntimeState state,
        string expectedLabel,
        bool expectStopVisible,
        bool expectStopEnabled)
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(state),
            AppInterfaceLanguage.English);

        Assert.AreEqual(expectedLabel, presentation.RuntimeLabel);
        Assert.IsTrue(presentation.IsProgressVisible);
        Assert.IsFalse(presentation.StartAction.IsEnabled);
        Assert.AreEqual(expectStopVisible, presentation.StopAction.IsVisible);
        Assert.AreEqual(expectStopEnabled, presentation.StopAction.IsEnabled);
    }

    [TestMethod]
    public void RunningTranslatedShowsBothConnectedChannelRoutes()
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Running,
                ChannelState.Connected,
                ChannelState.Connected,
                InboundRoute.Translated,
                OutboundRoute.Translated),
            AppInterfaceLanguage.English);

        Assert.IsTrue(presentation.InboundChannel.IsVisible);
        Assert.AreEqual(
            "Connected",
            presentation.InboundChannel.StatusLabel);
        Assert.AreEqual(
            "Translated audio",
            presentation.InboundChannel.RouteLabel);
        Assert.IsTrue(presentation.OutboundChannel.IsVisible);
        Assert.AreEqual(
            "Connected",
            presentation.OutboundChannel.StatusLabel);
        Assert.AreEqual(
            "Translated audio",
            presentation.OutboundChannel.RouteLabel);
        Assert.IsFalse(presentation.StartAction.IsVisible);
        Assert.IsTrue(presentation.StopAction.IsEnabled);
    }

    [TestMethod]
    public void DegradedInboundExplainsOriginalAudioFailOpen()
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Degraded,
                ChannelState.Degraded,
                ChannelState.Connected,
                InboundRoute.OriginalFailOpen,
                OutboundRoute.Translated),
            AppInterfaceLanguage.English);

        Assert.AreEqual(
            PresentationSeverity.Warning,
            presentation.Severity);
        Assert.AreEqual(
            "Original audio remains audible while inbound translation recovers.",
            presentation.InboundChannel.SafetyMessage);
        Assert.AreEqual(
            "Original audio fail-open",
            presentation.InboundChannel.RouteLabel);
        Assert.IsNull(presentation.OutboundChannel.SafetyMessage);
    }

    [TestMethod]
    public void DegradedOutboundWarnsThatMeetingMicrophoneIsFailClosed()
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Degraded,
                ChannelState.Connected,
                ChannelState.Degraded,
                InboundRoute.Translated,
                OutboundRoute.MutedFailClosed),
            AppInterfaceLanguage.English);

        Assert.AreEqual(
            "Your meeting microphone is muted while outbound translation recovers.",
            presentation.OutboundChannel.SafetyMessage);
        Assert.AreEqual(
            "Muted fail-closed",
            presentation.OutboundChannel.RouteLabel);
        Assert.IsNull(presentation.InboundChannel.SafetyMessage);
    }

    [TestMethod]
    public void FailedInboundRemainsCriticalWhenFailOpenMessageIsPresent()
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Degraded,
                ChannelState.Failed,
                ChannelState.Connected,
                InboundRoute.OriginalFailOpen,
                OutboundRoute.Translated),
            AppInterfaceLanguage.English);

        Assert.AreEqual(
            PresentationSeverity.Critical,
            presentation.InboundChannel.Severity);
        Assert.AreEqual(
            "Original audio remains audible while inbound translation recovers.",
            presentation.InboundChannel.SafetyMessage);
    }

    [TestMethod]
    public void FailedOutboundRemainsCriticalWhenFailClosedMessageIsPresent()
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Degraded,
                ChannelState.Connected,
                ChannelState.Failed,
                InboundRoute.Translated,
                OutboundRoute.MutedFailClosed),
            AppInterfaceLanguage.English);

        Assert.AreEqual(
            PresentationSeverity.Critical,
            presentation.OutboundChannel.Severity);
        Assert.AreEqual(
            "Your meeting microphone is muted while outbound translation recovers.",
            presentation.OutboundChannel.SafetyMessage);
    }

    [TestMethod]
    public void ExplicitBypassProducesPersistentBadgesForEachDirection()
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Running,
                ChannelState.Bypassed,
                ChannelState.Bypassed,
                InboundRoute.OriginalBypass,
                OutboundRoute.OriginalBypass),
            AppInterfaceLanguage.English);

        Assert.IsTrue(presentation.InboundChannel.IsBypassActive);
        Assert.AreEqual(
            "Bypass on",
            presentation.InboundChannel.BypassBadge);
        Assert.IsTrue(presentation.OutboundChannel.IsBypassActive);
        Assert.AreEqual(
            "Bypass on",
            presentation.OutboundChannel.BypassBadge);
    }

    [DataRow(
        ErrorCategory.Driver,
        "translationRuntime.driverIncompatible",
        RecoveryAction.InstallDriver,
        "The EMKE virtual audio driver is missing or incompatible.",
        "InstallOrRepairDriver",
        "Install or Repair Driver")]
    [DataRow(
        ErrorCategory.Permission,
        "translationRuntime.permissionDenied",
        RecoveryAction.OpenPrivacySettings,
        "Windows privacy settings are blocking microphone access.",
        "OpenPrivacySettings",
        "Open Privacy Settings")]
    [DataRow(
        ErrorCategory.Authentication,
        "translationRuntime.secretMissing",
        RecoveryAction.UpdateApiKey,
        "Add an API key before starting translation.",
        "EditApiKey",
        "Edit API Key")]
    [TestMethod]
    public void StableRuntimeErrorsMapToLocalizedMessageAndRecoveryAction(
        ErrorCategory category,
        string code,
        RecoveryAction recoveryAction,
        string expectedMessage,
        string expectedAction,
        string expectedActionLabel)
    {
        RuntimeError error = new(
            category,
            code,
            new Dictionary<string, string>(),
            recoveryAction);
        AppPresentation presentation = Mapper.Map(
            Snapshot(RuntimeState.Failed, error: error),
            AppInterfaceLanguage.English);

        Assert.IsNotNull(presentation.Error);
        Assert.AreEqual(expectedMessage, presentation.Error.Message);
        Assert.AreEqual(
            expectedAction,
            presentation.Error.Action.Kind.ToString());
        Assert.AreEqual(
            expectedActionLabel,
            presentation.Error.Action.Label);
        Assert.IsFalse(
            presentation.Error.Message.Contains(
                code,
                StringComparison.Ordinal));
    }

    [DataRow(RuntimeState.Stopped, "Translation stopped")]
    [DataRow(RuntimeState.Starting, "Translation starting")]
    [DataRow(RuntimeState.Running, "Translation running")]
    [DataRow(RuntimeState.Stopping, "Translation stopping")]
    [DataRow(RuntimeState.Degraded, "Translation degraded")]
    [DataRow(RuntimeState.Failed, "Translation failed")]
    [TestMethod]
    public void EveryRuntimeStateHasAnExactLocalizedLabel(
        RuntimeState state,
        string expectedLabel)
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(state),
            AppInterfaceLanguage.English);

        Assert.AreEqual(expectedLabel, presentation.RuntimeLabel);
    }

    [DataRow(ChannelState.Inactive, "Inactive")]
    [DataRow(ChannelState.Connecting, "Connecting")]
    [DataRow(ChannelState.Connected, "Connected")]
    [DataRow(ChannelState.Reconnecting, "Reconnecting")]
    [DataRow(ChannelState.Bypassed, "Bypassed")]
    [DataRow(ChannelState.Degraded, "Degraded")]
    [DataRow(ChannelState.Failed, "Failed")]
    [TestMethod]
    public void EveryChannelStateHasAnExactLocalizedLabel(
        ChannelState state,
        string expectedLabel)
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Running,
                state,
                state,
                InboundRoute.Translated,
                OutboundRoute.Translated),
            AppInterfaceLanguage.English);

        Assert.AreEqual(
            expectedLabel,
            presentation.InboundChannel.StatusLabel);
        Assert.AreEqual(
            expectedLabel,
            presentation.OutboundChannel.StatusLabel);
    }

    [DataRow(InboundRoute.Stopped, "Stopped")]
    [DataRow(InboundRoute.Translated, "Translated audio")]
    [DataRow(InboundRoute.OriginalFailOpen, "Original audio fail-open")]
    [DataRow(InboundRoute.OriginalBypass, "Original audio bypass")]
    [TestMethod]
    public void EveryInboundRouteHasAnExactLocalizedLabel(
        InboundRoute route,
        string expectedLabel)
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Running,
                ChannelState.Connected,
                ChannelState.Connected,
                route,
                OutboundRoute.Translated),
            AppInterfaceLanguage.English);

        Assert.AreEqual(
            expectedLabel,
            presentation.InboundChannel.RouteLabel);
    }

    [DataRow(OutboundRoute.Stopped, "Stopped")]
    [DataRow(OutboundRoute.Translated, "Translated audio")]
    [DataRow(OutboundRoute.MutedFailClosed, "Muted fail-closed")]
    [DataRow(OutboundRoute.OriginalBypass, "Original audio bypass")]
    [TestMethod]
    public void EveryOutboundRouteHasAnExactLocalizedLabel(
        OutboundRoute route,
        string expectedLabel)
    {
        AppPresentation presentation = Mapper.Map(
            Snapshot(
                RuntimeState.Running,
                ChannelState.Connected,
                ChannelState.Connected,
                InboundRoute.Translated,
                route),
            AppInterfaceLanguage.English);

        Assert.AreEqual(
            expectedLabel,
            presentation.OutboundChannel.RouteLabel);
    }

    [DataRow(ErrorCategory.Configuration, "Translation settings need attention.")]
    [DataRow(ErrorCategory.Permission, "Windows privacy settings are blocking microphone access.")]
    [DataRow(ErrorCategory.Driver, "The EMKE virtual audio driver needs attention.")]
    [DataRow(ErrorCategory.Device, "An audio device is unavailable.")]
    [DataRow(ErrorCategory.Authentication, "Authentication failed. Check the API key.")]
    [DataRow(ErrorCategory.EndpointModel, "The Translation endpoint or model is incompatible.")]
    [DataRow(ErrorCategory.Protocol, "The Translation service returned an incompatible response.")]
    [DataRow(ErrorCategory.Network, "The Translation service connection was interrupted.")]
    [DataRow(ErrorCategory.Backpressure, "Audio processing could not keep up safely.")]
    [DataRow(ErrorCategory.CloseTimeout, "Translation did not close within the safe deadline.")]
    [TestMethod]
    public void UnknownStableErrorCodeFallsBackToItsLocalizedCategory(
        ErrorCategory category,
        string expectedMessage)
    {
        RuntimeError error = new(
            category,
            "future.stableCode",
            new Dictionary<string, string>
            {
                ["detail"] = "not-for-display",
            },
            RecoveryAction.Retry);
        AppPresentation presentation = Mapper.Map(
            Snapshot(RuntimeState.Failed, error: error),
            AppInterfaceLanguage.English);

        Assert.IsNotNull(presentation.Error);
        Assert.AreEqual(expectedMessage, presentation.Error.Message);
        Assert.IsFalse(
            presentation.Error.Message.Contains(
                "future.stableCode",
                StringComparison.Ordinal));
        Assert.IsFalse(
            presentation.Error.Message.Contains(
                "not-for-display",
                StringComparison.Ordinal));
    }

    [DataRow(RecoveryAction.None, "Dismiss", "Dismiss")]
    [DataRow(RecoveryAction.EditSettings, "EditSettings", "Edit Settings")]
    [DataRow(RecoveryAction.OpenPrivacySettings, "OpenPrivacySettings", "Open Privacy Settings")]
    [DataRow(RecoveryAction.InstallDriver, "InstallOrRepairDriver", "Install or Repair Driver")]
    [DataRow(RecoveryAction.SelectDevice, "SelectDevice", "Select Audio Device")]
    [DataRow(RecoveryAction.UpdateApiKey, "EditApiKey", "Edit API Key")]
    [DataRow(RecoveryAction.Retry, "Retry", "Retry")]
    [DataRow(RecoveryAction.ReportCompatibility, "ReportCompatibility", "View Compatibility Report")]
    [TestMethod]
    public void EveryRecoveryActionProducesAVisibleLocalizedUserAction(
        RecoveryAction recoveryAction,
        string expectedKind,
        string expectedLabel)
    {
        RuntimeError error = new(
            ErrorCategory.Protocol,
            "future.stableCode",
            new Dictionary<string, string>(),
            recoveryAction);
        AppPresentation presentation = Mapper.Map(
            Snapshot(RuntimeState.Failed, error: error),
            AppInterfaceLanguage.English);

        Assert.IsNotNull(presentation.Error);
        Assert.IsTrue(presentation.Error.Action.IsVisible);
        Assert.IsTrue(presentation.Error.Action.IsEnabled);
        Assert.AreEqual(
            expectedKind,
            presentation.Error.Action.Kind.ToString());
        Assert.AreEqual(expectedLabel, presentation.Error.Action.Label);
    }

    [TestMethod]
    public void LanguageChangeRemapsSameSnapshotWithoutChangingRuntimeData()
    {
        RuntimeError error = new(
            ErrorCategory.Driver,
            "translationRuntime.driverIncompatible",
            new Dictionary<string, string>(),
            RecoveryAction.InstallDriver);
        AppSnapshot snapshot = Snapshot(
            RuntimeState.Degraded,
            ChannelState.Degraded,
            ChannelState.Connected,
            InboundRoute.OriginalFailOpen,
            OutboundRoute.Translated,
            error);

        AppPresentation english = Mapper.Map(
            snapshot,
            AppInterfaceLanguage.English);
        AppPresentation simplifiedChinese = Mapper.Map(
            snapshot,
            AppInterfaceLanguage.ZhHans);

        Assert.AreEqual(99UL, english.SnapshotVersion);
        Assert.AreEqual(99UL, simplifiedChinese.SnapshotVersion);
        Assert.AreEqual("Translation degraded", english.RuntimeLabel);
        Assert.AreEqual("翻译服务降级", simplifiedChinese.RuntimeLabel);
        Assert.AreEqual(
            "The EMKE virtual audio driver is missing or incompatible.",
            english.Error?.Message);
        Assert.AreEqual(
            "EMKE 虚拟音频驱动缺失或不兼容。",
            simplifiedChinese.Error?.Message);
        Assert.AreEqual(0.25, simplifiedChinese.InboundLevel);
        Assert.AreEqual(0.75, simplifiedChinese.OutboundLevel);
        Assert.AreEqual("source", simplifiedChinese.SourceCaption);
        Assert.AreEqual("translated", simplifiedChinese.TranslatedCaption);
        Assert.AreSame(error, snapshot.Error);
    }

    private static AppSnapshot Snapshot(
        RuntimeState runtimeState,
        ChannelState inboundChannelState = ChannelState.Inactive,
        ChannelState outboundChannelState = ChannelState.Inactive,
        InboundRoute inboundRoute = InboundRoute.Stopped,
        OutboundRoute outboundRoute = OutboundRoute.Stopped,
        RuntimeError? error = null,
        DriverCompatibility? driverCompatibility = null)
    {
        return new AppSnapshot(
            contractVersion: 1,
            version: 99,
            runtimeState,
            inboundChannelState,
            outboundChannelState,
            inboundRoute,
            outboundRoute,
            inboundLevel: 0.25,
            outboundLevel: 0.75,
            sourceCaption: "source",
            translatedCaption: "translated",
            new AudioSelection("input", "output"),
            driverCompatibility ?? new DriverCompatibility(true, "compatible"),
            connectionReport: null,
            new AudioDiagnostics(true, 0),
            new UpdateAvailability(false, string.Empty),
            error);
    }
}
