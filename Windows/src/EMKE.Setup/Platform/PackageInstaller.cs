using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace EMKE.Setup.Platform;

internal sealed record PackagePayloadIdentity(
    string Name,
    string Publisher,
    Version Version,
    Architecture Architecture,
    bool SignatureValid);

internal sealed record InstalledUserPackage(
    string Name,
    string FamilyName,
    string FullName,
    string Publisher,
    Version Version,
    Architecture Architecture,
    string InstallLocation,
    string UserSid,
    bool InstallLocationTrusted,
    bool SignatureValid);

internal sealed record PackageInstallContract
{
    private const string FrozenName = "EMKE.Translation.Internal";
    private const string FrozenFamily =
        "EMKE.Translation.Internal_kvab4te83cr7p";
    private const string FrozenFullName =
        "EMKE.Translation.Internal_0.2.0.0_x64__kvab4te83cr7p";
    private const string FrozenPublisher = "CN=EMKE Internal Test";
    private static readonly Version FrozenVersion = new(0, 2, 0, 0);

    public PackageInstallContract(
        string name,
        string familyName,
        string fullName,
        string publisher,
        Version version,
        Architecture architecture)
    {
        RequireFrozen(name, FrozenName, nameof(name));
        RequireFrozen(familyName, FrozenFamily, nameof(familyName));
        RequireFrozen(fullName, FrozenFullName, nameof(fullName));
        RequireFrozen(publisher, FrozenPublisher, nameof(publisher));
        ArgumentNullException.ThrowIfNull(version);
        ArgumentOutOfRangeException.ThrowIfNotEqual(version, FrozenVersion);
        if (architecture != Architecture.X64)
        {
            throw new ArgumentOutOfRangeException(nameof(architecture));
        }

        Name = name;
        FamilyName = familyName;
        FullName = fullName;
        Publisher = publisher;
        Version = version;
        Architecture = architecture;
    }

    public string Name { get; }

    public string FamilyName { get; }

    public string FullName { get; }

    public string Publisher { get; }

    public Version Version { get; }

    public Architecture Architecture { get; }

    private static void RequireFrozen(
        string actual,
        string expected,
        string parameterName)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The package identity does not match the frozen contract.",
                parameterName);
        }
    }
}

internal enum PackageInstallOutcome
{
    Succeeded,
    Blocked,
    Failed,
}

internal sealed record PackageInstallReceipt(
    InstalledUserPackage Package,
    bool CreatedByAttempt,
    bool UpgradedByAttempt,
    string? PreviousFullName);

internal sealed record PackageInstallResult(
    PackageInstallOutcome Outcome,
    PackageInstallReceipt? Receipt,
    string? FailureCode);

internal sealed record PackageRollbackResult(
    bool Succeeded,
    bool Removed,
    string? FailureCode);

internal interface IPackageDeploymentApi
{
    bool IsCurrentProcessElevated { get; }

    string CurrentUserSid { get; }

    PackagePayloadIdentity InspectPayload(VerifiedSetupPayload payload);

    IReadOnlyList<InstalledUserPackage> FindPackages(
        string userSid,
        string familyName);

    Task AddPackageAsync(
        VerifiedSetupPayload payload,
        CancellationToken cancellationToken);

    Task RemovePackageAsync(
        string packageFullName,
        string userSid,
        CancellationToken cancellationToken);
}

internal interface IUserPackageInstaller
{
    Task<PackageInstallResult> InstallAsync(
        VerifiedSetupPayload msix,
        PackageInstallContract contract,
        string invokingSid,
        CancellationToken cancellationToken);

    Task<PackageRollbackResult> RollbackAsync(
        PackageInstallReceipt receipt,
        Guid transactionId,
        CancellationToken cancellationToken);
}

internal sealed class PackageInstaller : IUserPackageInstaller
{
    private readonly IPackageDeploymentApi _api;
    private readonly ISetupRecoveryRecordWriter _recoveryWriter;

    public PackageInstaller()
        : this(
            WindowsPackageDeploymentApi.Instance,
            WindowsSetupRecoveryRecordWriter.Instance)
    {
    }

    internal PackageInstaller(
        IPackageDeploymentApi api,
        ISetupRecoveryRecordWriter recoveryWriter)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _recoveryWriter = recoveryWriter
            ?? throw new ArgumentNullException(nameof(recoveryWriter));
    }

#pragma warning disable CA1031 // Deployment failures are closed structured outcomes.
    public async Task<PackageInstallResult> InstallAsync(
        VerifiedSetupPayload msix,
        PackageInstallContract contract,
        string invokingSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(msix);
        ArgumentNullException.ThrowIfNull(contract);
        ValidateSid(invokingSid);
        cancellationToken.ThrowIfCancellationRequested();

        if (_api.IsCurrentProcessElevated)
        {
            return Blocked("packageMustRunUnelevated");
        }
        if (!CurrentSidMatches(invokingSid))
        {
            return Blocked("invokingSidChanged");
        }
        if (msix.ManifestPayload.Kind != SetupPayloadKind.Msix
            || msix.Length != msix.ManifestPayload.Length
            || !string.Equals(
                msix.Sha256,
                msix.ManifestPayload.Sha256,
                StringComparison.Ordinal))
        {
            return Blocked("packagePayloadMismatch");
        }

        try
        {
            PackagePayloadIdentity payload = _api.InspectPayload(msix);
            if (!PayloadMatches(payload, contract))
            {
                return Blocked("packagePayloadIdentityMismatch");
            }

            IReadOnlyList<InstalledUserPackage> existing =
                _api.FindPackages(invokingSid, contract.FamilyName);
            if (existing.Count > 1)
            {
                return Blocked("packageIdentityConflict");
            }

            InstalledUserPackage? before = existing.SingleOrDefault();
            if (before is not null)
            {
                if (!string.Equals(
                        before.Publisher,
                        contract.Publisher,
                        StringComparison.Ordinal))
                {
                    return Blocked("packagePublisherConflict");
                }
                if (InstalledMatches(before, contract, invokingSid))
                {
                    return Succeeded(new PackageInstallReceipt(
                        before,
                        CreatedByAttempt: false,
                        UpgradedByAttempt: false,
                        PreviousFullName: null));
                }
                if (!CanUpgrade(before, contract, invokingSid))
                {
                    return Blocked("packageIdentityConflict");
                }
            }

            if (!CurrentSidMatches(invokingSid) || _api.IsCurrentProcessElevated)
            {
                return Blocked("invokingSidChanged");
            }

            await _api.AddPackageAsync(msix, cancellationToken)
                .ConfigureAwait(false);
            InstalledUserPackage? installed = _api
                .FindPackages(invokingSid, contract.FamilyName)
                .SingleOrDefault(package => string.Equals(
                    package.FullName,
                    contract.FullName,
                    StringComparison.Ordinal));
            PackageInstallReceipt? receipt = installed is null
                ? null
                : new PackageInstallReceipt(
                    installed,
                    CreatedByAttempt: before is null,
                    UpgradedByAttempt: before is not null,
                    PreviousFullName: before?.FullName);

            if (!CurrentSidMatches(invokingSid))
            {
                return Failed("invokingSidChanged", receipt);
            }
            if (installed is null
                || !InstalledMatches(installed, contract, invokingSid))
            {
                return Failed("packagePostInstallMismatch", receipt);
            }
            return Succeeded(receipt!);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed("packageInstallFailed", receipt: null);
        }
    }

    public async Task<PackageRollbackResult> RollbackAsync(
        PackageInstallReceipt receipt,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The transaction ID must not be empty.",
                nameof(transactionId));
        }
        if (!receipt.CreatedByAttempt && !receipt.UpgradedByAttempt)
        {
            return new PackageRollbackResult(true, false, null);
        }

        try
        {
            IReadOnlyList<InstalledUserPackage> current = _api.FindPackages(
                receipt.Package.UserSid,
                receipt.Package.FamilyName);
            InstalledUserPackage? exact = current.SingleOrDefault(package =>
                string.Equals(
                    package.FullName,
                    receipt.Package.FullName,
                    StringComparison.Ordinal));
            if (exact is null)
            {
                return current.Count == 0
                    ? new PackageRollbackResult(true, false, null)
                    : RecoveryRequired(
                        transactionId,
                        "packageRollbackIdentityChanged",
                        receipt.Package.FullName);
            }
            if (exact != receipt.Package)
            {
                return RecoveryRequired(
                    transactionId,
                    "packageRollbackIdentityChanged",
                    receipt.Package.FullName);
            }

            await _api.RemovePackageAsync(
                    receipt.Package.FullName,
                    receipt.Package.UserSid,
                    cancellationToken)
                .ConfigureAwait(false);
            bool stillPresent = _api
                .FindPackages(
                    receipt.Package.UserSid,
                    receipt.Package.FamilyName)
                .Any(package => string.Equals(
                    package.FullName,
                    receipt.Package.FullName,
                    StringComparison.Ordinal));
            return stillPresent
                ? RecoveryRequired(
                    transactionId,
                    "packageRollbackFailed",
                    receipt.Package.FullName)
                : new PackageRollbackResult(true, true, null);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RecoveryRequired(
                transactionId,
                "packageRollbackFailed",
                receipt.Package.FullName);
        }
    }
#pragma warning restore CA1031

    private static bool PayloadMatches(
        PackagePayloadIdentity payload,
        PackageInstallContract contract) => payload.SignatureValid
        && string.Equals(payload.Name, contract.Name, StringComparison.Ordinal)
        && string.Equals(
            payload.Publisher,
            contract.Publisher,
            StringComparison.Ordinal)
        && payload.Version == contract.Version
        && payload.Architecture == contract.Architecture;

    private static bool InstalledMatches(
        InstalledUserPackage package,
        PackageInstallContract contract,
        string invokingSid) => package.SignatureValid
        && package.InstallLocationTrusted
        && string.Equals(package.UserSid, invokingSid, StringComparison.Ordinal)
        && string.Equals(package.Name, contract.Name, StringComparison.Ordinal)
        && string.Equals(
            package.FamilyName,
            contract.FamilyName,
            StringComparison.Ordinal)
        && string.Equals(
            package.FullName,
            contract.FullName,
            StringComparison.Ordinal)
        && string.Equals(
            package.Publisher,
            contract.Publisher,
            StringComparison.Ordinal)
        && package.Version == contract.Version
        && package.Architecture == contract.Architecture;

    private static bool CanUpgrade(
        InstalledUserPackage package,
        PackageInstallContract contract,
        string invokingSid) => package.SignatureValid
        && package.InstallLocationTrusted
        && string.Equals(package.UserSid, invokingSid, StringComparison.Ordinal)
        && string.Equals(package.Name, contract.Name, StringComparison.Ordinal)
        && string.Equals(
            package.FamilyName,
            contract.FamilyName,
            StringComparison.Ordinal)
        && string.Equals(
            package.Publisher,
            contract.Publisher,
            StringComparison.Ordinal)
        && package.Architecture == contract.Architecture
        && package.Version < contract.Version;

    private bool CurrentSidMatches(string invokingSid) => string.Equals(
        _api.CurrentUserSid,
        invokingSid,
        StringComparison.Ordinal);

    private static void ValidateSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        _ = new SecurityIdentifier(sid);
    }

    private static PackageInstallResult Succeeded(PackageInstallReceipt receipt) =>
        new(PackageInstallOutcome.Succeeded, receipt, null);

    private static PackageInstallResult Blocked(string failureCode) =>
        new(PackageInstallOutcome.Blocked, null, failureCode);

    private static PackageInstallResult Failed(
        string failureCode,
        PackageInstallReceipt? receipt) =>
        new(PackageInstallOutcome.Failed, receipt, failureCode);

    private PackageRollbackResult RecoveryRequired(
        Guid transactionId,
        string failureCode,
        string identity)
    {
        _recoveryWriter.Write(new SetupRecoveryRecord(
            transactionId,
            "user-package",
            failureCode,
            identity,
            DateTimeOffset.UtcNow));
        return new PackageRollbackResult(false, false, failureCode);
    }
}

internal sealed class WindowsPackageDeploymentApi : IPackageDeploymentApi
{
    private readonly PackageManager _manager = new();

    public static WindowsPackageDeploymentApi Instance { get; } = new();

    private WindowsPackageDeploymentApi()
    {
    }

    public bool IsCurrentProcessElevated => WindowsTokenElevation.IsElevated();

    public string CurrentUserSid
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value
                ?? throw new InvalidOperationException(
                    "The current Windows SID is unavailable.");
        }
    }

    public PackagePayloadIdentity InspectPayload(VerifiedSetupPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        SetupMsixSignatureEvidence signature =
            WindowsSetupSignatureProbe.Instance.VerifyMsix(payload);
        using Stream view = payload.Lease.OpenReadView();
        using ZipArchive archive = new(view, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry entry = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidDataException("MSIX identity manifest is missing.");
        using Stream manifest = entry.Open();
        XDocument document = XDocument.Load(manifest, LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidDataException("MSIX identity manifest is missing.");
        XElement identity = root.Element(root.Name.Namespace + "Identity")
            ?? throw new InvalidDataException("MSIX identity is missing.");
        string name = RequiredAttribute(identity, "Name");
        string publisher = RequiredAttribute(identity, "Publisher");
        Version version = Version.Parse(RequiredAttribute(identity, "Version"));
        Architecture architecture = ParseArchitecture(
            RequiredAttribute(identity, "ProcessorArchitecture"));
        return new PackagePayloadIdentity(
            name,
            publisher,
            version,
            architecture,
            signature.SignatureValid
                && string.Equals(
                    signature.IdentityPublisher,
                    publisher,
                    StringComparison.Ordinal));
    }

    public IReadOnlyList<InstalledUserPackage> FindPackages(
        string userSid,
        string familyName) => _manager
        .FindPackagesForUser(userSid)
        .Where(package => string.Equals(
            package.Id.FamilyName,
            familyName,
            StringComparison.Ordinal))
        .Select(package => Map(package, userSid))
        .ToArray();

    public async Task AddPackageAsync(
        VerifiedSetupPayload payload,
        CancellationToken cancellationToken)
    {
        Uri packageUri = new(Path.GetFullPath(payload.DisplayPath));
        DeploymentResult result = await _manager.AddPackageAsync(
                packageUri,
                Array.Empty<Uri>(),
                DeploymentOptions.None)
            .AsTask(cancellationToken).ConfigureAwait(false);
        ThrowOnDeploymentFailure(result, "Package deployment failed.");
    }

    public async Task RemovePackageAsync(
        string packageFullName,
        string userSid,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(CurrentUserSid, userSid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Package removal must run as the invoking user.");
        }
        DeploymentResult result = await _manager.RemovePackageAsync(
                packageFullName,
                RemovalOptions.None)
            .AsTask(cancellationToken).ConfigureAwait(false);
        ThrowOnDeploymentFailure(result, "Package removal failed.");
    }

    private static InstalledUserPackage Map(Package package, string userSid)
    {
        string location = Path.GetFullPath(package.InstalledLocation.Path);
        bool trustedLocation = Directory.Exists(location)
            && (File.GetAttributes(location) & FileAttributes.ReparsePoint) == 0;
        return new InstalledUserPackage(
            package.Id.Name,
            package.Id.FamilyName,
            package.Id.FullName,
            package.Id.Publisher,
            ToVersion(package.Id.Version),
            MapArchitecture(package.Id.Architecture),
            location,
            userSid,
            trustedLocation,
            package.SignatureKind != PackageSignatureKind.None
                && package.Status.VerifyIsOK());
    }

    private static Version ToVersion(PackageVersion version) => new(
        version.Major,
        version.Minor,
        version.Build,
        version.Revision);

    private static Architecture MapArchitecture(
        Windows.System.ProcessorArchitecture architecture) => architecture switch
        {
            Windows.System.ProcessorArchitecture.X64 => Architecture.X64,
            Windows.System.ProcessorArchitecture.X86 => Architecture.X86,
            Windows.System.ProcessorArchitecture.Arm64 => Architecture.Arm64,
            Windows.System.ProcessorArchitecture.Arm => Architecture.Arm,
            _ => throw new InvalidDataException(
                "The installed package architecture is unsupported."),
        };

    private static Architecture ParseArchitecture(string value) => value switch
    {
        "x64" => Architecture.X64,
        "x86" => Architecture.X86,
        "arm64" => Architecture.Arm64,
        "arm" => Architecture.Arm,
        _ => throw new InvalidDataException(
            "The MSIX package architecture is unsupported."),
    };

    private static string RequiredAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new InvalidDataException(
            $"MSIX identity attribute '{name}' is missing.");

    private static void ThrowOnDeploymentFailure(
        DeploymentResult result,
        string message)
    {
        if (result.ExtendedErrorCode is not null)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal static class WindowsTokenElevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformationClass = 20;

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenQuery,
                out nint token))
        {
            throw new InvalidOperationException(
                "The current process token could not be opened.");
        }
        try
        {
            TokenElevation elevation = default;
            if (!GetTokenInformation(
                    token,
                    TokenElevationInformationClass,
                    ref elevation,
                    (uint)Marshal.SizeOf<TokenElevation>(),
                    out _))
            {
                throw new InvalidOperationException(
                    "The current process elevation state is unavailable.");
            }
            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public uint TokenIsElevated;
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token,
        int informationClass,
        ref TokenElevation information,
        uint informationLength,
        out uint returnLength);
}
