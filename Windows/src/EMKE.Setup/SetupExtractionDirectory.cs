using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup;

internal sealed class SetupExtractionException : Exception
{
    private const string DefaultFailureCode = "setupExtractionFailed";

    public SetupExtractionException()
        : this(DefaultFailureCode)
    {
    }

    public SetupExtractionException(string failureCode)
        : base("Setup extraction failed.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        FailureCode = failureCode;
    }

    public SetupExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(innerException);
        FailureCode = DefaultFailureCode;
    }

    public string FailureCode { get; }
}

internal sealed class SetupExtractionResult
{
    private SetupExtractionResult(
        bool succeeded,
        string? failureCode,
        VerifiedSetupPayload? payload)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Payload = payload;
    }

    public bool Succeeded { get; }

    public string? FailureCode { get; }

    public VerifiedSetupPayload? Payload { get; }

    public static SetupExtractionResult Success(VerifiedSetupPayload payload) => new(
        true,
        null,
        payload ?? throw new ArgumentNullException(nameof(payload)));

    public static SetupExtractionResult Rejected(string failureCode) => new(
        false,
        failureCode,
        null);
}

internal sealed class SetupExtractionDirectory : IDisposable
{
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int CopyBufferSize = 81920;
    private const int DirectoryQueryBufferSize = 65536;
    private const int FileNamesInformation = 12;
    private const int FileNamesInformationHeaderSize = 12;
    private const int StatusSuccess = 0;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint CreateNew = 1;
    private const uint FileAttributeReadOnly = 0x00000001;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private readonly object _cleanupGate = new();
    private readonly List<VerifiedPayloadLease> _payloadLeases = [];
    private readonly SafeFileHandle _rootHandle;
    private bool _disposed;
    private SetupCleanupOutcome? _cleanupOutcome;

    private SetupExtractionDirectory(AtomicSetupDirectory root)
    {
        RootPath = root.FullPath;
        _rootHandle = root.Handle;
    }

    public string RootPath { get; }

    public SetupCleanupOutcome CleanupState
    {
        get
        {
            lock (_cleanupGate)
            {
                return _cleanupOutcome ?? SetupCleanupOutcome.NotAttempted;
            }
        }
    }

    public static SetupExtractionDirectory Create(
        string setupOwnedBase,
        Version productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupOwnedBase);
        ArgumentNullException.ThrowIfNull(productVersion);

        string basePath = EnsureSafeBase(setupOwnedBase);
        WindowsAtomicSetupDirectoryFactory factory = new();
        for (int attempt = 0;
            attempt < WindowsAtomicSetupDirectoryFactory.MaximumCreateAttempts;
            attempt++)
        {
            byte[] random = RandomNumberGenerator.GetBytes(16);
            string rootName = string.Concat(
                productVersion.ToString(3), "-", Convert.ToHexStringLower(random));
            try
            {
                return new SetupExtractionDirectory(factory.Create(basePath, rootName));
            }
            catch (SetupExtractionException exception)
                when (exception.FailureCode == "extractionRootAlreadyExists")
            {
            }
        }

        throw new SetupExtractionException("extractionRootCollisionLimit");
    }

    public static SetupExtractionDirectory CreateForCurrentUser(
        Version productVersion)
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new SetupExtractionException("setupOwnedBaseUnavailable");
        }

        return Create(
            Path.Combine(localApplicationData, "EMKE", "Translation", "Setup"),
            productVersion);
    }

    // This deterministic entrypoint is intentionally internal and only visible to
    // the Setup test assembly; production always uses cryptographic random bytes.
    internal static SetupExtractionDirectory CreateNamedForTest(
        string setupOwnedBase,
        string rootName,
        Version productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupOwnedBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootName);
        ArgumentNullException.ThrowIfNull(productVersion);

        string basePath = EnsureSafeBase(setupOwnedBase);
        if (!IsSafeRootName(rootName, productVersion))
        {
            throw new SetupExtractionException("unsafeOutputPath");
        }

        WindowsAtomicSetupDirectoryFactory factory = new();
        return new SetupExtractionDirectory(factory.Create(basePath, rootName));
    }

    public SetupExtractionResult CopyVerified(
        Stream source,
        SetupPayload expectedPayload)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expectedPayload);

        string outputName = expectedPayload.FileName;
        if (!IsSafeOutputName(outputName))
        {
            return SetupExtractionResult.Rejected("unsafeOutputPath");
        }

        string outputPath = Path.Combine(RootPath, outputName);
        VerifiedPayloadLease? lease = null;
        try
        {
            EnsureNoReparsePointAtAnyExistingComponent(outputPath);
            if (!IsContainedByRoot(outputPath, RootPath))
            {
                return SetupExtractionResult.Rejected("unsafeOutputPath");
            }

            lease = CreateTrackedPayloadLease(expectedPayload, outputPath);
            (long length, string hash) = lease.UseHandle(handle =>
                CopyAndHash(source, handle, expectedPayload.Length));
            if (length != expectedPayload.Length)
            {
                return RejectCreatedPayload(lease, "tamperedPayloadLength");
            }
            if (!string.Equals(hash, expectedPayload.Sha256, StringComparison.Ordinal))
            {
                return RejectCreatedPayload(lease, "tamperedPayloadHash");
            }
            if (!lease.UseHandle(handle =>
                    IsFinalResolvedPathContainedByRoot(handle, RootPath))
                || !lease.UseHandle(handle =>
                    SetFileAttributesByHandle(handle, FileAttributeReadOnly)))
            {
                return RejectCreatedPayload(lease, "unsafeOutputPath");
            }

            VerifiedSetupPayload payload = new(
                expectedPayload,
                length,
                hash,
                lease);
            return SetupExtractionResult.Success(payload);
        }
        catch (SetupExtractionException exception)
        {
            if (lease is not null)
            {
                _ = TryCleanupPayloadLease(lease);
            }
            return SetupExtractionResult.Rejected(exception.FailureCode);
        }
        catch (IOException)
        {
            if (lease is not null)
            {
                _ = TryCleanupPayloadLease(lease);
            }
            return SetupExtractionResult.Rejected("payloadWriteFailed");
        }
    }

    public SetupCleanupOutcome Cleanup()
    {
        lock (_cleanupGate)
        {
            if (_cleanupOutcome is not null)
            {
                return _cleanupOutcome;
            }

            _disposed = true;
            List<string> retainedPayloads = [];
            foreach (VerifiedPayloadLease lease in
                _payloadLeases.AsEnumerable().Reverse().ToArray())
            {
                if (!lease.Cleanup())
                {
                    retainedPayloads.Add(lease.LogicalName);
                }
            }

            if (retainedPayloads.Count > 0)
            {
                return FreezeResidual(
                    "payloadCleanupUncertain",
                    retainedPayloads);
            }

            if (!TryRootHasUnexpectedEntries(out bool hasUnexpectedEntries))
            {
                return FreezeResidual(
                    "rootCleanupUncertain",
                    Array.Empty<string>());
            }

            if (hasUnexpectedEntries)
            {
                return FreezeResidual(
                    "unexpectedExtractionEntriesRetained",
                    ["unexpected-entry"]);
            }

            if (!TrySetDeleteDisposition(_rootHandle))
            {
                return FreezeResidual(
                    "rootCleanupUncertain",
                    Array.Empty<string>());
            }

            _cleanupOutcome = SetupCleanupOutcome.Cleaned;
            CloseOwnedHandlesAfterOutcome();
            return _cleanupOutcome;
        }
    }

    public void Dispose() => _ = Cleanup();

    private SetupCleanupOutcome FreezeResidual(
        string failureCode,
        IEnumerable<string> retainedLogicalNames)
    {
        _cleanupOutcome = SetupCleanupOutcome.Residual(
            failureCode,
            retainedLogicalNames);
        CloseOwnedHandlesAfterOutcome();
        return _cleanupOutcome;
    }

    private void CloseOwnedHandlesAfterOutcome()
    {
        foreach (VerifiedPayloadLease lease in _payloadLeases)
        {
            lease.CloseAfterResidual();
        }
        _payloadLeases.Clear();
        _rootHandle.Dispose();
    }

    private static string EnsureSafeBase(string setupOwnedBase)
    {
        string fullBasePath = Path.GetFullPath(setupOwnedBase);
        EnsureNoReparsePointAtAnyExistingComponent(fullBasePath);
        _ = Directory.CreateDirectory(fullBasePath);
        EnsureNoReparsePointAtAnyExistingComponent(fullBasePath);
        return fullBasePath;
    }

    private VerifiedPayloadLease CreateTrackedPayloadLease(
        SetupPayload payload,
        string outputPath)
    {
        VerifiedPayloadLease lease = CreatePayloadLease(payload, outputPath);
        try
        {
            _payloadLeases.Add(lease);
            return lease;
        }
        catch
        {
            _ = lease.Cleanup();
            lease.CloseAfterResidual();
            throw;
        }
    }

    private static VerifiedPayloadLease CreatePayloadLease(
        SetupPayload payload,
        string outputPath)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFile(
                outputPath,
                GenericRead
                    | GenericWrite
                    | FileReadAttributes
                    | FileWriteAttributes
                    | SynchronizeAccess
                    | DeleteAccess,
                FileShareRead,
                nint.Zero,
                CreateNew,
                FileAttributeNormal | FileFlagOpenReparsePoint,
                nint.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                handle = null;
                throw error is ErrorFileExists or ErrorAlreadyExists
                    ? new SetupExtractionException("existingOutputRejected")
                    : new Win32Exception(error);
            }

            VerifiedPayloadLease lease = new(
                handle,
                payload.LogicalName,
                outputPath);
            handle = null;
            return lease;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static (long length, string hash) CopyAndHash(
        Stream source,
        SafeFileHandle destination,
        long maximumLength)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        long length = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read > maximumLength - length)
            {
                throw new SetupExtractionException("tamperedPayloadLength");
            }

            RandomAccess.Write(destination, buffer.AsSpan(0, read), length);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        RandomAccess.FlushToDisk(destination);
        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private SetupExtractionResult RejectCreatedPayload(
        VerifiedPayloadLease lease,
        string failureCode)
    {
        _ = TryCleanupPayloadLease(lease);
        return SetupExtractionResult.Rejected(failureCode);
    }

    private bool TryCleanupPayloadLease(VerifiedPayloadLease lease)
    {
        bool cleaned = lease.Cleanup();
        if (cleaned)
        {
            _ = _payloadLeases.Remove(lease);
        }
        return cleaned;
    }

    private bool TryRootHasUnexpectedEntries(out bool hasUnexpectedEntries)
    {
        byte[] buffer = new byte[DirectoryQueryBufferSize];
        bool restartScan = true;
        hasUnexpectedEntries = false;
        while (true)
        {
            int status = NtQueryDirectoryFile(
                _rootHandle,
                nint.Zero,
                nint.Zero,
                nint.Zero,
                out IoStatusBlock ioStatusBlock,
                buffer,
                checked((uint)buffer.Length),
                FileNamesInformation,
                returnSingleEntry: false,
                nint.Zero,
                restartScan);
            restartScan = false;

            if (status == StatusNoMoreFiles)
            {
                return true;
            }
            if (status != StatusSuccess
                || ioStatusBlock.Information == 0
                || ioStatusBlock.Information > (nuint)buffer.Length)
            {
                return false;
            }

            int returnedLength = checked((int)ioStatusBlock.Information);
            if (!TryParseDirectoryNames(
                buffer.AsSpan(0, returnedLength),
                ref hasUnexpectedEntries))
            {
                return false;
            }
        }
    }

    private static bool TryParseDirectoryNames(
        ReadOnlySpan<byte> buffer,
        ref bool hasUnexpectedEntries)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            ReadOnlySpan<byte> remaining = buffer[offset..];
            if (remaining.Length < FileNamesInformationHeaderSize)
            {
                return false;
            }

            uint nextEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            uint fileNameLength = BinaryPrimitives.ReadUInt32LittleEndian(
                remaining[8..]);
            if (fileNameLength == 0 || fileNameLength % sizeof(char) != 0)
            {
                return false;
            }

            int entryLength;
            if (nextEntryOffset == 0)
            {
                entryLength = remaining.Length;
            }
            else
            {
                if (nextEntryOffset < FileNamesInformationHeaderSize
                    || nextEntryOffset > (uint)remaining.Length
                    || nextEntryOffset % sizeof(uint) != 0)
                {
                    return false;
                }
                entryLength = checked((int)nextEntryOffset);
            }

            if (fileNameLength
                > (uint)(entryLength - FileNamesInformationHeaderSize))
            {
                return false;
            }

            string fileName = Encoding.Unicode.GetString(remaining.Slice(
                FileNamesInformationHeaderSize,
                checked((int)fileNameLength)));
            if (fileName is not "." and not "..")
            {
                hasUnexpectedEntries = true;
            }

            if (nextEntryOffset == 0)
            {
                return true;
            }
            offset = checked(offset + entryLength);
        }

        return false;
    }

    private static void EnsureNoReparsePointAtAnyExistingComponent(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath)
            ?? throw new SetupExtractionException("unsafeOutputPath");
        string relative = Path.GetRelativePath(root, fullPath);
        string current = root;
        foreach (string component in relative.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar))
        {
            if (component is "" or ".")
            {
                continue;
            }

            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SetupExtractionException("reparsePointDetected");
            }
        }
    }

    private static bool IsSafeRootName(string rootName, Version productVersion)
    {
        return rootName.StartsWith(
                productVersion.ToString(3) + "-",
                StringComparison.Ordinal)
            && IsSafeOutputName(rootName);
    }

    private static bool IsSafeOutputName(string outputName)
    {
        return outputName is not "." and not ".."
            && !Path.IsPathRooted(outputName)
            && !outputName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(static component => component is "" or "." or "..");
    }

    private static bool IsContainedByRoot(string candidate, string root)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        string fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return fullCandidate.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFinalResolvedPathContainedByRoot(
        SafeFileHandle output,
        string root)
    {
        return TryGetFinalPath(output, out string finalOutput)
            && IsContainedByRoot(finalOutput, root);
    }

    private static bool TryGetFinalPath(
        SafeFileHandle handle,
        out string finalPath)
    {
        char[] buffer = new char[32768];
        uint length = GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Length),
            flags: 0);
        if (length == 0 || length >= buffer.Length)
        {
            finalPath = string.Empty;
            return false;
        }

        finalPath = NormalizeFinalPath(new string(buffer, 0, checked((int)length)));
        return true;
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

    private static bool SetFileAttributesByHandle(
        SafeFileHandle handle,
        uint attributes)
    {
        FileBasicInformation information = new()
        {
            FileAttributes = attributes,
        };
        return SetFileBasicInformationByHandle(
            handle,
            FileInformationClass.FileBasicInfo,
            ref information,
            checked((uint)Marshal.SizeOf<FileBasicInformation>()));
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint StatusOrPointer;
        public nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
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

    [DllImport(
        "ntdll.dll",
        EntryPoint = "NtQueryDirectoryFile",
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryDirectoryFile(
        SafeFileHandle fileHandle,
        nint eventHandle,
        nint apcRoutine,
        nint apcContext,
        out IoStatusBlock ioStatusBlock,
        [Out] byte[] fileInformation,
        uint length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        nint fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);

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
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileBasicInformationByHandle(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        ref FileBasicInformation fileInformation,
        uint bufferSize);

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
