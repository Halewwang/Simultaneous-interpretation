using System.Runtime.InteropServices;

namespace EMKE.Setup.Platform;

internal sealed record SetupHostInfo(
    int WindowsBuild,
    Architecture Architecture,
    bool IsServer)
{
    public SetupHostInfo
    {
        ArgumentOutOfRangeException.ThrowIfNegative(WindowsBuild);
        if (!Enum.IsDefined(Architecture))
        {
            throw new ArgumentOutOfRangeException(nameof(Architecture));
        }
    }
}

internal interface ISetupHostProbe
{
    SetupHostInfo Read();
}
