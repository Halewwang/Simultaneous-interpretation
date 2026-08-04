using EMKE.Platform.Driver;

namespace EMKE.Setup.Platform;

internal sealed record DriverInstallContract
{
    private const string FrozenHardwareId = "ROOT\\EMKEVIRTUALAUDIO";
    private static readonly Version FrozenVersion = new(1, 0, 0, 2);

    public DriverInstallContract(
        string hardwareId,
        Version version,
        string infSha256,
        string sysSha256,
        string catalogSha256)
    {
        if (!string.Equals(hardwareId, FrozenHardwareId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The driver hardware ID does not match the frozen contract.",
                nameof(hardwareId));
        }
        ArgumentNullException.ThrowIfNull(version);
        ArgumentOutOfRangeException.ThrowIfNotEqual(version, FrozenVersion);
        ValidateSha256(infSha256, nameof(infSha256));
        ValidateSha256(sysSha256, nameof(sysSha256));
        ValidateSha256(catalogSha256, nameof(catalogSha256));
        HardwareId = hardwareId;
        Version = version;
        InfSha256 = infSha256;
        SysSha256 = sysSha256;
        CatalogSha256 = catalogSha256;
    }

    public string HardwareId { get; }

    public Version Version { get; }

    public string InfSha256 { get; }

    public string SysSha256 { get; }

    public string CatalogSha256 { get; }

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64
            || value.Any(character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The driver hash must be canonical lowercase SHA-256.",
                parameterName);
        }
    }
}

internal sealed record DriverPackageState(
    bool Present,
    string? PublishedInfName,
    string? HardwareId,
    Version Version,
    string? CatalogSha256,
    string? SignerSubject,
    bool KernelTrustValid)
{
    public static DriverPackageState Missing { get; } = new(
        Present: false,
        PublishedInfName: null,
        HardwareId: null,
        new Version(0, 0, 0, 0),
        CatalogSha256: null,
        SignerSubject: null,
        KernelTrustValid: false);
}

internal sealed record DriverDeviceState(
    bool Present,
    string? DeviceInstanceId,
    string? HardwareId,
    string? PublishedInfName,
    Version Version,
    string? CatalogSha256)
{
    public static DriverDeviceState Missing { get; } = new(
        Present: false,
        DeviceInstanceId: null,
        HardwareId: null,
        PublishedInfName: null,
        new Version(0, 0, 0, 0),
        CatalogSha256: null);
}

internal sealed record DriverMachineState(
    DriverPackageState Package,
    DriverDeviceState Device)
{
    public static DriverMachineState Missing { get; } = new(
        DriverPackageState.Missing,
        DriverDeviceState.Missing);
}

internal sealed record DriverPayloadTrustEvidence(
    bool KernelPolicyValid,
    bool CatalogEntriesMatch,
    bool MemberTrustValid,
    bool Allowed);

internal interface IDriverPayloadTrustVerifier
{
    DriverPayloadTrustEvidence Verify(
        VerifiedSetupPayload catalog,
        VerifiedSetupPayload inf,
        VerifiedSetupPayload sys);
}

internal sealed record DriverNativeInstallResult(
    bool Succeeded,
    bool RebootRequired,
    string? PublishedInfName,
    string? DeviceInstanceId,
    string? FailureCode);

internal interface IDriverSetupApi
{
    DriverMachineState ReadState(string hardwareId);

    DriverNativeInstallResult Install(
        VerifiedSetupPayload inf,
        string hardwareId);

    bool RemoveDevice(string deviceInstanceId);

    bool RemovePackage(string publishedInfName);
}

internal enum DriverInstallOutcome
{
    Succeeded,
    RebootRequired,
    Blocked,
    Failed,
}

internal sealed record DriverInstallReceipt(
    DriverPackageState Package,
    DriverDeviceState Device,
    bool PackageCreatedByAttempt,
    bool DeviceCreatedByAttempt);

internal sealed record DriverInstallResult(
    DriverInstallOutcome Outcome,
    DriverInstallReceipt? Receipt,
    string? FailureCode);

internal sealed record DriverRollbackResult(
    bool Succeeded,
    string? FailureCode);

internal interface IDriverMachineInstaller
{
    DriverInstallResult Install(
        VerifiedSetupPayload inf,
        VerifiedSetupPayload sys,
        VerifiedSetupPayload catalog,
        DriverInstallContract contract,
        Guid transactionId);

    DriverRollbackResult Rollback(
        DriverInstallReceipt receipt,
        Guid transactionId);
}

internal sealed class DriverInstaller : IDriverMachineInstaller
{
    private readonly IDriverSetupApi _api;
    private readonly IDriverPayloadTrustVerifier _payloadTrustVerifier;
    private readonly IDriverCatalogTrustPolicy _installedTrustPolicy;
    private readonly ISetupRecoveryRecordWriter _recoveryWriter;

    public DriverInstaller()
        : this(
            WindowsDriverSetupApi.Instance,
            WindowsDriverPayloadTrustVerifier.Instance,
            MicrosoftDriverCatalogTrustPolicy.Instance,
            WindowsSetupRecoveryRecordWriter.Instance)
    {
    }

    internal DriverInstaller(
        IDriverSetupApi api,
        IDriverPayloadTrustVerifier payloadTrustVerifier,
        IDriverCatalogTrustPolicy installedTrustPolicy,
        ISetupRecoveryRecordWriter recoveryWriter)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _payloadTrustVerifier = payloadTrustVerifier
            ?? throw new ArgumentNullException(nameof(payloadTrustVerifier));
        _installedTrustPolicy = installedTrustPolicy
            ?? throw new ArgumentNullException(nameof(installedTrustPolicy));
        _recoveryWriter = recoveryWriter
            ?? throw new ArgumentNullException(nameof(recoveryWriter));
    }

#pragma warning disable CA1031 // Native machine mutation errors must fail closed.
    public DriverInstallResult Install(
        VerifiedSetupPayload inf,
        VerifiedSetupPayload sys,
        VerifiedSetupPayload catalog,
        DriverInstallContract contract,
        Guid transactionId)
    {
        ArgumentNullException.ThrowIfNull(inf);
        ArgumentNullException.ThrowIfNull(sys);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(contract);
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The transaction ID must not be empty.",
                nameof(transactionId));
        }
        if (!PayloadMatches(inf, SetupPayloadKind.DriverInf, contract.InfSha256)
            || !PayloadMatches(sys, SetupPayloadKind.DriverSys, contract.SysSha256)
            || !PayloadMatches(
                catalog,
                SetupPayloadKind.DriverCatalog,
                contract.CatalogSha256))
        {
            return Blocked("driverPayloadMismatch");
        }

        DriverMachineState? before = null;
        try
        {
            DriverPayloadTrustEvidence trust = _payloadTrustVerifier.Verify(
                catalog,
                inf,
                sys);
            if (!trust.KernelPolicyValid
                || !trust.CatalogEntriesMatch
                || !trust.MemberTrustValid
                || !trust.Allowed)
            {
                return Blocked("driverCatalogRejected");
            }

            before = _api.ReadState(contract.HardwareId);
            if (!ExistingStateCompatible(before, contract))
            {
                return Blocked("incompatibleDriverPresent");
            }
            if (before.Package.Present && before.Device.Present)
            {
                return Completed(
                    DriverInstallOutcome.Succeeded,
                    new DriverInstallReceipt(
                        before.Package,
                        before.Device,
                        PackageCreatedByAttempt: false,
                        DeviceCreatedByAttempt: false));
            }

            DriverNativeInstallResult native = _api.Install(
                inf,
                contract.HardwareId);
            DriverMachineState after = _api.ReadState(contract.HardwareId);
            DriverInstallReceipt? receipt = BuildReceipt(before, after);
            if (!native.Succeeded)
            {
                return new DriverInstallResult(
                    DriverInstallOutcome.Failed,
                    receipt,
                    native.FailureCode ?? "driverInstallFailed");
            }
            if (!ExactState(after, contract) || receipt is null)
            {
                return new DriverInstallResult(
                    DriverInstallOutcome.Failed,
                    receipt,
                    "driverPostInstallMismatch");
            }
            return Completed(
                native.RebootRequired
                    ? DriverInstallOutcome.RebootRequired
                    : DriverInstallOutcome.Succeeded,
                receipt);
        }
        catch (Exception)
        {
            DriverInstallReceipt? receipt = null;
            if (before is not null)
            {
                try
                {
                    receipt = BuildReceipt(
                        before,
                        _api.ReadState(contract.HardwareId));
                }
                catch (Exception)
                {
                    receipt = null;
                }
            }
            return new DriverInstallResult(
                DriverInstallOutcome.Failed,
                receipt,
                "driverInstallFailed");
        }
    }

    public DriverRollbackResult Rollback(
        DriverInstallReceipt receipt,
        Guid transactionId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The transaction ID must not be empty.",
                nameof(transactionId));
        }
        if (!receipt.DeviceCreatedByAttempt && !receipt.PackageCreatedByAttempt)
        {
            return new DriverRollbackResult(Succeeded: true, FailureCode: null);
        }

        try
        {
            DriverMachineState current = _api.ReadState(
                receipt.Package.HardwareId
                    ?? receipt.Device.HardwareId
                    ?? throw new InvalidDataException(
                        "The driver receipt has no hardware identity."));
            if (receipt.DeviceCreatedByAttempt && current.Device.Present)
            {
                if (!SameDevice(current.Device, receipt.Device))
                {
                    return RecoveryRequired(
                        transactionId,
                        "driverRollbackIdentityChanged",
                        receipt);
                }
                if (string.IsNullOrWhiteSpace(receipt.Device.DeviceInstanceId)
                    || !_api.RemoveDevice(receipt.Device.DeviceInstanceId))
                {
                    return RecoveryRequired(
                        transactionId,
                        "driverRollbackFailed",
                        receipt);
                }
                current = _api.ReadState(receipt.Device.HardwareId!);
                if (current.Device.Present)
                {
                    return RecoveryRequired(
                        transactionId,
                        "driverRollbackFailed",
                        receipt);
                }
            }

            if (receipt.PackageCreatedByAttempt && current.Package.Present)
            {
                if (!SamePackage(current.Package, receipt.Package)
                    || current.Device.Present)
                {
                    return RecoveryRequired(
                        transactionId,
                        "driverRollbackIdentityChanged",
                        receipt);
                }
                if (string.IsNullOrWhiteSpace(receipt.Package.PublishedInfName)
                    || !_api.RemovePackage(receipt.Package.PublishedInfName))
                {
                    return RecoveryRequired(
                        transactionId,
                        "driverRollbackFailed",
                        receipt);
                }
                current = _api.ReadState(receipt.Package.HardwareId!);
                if (current.Package.Present)
                {
                    return RecoveryRequired(
                        transactionId,
                        "driverRollbackFailed",
                        receipt);
                }
            }

            return new DriverRollbackResult(Succeeded: true, FailureCode: null);
        }
        catch (Exception)
        {
            return RecoveryRequired(
                transactionId,
                "driverRollbackFailed",
                receipt);
        }
    }
#pragma warning restore CA1031

    private bool ExistingStateCompatible(
        DriverMachineState state,
        DriverInstallContract contract)
    {
        if (state.Package.Present && !PackageMatches(state.Package, contract))
        {
            return false;
        }
        if (state.Device.Present && !DeviceMatches(state.Device, contract))
        {
            return false;
        }
        return !state.Device.Present || state.Package.Present;
    }

    private bool ExactState(
        DriverMachineState state,
        DriverInstallContract contract) =>
        state.Package.Present
        && state.Device.Present
        && PackageMatches(state.Package, contract)
        && DeviceMatches(state.Device, contract)
        && string.Equals(
            state.Package.PublishedInfName,
            state.Device.PublishedInfName,
            StringComparison.OrdinalIgnoreCase);

    private bool PackageMatches(
        DriverPackageState package,
        DriverInstallContract contract)
    {
        if (!package.Present
            || string.IsNullOrWhiteSpace(package.PublishedInfName)
            || string.IsNullOrWhiteSpace(package.SignerSubject)
            || !string.Equals(
                package.HardwareId,
                contract.HardwareId,
                StringComparison.OrdinalIgnoreCase)
            || package.Version != contract.Version
            || !string.Equals(
                package.CatalogSha256,
                contract.CatalogSha256,
                StringComparison.Ordinal))
        {
            return false;
        }
        DriverCatalogTrustDecision decision = _installedTrustPolicy.Evaluate(
            package.SignerSubject,
            package.KernelTrustValid,
            catalogMembersValid: true);
        return decision.Allowed;
    }

    private static bool DeviceMatches(
        DriverDeviceState device,
        DriverInstallContract contract) =>
        device.Present
        && !string.IsNullOrWhiteSpace(device.DeviceInstanceId)
        && !string.IsNullOrWhiteSpace(device.PublishedInfName)
        && string.Equals(
            device.HardwareId,
            contract.HardwareId,
            StringComparison.OrdinalIgnoreCase)
        && device.Version == contract.Version
        && string.Equals(
            device.CatalogSha256,
            contract.CatalogSha256,
            StringComparison.Ordinal);

    private static bool SamePackage(
        DriverPackageState current,
        DriverPackageState receipt) =>
        current.Present
        && receipt.Present
        && string.Equals(
            current.PublishedInfName,
            receipt.PublishedInfName,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            current.HardwareId,
            receipt.HardwareId,
            StringComparison.OrdinalIgnoreCase)
        && current.Version == receipt.Version
        && string.Equals(
            current.CatalogSha256,
            receipt.CatalogSha256,
            StringComparison.Ordinal)
        && string.Equals(
            current.SignerSubject,
            receipt.SignerSubject,
            StringComparison.Ordinal)
        && current.KernelTrustValid == receipt.KernelTrustValid;

    private static bool SameDevice(
        DriverDeviceState current,
        DriverDeviceState receipt) =>
        current.Present
        && receipt.Present
        && string.Equals(
            current.DeviceInstanceId,
            receipt.DeviceInstanceId,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            current.HardwareId,
            receipt.HardwareId,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            current.PublishedInfName,
            receipt.PublishedInfName,
            StringComparison.OrdinalIgnoreCase)
        && current.Version == receipt.Version
        && string.Equals(
            current.CatalogSha256,
            receipt.CatalogSha256,
            StringComparison.Ordinal);

    private static bool PayloadMatches(
        VerifiedSetupPayload payload,
        SetupPayloadKind kind,
        string expectedSha256) =>
        payload.ManifestPayload.Kind == kind
        && string.Equals(payload.Sha256, expectedSha256, StringComparison.Ordinal)
        && string.Equals(
            payload.ManifestPayload.Sha256,
            expectedSha256,
            StringComparison.Ordinal);

    private static DriverInstallReceipt? BuildReceipt(
        DriverMachineState before,
        DriverMachineState after)
    {
        if (!after.Package.Present && !after.Device.Present)
        {
            return null;
        }
        return new DriverInstallReceipt(
            after.Package,
            after.Device,
            PackageCreatedByAttempt: !before.Package.Present,
            DeviceCreatedByAttempt: !before.Device.Present);
    }

    private static DriverInstallResult Completed(
        DriverInstallOutcome outcome,
        DriverInstallReceipt receipt) => new(outcome, receipt, FailureCode: null);

    private static DriverInstallResult Blocked(string failureCode) => new(
        DriverInstallOutcome.Blocked,
        Receipt: null,
        failureCode);

#pragma warning disable CA1031 // Recovery writer failure must remain a closed result.
    private DriverRollbackResult RecoveryRequired(
        Guid transactionId,
        string failureCode,
        DriverInstallReceipt receipt)
    {
        string expectedIdentity = string.Join(
            '|',
            receipt.Package.PublishedInfName,
            receipt.Package.HardwareId,
            receipt.Package.Version,
            receipt.Package.CatalogSha256);
        try
        {
            _recoveryWriter.Write(new SetupRecoveryRecord(
                transactionId,
                "driver",
                failureCode,
                expectedIdentity,
                DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
            return new DriverRollbackResult(
                Succeeded: false,
                FailureCode: "driverRecoveryRecordFailed");
        }
        return new DriverRollbackResult(Succeeded: false, failureCode);
    }
#pragma warning restore CA1031
}

internal sealed class WindowsDriverPayloadTrustVerifier
    : IDriverPayloadTrustVerifier
{
    public static WindowsDriverPayloadTrustVerifier Instance { get; } = new();

    private WindowsDriverPayloadTrustVerifier()
    {
    }

    public DriverPayloadTrustEvidence Verify(
        VerifiedSetupPayload catalog,
        VerifiedSetupPayload inf,
        VerifiedSetupPayload sys)
    {
        SetupDriverCatalogEvidence evidence = WindowsSetupSignatureProbe.Instance
            .VerifyDriverCatalog(catalog, inf, sys);
        return new DriverPayloadTrustEvidence(
            evidence.KernelPolicyValid,
            evidence.CatalogEntriesMatch,
            evidence.MemberTrustValid,
            evidence.Allowed);
    }
}

internal sealed class WindowsDriverSetupApi : IDriverSetupApi
{
    public static WindowsDriverSetupApi Instance { get; } = new();

    private WindowsDriverSetupApi()
    {
    }

    public DriverMachineState ReadState(string hardwareId) =>
        SetupApiNativeMethods.ReadDriverState(hardwareId);

    public DriverNativeInstallResult Install(
        VerifiedSetupPayload inf,
        string hardwareId) => SetupApiNativeMethods.InstallRootDriver(
            inf,
            hardwareId);

    public bool RemoveDevice(string deviceInstanceId) =>
        SetupApiNativeMethods.RemoveDevice(deviceInstanceId);

    public bool RemovePackage(string publishedInfName) =>
        SetupApiNativeMethods.RemoveDriverPackage(publishedInfName);
}
