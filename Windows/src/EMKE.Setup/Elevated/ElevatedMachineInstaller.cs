using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EMKE.Setup.Platform;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup.Elevated;

internal sealed class ElevatedMachinePayloadSet : IDisposable
{
    private readonly bool _ownsPayloads;
    private bool _disposed;

    public ElevatedMachinePayloadSet(
        VerifiedSetupPayload certificate,
        VerifiedSetupPayload inf,
        VerifiedSetupPayload sys,
        VerifiedSetupPayload catalog,
        bool ownsPayloads)
    {
        Certificate = certificate
            ?? throw new ArgumentNullException(nameof(certificate));
        Inf = inf ?? throw new ArgumentNullException(nameof(inf));
        Sys = sys ?? throw new ArgumentNullException(nameof(sys));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _ownsPayloads = ownsPayloads;
    }

    public VerifiedSetupPayload Certificate { get; }

    public VerifiedSetupPayload Inf { get; }

    public VerifiedSetupPayload Sys { get; }

    public VerifiedSetupPayload Catalog { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!_ownsPayloads)
        {
            return;
        }
        Catalog.Lease.Dispose();
        Sys.Lease.Dispose();
        Inf.Lease.Dispose();
        Certificate.Lease.Dispose();
    }
}

internal interface IElevatedMachinePayloadSource
{
    ElevatedMachinePayloadSet Open(SetupElevationRequest request);
}

internal sealed class ElevatedMachineInstaller : ISetupElevatedRequestHandler
{
    private const string CertificateSubject = "CN=EMKE Internal Test";
    private readonly IElevatedMachinePayloadSource _payloadSource;
    private readonly ICertificateMachineInstaller _certificateInstaller;
    private readonly IDriverMachineInstaller _driverInstaller;
    private readonly TimeProvider _timeProvider;

    public ElevatedMachineInstaller()
        : this(
            WindowsElevatedMachinePayloadSource.Instance,
            new CertificateInstaller(),
            new DriverInstaller(),
            TimeProvider.System)
    {
    }

    internal ElevatedMachineInstaller(
        IElevatedMachinePayloadSource payloadSource,
        ICertificateMachineInstaller certificateInstaller,
        IDriverMachineInstaller driverInstaller,
        TimeProvider? timeProvider = null)
    {
        _payloadSource = payloadSource
            ?? throw new ArgumentNullException(nameof(payloadSource));
        _certificateInstaller = certificateInstaller
            ?? throw new ArgumentNullException(nameof(certificateInstaller));
        _driverInstaller = driverInstaller
            ?? throw new ArgumentNullException(nameof(driverInstaller));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

#pragma warning disable CA1031 // The authenticated helper returns only closed outcomes.
    public Task<SetupElevatedHelperOutcome> HandleAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken)
    {
        return HandleAndCommitAsync(request, cancellationToken);
    }

    private async Task<SetupElevatedHelperOutcome> HandleAndCommitAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken)
    {
        SetupElevatedPreparedChange prepared = await PrepareAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (prepared.Outcome != SetupElevatedHelperOutcome.Failed)
        {
            _ = await FinalizeAsync(
                    prepared,
                    SetupElevationFinalizationAction.Commit,
                    request.TransactionId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return prepared.Outcome;
    }

    public Task<SetupElevatedPreparedChange> PrepareAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        CertificateInstallReceipt? certificateReceipt = null;
        DriverInstallReceipt? driverReceipt = null;
        try
        {
            using ElevatedMachinePayloadSet payloads = _payloadSource.Open(request);
            CertificateInstallContract certificateContract = new(
                CertificateSubject,
                request.AllowedCertificateThumbprint,
                request.PayloadHashes.CertificateSha256,
                _timeProvider.GetUtcNow());
            CertificateInstallResult certificate = _certificateInstaller.Install(
                payloads.Certificate,
                certificateContract,
                request.TransactionId);
            certificateReceipt = certificate.Receipt;
            if (certificate.Outcome != CertificateInstallOutcome.Succeeded
                || certificate.Receipt is null)
            {
                Rollback(
                    driverReceipt: null,
                    certificateReceipt,
                    request.TransactionId);
                return Task.FromResult(FailedPreparation);
            }

            DriverInstallContract driverContract = new(
                request.AllowedDriverHardwareId,
                request.AllowedDriverVersion,
                request.PayloadHashes.DriverInfSha256,
                request.PayloadHashes.DriverSysSha256,
                request.PayloadHashes.DriverCatalogSha256);
            DriverInstallResult driver = _driverInstaller.Install(
                payloads.Inf,
                payloads.Sys,
                payloads.Catalog,
                driverContract,
                request.TransactionId);
            driverReceipt = driver.Receipt;
            if (driver.Outcome is DriverInstallOutcome.Succeeded
                    or DriverInstallOutcome.RebootRequired
                && driverReceipt is null)
            {
                Rollback(driverReceipt, certificateReceipt, request.TransactionId);
                return Task.FromResult(FailedPreparation);
            }
            if (driver.Outcome == DriverInstallOutcome.Succeeded)
            {
                return Task.FromResult(CreatePrepared(
                    SetupElevatedHelperOutcome.Succeeded,
                    request.TransactionId,
                    certificateReceipt!,
                    driverReceipt!));
            }
            if (driver.Outcome == DriverInstallOutcome.RebootRequired)
            {
                return Task.FromResult(CreatePrepared(
                    SetupElevatedHelperOutcome.RebootRequired,
                    request.TransactionId,
                    certificateReceipt!,
                    driverReceipt!));
            }

            Rollback(driverReceipt, certificateReceipt, request.TransactionId);
            return Task.FromResult(FailedPreparation);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Rollback(driverReceipt, certificateReceipt, request.TransactionId);
            throw;
        }
        catch (Exception)
        {
            Rollback(driverReceipt, certificateReceipt, request.TransactionId);
            return Task.FromResult(FailedPreparation);
        }
    }

    public Task<bool> FinalizeAsync(
        SetupElevatedPreparedChange prepared,
        SetupElevationFinalizationAction action,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        if (prepared.State is not ElevatedMachinePreparedState state
            || state.TransactionId != transactionId)
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(action switch
        {
            SetupElevationFinalizationAction.Commit => true,
            SetupElevationFinalizationAction.Rollback => Rollback(
                state.DriverReceipt,
                state.CertificateReceipt,
                transactionId),
            _ => false,
        });
    }
#pragma warning restore CA1031

    private static SetupElevatedPreparedChange FailedPreparation { get; } = new(
        SetupElevatedHelperOutcome.Failed,
        new SetupMachineCreatedState(false, false, false),
        State: null);

    private static SetupElevatedPreparedChange CreatePrepared(
        SetupElevatedHelperOutcome outcome,
        Guid transactionId,
        CertificateInstallReceipt certificateReceipt,
        DriverInstallReceipt driverReceipt) => new(
            outcome,
            new SetupMachineCreatedState(
                certificateReceipt.CreatedByAttempt,
                driverReceipt.PackageCreatedByAttempt,
                driverReceipt.DeviceCreatedByAttempt),
            new ElevatedMachinePreparedState(
                transactionId,
                certificateReceipt,
                driverReceipt));

#pragma warning disable CA1031 // Each rollback must continue even if its peer fails.
    private bool Rollback(
        DriverInstallReceipt? driverReceipt,
        CertificateInstallReceipt? certificateReceipt,
        Guid transactionId)
    {
        bool succeeded = true;
        if (driverReceipt is not null)
        {
            try
            {
                succeeded &= _driverInstaller.Rollback(
                    driverReceipt,
                    transactionId).Succeeded;
            }
            catch (Exception)
            {
                // The concrete installer emits the durable recovery record.
                succeeded = false;
            }
        }
        if (certificateReceipt is not null)
        {
            try
            {
                succeeded &= _certificateInstaller.Rollback(
                    certificateReceipt,
                    transactionId).Succeeded;
            }
            catch (Exception)
            {
                // The concrete installer emits the durable recovery record.
                succeeded = false;
            }
        }
        return succeeded;
    }
#pragma warning restore CA1031

    private sealed record ElevatedMachinePreparedState(
        Guid TransactionId,
        CertificateInstallReceipt CertificateReceipt,
        DriverInstallReceipt DriverReceipt);
}

internal sealed class WindowsElevatedMachinePayloadSource
    : IElevatedMachinePayloadSource
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint VolumeNameDos = 0;
    private const int MaximumFinalPathCharacters = 32768;
    public static WindowsElevatedMachinePayloadSource Instance { get; } = new();

    private WindowsElevatedMachinePayloadSource()
    {
    }

    public ElevatedMachinePayloadSet Open(SetupElevationRequest request)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(request);
        using SafeFileHandle root = OpenRoot(request.ExtractionRoot);
        string finalRoot = GetFinalPath(root).TrimEnd('\\');
        List<VerifiedSetupPayload> opened = [];
        try
        {
            opened.Add(OpenPayload(
                root,
                finalRoot,
                request.ExtractionRoot.FullPath,
                "EMKE-Translation-Windows-0.2.0-internal-x64.cer",
                "application-certificate",
                SetupPayloadKind.Certificate,
                request.PayloadHashes.CertificateSha256));
            opened.Add(OpenPayload(
                root,
                finalRoot,
                request.ExtractionRoot.FullPath,
                "EMKE.VirtualAudio.inf",
                "driver-inf",
                SetupPayloadKind.DriverInf,
                request.PayloadHashes.DriverInfSha256));
            opened.Add(OpenPayload(
                root,
                finalRoot,
                request.ExtractionRoot.FullPath,
                "EMKE.VirtualAudio.sys",
                "driver-sys",
                SetupPayloadKind.DriverSys,
                request.PayloadHashes.DriverSysSha256));
            opened.Add(OpenPayload(
                root,
                finalRoot,
                request.ExtractionRoot.FullPath,
                "EMKE.VirtualAudio.cat",
                "driver-catalog",
                SetupPayloadKind.DriverCatalog,
                request.PayloadHashes.DriverCatalogSha256));
            return new ElevatedMachinePayloadSet(
                opened[0],
                opened[1],
                opened[2],
                opened[3],
                ownsPayloads: true);
        }
        catch
        {
            foreach (VerifiedSetupPayload payload in opened.AsEnumerable().Reverse())
            {
                payload.Lease.Dispose();
            }
            throw;
        }
    }

    private static SafeFileHandle OpenRoot(SetupExtractionRootIdentity identity)
    {
        SafeFileHandle root = CreateFile(
            identity.FullPath,
            FileReadAttributes,
            FileShare.Read,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            nint.Zero);
        if (root.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            root.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            ByHandleFileInformation information = ReadInformation(root);
            if (information.VolumeSerialNumber != identity.VolumeSerialNumber
                || information.FileIndexHigh != identity.FileIndexHigh
                || information.FileIndexLow != identity.FileIndexLow
                || information.FileAttributes != identity.FileAttributes
                || (information.FileAttributes & FileAttributeDirectory) == 0
                || (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The extraction root identity changed before elevation.");
            }
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private static VerifiedSetupPayload OpenPayload(
        SafeFileHandle root,
        string finalRoot,
        string displayRoot,
        string fileName,
        string logicalName,
        SetupPayloadKind kind,
        string expectedSha256)
    {
        _ = root;
        string displayPath = Path.Combine(displayRoot, fileName);
        SafeFileHandle handle = CreateFile(
            displayPath,
            GenericRead | FileReadAttributes,
            FileShare.Read,
            nint.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            nint.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            ByHandleFileInformation information = ReadInformation(handle);
            if ((information.FileAttributes & (
                    FileAttributeDirectory | FileAttributeReparsePoint)) != 0
                || information.NumberOfLinks != 1)
            {
                throw new InvalidDataException(
                    "An elevated payload is not one regular single-link file.");
            }
            string finalPath = GetFinalPath(handle);
            string expectedFinal = string.Concat(finalRoot, "\\", fileName);
            if (!string.Equals(
                    finalPath,
                    expectedFinal,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "An elevated payload resolved outside the verified root.");
            }

            long length = checked(
                ((long)information.FileSizeHigh << 32)
                | information.FileSizeLow);
            if (length <= 0)
            {
                throw new InvalidDataException(
                    "An elevated payload has an invalid length.");
            }
            string actualSha256 = HashHandle(handle, length);
            if (!string.Equals(
                    actualSha256,
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "An elevated payload hash changed after verification.");
            }

            SetupPayload manifestPayload = new(
                logicalName,
                fileName,
                length,
                expectedSha256,
                kind);
            VerifiedPayloadLease lease = new(handle, logicalName, displayPath);
            return new VerifiedSetupPayload(
                manifestPayload,
                length,
                actualSha256,
                lease);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string HashHandle(SafeFileHandle handle, long length)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            int requested = checked((int)Math.Min(buffer.Length, length - offset));
            int read = RandomAccess.Read(
                handle,
                buffer.AsSpan(0, requested),
                offset);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    "An elevated payload ended before its verified length.");
            }
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        if (RandomAccess.Read(handle, buffer.AsSpan(0, 1), offset) != 0)
        {
            throw new InvalidDataException(
                "An elevated payload exceeds its verified length.");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ByHandleFileInformation ReadInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return info;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[MaximumFinalPathCharacters];
        uint length = GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Length),
            VolumeNameDos);
        if (length == 0 || length >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        string path = new(buffer, 0, checked((int)length));
        return path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Elevated payload reopening requires Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandle",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathCharacters,
        uint flags);
}
