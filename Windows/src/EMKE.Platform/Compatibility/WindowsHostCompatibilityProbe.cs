using System.ComponentModel;
using System.Runtime.InteropServices;
using EMKE.Application;
using EMKE.Core;

namespace EMKE.Platform.Compatibility;

public sealed record WindowsHostEvidence(
    bool IsWindows,
    int Build,
    Architecture Architecture,
    byte ProductType);

public interface IWindowsHostEvidenceSource
{
    WindowsHostEvidence Read();
}

public sealed class WindowsHostBuildGate : IWindowsBuildGate
{
    private const byte VerNtWorkstation = 1;
    private readonly CompatibilityManifest _compatibilityManifest;
    private readonly IWindowsHostEvidenceSource _evidenceSource;

    public WindowsHostBuildGate(
        CompatibilityManifest compatibilityManifest,
        IWindowsHostEvidenceSource evidenceSource)
    {
        _compatibilityManifest = compatibilityManifest
            ?? throw new ArgumentNullException(nameof(compatibilityManifest));
        _evidenceSource = evidenceSource
            ?? throw new ArgumentNullException(nameof(evidenceSource));
    }

    public ValueTask<RuntimeError?> CheckAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsHostEvidence evidence = _evidenceSource.Read();
        if (!evidence.IsWindows)
        {
            return ValueTask.FromResult<RuntimeError?>(
                Unsupported("unsupportedWindowsPlatform"));
        }

        if (evidence.ProductType != VerNtWorkstation)
        {
            return ValueTask.FromResult<RuntimeError?>(
                Unsupported("unsupportedWindowsProductType"));
        }

        if (evidence.Architecture != Architecture.X64)
        {
            return ValueTask.FromResult<RuntimeError?>(
                Unsupported("unsupportedWindowsArchitecture"));
        }

        return ValueTask.FromResult<RuntimeError?>(
            evidence.Build < _compatibilityManifest.MinimumWindowsBuild
                ? Unsupported("unsupportedWindowsBuild")
                : null);
    }

    private static RuntimeError Unsupported(string code)
    {
        return new RuntimeError(
            ErrorCategory.Configuration,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.ReportCompatibility);
    }
}

public sealed class WindowsHostCompatibilityProbe : IWindowsHostEvidenceSource
{
    private const int StatusSuccess = 0;

    public WindowsHostEvidence Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsHostEvidence(
                IsWindows: false,
                Build: 0,
                Architecture: RuntimeInformation.OSArchitecture,
                ProductType: 0);
        }

        OsVersionInfoEx version = new()
        {
            Size = checked((uint)Marshal.SizeOf<OsVersionInfoEx>()),
        };
        int status = NativeMethods.RtlGetVersion(ref version);
        if (status != StatusSuccess)
        {
            throw new Win32Exception(status, "RtlGetVersion failed.");
        }

        return new WindowsHostEvidence(
            IsWindows: true,
            Build: checked((int)version.BuildNumber),
            Architecture: RuntimeInformation.OSArchitecture,
            ProductType: version.ProductType);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct OsVersionInfoEx
    {
        public uint Size;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        public fixed char CsdVersion[128];
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ntdll.dll", EntryPoint = "RtlGetVersion")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int RtlGetVersion(ref OsVersionInfoEx version);
    }
}
