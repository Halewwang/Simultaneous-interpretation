using System.Runtime.InteropServices;
using EMKE.Setup.Platform;

namespace EMKE.Setup;

internal sealed class SetupPreflightDecision
{
    public SetupPreflightDecision(bool allowed, string? failureCode)
    {
        if (allowed && failureCode is not null)
        {
            throw new ArgumentException(
                "An allowed preflight decision cannot have a failure code.",
                nameof(failureCode));
        }
        if (!allowed && string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException(
                "A rejected preflight decision requires a failure code.",
                nameof(failureCode));
        }

        Allowed = allowed;
        FailureCode = failureCode;
    }

    public bool Allowed { get; }

    public string? FailureCode { get; }

    public static SetupPreflightDecision Admitted { get; } = new(true, null);

    public static SetupPreflightDecision Rejected(string failureCode) =>
        new(false, failureCode);
}

internal sealed class SetupPreflight
{
    private readonly ISetupHostProbe _hostProbe;

    public SetupPreflight()
        : this(WindowsSetupHostProbe.Instance)
    {
    }

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
