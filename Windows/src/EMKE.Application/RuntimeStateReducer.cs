using EMKE.Core;
using EMKE.Routing;

namespace EMKE.Application;

internal sealed class RuntimeStateReducer
{
    private long _generation;

    public RuntimeStateReducer()
    {
        Current = CreateInitialSnapshot();
    }

    public AppSnapshot Current { get; private set; }

    public long Generation => _generation;

    public static AppSnapshot CreateInitialSnapshot()
    {
        return new AppSnapshot(
            contractVersion: 1,
            version: 0,
            RuntimeState.Stopped,
            ChannelState.Inactive,
            ChannelState.Inactive,
            InboundRoute.Stopped,
            OutboundRoute.Stopped,
            inboundLevel: 0,
            outboundLevel: 0,
            sourceCaption: string.Empty,
            translatedCaption: string.Empty,
            new AudioSelection(string.Empty, string.Empty),
            new DriverCompatibility(false, string.Empty),
            connectionReport: null,
            new AudioDiagnostics(true, 0),
            new UpdateAvailability(false, string.Empty),
            error: null);
    }

    public long BeginStart()
    {
        _generation = checked(_generation + 1);
        Current = Next(
            RuntimeState.Starting,
            ChannelState.Connecting,
            ChannelState.Connecting,
            InboundRoute.Stopped,
            OutboundRoute.Stopped,
            Current.InboundLevel,
            Current.OutboundLevel,
            Current.SourceCaption,
            Current.TranslatedCaption,
            Current.AudioSelection,
            Current.DriverCompatibility,
            error: null);
        return _generation;
    }

    public long BeginStop()
    {
        _generation = checked(_generation + 1);
        Current = Next(
            RuntimeState.Stopping,
            Current.InboundChannelState,
            Current.OutboundChannelState,
            Current.InboundRoute,
            Current.OutboundRoute,
            Current.InboundLevel,
            Current.OutboundLevel,
            Current.SourceCaption,
            Current.TranslatedCaption,
            Current.AudioSelection,
            Current.DriverCompatibility,
            Current.Error);
        return _generation;
    }

    public AppSnapshot CompleteStop(
        long generation,
        RuntimeError? error = null)
    {
        if (generation != _generation)
        {
            return Current;
        }

        Current = Next(
            RuntimeState.Stopped,
            ChannelState.Inactive,
            ChannelState.Inactive,
            InboundRoute.Stopped,
            OutboundRoute.Stopped,
            inboundLevel: 0,
            outboundLevel: 0,
            sourceCaption: string.Empty,
            translatedCaption: string.Empty,
            Current.AudioSelection,
            Current.DriverCompatibility,
            error,
            audioDiagnostics: new AudioDiagnostics(true, 0));
        return Current;
    }

    public AppSnapshot FailStart(
        long generation,
        RuntimeError error,
        DriverCompatibility? driverCompatibility = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (generation != _generation)
        {
            return Current;
        }

        Current = Next(
            RuntimeState.Failed,
            ChannelState.Inactive,
            ChannelState.Inactive,
            InboundRoute.Stopped,
            OutboundRoute.Stopped,
            inboundLevel: 0,
            outboundLevel: 0,
            sourceCaption: string.Empty,
            translatedCaption: string.Empty,
            Current.AudioSelection,
            driverCompatibility ?? Current.DriverCompatibility,
            error);
        return Current;
    }

    public AppSnapshot SetStartupEnvironment(
        long generation,
        AudioSelection audioSelection,
        DriverCompatibility driverCompatibility)
    {
        ArgumentNullException.ThrowIfNull(audioSelection);
        ArgumentNullException.ThrowIfNull(driverCompatibility);
        if (generation != _generation)
        {
            return Current;
        }

        Current = Next(
            Current.RuntimeState,
            Current.InboundChannelState,
            Current.OutboundChannelState,
            Current.InboundRoute,
            Current.OutboundRoute,
            Current.InboundLevel,
            Current.OutboundLevel,
            Current.SourceCaption,
            Current.TranslatedCaption,
            audioSelection,
            driverCompatibility,
            Current.Error);
        return Current;
    }

    public AppSnapshot ApplyRouting(
        long generation,
        RoutingPolicySnapshot routing,
        RuntimeError? error)
    {
        ArgumentNullException.ThrowIfNull(routing);
        if (generation != _generation)
        {
            return Current;
        }

        RuntimeState state = Current.RuntimeState;
        if (state is RuntimeState.Running or RuntimeState.Degraded)
        {
            state = error is null
                && routing.InboundChannelState == ChannelState.Connected
                && routing.OutboundChannelState is (
                    ChannelState.Connected or ChannelState.Bypassed)
                ? RuntimeState.Running
                : RuntimeState.Degraded;
        }

        Current = Next(
            state,
            routing.InboundChannelState,
            routing.OutboundChannelState,
            routing.InboundRoute,
            routing.OutboundRoute,
            Current.InboundLevel,
            Current.OutboundLevel,
            Current.SourceCaption,
            Current.TranslatedCaption,
            Current.AudioSelection,
            Current.DriverCompatibility,
            error);
        return Current;
    }

    public AppSnapshot UpdateAudioSelection(
        long generation,
        AudioSelection audioSelection)
    {
        ArgumentNullException.ThrowIfNull(audioSelection);
        if (generation != _generation)
        {
            return Current;
        }

        Current = Next(
            Current.RuntimeState,
            Current.InboundChannelState,
            Current.OutboundChannelState,
            Current.InboundRoute,
            Current.OutboundRoute,
            Current.InboundLevel,
            Current.OutboundLevel,
            Current.SourceCaption,
            Current.TranslatedCaption,
            audioSelection,
            Current.DriverCompatibility,
            Current.Error);
        return Current;
    }

    public AppSnapshot UpdateAudioDiagnostics(
        long generation,
        ulong droppedFrameCount)
    {
        if (generation != _generation)
        {
            return Current;
        }

        Current = Current.Next(
            Current.RuntimeState,
            Current.InboundChannelState,
            Current.OutboundChannelState,
            Current.InboundRoute,
            Current.OutboundRoute,
            Current.InboundLevel,
            Current.OutboundLevel,
            Current.SourceCaption,
            Current.TranslatedCaption,
            Current.AudioSelection,
            Current.DriverCompatibility,
            Current.ConnectionReport,
            new AudioDiagnostics(
                droppedFrameCount == 0,
                droppedFrameCount),
            Current.UpdateAvailability,
            Current.Error);
        return Current;
    }

    public bool TryCompleteStart(
        long generation,
        RoutingPolicySnapshot routing,
        RuntimeError? error,
        out AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(routing);
        if (generation != _generation
            || Current.RuntimeState != RuntimeState.Starting)
        {
            snapshot = Current;
            return false;
        }

        RuntimeState state = error is null
            && routing.InboundChannelState == ChannelState.Connected
            && routing.OutboundChannelState is (
                ChannelState.Connected or ChannelState.Bypassed)
            ? RuntimeState.Running
            : RuntimeState.Degraded;
        Current = Next(
            state,
            routing.InboundChannelState,
            routing.OutboundChannelState,
            routing.InboundRoute,
            routing.OutboundRoute,
            Current.InboundLevel,
            Current.OutboundLevel,
            Current.SourceCaption,
            Current.TranslatedCaption,
            Current.AudioSelection,
            Current.DriverCompatibility,
            error);
        snapshot = Current;
        return true;
    }

    public AppSnapshot UpdateCaptions(
        long generation,
        string sourceCaption,
        string translatedCaption)
    {
        ArgumentNullException.ThrowIfNull(sourceCaption);
        ArgumentNullException.ThrowIfNull(translatedCaption);
        if (generation != _generation)
        {
            return Current;
        }

        Current = Next(
            Current.RuntimeState,
            Current.InboundChannelState,
            Current.OutboundChannelState,
            Current.InboundRoute,
            Current.OutboundRoute,
            Current.InboundLevel,
            Current.OutboundLevel,
            sourceCaption,
            translatedCaption,
            Current.AudioSelection,
            Current.DriverCompatibility,
            Current.Error);
        return Current;
    }

    public AppSnapshot UpdateLevels(
        long generation,
        double inboundLevel,
        double outboundLevel)
    {
        if (generation != _generation)
        {
            return Current;
        }

        Current = Next(
            Current.RuntimeState,
            Current.InboundChannelState,
            Current.OutboundChannelState,
            Current.InboundRoute,
            Current.OutboundRoute,
            inboundLevel,
            outboundLevel,
            Current.SourceCaption,
            Current.TranslatedCaption,
            Current.AudioSelection,
            Current.DriverCompatibility,
            Current.Error);
        return Current;
    }

    private AppSnapshot Next(
        RuntimeState runtimeState,
        ChannelState inboundChannelState,
        ChannelState outboundChannelState,
        InboundRoute inboundRoute,
        OutboundRoute outboundRoute,
        double inboundLevel,
        double outboundLevel,
        string sourceCaption,
        string translatedCaption,
        AudioSelection audioSelection,
        DriverCompatibility driverCompatibility,
        RuntimeError? error,
        AudioDiagnostics? audioDiagnostics = null)
    {
        return Current.Next(
            runtimeState,
            inboundChannelState,
            outboundChannelState,
            inboundRoute,
            outboundRoute,
            inboundLevel,
            outboundLevel,
            sourceCaption,
            translatedCaption,
            audioSelection,
            driverCompatibility,
            Current.ConnectionReport,
            audioDiagnostics ?? Current.AudioDiagnostics,
            Current.UpdateAvailability,
            error);
    }
}
