namespace EMKE.Core;

#pragma warning disable CA1034 // Nesting intentionally prevents commands outside this closed hierarchy.
#pragma warning disable CA1716 // Stable command names are part of the runtime domain vocabulary.

public abstract record RuntimeCommand
{
    private RuntimeCommand()
    {
    }

    public sealed record Start : RuntimeCommand;

    public sealed record Stop : RuntimeCommand;

    public sealed record Exit : RuntimeCommand;

    public sealed record SetInboundBypass(bool Enabled) : RuntimeCommand;

    public sealed record SetOutboundBypass(bool Enabled) : RuntimeCommand;

    public sealed record RefreshDevices : RuntimeCommand;

    public sealed record CheckForUpdates : RuntimeCommand;
}

#pragma warning restore CA1716
#pragma warning restore CA1034
