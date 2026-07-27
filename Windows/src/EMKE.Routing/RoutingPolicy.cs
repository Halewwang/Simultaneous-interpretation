using EMKE.Core;

namespace EMKE.Routing;

public sealed record RoutingPolicySnapshot(
    ChannelState InboundChannelState,
    ChannelState OutboundChannelState,
    InboundRoute InboundRoute,
    OutboundRoute OutboundRoute,
    ErrorCategory? InboundErrorCategory,
    ErrorCategory? OutboundErrorCategory,
    bool OutputsZeros,
    bool PhysicalMicrophoneAllowed,
    bool BypassPersisted,
    long Generation)
{
    public ErrorCategory? ErrorCategory =>
        OutboundErrorCategory ?? InboundErrorCategory;
}

public sealed class RoutingPolicy
{
    private bool _outboundBypassRequested;
    private bool _outboundReconnectPending;
    private bool _inboundRecoveryPending;
    private long _generation;

    public RoutingPolicySnapshot Snapshot { get; private set; } = new(
        ChannelState.Inactive,
        ChannelState.Inactive,
        InboundRoute.Stopped,
        OutboundRoute.Stopped,
        InboundErrorCategory: null,
        OutboundErrorCategory: null,
        OutputsZeros: false,
        PhysicalMicrophoneAllowed: false,
        BypassPersisted: false,
        Generation: 0);

    public RoutingPolicySnapshot Start(bool outboundLocalBypass)
    {
        long nextGeneration = checked(_generation + 1);
        _generation = nextGeneration;
        _outboundBypassRequested = outboundLocalBypass;
        _outboundReconnectPending = false;
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
            InboundErrorCategory: null,
            OutboundErrorCategory: null,
            OutputsZeros: false,
            PhysicalMicrophoneAllowed: outboundLocalBypass,
            BypassPersisted: false,
            Generation: nextGeneration);
        return Snapshot;
    }

    public RoutingPolicySnapshot FailInbound(ErrorCategory category)
    {
        ValidateCategory(category);
        if (Snapshot.InboundChannelState == ChannelState.Inactive)
        {
            return Snapshot;
        }

        _inboundRecoveryPending = false;
        Snapshot = Snapshot with
        {
            InboundChannelState = ChannelState.Failed,
            InboundRoute = InboundRoute.OriginalFailOpen,
            InboundErrorCategory = category,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot RecoverInbound()
    {
        return RecoverInbound(_generation);
    }

    public RoutingPolicySnapshot RecoverInbound(long generation)
    {
        if (generation != _generation
            || Snapshot.InboundChannelState != ChannelState.Failed
            || Snapshot.InboundRoute != InboundRoute.OriginalFailOpen)
        {
            return Snapshot;
        }

        _inboundRecoveryPending = true;
        Snapshot = Snapshot with
        {
            InboundChannelState = ChannelState.Connected,
            InboundErrorCategory = null,
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
        if (Snapshot.OutboundChannelState == ChannelState.Inactive)
        {
            return Snapshot;
        }

        _outboundReconnectPending = false;
        Snapshot = Snapshot with
        {
            OutboundChannelState = ChannelState.Failed,
            OutboundRoute = OutboundRoute.MutedFailClosed,
            OutboundErrorCategory = category,
            OutputsZeros = true,
            PhysicalMicrophoneAllowed = false,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot HandleOutboundUnderrun()
    {
        if (Snapshot.OutboundChannelState is ChannelState.Inactive
            or ChannelState.Bypassed)
        {
            return Snapshot;
        }

        _outboundReconnectPending = false;
        Snapshot = Snapshot with
        {
            OutboundChannelState = ChannelState.Degraded,
            OutboundRoute = OutboundRoute.MutedFailClosed,
            OutboundErrorCategory = ErrorCategory.Backpressure,
            OutputsZeros = true,
            PhysicalMicrophoneAllowed = false,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot EnableOutboundBypass()
    {
        if (Snapshot.OutboundChannelState == ChannelState.Inactive)
        {
            return Snapshot;
        }

        _outboundBypassRequested = true;
        _outboundReconnectPending = false;
        Snapshot = Snapshot with
        {
            OutboundChannelState = ChannelState.Bypassed,
            OutboundRoute = OutboundRoute.OriginalBypass,
            OutboundErrorCategory = null,
            OutputsZeros = false,
            PhysicalMicrophoneAllowed = true,
        };
        return Snapshot;
    }

    public RoutingPolicySnapshot DisconnectOutbound()
    {
        if (Snapshot.OutboundChannelState is not (
            ChannelState.Connected or ChannelState.Bypassed))
        {
            return Snapshot;
        }

        _outboundReconnectPending = true;
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
        return ReconnectOutbound(_generation);
    }

    public RoutingPolicySnapshot ReconnectOutbound(long generation)
    {
        if (generation != _generation || !_outboundReconnectPending)
        {
            return Snapshot;
        }

        _outboundReconnectPending = false;
        Snapshot = _outboundBypassRequested
            ? Snapshot with
            {
                OutboundChannelState = ChannelState.Bypassed,
                OutboundRoute = OutboundRoute.OriginalBypass,
                OutboundErrorCategory = null,
                OutputsZeros = false,
                PhysicalMicrophoneAllowed = true,
                BypassPersisted = true,
            }
            : Snapshot with
            {
                OutboundChannelState = ChannelState.Connected,
                OutboundRoute = OutboundRoute.Translated,
                OutboundErrorCategory = null,
                OutputsZeros = false,
                PhysicalMicrophoneAllowed = false,
            };
        return Snapshot;
    }

    public RoutingPolicySnapshot Stop()
    {
        _outboundBypassRequested = false;
        _outboundReconnectPending = false;
        _inboundRecoveryPending = false;
        Snapshot = new(
            ChannelState.Inactive,
            ChannelState.Inactive,
            InboundRoute.Stopped,
            OutboundRoute.Stopped,
            InboundErrorCategory: null,
            OutboundErrorCategory: null,
            OutputsZeros: false,
            PhysicalMicrophoneAllowed: false,
            BypassPersisted: false,
            Generation: _generation);
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
