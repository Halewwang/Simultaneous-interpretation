using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EMKE.Setup.Tests;

internal static partial class TestNativeFileMethods
{
    public static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        if (!CreateHardLinkNative(linkPath, existingPath, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkNative(
        string fileName,
        string existingFileName,
        nint securityAttributes);
}
