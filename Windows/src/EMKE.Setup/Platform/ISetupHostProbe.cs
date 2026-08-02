using System.Runtime.InteropServices;

namespace EMKE.Setup.Platform;

internal sealed class SetupHostInfo
{
    public SetupHostInfo(int windowsBuild, Architecture architecture, bool isServer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowsBuild);
        if (!Enum.IsDefined(architecture))
        {
            throw new ArgumentOutOfRangeException(nameof(architecture));
        }

        WindowsBuild = windowsBuild;
        Architecture = architecture;
        IsServer = isServer;
    }

    public int WindowsBuild { get; }

    public Architecture Architecture { get; }

    public bool IsServer { get; }
}

internal interface ISetupHostProbe
{
    SetupHostInfo Read();
}

internal sealed partial class WindowsSetupHostProbe : ISetupHostProbe
{
    private const byte VerNtWorkstation = 1;

    public static WindowsSetupHostProbe Instance { get; } = new();

    private WindowsSetupHostProbe()
    {
    }

    public SetupHostInfo Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Setup host probing is available only on Windows.");
        }

        OsVersionInfo version = new()
        {
            Size = (uint)Marshal.SizeOf<OsVersionInfo>(),
        };
        int status = RtlGetVersion(ref version);
        if (status != 0)
        {
            throw new InvalidOperationException("Windows host probing failed.");
        }

        return new SetupHostInfo(
            checked((int)version.BuildNumber),
            RuntimeInformation.OSArchitecture,
            version.ProductType != VerNtWorkstation);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public uint Size;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int RtlGetVersion(ref OsVersionInfo versionInformation);
}
