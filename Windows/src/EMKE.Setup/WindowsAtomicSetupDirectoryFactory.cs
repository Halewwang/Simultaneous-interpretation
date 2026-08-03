using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup;

internal readonly record struct SetupFileIdentity(
    uint VolumeSerialNumber,
    uint FileIndexHigh,
    uint FileIndexLow,
    uint FileAttributes);

#pragma warning disable CA1001 // Handle ownership immediately transfers to SetupExtractionDirectory.
internal sealed record AtomicSetupDirectory(
    string FullPath,
    SafeFileHandle Handle,
    SetupFileIdentity Identity);
#pragma warning restore CA1001

internal sealed class WindowsAtomicSetupDirectoryFactory
{
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const uint DesiredAccess = 0x00000001 | 0x00000020 | 0x00000080
        | 0x00010000 | 0x00100000;
    private const uint ShareAccess = 0x00000001 | 0x00000002;
    private const uint FileCreate = 2;
    private const uint CreateOptions = 0x00000001 | 0x00000020 | 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const nuint FileCreated = 2;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileTraverse = 0x00000020;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int MaximumFinalPathLength = 32768;

    internal const int MaximumCreateAttempts = 8;

#pragma warning disable CA1822 // The task contract requires an instance factory operation.
    public AtomicSetupDirectory Create(string basePath, string leafName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(leafName);

        string fullBasePath = Path.GetFullPath(basePath);
        string fullPath = Path.Combine(fullBasePath, leafName);
        using SafeFileHandle baseHandle = OpenVerifiedBase(fullBasePath);
        return CreateRelative(baseHandle, fullPath, leafName);
    }
#pragma warning restore CA1822

    private static SafeFileHandle OpenVerifiedBase(string basePath)
    {
        SafeFileHandle? handle = CreateFile(
            basePath,
            FileTraverse | FileReadAttributes,
            FileShareRead | FileShareWrite,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            nint.Zero);
        try
        {
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                throw CreateFailure(error);
            }

            if (!TryReadIdentity(handle, out SetupFileIdentity identity)
                || (identity.FileAttributes & FileAttributeReparsePoint) != 0
                || !IsFinalResolvedPathEqual(handle, basePath))
            {
                throw new SetupExtractionException("atomicExtractionRootUnavailable");
            }

            SafeFileHandle verifiedHandle = handle;
            handle = null;
            return verifiedHandle;
        }
        finally
        {
            handle?.Dispose();
        }
    }

#pragma warning disable CA2000 // The returned transfer record carries the created handle to its owner.
    private static AtomicSetupDirectory CreateRelative(
        SafeFileHandle baseHandle,
        string fullPath,
        string leafName)
    {
        int nameByteLength = checked(leafName.Length * sizeof(char));
        if (nameByteLength > ushort.MaxValue - sizeof(char))
        {
            throw new SetupExtractionException("atomicExtractionRootUnavailable");
        }

        nint nameBuffer = nint.Zero;
        nint unicodeStringBuffer = nint.Zero;
        SafeFileHandle? createdHandle = null;
        try
        {
            nameBuffer = Marshal.StringToHGlobalUni(leafName);
            UnicodeString unicodeString = new()
            {
                Length = checked((ushort)nameByteLength),
                MaximumLength = checked((ushort)(nameByteLength + sizeof(char))),
                Buffer = nameBuffer,
            };
            unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringBuffer, fDeleteOld: false);

            ObjectAttributes objectAttributes = new()
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = baseHandle.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = ObjectCaseInsensitive,
            };
            int status = NtCreateFile(
                out createdHandle,
                DesiredAccess,
                ref objectAttributes,
                out IoStatusBlock ioStatusBlock,
                nint.Zero,
                fileAttributes: 0,
                ShareAccess,
                FileCreate,
                CreateOptions,
                nint.Zero,
                eaLength: 0);
            GC.KeepAlive(baseHandle);

            if (status == StatusObjectNameCollision)
            {
                throw CreateNtFailure("extractionRootAlreadyExists", status);
            }
            if (status < 0
                || ioStatusBlock.Information != FileCreated
                || createdHandle.IsInvalid)
            {
                throw CreateNtFailure("atomicExtractionRootUnavailable", status);
            }

            if (!TryReadIdentity(createdHandle, out SetupFileIdentity identity)
                || (identity.FileAttributes & FileAttributeReparsePoint) != 0
                || !IsFinalResolvedPathEqual(createdHandle, fullPath))
            {
                _ = TrySetDeleteDisposition(createdHandle);
                throw new SetupExtractionException("atomicExtractionRootUnavailable");
            }

            AtomicSetupDirectory root = new(fullPath, createdHandle, identity);
            createdHandle = null;
            return root;
        }
        finally
        {
            createdHandle?.Dispose();
            if (unicodeStringBuffer != nint.Zero)
            {
                Marshal.FreeHGlobal(unicodeStringBuffer);
            }
            if (nameBuffer != nint.Zero)
            {
                Marshal.FreeHGlobal(nameBuffer);
            }
        }
    }
#pragma warning restore CA2000

    private static SetupExtractionException CreateFailure(int win32Error)
    {
        SetupExtractionException exception = new("atomicExtractionRootUnavailable");
        exception.Data["win32Error"] = unchecked((uint)win32Error);
        return exception;
    }

    private static SetupExtractionException CreateNtFailure(
        string failureCode,
        int status)
    {
        SetupExtractionException exception = new(failureCode);
        exception.Data["ntStatus"] = unchecked((uint)status);
        exception.Data["win32Error"] = RtlNtStatusToDosError(status);
        return exception;
    }

    private static bool TryReadIdentity(
        SafeFileHandle handle,
        out SetupFileIdentity identity)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            identity = default;
            return false;
        }

        identity = new SetupFileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow,
            information.FileAttributes);
        return true;
    }

    private static bool IsFinalResolvedPathEqual(
        SafeFileHandle handle,
        string expectedPath)
    {
        char[] buffer = new char[MaximumFinalPathLength];
        uint length = GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Length),
            flags: 0);
        if (length == 0 || length >= buffer.Length)
        {
            return false;
        }

        string finalPath = NormalizeFinalPath(new string(buffer, 0, checked((int)length)));
        return string.Equals(
            Path.GetFullPath(finalPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(expectedPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFinalPath(string path)
    {
        const string ExtendedPathPrefix = @"\\?\";
        const string ExtendedUncPrefix = @"\\?\UNC\";
        return path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[ExtendedUncPrefix.Length..]
            : path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal)
                ? path[ExtendedPathPrefix.Length..]
                : path;
    }

    private static bool TrySetDeleteDisposition(SafeFileHandle handle)
    {
        FileDispositionInformation information = new()
        {
            DeleteFile = true,
        };
        return SetFileDispositionByHandle(
            handle,
            FileInformationClass.FileDispositionInfo,
            ref information,
            checked((uint)Marshal.SizeOf<FileDispositionInformation>()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint StatusOrPointer;
        public nuint Information;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    private enum FileInformationClass
    {
        FileBasicInfo,
        FileStandardInfo,
        FileNameInfo,
        FileRenameInfo,
        FileDispositionInfo,
    }

    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint eaBuffer,
        uint eaLength);

    [DllImport(
        "ntdll.dll",
        EntryPoint = "RtlNtStatusToDosError",
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandle",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileDispositionByHandle(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}
