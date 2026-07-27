using EMKE.Core;

namespace EMKE.Routing;

public sealed record RoutingPolicySnapshot(
    ChannelState InboundChannelState,
    ChannelState OutboundChannelState,
    InboundRoute InboundRoute,
    OutboundRoute OutboundRoute,
    ErrorCategory? ErrorCategory,
    bool OutputsZeros,
    bool PhysicalMicrophoneAllowed,
    bool BypassPersisted);

public sealed class RoutingPolicy
{
    private bool _outboundBypassRequested;
    private bool _inboundRecoveryPending;

    public RoutingPolicySnapshot Snapshot { get; private set; } = new(
        ChannelState.Inactive,
        ChannelState.Inactive,
        InboundRoute.Stopped,
        OutboundRoute.Stopped,
        ErrorCategory: null,
        OutputsZeros: false,
        PhysicalMicrophoneAllowed: false,
        BypassPersisted: false);

    public RoutingPolicySnapshot Start(bool outboundLocalBypass)
    {
        _outboundBypassRequested = outboundLocalBypass;
        _inboundRecoveryPending = false;
        Snapshot = new(
            ChannelState.Connected,
            outboundLocalBypass
                ? ChannelState.Bypassed
                : ChannelState.Connected,
            InboundRoute.Translated,
            outboundLocalBypass
                ? OutboundRoute.OriginalBypass
                : OutboundRoute.Translated,
            ErrorCategory: null,
            OutputsZeros: false,
            PhysicalMicrophoneAllowed: outboundLocalBypass,
            BypassPersisted: false);
        return Snapshot;
    }

    public RoutingPolicySnapshot FailInbound(ErrorCategory category)
    {
        ValidateCategory(category);
        _inboundRecoveryPending = false;
        Snapshot = Snapshot with
        {
            InboundChannelState = ChannelState.Failed,
            InboundRoute = InboundRoute.OriginalFailOpen,
            ErrorCategory = category,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot RecoverInbound()
    {
        _inboundRecoveryPending = true;
        Snapshot = Snapshot with
        {
            InboundChannelState = ChannelState.Connected,
            ErrorCategory = null,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot CompleteInboundUtterance()
    {
        if (_inboundRecoveryPending)
        {
            _inboundRecoveryPending = false;
            Snapshot = Snapshot with
            {
                InboundRoute = InboundRoute.Translated,
            };
        }

        return Snapshot;
    }

    public RoutingPolicySnapshot FailOutbound(ErrorCategory category)
    {
        ValidateCategory(category);
        Snapshot = Snapshot with
        {
            OutboundChannelState = ChannelState.Failed,
            OutboundRoute = OutboundRoute.MutedFailClosed,
            ErrorCategory = category,
            OutputsZeros = true,
            PhysicalMicrophoneAllowed = false,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot HandleOutboundUnderrun()
    {
        Snapshot = Snapshot with
        {
            OutboundChannelState = ChannelState.Degraded,
            OutboundRoute = OutboundRoute.MutedFailClosed,
            ErrorCategory = ErrorCategory.Backpressure,
            OutputsZeros = true,
            PhysicalMicrophoneAllowed = false,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot EnableOutboundBypass()
    {
        _outboundBypassRequested = true;
        Snapshot = Snapshot with
        {
            OutboundChannelState = ChannelState.Bypassed,
            OutboundRoute = OutboundRoute.OriginalBypass,
            ErrorCategory = null,
            OutputsZeros = false,
            PhysicalMicrophoneAllowed = true,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot DisconnectOutbound()
    {
        Snapshot = _outboundBypassRequested
            ? Snapshot with
            {
                OutboundChannelState = ChannelState.Bypassed,
                OutboundRoute = OutboundRoute.OriginalBypass,
                BypassPersisted = true,
            }
            : Snapshot with
            {
                OutboundChannelState = ChannelState.Reconnecting,
                OutboundRoute = OutboundRoute.MutedFailClosed,
                OutputsZeros = true,
                PhysicalMicrophoneAllowed = false,
            };
        return Snapshot;
    }

    public RoutingPolicySnapshot ReconnectOutbound()
    {
        Snapshot = _outboundBypassRequested
            ? Snapshot with
            {
                OutboundChannelState = ChannelState.Bypassed,
                OutboundRoute = OutboundRoute.OriginalBypass,
                ErrorCategory = null,
                OutputsZeros = false,
                PhysicalMicrophoneAllowed = true,
                BypassPersisted = true,
            }
            : Snapshot with
            {
                OutboundChannelState = ChannelState.Connected,
                OutboundRoute = OutboundRoute.Translated,
                ErrorCategory = null,
                OutputsZeros = false,
                PhysicalMicrophoneAllowed = false,
            };
        return Snapshot;
    }

    public RoutingPolicySnapshot Stop()
    {
        _outboundBypassRequested = false;
        _inboundRecoveryPending = false;
        Snapshot = new(
            ChannelState.Inactive,
            ChannelState.Inactive,
            InboundRoute.Stopped,
            OutboundRoute.Stopped,
            ErrorCategory: null,
            OutputsZeros: false,
            PhysicalMicrophoneAllowed: false,
            BypassPersisted: false);
        return Snapshot;
    }

    private static void ValidateCategory(ErrorCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }
    }
}
