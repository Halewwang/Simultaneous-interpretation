using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Native;

namespace EMKE.Platform.Driver;

public sealed record WindowsInstalledDriverEndpointState
{
    public WindowsInstalledDriverEndpointState(string role, string state)
    {
        Role = string.IsNullOrWhiteSpace(role)
            ? throw new ArgumentException(
                "The endpoint role is required.",
                nameof(role))
            : role;
        State = string.IsNullOrWhiteSpace(state)
            ? throw new ArgumentException(
                "The endpoint state is required.",
                nameof(state))
            : state;
    }

    public string Role { get; }

    public string State { get; }
}

public sealed record WindowsInstalledDriverSnapshot
{
    public WindowsInstalledDriverSnapshot(
        bool present,
        string? rootDevnodeHardwareId,
        Version driverFileVersion,
        int driverAbiProperty,
        string? catalogSigner,
        bool catalogChainValid,
        IEnumerable<WindowsInstalledDriverEndpointState> endpointStates)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(driverAbiProperty);
        ArgumentNullException.ThrowIfNull(endpointStates);

        WindowsInstalledDriverEndpointState[] endpoints = endpointStates.ToArray();
        if (endpoints.Any(static endpoint => endpoint is null))
        {
            throw new ArgumentException(
                "Endpoint states cannot contain null values.",
                nameof(endpointStates));
        }

        Present = present;
        RootDevnodeHardwareId = rootDevnodeHardwareId;
        DriverFileVersion = driverFileVersion
            ?? throw new ArgumentNullException(nameof(driverFileVersion));
        DriverAbiProperty = driverAbiProperty;
        CatalogSigner = catalogSigner;
        CatalogChainValid = catalogChainValid;
        EndpointStates = Array.AsReadOnly(endpoints);
    }

    public bool Present { get; }

    public string? RootDevnodeHardwareId { get; }

    public Version DriverFileVersion { get; }

    public int DriverAbiProperty { get; }

    public string? CatalogSigner { get; }

    public bool CatalogChainValid { get; }

    public IReadOnlyList<WindowsInstalledDriverEndpointState> EndpointStates { get; }
}

public interface IWindowsDriverSnapshotSource
{
    ValueTask<WindowsInstalledDriverSnapshot> ReadAsync(
        CancellationToken cancellationToken);
}

internal sealed record WindowsRootDriverEvidence(
    bool Present,
    string? HardwareId,
    Version DriverFileVersion,
    int DriverAbi,
    string? CatalogPath,
    string? InfPath,
    string? DriverBinaryPath)
{
    public static WindowsRootDriverEvidence Missing { get; } = new(
        Present: false,
        HardwareId: null,
        new Version(0, 0, 0),
        DriverAbi: 0,
        CatalogPath: null,
        InfPath: null,
        DriverBinaryPath: null);
}

internal sealed record WindowsCatalogEvidence(
    string? Signer,
    bool ChainValid);

internal interface IWindowsDriverEvidenceApi
{
    WindowsRootDriverEvidence ReadRootDriver();

    WindowsCatalogEvidence ReadCatalog(
        string catalogPath,
        string infPath,
        string driverBinaryPath);

    IReadOnlyList<WindowsInstalledDriverEndpointState> ReadEndpointStates();
}

public sealed class WindowsDriverSnapshotSource
    : IWindowsDriverSnapshotSource
{
    private readonly IWindowsDriverEvidenceApi _api;

    public WindowsDriverSnapshotSource()
        : this(WindowsDriverEvidenceApi.Instance)
    {
    }

    internal WindowsDriverSnapshotSource(IWindowsDriverEvidenceApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

#pragma warning disable CA1031 // Any incomplete OS evidence must fail closed.
    public ValueTask<WindowsInstalledDriverSnapshot> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            WindowsRootDriverEvidence root = _api.ReadRootDriver();
            if (!root.Present
                || string.IsNullOrWhiteSpace(root.HardwareId)
                || string.IsNullOrWhiteSpace(root.CatalogPath)
                || string.IsNullOrWhiteSpace(root.InfPath)
                || string.IsNullOrWhiteSpace(root.DriverBinaryPath))
            {
                return ValueTask.FromResult(MissingSnapshot());
            }

            WindowsCatalogEvidence catalog =
                _api.ReadCatalog(
                    root.CatalogPath,
                    root.InfPath,
                    root.DriverBinaryPath);
            IReadOnlyList<WindowsInstalledDriverEndpointState> endpoints =
                _api.ReadEndpointStates();
            return ValueTask.FromResult(
                new WindowsInstalledDriverSnapshot(
                    present: true,
                    root.HardwareId,
                    root.DriverFileVersion,
                    root.DriverAbi,
                    catalog.Signer,
                    catalog.ChainValid,
                    endpoints));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ValueTask.FromResult(MissingSnapshot());
        }
    }
#pragma warning restore CA1031

    private static WindowsInstalledDriverSnapshot MissingSnapshot()
    {
        return new WindowsInstalledDriverSnapshot(
            present: false,
            rootDevnodeHardwareId: null,
            driverFileVersion: new Version(0, 0, 0),
            driverAbiProperty: 0,
            catalogSigner: null,
            catalogChainValid: false,
            endpointStates: []);
    }
}

internal sealed record WindowsCatalogSignerEvidence(
    string? Subject,
    bool LocalChainValid);

internal sealed record WindowsCatalogRevocationConfiguration(
    X509RevocationMode ChainRevocationMode,
    X509RevocationFlag ChainRevocationFlag,
    uint WinTrustRevocationChecks,
    uint WinTrustProviderFlags,
    bool CertificateDownloadsEnabled);

internal interface IWindowsCatalogTrustNativeApi
{
    WindowsCatalogSignerEvidence ReadCatalogSigner(string catalogPath);

    int VerifyCatalogSignature(string catalogPath);

    int VerifyCatalogMember(
        string catalogPath,
        string memberPath);
}

public sealed record DriverCatalogTrustDecision(
    bool Allowed,
    string Reason);

public interface IDriverCatalogTrustPolicy
{
    DriverCatalogTrustDecision Evaluate(
        string signerSubject,
        bool kernelPolicyValid,
        bool catalogMembersValid);
}

public sealed class MicrosoftDriverCatalogTrustPolicy
    : IDriverCatalogTrustPolicy
{
    private const string MicrosoftPublisherCommonName =
        "Microsoft Windows Hardware Compatibility Publisher";
    private const string MicrosoftOrganization = "Microsoft Corporation";

    public static MicrosoftDriverCatalogTrustPolicy Instance { get; } = new();

    private MicrosoftDriverCatalogTrustPolicy()
    {
    }

    public DriverCatalogTrustDecision Evaluate(
        string signerSubject,
        bool kernelPolicyValid,
        bool catalogMembersValid)
    {
        ArgumentNullException.ThrowIfNull(signerSubject);
        if (!kernelPolicyValid)
        {
            return new DriverCatalogTrustDecision(
                Allowed: false,
                Reason: "kernelPolicyInvalid");
        }

        if (!catalogMembersValid)
        {
            return new DriverCatalogTrustDecision(
                Allowed: false,
                Reason: "catalogMembersInvalid");
        }

        bool microsoftPublisher = HasSubjectComponent(
                signerSubject,
                "CN",
                MicrosoftPublisherCommonName)
            && HasSubjectComponent(
                signerSubject,
                "O",
                MicrosoftOrganization);
        return microsoftPublisher
            ? new DriverCatalogTrustDecision(
                Allowed: true,
                Reason: "microsoftKernelCatalogTrusted")
            : new DriverCatalogTrustDecision(
                Allowed: false,
                Reason: "catalogSignerNotMicrosoft");
    }

    private static bool HasSubjectComponent(
        string subject,
        string expectedName,
        string expectedValue)
    {
        foreach (string component in subject.Split(','))
        {
            int separator = component.IndexOf(
                '=',
                StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            if (string.Equals(
                    component[..separator].Trim(),
                    expectedName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    component[(separator + 1)..].Trim(),
                    expectedValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class WindowsCatalogTrustVerifier
{
    private const int TrustSuccess = 0;
    private readonly IWindowsCatalogTrustNativeApi _native;
    private readonly IDriverCatalogTrustPolicy _trustPolicy;

    public static WindowsCatalogTrustVerifier Instance { get; } = new(
        WindowsCatalogTrustNativeApi.Instance,
        MicrosoftDriverCatalogTrustPolicy.Instance);

    internal WindowsCatalogTrustVerifier(
        IWindowsCatalogTrustNativeApi native)
        : this(native, MicrosoftDriverCatalogTrustPolicy.Instance)
    {
    }

    internal WindowsCatalogTrustVerifier(
        IWindowsCatalogTrustNativeApi native,
        IDriverCatalogTrustPolicy trustPolicy)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _trustPolicy = trustPolicy
            ?? throw new ArgumentNullException(nameof(trustPolicy));
    }

    internal IDriverCatalogTrustPolicy TrustPolicy => _trustPolicy;

#pragma warning disable CA1031 // Incomplete trust evidence must fail closed.
    public WindowsCatalogEvidence Verify(
        string catalogPath,
        string infPath,
        string driverBinaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(infPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverBinaryPath);
        try
        {
            WindowsCatalogSignerEvidence signer =
                _native.ReadCatalogSigner(catalogPath);
            bool kernelPolicyValid =
                _native.VerifyCatalogSignature(catalogPath) == TrustSuccess
                && signer.LocalChainValid;
            bool catalogMembersValid =
                _native.VerifyCatalogMember(catalogPath, infPath)
                    == TrustSuccess
                && _native.VerifyCatalogMember(
                    catalogPath,
                    driverBinaryPath) == TrustSuccess;
            DriverCatalogTrustDecision decision = _trustPolicy.Evaluate(
                signer.Subject ?? string.Empty,
                kernelPolicyValid,
                catalogMembersValid);
            if (!decision.Allowed)
            {
                return Untrusted();
            }

            return new WindowsCatalogEvidence(
                signer.Subject,
                ChainValid: true);
        }
        catch (Exception)
        {
            return Untrusted();
        }
    }
#pragma warning restore CA1031

    private static WindowsCatalogEvidence Untrusted()
    {
        return new WindowsCatalogEvidence(
            Signer: null,
            ChainValid: false);
    }
}

internal sealed class WindowsDriverEvidenceApi
    : IWindowsDriverEvidenceApi
{
    private const string RootHardwareId = @"ROOT\EMKEVIRTUALAUDIO";
    private const string DriverAbiValueName = "DriverAbi";
    private const string DriverBinaryValueName = "Driver";
    private const string DriverVersionValueName = "DriverVersion";
    private const string InfPathValueName = "InfPath";
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDrv = 0x00000002;
    private const uint KeyRead = 0x00020019;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const uint RegDword = 4;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorSuccess = 0;
    private const uint DeviceStateActive = 0x00000001;
    private const uint DeviceStateDisabled = 0x00000002;
    private const uint DeviceStateNotPresent = 0x00000004;
    private const uint DeviceStateUnplugged = 0x00000008;
    private static readonly nint InvalidHandleValue = new(-1);
    private readonly WindowsCatalogTrustVerifier _catalogTrustVerifier;

    public static WindowsDriverEvidenceApi Instance { get; } = new(
        WindowsCatalogTrustVerifier.Instance);

    private WindowsDriverEvidenceApi(
        WindowsCatalogTrustVerifier catalogTrustVerifier)
    {
        _catalogTrustVerifier = catalogTrustVerifier
            ?? throw new ArgumentNullException(
                nameof(catalogTrustVerifier));
    }

    public WindowsRootDriverEvidence ReadRootDriver()
    {
        EnsureWindows();
        nint devices = WindowsDriverNativeMethods.SetupDiGetClassDevs(
            nint.Zero,
            enumerator: null,
            nint.Zero,
            DigcfAllClasses | DigcfPresent);
        if (devices == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                WindowsDriverNativeMethods.DeviceInfoData device = new()
                {
                    Size = checked((uint)Marshal.SizeOf<
                        WindowsDriverNativeMethods.DeviceInfoData>()),
                };
                if (!WindowsDriverNativeMethods.SetupDiEnumDeviceInfo(
                        devices,
                        index,
                        ref device))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreItems)
                    {
                        return WindowsRootDriverEvidence.Missing;
                    }

                    throw new Win32Exception(error);
                }

                string[] hardwareIds =
                    ReadDeviceMultiString(
                        devices,
                        ref device,
                        SpdrpHardwareId);
                string? match = hardwareIds.FirstOrDefault(static value =>
                    string.Equals(
                        value,
                        RootHardwareId,
                        StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    continue;
                }

                return ReadDriverRegistryEvidence(
                    devices,
                    ref device,
                    match);
            }
        }
        finally
        {
            _ = WindowsDriverNativeMethods.SetupDiDestroyDeviceInfoList(
                devices);
        }
    }

    public WindowsCatalogEvidence ReadCatalog(
        string catalogPath,
        string infPath,
        string driverBinaryPath)
    {
        return _catalogTrustVerifier.Verify(
            catalogPath,
            infPath,
            driverBinaryPath);
    }

    public IReadOnlyList<WindowsInstalledDriverEndpointState>
        ReadEndpointStates()
    {
        EnsureWindows();
        NativeAudioEndpointSnapshot snapshot = default;
        snapshot.Size = checked(
            (uint)Marshal.SizeOf<NativeAudioEndpointSnapshot>());
        snapshot.AbiVersion = NativeAudioConstants.AbiVersion;
        NativeAudioStatus status =
            PInvokeNativeAudioApi.Instance.DiscoverEndpoints(ref snapshot);
        if (status != NativeAudioStatus.Ok)
        {
            throw new InvalidDataException(
                $"Endpoint discovery failed with status {(int)status}.");
        }

        if (snapshot.Size != Marshal.SizeOf<NativeAudioEndpointSnapshot>()
            || snapshot.AbiVersion != NativeAudioConstants.AbiVersion)
        {
            throw new InvalidDataException(
                "Endpoint discovery returned incompatible evidence.");
        }

        return
        [
            MapEndpoint(snapshot.VirtualEndpoint0),
            MapEndpoint(snapshot.VirtualEndpoint1),
            MapEndpoint(snapshot.VirtualEndpoint2),
            MapEndpoint(snapshot.VirtualEndpoint3),
        ];
    }

    private static WindowsRootDriverEvidence ReadDriverRegistryEvidence(
        nint devices,
        ref WindowsDriverNativeMethods.DeviceInfoData device,
        string hardwareId)
    {
        nint driverKey = WindowsDriverNativeMethods.SetupDiOpenDevRegKey(
            devices,
            ref device,
            DicsFlagGlobal,
            hardwareProfile: 0,
            DiregDrv,
            KeyRead);
        if (driverKey == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            Version version = Version.Parse(
                ReadRegistryString(
                    driverKey,
                    DriverVersionValueName));
            int driverAbi = checked(
                (int)ReadRegistryDword(
                    driverKey,
                    DriverAbiValueName));
            string infName = Path.GetFileName(
                ReadRegistryString(
                    driverKey,
                    InfPathValueName));
            string driverBinaryName = Path.GetFileName(
                ReadRegistryString(
                    driverKey,
                    DriverBinaryValueName));
            WindowsDriverPackagePaths package =
                ResolveDriverPackagePaths(
                    infName,
                    driverBinaryName);
            return new WindowsRootDriverEvidence(
                Present: true,
                hardwareId,
                version,
                driverAbi,
                package.CatalogPath,
                package.InfPath,
                package.DriverBinaryPath);
        }
        finally
        {
            _ = WindowsDriverNativeMethods.RegCloseKey(driverKey);
        }
    }

    private static string[] ReadDeviceMultiString(
        nint devices,
        ref WindowsDriverNativeMethods.DeviceInfoData device,
        uint property)
    {
        _ = WindowsDriverNativeMethods.SetupDiGetDeviceRegistryProperty(
            devices,
            ref device,
            property,
            out uint registryType,
            propertyBuffer: null,
            propertyBufferSize: 0,
            out uint requiredSize);
        int error = Marshal.GetLastPInvokeError();
        if (requiredSize == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }

        byte[] buffer = new byte[requiredSize];
        if (!WindowsDriverNativeMethods.SetupDiGetDeviceRegistryProperty(
                devices,
                ref device,
                property,
                out registryType,
                buffer,
                checked((uint)buffer.Length),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        string value = Encoding.Unicode.GetString(buffer);
        return value.Split(
            '\0',
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
    }

    private static string ReadRegistryString(
        nint key,
        string valueName)
    {
        byte[] bytes = ReadRegistryBytes(
            key,
            valueName,
            out uint registryType);
        if (registryType is not (RegSz or RegExpandSz))
        {
            throw new InvalidDataException(
                $"Driver registry value {valueName} is not a string.");
        }

        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static uint ReadRegistryDword(
        nint key,
        string valueName)
    {
        byte[] bytes = ReadRegistryBytes(
            key,
            valueName,
            out uint registryType);
        if (registryType != RegDword
            || bytes.Length < sizeof(uint))
        {
            throw new InvalidDataException(
                $"Driver registry value {valueName} is not a DWORD.");
        }

        return BitConverter.ToUInt32(bytes, 0);
    }

    private static byte[] ReadRegistryBytes(
        nint key,
        string valueName,
        out uint registryType)
    {
        uint requiredSize = 0;
        int result = WindowsDriverNativeMethods.RegQueryValue(
            key,
            valueName,
            reserved: nint.Zero,
            out registryType,
            data: null,
            ref requiredSize);
        if (result != ErrorSuccess || requiredSize == 0)
        {
            throw new Win32Exception(result);
        }

        byte[] bytes = new byte[requiredSize];
        result = WindowsDriverNativeMethods.RegQueryValue(
            key,
            valueName,
            reserved: nint.Zero,
            out registryType,
            bytes,
            ref requiredSize);
        if (result != ErrorSuccess)
        {
            throw new Win32Exception(result);
        }

        return bytes;
    }

    private static WindowsDriverPackagePaths ResolveDriverPackagePaths(
        string infName,
        string driverBinaryName)
    {
        if (string.IsNullOrWhiteSpace(infName)
            || !string.Equals(
                Path.GetFileName(infName),
                infName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The installed driver INF name is invalid.");
        }

        if (string.IsNullOrWhiteSpace(driverBinaryName)
            || !string.Equals(
                Path.GetFileName(driverBinaryName),
                driverBinaryName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The installed driver binary name is invalid.");
        }

        string windowsDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string installedInfPath = Path.Combine(
            windowsDirectory,
            "INF",
            infName);
        string catalogName = File.ReadLines(installedInfPath)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith(
                "CatalogFile",
                StringComparison.OrdinalIgnoreCase))
            .Select(static line =>
            {
                int separator = line.IndexOf(
                    '=',
                    StringComparison.Ordinal);
                return separator < 0
                    ? string.Empty
                    : line[(separator + 1)..].Trim();
            })
            .SingleOrDefault()
            ?? throw new InvalidDataException(
                "The installed driver INF has no catalog.");
        if (string.IsNullOrWhiteSpace(catalogName)
            || !string.Equals(
                Path.GetFileName(catalogName),
                catalogName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The installed driver catalog name is invalid.");
        }

        string driverStore = Path.Combine(
            windowsDirectory,
            "System32",
            "DriverStore",
            "FileRepository");
        string[] matches = Directory.EnumerateFiles(
                driverStore,
                catalogName,
                SearchOption.AllDirectories)
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                "The installed driver catalog could not be resolved uniquely.");
        }

        string catalogPath = matches[0];
        string packageDirectory = Path.GetDirectoryName(catalogPath)
            ?? throw new InvalidDataException(
                "The installed driver catalog directory is invalid.");
        return new WindowsDriverPackagePaths(
            installedInfPath,
            catalogPath,
            Path.Combine(packageDirectory, driverBinaryName));
    }

    private static WindowsInstalledDriverEndpointState MapEndpoint(
        NativeAudioDiscoveredEndpoint endpoint)
    {
        if (endpoint.Size != Marshal.SizeOf<NativeAudioDiscoveredEndpoint>()
            || endpoint.AbiVersion != NativeAudioConstants.AbiVersion)
        {
            throw new InvalidDataException(
                "Endpoint evidence has an incompatible native layout.");
        }

        NativeAudioEndpointRole role =
            Enum.IsDefined(typeof(NativeAudioEndpointRole), endpoint.Role)
                ? (NativeAudioEndpointRole)endpoint.Role
                : throw new InvalidDataException(
                    "Endpoint evidence contains an unknown role.");
        string stableRole = role switch
        {
            NativeAudioEndpointRole.MeetingSpeakerRender =>
                "meetingSpeakerRender",
            NativeAudioEndpointRole.AppSpeakerCapture =>
                "appSpeakerCapture",
            NativeAudioEndpointRole.AppMicrophoneRender =>
                "appMicrophoneRender",
            NativeAudioEndpointRole.MeetingMicrophoneCapture =>
                "meetingMicrophoneCapture",
            _ => throw new InvalidDataException(
                "Endpoint evidence contains an unsupported role."),
        };
        return new WindowsInstalledDriverEndpointState(
            stableRole,
            MapEndpointState(endpoint.State));
    }

    private static string MapEndpointState(uint state)
    {
        return state switch
        {
            DeviceStateActive => "active",
            DeviceStateDisabled => "disabled",
            DeviceStateNotPresent => "missing",
            DeviceStateUnplugged => "inactive",
            _ => "inactive",
        };
    }

    private sealed record WindowsDriverPackagePaths(
        string InfPath,
        string CatalogPath,
        string DriverBinaryPath);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows driver evidence is available only on Windows.");
        }
    }
}

internal sealed class WindowsCatalogTrustNativeApi
    : IWindowsCatalogTrustNativeApi
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdRevocationCheckChain = 0x00000040;
    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly Guid GenericVerifyV2 = new(
        "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private static readonly Guid DriverActionVerify = new(
        "F750E6C3-38EE-11D1-85E5-00C04FC295EE");

    public static WindowsCatalogTrustNativeApi Instance { get; } = new();

    internal static WindowsCatalogRevocationConfiguration
        RevocationConfiguration { get; } = new(
            X509RevocationMode.Online,
            X509RevocationFlag.EntireChain,
            WtdRevokeWholeChain,
            WtdRevocationCheckChain,
            CertificateDownloadsEnabled: true);

    private WindowsCatalogTrustNativeApi()
    {
    }

    public WindowsCatalogSignerEvidence ReadCatalogSigner(
        string catalogPath)
    {
        string fullCatalogPath = RequireWindowsFile(
            catalogPath,
            "catalog");

#pragma warning disable SYSLIB0057 // Signed-file extraction has no loader replacement.
        using X509Certificate signer =
            X509Certificate.CreateFromSignedFile(fullCatalogPath);
#pragma warning restore SYSLIB0057
        using X509Certificate2 signerCertificate =
            X509CertificateLoader.LoadCertificate(signer.GetRawCertData());
        using X509Chain chain = new();
        chain.ChainPolicy.RevocationMode =
            RevocationConfiguration.ChainRevocationMode;
        chain.ChainPolicy.RevocationFlag =
            RevocationConfiguration.ChainRevocationFlag;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = DateTime.Now;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(30);
        chain.ChainPolicy.DisableCertificateDownloads =
            !RevocationConfiguration.CertificateDownloadsEnabled;
        return new WindowsCatalogSignerEvidence(
            signerCertificate.Subject,
            chain.Build(signerCertificate));
    }

    public unsafe int VerifyCatalogSignature(string catalogPath)
    {
        string fullCatalogPath = RequireWindowsFile(
            catalogPath,
            "catalog");
        fixed (char* path = fullCatalogPath)
        {
            WindowsCatalogNativeMethods.WinTrustFileInfo fileInfo = new()
            {
                Size = checked(
                    (uint)Marshal.SizeOf<
                        WindowsCatalogNativeMethods.WinTrustFileInfo>()),
                FilePath = (nint)path,
            };
            WindowsCatalogNativeMethods.WinTrustData trustData =
                CreateTrustData(
                    WtdChoiceFile,
                    (nint)(&fileInfo));
            Guid action = DriverActionVerify;
            return WindowsCatalogNativeMethods.WinVerifyTrust(
                InvalidHandleValue,
                ref action,
                ref trustData);
        }
    }

    public int VerifyCatalogMember(
        string catalogPath,
        string memberPath)
    {
        string fullCatalogPath = RequireWindowsFile(
            catalogPath,
            "catalog");
        string fullMemberPath = RequireWindowsFile(
            memberPath,
            "catalog member");
        Guid subsystem = DriverActionVerify;
        if (!WindowsCatalogNativeMethods.CryptCATAdminAcquireContext2(
                out nint catalogAdmin,
                ref subsystem,
                hashAlgorithm: null,
                strongHashPolicy: nint.Zero,
                flags: 0))
        {
            return LastNativeError();
        }

        try
        {
            using SafeFileHandle memberFile = File.OpenHandle(
                fullMemberPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            uint hashSize = 0;
            if (!WindowsCatalogNativeMethods
                    .CryptCATAdminCalcHashFromFileHandle2(
                        catalogAdmin,
                        memberFile,
                        ref hashSize,
                        hash: null,
                        flags: 0)
                || hashSize == 0)
            {
                return LastNativeError();
            }

            byte[] hash = new byte[hashSize];
            if (!WindowsCatalogNativeMethods
                    .CryptCATAdminCalcHashFromFileHandle2(
                        catalogAdmin,
                        memberFile,
                        ref hashSize,
                        hash,
                        flags: 0))
            {
                return LastNativeError();
            }

            return VerifyCatalogMemberTrust(
                fullCatalogPath,
                fullMemberPath,
                memberFile,
                catalogAdmin,
                hash);
        }
        finally
        {
            _ = WindowsCatalogNativeMethods.CryptCATAdminReleaseContext(
                catalogAdmin,
                flags: 0);
        }
    }

    private static unsafe int VerifyCatalogMemberTrust(
        string catalogPath,
        string memberPath,
        SafeFileHandle memberFile,
        nint catalogAdmin,
        byte[] hash)
    {
        string memberTag = Convert.ToHexString(hash);
        fixed (char* catalogPathPointer = catalogPath)
        fixed (char* memberPathPointer = memberPath)
        fixed (char* memberTagPointer = memberTag)
        fixed (byte* hashPointer = hash)
        {
            WindowsCatalogNativeMethods.WinTrustCatalogInfo catalogInfo =
                new()
                {
                    Size = checked(
                        (uint)Marshal.SizeOf<
                            WindowsCatalogNativeMethods
                                .WinTrustCatalogInfo>()),
                    CatalogFilePath = (nint)catalogPathPointer,
                    MemberTag = (nint)memberTagPointer,
                    MemberFilePath = (nint)memberPathPointer,
                    MemberFile = memberFile.DangerousGetHandle(),
                    CalculatedFileHash = (nint)hashPointer,
                    CalculatedFileHashSize =
                        checked((uint)hash.Length),
                    CatalogAdmin = catalogAdmin,
                };
            WindowsCatalogNativeMethods.WinTrustData trustData =
                CreateTrustData(
                    WtdChoiceCatalog,
                    (nint)(&catalogInfo));
            Guid action = GenericVerifyV2;
            return WindowsCatalogNativeMethods.WinVerifyTrust(
                InvalidHandleValue,
                ref action,
                ref trustData);
        }
    }

    private static WindowsCatalogNativeMethods.WinTrustData CreateTrustData(
        uint unionChoice,
        nint unionInfo)
    {
        return new WindowsCatalogNativeMethods.WinTrustData
        {
            Size = checked(
                (uint)Marshal.SizeOf<
                    WindowsCatalogNativeMethods.WinTrustData>()),
            UiChoice = WtdUiNone,
            RevocationChecks =
                RevocationConfiguration.WinTrustRevocationChecks,
            UnionChoice = unionChoice,
            UnionInfo = unionInfo,
            StateAction = WtdStateActionIgnore,
            ProviderFlags =
                RevocationConfiguration.WinTrustProviderFlags,
        };
    }

    private static string RequireWindowsFile(
        string path,
        string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows catalog trust is available only on Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"The driver {description} was not found.");
        }

        return fullPath;
    }

    private static int LastNativeError()
    {
        int error = Marshal.GetLastPInvokeError();
        return error == 0 ? -1 : error;
    }
}

internal static partial class WindowsCatalogNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustCatalogInfo
    {
        public uint Size;
        public uint CatalogVersion;
        public nint CatalogFilePath;
        public nint MemberTag;
        public nint MemberFilePath;
        public nint MemberFile;
        public nint CalculatedFileHash;
        public uint CalculatedFileHashSize;
        public nint CatalogContext;
        public nint CatalogAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint UnionInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int WinVerifyTrust(
        nint window,
        ref Guid actionId,
        ref WinTrustData trustData);

    [LibraryImport(
        "wintrust.dll",
        EntryPoint = "CryptCATAdminAcquireContext2",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminAcquireContext2(
        out nint catalogAdmin,
        ref Guid subsystem,
        string? hashAlgorithm,
        nint strongHashPolicy,
        uint flags);

    [LibraryImport(
        "wintrust.dll",
        EntryPoint = "CryptCATAdminCalcHashFromFileHandle2",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminCalcHashFromFileHandle2(
        nint catalogAdmin,
        SafeFileHandle file,
        ref uint hashSize,
        [Out] byte[]? hash,
        uint flags);

    [LibraryImport(
        "wintrust.dll",
        EntryPoint = "CryptCATAdminReleaseContext",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminReleaseContext(
        nint catalogAdmin,
        uint flags);
}

internal static partial class WindowsDriverNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public nint Reserved;
    }

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint SetupDiGetClassDevs(
        nint classGuid,
        string? enumerator,
        nint parentWindow,
        uint flags);

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiEnumDeviceInfo",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet,
        uint memberIndex,
        ref DeviceInfoData deviceInfoData);

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceRegistryPropertyW",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceRegistryProperty(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        uint property,
        out uint propertyRegistryDataType,
        [Out] byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiOpenDevRegKey",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint SetupDiOpenDevRegKey(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        uint scope,
        uint hardwareProfile,
        uint keyType,
        uint desiredAccess);

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiDestroyDeviceInfoList",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(
        nint deviceInfoSet);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "RegQueryValueExW",
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int RegQueryValue(
        nint key,
        string valueName,
        nint reserved,
        out uint registryType,
        [Out] byte[]? data,
        ref uint dataSize);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int RegCloseKey(nint key);
}

public interface IWindowsHostCompatibilitySource
{
    int GetCurrentWindowsBuild();
}

internal sealed class SystemWindowsHostCompatibilitySource
    : IWindowsHostCompatibilitySource
{
    public static SystemWindowsHostCompatibilitySource Instance { get; } = new();

    private SystemWindowsHostCompatibilitySource()
    {
    }

    public int GetCurrentWindowsBuild()
    {
        return OperatingSystem.IsWindows()
            ? Environment.OSVersion.Version.Build
            : 0;
    }
}

internal sealed record WindowsDriverCompatibilityObservation(
    bool Allowed,
    string Reason,
    bool UpdateRecommended,
    bool RepairAvailable);

internal interface IWindowsDriverCompatibilityDiagnostics
{
    void Record(WindowsDriverCompatibilityObservation observation);
}

internal sealed class NullWindowsDriverCompatibilityDiagnostics
    : IWindowsDriverCompatibilityDiagnostics
{
    public static NullWindowsDriverCompatibilityDiagnostics Instance { get; } = new();

    private NullWindowsDriverCompatibilityDiagnostics()
    {
    }

    public void Record(WindowsDriverCompatibilityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
    }
}

public sealed class WindowsDriverManager : IDriverManager
{
    private readonly IWindowsDriverSnapshotSource _snapshotSource;
    private readonly CompatibilityManifest _manifest;
    private readonly IWindowsHostCompatibilitySource _hostSource;
    private readonly IWindowsDriverCompatibilityDiagnostics _diagnostics;

    public WindowsDriverManager(
        IWindowsDriverSnapshotSource snapshotSource,
        CompatibilityManifest manifest)
        : this(
            snapshotSource,
            manifest,
            SystemWindowsHostCompatibilitySource.Instance,
            NullWindowsDriverCompatibilityDiagnostics.Instance)
    {
    }

    public WindowsDriverManager(
        IWindowsDriverSnapshotSource snapshotSource,
        CompatibilityManifest manifest,
        IWindowsHostCompatibilitySource hostSource)
        : this(
            snapshotSource,
            manifest,
            hostSource,
            NullWindowsDriverCompatibilityDiagnostics.Instance)
    {
    }

    internal WindowsDriverManager(
        IWindowsDriverSnapshotSource snapshotSource,
        CompatibilityManifest manifest,
        IWindowsHostCompatibilitySource hostSource,
        IWindowsDriverCompatibilityDiagnostics diagnostics)
    {
        _snapshotSource =
            snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _hostSource =
            hostSource ?? throw new ArgumentNullException(nameof(hostSource));
        _diagnostics =
            diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public async Task<DriverCompatibility> CheckCompatibilityAsync(
        CancellationToken cancellationToken)
    {
        WindowsInstalledDriverSnapshot snapshot =
            await _snapshotSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        int windowsBuild = _hostSource.GetCurrentWindowsBuild();
        CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
            _manifest,
            windowsBuild,
            new InstalledDriverEvidence(
                snapshot.Present,
                snapshot.RootDevnodeHardwareId,
                snapshot.DriverFileVersion,
                snapshot.DriverAbiProperty,
                snapshot.CatalogSigner,
                snapshot.CatalogChainValid,
                snapshot.EndpointStates.Select(static endpoint =>
                    new InstalledDriverEndpointEvidence(
                        endpoint.Role,
                        endpoint.State))));
        _diagnostics.Record(new WindowsDriverCompatibilityObservation(
            decision.Allowed,
            decision.Reason,
            decision.UpdateRecommended,
            decision.RepairAvailable));
        return decision.ToDriverCompatibility();
    }
}
