using System.Runtime.InteropServices;
using EMKE.Setup.Platform;

namespace EMKE.Setup;

internal sealed record SetupPreflightDecision(
    bool Allowed,
    string? FailureCode)
{
    public SetupPreflightDecision
    {
        if (Allowed && FailureCode is not null)
        {
            throw new ArgumentException(
                "An allowed preflight decision cannot have a failure code.",
                nameof(FailureCode));
        }
        if (!Allowed && string.IsNullOrWhiteSpace(FailureCode))
        {
            throw new ArgumentException(
                "A rejected preflight decision requires a failure code.",
                nameof(FailureCode));
        }
    }

    public static SetupPreflightDecision Admitted { get; } = new(true, null);

    public static SetupPreflightDecision Rejected(string failureCode) =>
        new(false, failureCode);
}

internal sealed class SetupPreflight
{
    private readonly ISetupHostProbe _hostProbe;

    public SetupPreflight(ISetupHostProbe hostProbe)
    {
        _hostProbe = hostProbe ?? throw new ArgumentNullException(nameof(hostProbe));
    }

    public SetupPreflightDecision Evaluate(SetupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        SetupHostInfo host = _hostProbe.Read();
        if (host.WindowsBuild < manifest.MinimumWindowsBuild)
        {
            return SetupPreflightDecision.Rejected("windowsBuildUnsupported");
        }
        if (host.Architecture != Architecture.X64)
        {
            return SetupPreflightDecision.Rejected("architectureUnsupported");
        }
        if (host.IsServer)
        {
            return SetupPreflightDecision.Rejected("windowsServerUnsupported");
        }

        return SetupPreflightDecision.Admitted;
    }
}
