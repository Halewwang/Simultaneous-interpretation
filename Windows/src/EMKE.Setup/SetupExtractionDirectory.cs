using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup;

internal sealed class SetupExtractionException : Exception
{
    public SetupExtractionException(string failureCode)
        : base("Setup extraction failed.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}

internal sealed class SetupExtractionResult : IDisposable
{
    private readonly FileStream? _verificationLease;

    private SetupExtractionResult(
        bool succeeded,
        string? failureCode,
        string outputPath,
        FileStream? verificationLease)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        OutputPath = outputPath;
        _verificationLease = verificationLease;
    }

    public bool Succeeded { get; }

    public string? FailureCode { get; }

    public string OutputPath { get; }

    public static SetupExtractionResult Success(
        string outputPath,
        FileStream? verificationLease) => new(
            true,
            null,
            outputPath,
            verificationLease);

    public static SetupExtractionResult Rejected(string failureCode) => new(
        false,
        failureCode,
        string.Empty,
        verificationLease: null);

    public void Dispose()
    {
        _verificationLease?.Dispose();
    }
}

internal sealed partial class SetupExtractionDirectory : IDisposable
{
    private const int ErrorAlreadyExists = 183;
    private const int MaximumCreateAttempts = 8;
    private readonly List<SetupExtractionResult> _verificationLeases = [];
    private readonly string _rootIdentityPath;
    private readonly FileStream _rootIdentityLease;
    private bool _disposed;

    private SetupExtractionDirectory(string rootPath)
    {
        RootPath = rootPath;
        _rootIdentityPath = Path.Combine(rootPath, ".emke-setup-root.lock");
        _rootIdentityLease = new FileStream(
            _rootIdentityPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read);
    }

    public string RootPath { get; }

    public static SetupExtractionDirectory Create(
        string setupOwnedBase,
        Version productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupOwnedBase);
        ArgumentNullException.ThrowIfNull(productVersion);

        string basePath = EnsureSafeBase(setupOwnedBase);
        for (int attempt = 0; attempt < MaximumCreateAttempts; attempt++)
        {
            byte[] random = RandomNumberGenerator.GetBytes(16);
            string rootName = string.Concat(
                productVersion.ToString(3), "-", Convert.ToHexStringLower(random));
            string candidate = Path.Combine(basePath, rootName);
            if (TryCreateNewDirectory(candidate))
            {
                return new SetupExtractionDirectory(candidate);
            }
        }

        throw new SetupExtractionException("extractionRootAlreadyExists");
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
    internal static SetupExtractionResult CreateNamedForTest(
        string setupOwnedBase,
        string rootName,
        Version productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupOwnedBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootName);
        ArgumentNullException.ThrowIfNull(productVersion);

        try
        {
            string basePath = EnsureSafeBase(setupOwnedBase);
            if (!IsSafeRootName(rootName, productVersion))
            {
                return SetupExtractionResult.Rejected("unsafeOutputPath");
            }

            string candidate = Path.Combine(basePath, rootName);
            return TryCreateNewDirectory(candidate)
                ? SetupExtractionResult.Success(
                    candidate,
                    verificationLease: null)
                : SetupExtractionResult.Rejected("extractionRootAlreadyExists");
        }
        catch (SetupExtractionException exception)
        {
            return SetupExtractionResult.Rejected(exception.FailureCode);
        }
    }

    public SetupExtractionResult CopyVerified(
        string outputName,
        Stream source,
        SetupPayload expectedPayload)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expectedPayload);

        if (!IsSafeOutputName(outputName)
            || !string.Equals(
                outputName,
                expectedPayload.FileName,
                StringComparison.Ordinal))
        {
            return SetupExtractionResult.Rejected("unsafeOutputPath");
        }

        string outputPath = Path.Combine(RootPath, outputName);
        try
        {
            EnsureNoReparsePointAtAnyExistingComponent(outputPath);
            if (!IsContainedByRoot(outputPath, RootPath))
            {
                return SetupExtractionResult.Rejected("unsafeOutputPath");
            }

            string hash;
            long length;
            FileStream lease;
            try
            {
                (length, hash, lease) = CopyAndHash(source, outputPath, expectedPayload.Length);
            }
            catch (IOException) when (File.Exists(outputPath))
            {
                return SetupExtractionResult.Rejected("existingOutputRejected");
            }

            if (length != expectedPayload.Length)
            {
                lease.Dispose();
                DeleteCreatedOutput(outputPath);
                return SetupExtractionResult.Rejected("tamperedPayloadLength");
            }
            if (!string.Equals(hash, expectedPayload.Sha256, StringComparison.Ordinal))
            {
                lease.Dispose();
                DeleteCreatedOutput(outputPath);
                return SetupExtractionResult.Rejected("tamperedPayloadHash");
            }

            File.SetAttributes(outputPath, FileAttributes.ReadOnly);
            if (!IsFinalResolvedPathContainedByRoot(lease, RootPath))
            {
                lease.Dispose();
                DeleteCreatedOutput(outputPath);
                return SetupExtractionResult.Rejected("unsafeOutputPath");
            }

            SetupExtractionResult result = SetupExtractionResult.Success(outputPath, lease);
            _verificationLeases.Add(result);
            return result;
        }
        catch (SetupExtractionException exception)
        {
            DeleteCreatedOutput(outputPath);
            return SetupExtractionResult.Rejected(exception.FailureCode);
        }
        catch (IOException)
        {
            DeleteCreatedOutput(outputPath);
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
        foreach (SetupExtractionResult lease in _verificationLeases.AsEnumerable().Reverse())
        {
            lease.Dispose();
            DeleteCreatedOutput(lease.OutputPath);
        }

        _rootIdentityLease.Dispose();
        DeleteCreatedOutput(_rootIdentityPath);
        try
        {
            EnsureNoReparsePointAtAnyExistingComponent(RootPath);
            Directory.Delete(RootPath, recursive: false);
        }
        catch (IOException)
        {
            // A non-empty or externally changed root is deliberately left intact.
        }
    }

    private static string EnsureSafeBase(string setupOwnedBase)
    {
        string fullBasePath = Path.GetFullPath(setupOwnedBase);
        EnsureNoReparsePointAtAnyExistingComponent(fullBasePath);
        _ = Directory.CreateDirectory(fullBasePath);
        EnsureNoReparsePointAtAnyExistingComponent(fullBasePath);
        return fullBasePath;
    }

    private static bool TryCreateNewDirectory(string candidate)
    {
        EnsureNoReparsePointAtAnyExistingComponent(Path.GetDirectoryName(candidate)!);
        if (!OperatingSystem.IsWindows())
        {
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return false;
            }

            _ = Directory.CreateDirectory(candidate);
            return true;
        }

        bool created = CreateDirectory(candidate, nint.Zero);
        if (created)
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        return error == ErrorAlreadyExists ? false : throw new IOException();
    }

    private static (long length, string hash, FileStream lease) CopyAndHash(
        Stream source,
        string outputPath,
        long maximumLength)
    {
        FileStream destination = new(
            outputPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long length = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (read > maximumLength - length)
                {
                    throw new SetupExtractionException("tamperedPayloadLength");
                }

                destination.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                length += read;
            }

            destination.Flush(flushToDisk: true);
            return (length, Convert.ToHexStringLower(hash.GetHashAndReset()), destination);
        }
        catch
        {
            destination.Dispose();
            throw;
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
        FileStream output,
        string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return IsContainedByRoot(Path.GetFullPath(output.Name), root);
        }

        char[] buffer = new char[32768];
        uint length = GetFinalPathNameByHandle(
            output.SafeFileHandle,
            buffer,
            checked((uint)buffer.Length),
            flags: 0);
        if (length == 0 || length >= buffer.Length)
        {
            return false;
        }

        string finalOutput = NormalizeFinalPath(new string(buffer, 0, (int)length));
        return IsContainedByRoot(finalOutput, root);
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

    private static void DeleteCreatedOutput(string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return;
        }

        try
        {
            File.SetAttributes(outputPath, FileAttributes.Normal);
            File.Delete(outputPath);
        }
        catch (IOException)
        {
            // Cleanup is best-effort and never expands to a parent directory.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string path, nint securityAttributes);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);
}
