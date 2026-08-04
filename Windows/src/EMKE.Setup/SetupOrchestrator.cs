using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EMKE.Setup.Elevated;
using EMKE.Setup.Platform;

namespace EMKE.Setup;

internal sealed record SetupMachineCreatedState(
    bool CertificateCreated,
    bool DriverPackageCreated,
    bool DriverDeviceCreated);

internal sealed record SetupMachineChangeReceipt(
    SetupMachineCreatedState CreatedState);

internal enum SetupMachineChangeOutcome
{
    Succeeded,
    RebootRequired,
    Cancelled,
    Failed,
}

internal sealed record SetupMachineChangeResult
{
    private SetupMachineChangeResult(
        SetupMachineChangeOutcome outcome,
        SetupMachineChangeReceipt? receipt,
        string? failureCode)
    {
        Outcome = outcome;
        Receipt = receipt;
        FailureCode = failureCode;
    }

    public SetupMachineChangeOutcome Outcome { get; }

    public SetupMachineChangeReceipt? Receipt { get; }

    public string? FailureCode { get; }

    public static SetupMachineChangeResult Succeeded(
        SetupMachineChangeReceipt receipt) => new(
            SetupMachineChangeOutcome.Succeeded,
            receipt ?? throw new ArgumentNullException(nameof(receipt)),
            null);

    public static SetupMachineChangeResult RebootRequired(
        SetupMachineChangeReceipt receipt) => new(
            SetupMachineChangeOutcome.RebootRequired,
            receipt ?? throw new ArgumentNullException(nameof(receipt)),
            null);

    public static SetupMachineChangeResult Failed(string failureCode) => new(
        SetupMachineChangeOutcome.Failed,
        null,
        string.IsNullOrWhiteSpace(failureCode)
            ? throw new ArgumentException(
                "A machine failure code is required.",
                nameof(failureCode))
            : failureCode);
}

internal interface ISetupMachineChangeCoordinator
{
    Task<SetupMachineChangeResult> ApplyAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken);

    Task<bool> RollbackAsync(
        SetupMachineChangeReceipt receipt,
        Guid transactionId,
        CancellationToken cancellationToken);

    Task<bool> VerifyResumeAsync(
        SetupMachineChangeReceipt receipt,
        SetupElevationRequest request,
        CancellationToken cancellationToken);
}

internal enum SetupApplicationLaunchMode
{
    ControlledNoTranslationConnect,
}

internal interface ISetupApplicationLauncher
{
    Task LaunchAsync(
        SetupApplicationLaunchMode mode,
        CancellationToken cancellationToken);
}

internal sealed record SetupOrchestrationRequest
{
    public SetupOrchestrationRequest(
        SetupElevationRequest elevationRequest,
        VerifiedSetupPayload msix,
        PackageInstallContract packageContract,
        string invokingSid)
    {
        ElevationRequest = elevationRequest
            ?? throw new ArgumentNullException(nameof(elevationRequest));
        Msix = msix ?? throw new ArgumentNullException(nameof(msix));
        PackageContract = packageContract
            ?? throw new ArgumentNullException(nameof(packageContract));
        ArgumentException.ThrowIfNullOrWhiteSpace(invokingSid);
        InvokingSid = invokingSid;
    }

    public SetupElevationRequest ElevationRequest { get; }

    public VerifiedSetupPayload Msix { get; }

    public PackageInstallContract PackageContract { get; }

    public string InvokingSid { get; }
}

internal sealed record SetupResumeRecord
{
    private static readonly byte[] AdditionalEntropy =
        Encoding.UTF8.GetBytes("EMKE.Setup.Resume.v1");

    private SetupResumeRecord(
        Guid transactionId,
        SetupMachineCreatedState machineCreatedState,
        SetupElevationPayloadHashes payloadHashes,
        string nextStep,
        string canonicalPayload,
        string authenticator)
    {
        TransactionId = transactionId;
        MachineCreatedState = machineCreatedState;
        PayloadHashes = payloadHashes;
        NextStep = nextStep;
        CanonicalPayload = canonicalPayload;
        Authenticator = authenticator;
    }

    public Guid TransactionId { get; }

    public SetupMachineCreatedState MachineCreatedState { get; }

    public SetupElevationPayloadHashes PayloadHashes { get; }

    public string NextStep { get; }

    public string CanonicalPayload { get; }

    public string Authenticator { get; }

    public static SetupResumeRecord Create(
        Guid transactionId,
        SetupMachineCreatedState createdState,
        SetupElevationPayloadHashes hashes)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The transaction ID must not be empty.",
                nameof(transactionId));
        }
        ArgumentNullException.ThrowIfNull(createdState);
        ArgumentNullException.ThrowIfNull(hashes);
        const string nextStep = "verifyMachineAndInstallUserPackage";
        string canonical = Canonicalize(
            transactionId,
            createdState,
            hashes,
            nextStep);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        byte[] protectedDigest = WindowsDpapi.Protect(
            digest,
            AdditionalEntropy);
        CryptographicOperations.ZeroMemory(digest);
        return new SetupResumeRecord(
            transactionId,
            createdState,
            hashes,
            nextStep,
            canonical,
            Convert.ToBase64String(protectedDigest));
    }

    public bool VerifyAuthenticator()
    {
        return VerifyProtectedDigest(CanonicalPayload, Authenticator)
            && string.Equals(
                CanonicalPayload,
                Canonicalize(
                    TransactionId,
                    MachineCreatedState,
                    PayloadHashes,
                    NextStep),
                StringComparison.Ordinal);
    }

    public static SetupResumeRecord ParseAndVerify(
        string canonicalPayload,
        string authenticator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticator);
        if (!VerifyProtectedDigest(canonicalPayload, authenticator))
        {
            throw new InvalidDataException(
                "The Setup recovery authenticator is invalid.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(canonicalPayload);
            JsonElement root = document.RootElement;
            RequireProperties(
                root,
                "version",
                "transactionId",
                "machineCreatedState",
                "payloadHashes",
                "nextStep");
            if (root.GetProperty("version").GetInt32() != 1)
            {
                throw new InvalidDataException(
                    "The Setup recovery version is unsupported.");
            }
            Guid transactionId = Guid.ParseExact(
                RequiredString(root, "transactionId"),
                "D");
            JsonElement created = root.GetProperty("machineCreatedState");
            RequireProperties(
                created,
                "certificateCreated",
                "driverPackageCreated",
                "driverDeviceCreated");
            SetupMachineCreatedState state = new(
                created.GetProperty("certificateCreated").GetBoolean(),
                created.GetProperty("driverPackageCreated").GetBoolean(),
                created.GetProperty("driverDeviceCreated").GetBoolean());
            JsonElement hashes = root.GetProperty("payloadHashes");
            RequireProperties(
                hashes,
                "msix",
                "certificate",
                "driverInf",
                "driverSys",
                "driverCatalog");
            SetupElevationPayloadHashes payloadHashes = new(
                RequiredString(hashes, "msix"),
                RequiredString(hashes, "certificate"),
                RequiredString(hashes, "driverInf"),
                RequiredString(hashes, "driverSys"),
                RequiredString(hashes, "driverCatalog"));
            string nextStep = RequiredString(root, "nextStep");
            if (!string.Equals(
                    nextStep,
                    "verifyMachineAndInstallUserPackage",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Setup recovery next step is invalid.");
            }
            string expectedCanonical = Canonicalize(
                transactionId,
                state,
                payloadHashes,
                nextStep);
            if (!string.Equals(
                    canonicalPayload,
                    expectedCanonical,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Setup recovery payload is not canonical.");
            }
            return new SetupResumeRecord(
                transactionId,
                state,
                payloadHashes,
                nextStep,
                canonicalPayload,
                authenticator);
        }
        catch (Exception exception) when (exception is
            JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException(
                "The Setup recovery payload is invalid.",
                exception);
        }
    }

    private static string Canonicalize(
        Guid transactionId,
        SetupMachineCreatedState createdState,
        SetupElevationPayloadHashes hashes,
        string nextStep) => JsonSerializer.Serialize(new
        {
            version = 1,
            transactionId = transactionId.ToString("D"),
            machineCreatedState = new
            {
                certificateCreated = createdState.CertificateCreated,
                driverPackageCreated = createdState.DriverPackageCreated,
                driverDeviceCreated = createdState.DriverDeviceCreated,
            },
            payloadHashes = new
            {
                msix = hashes.MsixSha256,
                certificate = hashes.CertificateSha256,
                driverInf = hashes.DriverInfSha256,
                driverSys = hashes.DriverSysSha256,
                driverCatalog = hashes.DriverCatalogSha256,
            },
            nextStep,
        });

    private static bool VerifyProtectedDigest(
        string canonicalPayload,
        string authenticator)
    {
        byte[] expected = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalPayload));
        byte[]? actual = null;
        try
        {
            actual = WindowsDpapi.Unprotect(
                Convert.FromBase64String(authenticator),
                AdditionalEntropy);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception exception) when (exception is
            CryptographicException or FormatException or Win32Exception)
        {
            return false;
        }
        finally
        {
            if (actual is not null)
            {
                CryptographicOperations.ZeroMemory(actual);
            }
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                "A Setup recovery string field is invalid.");
        }
        return value.GetString()!;
    }

    private static void RequireProperties(
        JsonElement element,
        params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A Setup recovery object field is invalid.");
        }
        string[] actual = element
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        if (actual.Length != expected.Length
            || !actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The Setup recovery field inventory is invalid.");
        }
    }
}

internal interface ISetupResumeRecordStore
{
    void Write(SetupResumeRecord record);

    SetupResumeRecord ReadVerified(Guid transactionId);
}

internal sealed class WindowsSetupResumeRecordStore : ISetupResumeRecordStore
{
    private readonly string _root;

    public WindowsSetupResumeRecordStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EMKE",
            "Setup",
            "resume"))
    {
    }

    internal WindowsSetupResumeRecordStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public void Write(SetupResumeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.VerifyAuthenticator())
        {
            throw new InvalidDataException(
                "An invalid Setup recovery record cannot be persisted.");
        }
        _ = Directory.CreateDirectory(_root);
        string destination = Path.Combine(
            _root,
            string.Concat(record.TransactionId.ToString("N"), ".json"));
        string temporary = string.Concat(
            destination,
            ".",
            Guid.NewGuid().ToString("N"),
            ".tmp");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            canonicalPayload = record.CanonicalPayload,
            authenticator = record.Authenticator,
        });
        try
        {
            using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public SetupResumeRecord ReadVerified(Guid transactionId)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The transaction ID must not be empty.",
                nameof(transactionId));
        }
        string path = Path.Combine(
            _root,
            string.Concat(transactionId.ToString("N"), ".json"));
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        JsonProperty[] properties = root.EnumerateObject().ToArray();
        if (properties.Length != 2
            || !string.Equals(
                properties[0].Name,
                "canonicalPayload",
                StringComparison.Ordinal)
            || !string.Equals(
                properties[1].Name,
                "authenticator",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Setup recovery envelope is invalid.");
        }
        SetupResumeRecord record = SetupResumeRecord.ParseAndVerify(
            properties[0].Value.GetString()
                ?? throw new InvalidDataException(
                    "The Setup recovery payload is missing."),
            properties[1].Value.GetString()
                ?? throw new InvalidDataException(
                    "The Setup recovery authenticator is missing."));
        if (record.TransactionId != transactionId)
        {
            throw new InvalidDataException(
                "The Setup recovery transaction identity changed.");
        }
        return record;
    }
}

internal sealed class SetupOrchestrator
{
    private readonly ISetupMachineChangeCoordinator _machine;
    private readonly IUserPackageInstaller _packageInstaller;
    private readonly IEndpointReadinessVerifier _endpointVerifier;
    private readonly ISetupApplicationLauncher _launcher;
    private readonly ISetupResumeRecordStore _resumeRecords;

    internal SetupOrchestrator(
        ISetupMachineChangeCoordinator machine,
        IUserPackageInstaller packageInstaller,
        IEndpointReadinessVerifier endpointVerifier,
        ISetupApplicationLauncher launcher,
        ISetupResumeRecordStore resumeRecords)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _packageInstaller = packageInstaller
            ?? throw new ArgumentNullException(nameof(packageInstaller));
        _endpointVerifier = endpointVerifier
            ?? throw new ArgumentNullException(nameof(endpointVerifier));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _resumeRecords = resumeRecords
            ?? throw new ArgumentNullException(nameof(resumeRecords));
    }

#pragma warning disable CA1031 // The orchestration boundary converts failures after rollback.
    public async Task<SetupResult> ExecuteAsync(
        SetupOrchestrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SetupStateMachine state = new();
        state.AdvanceTo(SetupState.Verified);
        state.AdvanceTo(SetupState.MachineChangesStarted);

        SetupMachineChangeResult machine = await _machine.ApplyAsync(
                request.ElevationRequest,
                cancellationToken)
            .ConfigureAwait(false);
        if (machine.Outcome == SetupMachineChangeOutcome.RebootRequired)
        {
            if (machine.Receipt is null)
            {
                return SetupResult.Failed(
                    state.State,
                    "machineReceiptMissing");
            }
            _resumeRecords.Write(SetupResumeRecord.Create(
                request.ElevationRequest.TransactionId,
                machine.Receipt.CreatedState,
                request.ElevationRequest.PayloadHashes));
            return SetupResult.RebootRequired(state.State);
        }
        if (machine.Outcome != SetupMachineChangeOutcome.Succeeded
            || machine.Receipt is null)
        {
            _ = state.Cancel(resumableRebootRequired: false);
            return SetupResult.Failed(
                state.State,
                machine.FailureCode ?? "machineChangesFailed");
        }
        state.AdvanceTo(SetupState.DriverReady);
        return await CompleteUserPhaseAsync(
                request,
                state,
                machine.Receipt,
                cancellationToken)
            .ConfigureAwait(false);
    }
#pragma warning restore CA1031

#pragma warning disable CA1031 // Recovery failures are closed before user mutation.
    public async Task<SetupResult> ResumeAsync(
        SetupOrchestrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SetupStateMachine state = new();
        try
        {
            SetupResumeRecord recovery = _resumeRecords.ReadVerified(
                request.ElevationRequest.TransactionId);
            if (!recovery.VerifyAuthenticator()
                || recovery.TransactionId
                    != request.ElevationRequest.TransactionId
                || !HashesMatch(
                    recovery.PayloadHashes,
                    request.ElevationRequest.PayloadHashes)
                || !string.Equals(
                    request.Msix.Sha256,
                    recovery.PayloadHashes.MsixSha256,
                    StringComparison.Ordinal))
            {
                return SetupResult.Failed(
                    state.State,
                    "recoveryRecordRejected");
            }

            SetupMachineChangeReceipt receipt = new(
                recovery.MachineCreatedState);
            bool machineVerified = await _machine.VerifyResumeAsync(
                    receipt,
                    request.ElevationRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!machineVerified)
            {
                return SetupResult.Failed(
                    state.State,
                    "recoveryMachineStateMismatch");
            }

            state.AdvanceTo(SetupState.Verified);
            state.AdvanceTo(SetupState.MachineChangesStarted);
            state.AdvanceTo(SetupState.DriverReady);
            return await CompleteUserPhaseAsync(
                    request,
                    state,
                    receipt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return SetupResult.Failed(
                state.State,
                "recoveryRecordRejected");
        }
    }
#pragma warning restore CA1031

#pragma warning disable CA1031 // The user phase rolls back all prepared mutations.
    private async Task<SetupResult> CompleteUserPhaseAsync(
        SetupOrchestrationRequest request,
        SetupStateMachine state,
        SetupMachineChangeReceipt machineReceipt,
        CancellationToken cancellationToken)
    {
        PackageInstallReceipt? packageReceipt = null;
        try
        {
            PackageInstallResult package = await _packageInstaller.InstallAsync(
                    request.Msix,
                    request.PackageContract,
                    request.InvokingSid,
                    cancellationToken)
                .ConfigureAwait(false);
            packageReceipt = package.Receipt;
            if (package.Outcome != PackageInstallOutcome.Succeeded
                || package.Receipt is null)
            {
                await RollbackAsync(
                    package.Receipt,
                    machineReceipt,
                    request.ElevationRequest.TransactionId,
                    cancellationToken).ConfigureAwait(false);
                _ = state.Cancel(resumableRebootRequired: false);
                return SetupResult.Failed(
                    state.State,
                    package.FailureCode ?? "packageInstallFailed");
            }
            state.AdvanceTo(SetupState.UserPackageReady);

            EndpointVerificationResult endpoints =
                await _endpointVerifier.VerifyAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!endpoints.Ready || !endpoints.LaunchAllowed)
            {
                await RollbackAsync(
                    package.Receipt,
                    machineReceipt,
                    request.ElevationRequest.TransactionId,
                    cancellationToken).ConfigureAwait(false);
                _ = state.Cancel(resumableRebootRequired: false);
                return SetupResult.Failed(
                    state.State,
                    endpoints.FailureCode ?? "endpointVerificationFailed");
            }
            state.AdvanceTo(SetupState.EndpointVerified);

            await _launcher.LaunchAsync(
                    SetupApplicationLaunchMode.ControlledNoTranslationConnect,
                    cancellationToken)
                .ConfigureAwait(false);
            state.AdvanceTo(SetupState.Complete);
            return SetupResult.Succeeded();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            await RollbackAsync(
                packageReceipt,
                machineReceipt,
                request.ElevationRequest.TransactionId,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await RollbackAsync(
                packageReceipt,
                machineReceipt,
                request.ElevationRequest.TransactionId,
                CancellationToken.None).ConfigureAwait(false);
            _ = state.Cancel(resumableRebootRequired: false);
            return SetupResult.Failed(state.State, "setupOrchestrationFailed");
        }
    }
#pragma warning restore CA1031

    private static bool HashesMatch(
        SetupElevationPayloadHashes left,
        SetupElevationPayloadHashes right) => string.Equals(
            left.MsixSha256,
            right.MsixSha256,
            StringComparison.Ordinal)
        && string.Equals(
            left.CertificateSha256,
            right.CertificateSha256,
            StringComparison.Ordinal)
        && string.Equals(
            left.DriverInfSha256,
            right.DriverInfSha256,
            StringComparison.Ordinal)
        && string.Equals(
            left.DriverSysSha256,
            right.DriverSysSha256,
            StringComparison.Ordinal)
        && string.Equals(
            left.DriverCatalogSha256,
            right.DriverCatalogSha256,
            StringComparison.Ordinal);

    private async Task RollbackAsync(
        PackageInstallReceipt? package,
        SetupMachineChangeReceipt machine,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        if (package is not null)
        {
            _ = await _packageInstaller.RollbackAsync(
                    package,
                    transactionId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        _ = await _machine.RollbackAsync(
                machine,
                transactionId,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

internal static class WindowsDpapi
{
    private const uint CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] plaintext, byte[] entropy) => Transform(
        plaintext,
        entropy,
        protect: true);

    public static byte[] Unprotect(byte[] ciphertext, byte[] entropy) => Transform(
        ciphertext,
        entropy,
        protect: false);

    private static byte[] Transform(
        byte[] input,
        byte[] entropy,
        bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entropy);
        DataBlob inputBlob = Allocate(input);
        DataBlob entropyBlob = Allocate(entropy);
        DataBlob outputBlob = default;
        try
        {
            bool succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    nint.Zero,
                    ref entropyBlob,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            byte[] result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Free(ref inputBlob, localAlloc: false);
            Free(ref entropyBlob, localAlloc: false);
            Free(ref outputBlob, localAlloc: true);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        nint data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        return new DataBlob(bytes.Length, data);
    }

    private static void Free(ref DataBlob blob, bool localAlloc)
    {
        if (blob.Data == nint.Zero)
        {
            return;
        }
        byte[] zero = new byte[blob.Length];
        Marshal.Copy(zero, 0, blob.Data, zero.Length);
        if (localAlloc)
        {
            _ = LocalFree(blob.Data);
        }
        else
        {
            Marshal.FreeHGlobal(blob.Data);
        }
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int length, nint data)
    {
        public int Length = length;
        public nint Data = data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        ref DataBlob entropy,
        nint reserved,
        nint prompt,
        uint flags,
        out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        nint description,
        ref DataBlob entropy,
        nint reserved,
        nint prompt,
        uint flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);
}
