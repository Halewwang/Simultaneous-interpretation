using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

internal sealed class SetupExtractionCleanupState
{
    private SetupExtractionCleanupState(
        bool completed,
        bool residualRetained,
        string? failureCode)
    {
        Completed = completed;
        ResidualRetained = residualRetained;
        FailureCode = failureCode;
    }

    public bool Completed { get; }

    public bool ResidualRetained { get; }

    public string? FailureCode { get; }

    public static SetupExtractionCleanupState NotAttempted { get; } = new(
        completed: false,
        residualRetained: false,
        failureCode: null);

    public static SetupExtractionCleanupState Cleaned { get; } = new(
        completed: true,
        residualRetained: false,
        failureCode: null);

    public static SetupExtractionCleanupState Residual(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new SetupExtractionCleanupState(
            completed: false,
            residualRetained: true,
            failureCode);
    }
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
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeReadOnly = 0x00000001;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private readonly List<VerifiedPayloadLease> _payloadLeases = [];
    private readonly SafeFileHandle _rootHandle;
    private readonly SetupFileIdentity _rootIdentity;
    private bool _disposed;
    private string? _cleanupUncertaintyCode;

    private SetupExtractionDirectory(AtomicSetupDirectory root)
    {
        RootPath = root.FullPath;
        _rootHandle = root.Handle;
        _rootIdentity = root.Identity;
    }

    public string RootPath { get; }

    public SetupExtractionCleanupState CleanupState { get; private set; } =
        SetupExtractionCleanupState.NotAttempted;

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
                _ = TryDeletePayloadLease(lease, closeOnFailure: false);
            }
            return SetupExtractionResult.Rejected(exception.FailureCode);
        }
        catch (IOException)
        {
            if (lease is not null)
            {
                _ = TryDeletePayloadLease(lease, closeOnFailure: false);
            }
            return SetupExtractionResult.Rejected("payloadWriteFailed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        bool payloadCleanupCertain = true;
        foreach (VerifiedPayloadLease lease in
            _payloadLeases.AsEnumerable().Reverse().ToArray())
        {
            payloadCleanupCertain &= TryDeletePayloadLease(lease, closeOnFailure: true);
        }

        if (!payloadCleanupCertain || _cleanupUncertaintyCode is not null)
        {
            _rootHandle.Dispose();
            CleanupState = SetupExtractionCleanupState.Residual(
                _cleanupUncertaintyCode ?? "payloadCleanupUncertain");
            return;
        }

        if (!IsRootPathStillBoundToHeldHandle())
        {
            _rootHandle.Dispose();
            CleanupState = SetupExtractionCleanupState.Residual("rootIdentityChanged");
            return;
        }

        bool rootInspectionSucceeded;
        bool rootHasEntries = TryRootHasEntries(out rootInspectionSucceeded);
        if (!rootInspectionSucceeded)
        {
            _rootHandle.Dispose();
            CleanupState = SetupExtractionCleanupState.Residual("rootInspectionFailed");
            return;
        }
        if (rootHasEntries)
        {
            _rootHandle.Dispose();
            CleanupState = SetupExtractionCleanupState.Residual(
                "unexpectedExtractionEntriesRetained");
            return;
        }

        bool rootDeleteMarked = TrySetDeleteDisposition(_rootHandle);
        _rootHandle.Dispose();
        CleanupState = rootDeleteMarked
            ? SetupExtractionCleanupState.Cleaned
            : SetupExtractionCleanupState.Residual("rootCleanupUncertain");
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
            _ = lease.UseHandle(handle =>
                SetFileAttributesByHandle(handle, FileAttributeNormal)
                    && TrySetDeleteDisposition(handle));
            lease.Dispose();
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
        _ = TryDeletePayloadLease(lease, closeOnFailure: false);
        return SetupExtractionResult.Rejected(failureCode);
    }

    private bool TryDeletePayloadLease(
        VerifiedPayloadLease lease,
        bool closeOnFailure)
    {
        if (lease.Closed)
        {
            _ = _payloadLeases.Remove(lease);
            return true;
        }

        bool deleteMarked = lease.UseHandle(handle =>
            SetFileAttributesByHandle(handle, FileAttributeNormal)
                && TrySetDeleteDisposition(handle));
        if (deleteMarked || closeOnFailure)
        {
            lease.Dispose();
            _ = _payloadLeases.Remove(lease);
        }
        if (!deleteMarked && closeOnFailure)
        {
            _cleanupUncertaintyCode ??= "payloadCleanupUncertain";
        }
        return deleteMarked;
    }

    private bool IsRootPathStillBoundToHeldHandle()
    {
        SafeFileHandle? pathHandle = null;
        try
        {
            pathHandle = CreateFile(
                RootPath,
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                nint.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                nint.Zero);
            return !pathHandle.IsInvalid
                && TryReadIdentity(pathHandle, out SetupFileIdentity currentIdentity)
                && currentIdentity.Equals(_rootIdentity)
                && (currentIdentity.FileAttributes & FileAttributeReparsePoint) == 0;
        }
        finally
        {
            pathHandle?.Dispose();
        }
    }

    private bool TryRootHasEntries(out bool inspectionSucceeded)
    {
        try
        {
            bool hasEntries = Directory.EnumerateFileSystemEntries(RootPath).Any();
            inspectionSucceeded = true;
            return hasEntries;
        }
        catch (IOException)
        {
            inspectionSucceeded = false;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            inspectionSucceeded = false;
            return true;
        }
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
