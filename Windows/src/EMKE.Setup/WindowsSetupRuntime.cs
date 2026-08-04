using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EMKE.Setup.Elevated;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup;

internal static class WindowsSetupDirectoryIdentity
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    public static SetupExtractionRootIdentity Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        using SafeFileHandle handle = CreateFile(
            fullPath,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            nint.Zero);
        if (handle.IsInvalid
            || !GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if ((info.FileAttributes & FileAttributeDirectory) == 0
            || (info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The Setup extraction root identity is invalid.");
        }
        return new SetupExtractionRootIdentity(
            fullPath,
            info.VolumeSerialNumber,
            info.FileIndexHigh,
            info.FileIndexLow,
            info.FileAttributes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}

internal sealed class WindowsSetupApplicationLauncher
    : ISetupApplicationLauncher
{
    private const string AppUserModelId =
        "EMKE.Translation.Internal_kvab4te83cr7p!EMKETranslation";

    public static WindowsSetupApplicationLauncher Instance { get; } = new();

    private WindowsSetupApplicationLauncher()
    {
    }

    public Task LaunchAsync(
        SetupApplicationLaunchMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode != SetupApplicationLaunchMode.ControlledNoTranslationConnect)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        using Process? process = Process.Start(new ProcessStartInfo(
            string.Concat("shell:AppsFolder\\", AppUserModelId))
        {
            UseShellExecute = true,
        });
        if (process is null)
        {
            throw new InvalidOperationException(
                "The installed application could not be launched.");
        }
        return Task.CompletedTask;
    }
}
