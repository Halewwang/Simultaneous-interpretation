using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace EMKE.Setup.Platform;

internal static partial class SetupApiNativeMethods
{
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDrv = 0x00000002;
    private const uint KeyRead = 0x00020019;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const uint SposPath = 1;
    private const uint DicdGenerateId = 0x00000001;
    private const uint DifRegisterDevice = 0x00000019;
    private const uint InstallFlagForce = 0x00000001;
    private const uint InstallFlagNonInteractive = 0x00000004;
    private const uint WinTrustNoUi = 2;
    private const uint WinTrustRevokeWholeChain = 1;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionVerify = 1;
    private const uint WinTrustStateActionClose = 2;
    private const uint WinTrustProviderRevocationCheckChainExcludeRoot = 0x00000080;
    private const int MaximumInfBytes = 1024 * 1024;
    private const int MaximumPathCharacters = 32768;
    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly Guid DriverActionVerify = new(
        "F750E6C3-38EE-11D1-85E5-00C04FC295EE");
    private static readonly Regex DriverVersionPattern = new(
        @"(?im)^\s*DriverVer\s*=\s*[^,\r\n]+,\s*(?<version>\d+\.\d+\.\d+\.\d+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex RootHardwareIdPattern = new(
        @"(?im),\s*(?<hardware>ROOT\\[^\s\r\n]+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static DriverMachineState ReadDriverState(string hardwareId)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);

        DriverMachineState? deviceState = TryReadDeviceState(hardwareId);
        if (deviceState is not null)
        {
            return deviceState;
        }

        DriverPackageState? package = FindPublishedPackage(hardwareId);
        return package is null
            ? DriverMachineState.Missing
            : new DriverMachineState(package, DriverDeviceState.Missing);
    }

    public static DriverNativeInstallResult InstallRootDriver(
        VerifiedSetupPayload inf,
        string hardwareId)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(inf);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        VerifyLeaseHash(inf);

        string publishedInf = StageInf(inf.DisplayPath);
        char[] className = new char[256];
        if (!SetupDiGetInfClass(
                inf.DisplayPath,
                out Guid classGuid,
                className,
                checked((uint)className.Length),
                out _))
        {
            return NativeFailure(
                "driverInfClassFailed",
                publishedInf,
                deviceInstanceId: null);
        }

        nint deviceSet = SetupDiCreateDeviceInfoList(ref classGuid, nint.Zero);
        if (deviceSet == InvalidHandleValue)
        {
            return NativeFailure(
                "driverDeviceSetCreateFailed",
                publishedInf,
                deviceInstanceId: null);
        }

        string? deviceInstanceId = null;
        try
        {
            DeviceInfoData device = NewDeviceInfoData();
            if (!SetupDiCreateDeviceInfo(
                    deviceSet,
                    "EMKE Virtual Audio Bridge",
                    ref classGuid,
                    deviceDescription: null,
                    nint.Zero,
                    DicdGenerateId,
                    ref device))
            {
                return NativeFailure(
                    "driverDeviceCreateFailed",
                    publishedInf,
                    deviceInstanceId: null);
            }

            byte[] hardwareMultiString = Encoding.Unicode.GetBytes(
                string.Concat(hardwareId, "\0\0"));
            if (!SetupDiSetDeviceRegistryProperty(
                    deviceSet,
                    ref device,
                    SpdrpHardwareId,
                    hardwareMultiString,
                    checked((uint)hardwareMultiString.Length)))
            {
                return NativeFailure(
                    "driverHardwareIdSetFailed",
                    publishedInf,
                    deviceInstanceId: null);
            }
            if (!SetupDiCallClassInstaller(
                    DifRegisterDevice,
                    deviceSet,
                    ref device))
            {
                return NativeFailure(
                    "driverDeviceRegisterFailed",
                    publishedInf,
                    deviceInstanceId: null);
            }

            deviceInstanceId = ReadDeviceInstanceId(deviceSet, ref device);
            if (!UpdateDriverForPlugAndPlayDevices(
                    nint.Zero,
                    hardwareId,
                    inf.DisplayPath,
                    InstallFlagForce | InstallFlagNonInteractive,
                    out bool rebootRequired))
            {
                return NativeFailure(
                    "driverUpdateFailed",
                    publishedInf,
                    deviceInstanceId);
            }
            return new DriverNativeInstallResult(
                Succeeded: true,
                rebootRequired,
                publishedInf,
                deviceInstanceId,
                FailureCode: null);
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    public static bool RemoveDevice(string deviceInstanceId)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInstanceId);
        nint deviceSet = SetupDiGetClassDevs(
            nint.Zero,
            enumerator: null,
            nint.Zero,
            DigcfAllClasses);
        if (deviceSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                DeviceInfoData device = NewDeviceInfoData();
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref device))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreItems)
                    {
                        return true;
                    }
                    throw new Win32Exception(error);
                }
                string candidate = ReadDeviceInstanceId(deviceSet, ref device);
                if (!string.Equals(
                        candidate,
                        deviceInstanceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return DiUninstallDevice(
                    nint.Zero,
                    deviceSet,
                    ref device,
                    flags: 0,
                    out _);
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    public static bool RemoveDriverPackage(string publishedInfName)
    {
        EnsureWindows();
        string safeLeaf = Path.GetFileName(publishedInfName);
        if (!string.Equals(
                safeLeaf,
                publishedInfName,
                StringComparison.OrdinalIgnoreCase)
            || !safeLeaf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
            || !safeLeaf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The published INF name is not canonical.",
                nameof(publishedInfName));
        }
        return SetupUninstallOemInf(safeLeaf, flags: 0, reserved: nint.Zero);
    }

    private static DriverMachineState? TryReadDeviceState(string hardwareId)
    {
        nint deviceSet = SetupDiGetClassDevs(
            nint.Zero,
            enumerator: null,
            nint.Zero,
            DigcfAllClasses | DigcfPresent);
        if (deviceSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        DriverMachineState? exact = null;
        DriverMachineState? conflicting = null;
        try
        {
            for (uint index = 0; ; index++)
            {
                DeviceInfoData device = NewDeviceInfoData();
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref device))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }
                    throw new Win32Exception(error);
                }

                string[] hardwareIds = ReadDeviceMultiString(
                    deviceSet,
                    ref device,
                    SpdrpHardwareId);
                string? matched = hardwareIds.FirstOrDefault(value => string.Equals(
                    value,
                    hardwareId,
                    StringComparison.OrdinalIgnoreCase));
                DriverRegistryIdentity? registry = TryReadDriverRegistry(
                    deviceSet,
                    ref device);
                bool emkeProvider = string.Equals(
                    registry?.ProviderName,
                    "EMKE",
                    StringComparison.OrdinalIgnoreCase);
                if (matched is null && !emkeProvider)
                {
                    continue;
                }

                string observedHardwareId = matched
                    ?? hardwareIds.FirstOrDefault(static value =>
                        value.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase))
                    ?? "ROOT\\UNKNOWN";
                if (registry is null)
                {
                    DriverMachineState incomplete = new(
                        new DriverPackageState(
                            Present: true,
                            PublishedInfName: null,
                            observedHardwareId,
                            new Version(0, 0, 0, 0),
                            CatalogSha256: null,
                            SignerSubject: null,
                            KernelTrustValid: false),
                        new DriverDeviceState(
                            Present: true,
                            ReadDeviceInstanceId(deviceSet, ref device),
                            observedHardwareId,
                            PublishedInfName: null,
                            new Version(0, 0, 0, 0),
                            CatalogSha256: null));
                    conflicting ??= incomplete;
                    continue;
                }

                DriverPackageState package = ReadPublishedPackageIdentity(
                    registry.InfPath,
                    observedHardwareId,
                    registry.DriverVersion);
                DriverDeviceState deviceState = new(
                    Present: true,
                    ReadDeviceInstanceId(deviceSet, ref device),
                    observedHardwareId,
                    package.PublishedInfName,
                    package.Version,
                    package.CatalogSha256);
                DriverMachineState candidate = new(package, deviceState);
                if (matched is not null)
                {
                    if (exact is null)
                    {
                        exact = candidate;
                    }
                    else
                    {
                        conflicting = MarkAmbiguous(candidate);
                    }
                }
                else
                {
                    conflicting ??= candidate;
                }
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
        return conflicting ?? exact;
    }

    private static DriverPackageState? FindPublishedPackage(string hardwareId)
    {
        string windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        string infDirectory = Path.Combine(windowsDirectory, "INF");
        List<DriverPackageState> candidates = [];
        foreach (string infPath in Directory.EnumerateFiles(
            infDirectory,
            "oem*.inf",
            SearchOption.TopDirectoryOnly))
        {
            FileInfo info = new(infPath);
            if (info.Length is <= 0 or > MaximumInfBytes)
            {
                continue;
            }
            string text = File.ReadAllText(infPath);
            if (!text.Contains("ProviderName=\"EMKE\"", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("Provider=EMKE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Match hardwareMatch = RootHardwareIdPattern.Match(text);
            string observedHardwareId = hardwareMatch.Success
                ? hardwareMatch.Groups["hardware"].Value
                : "ROOT\\UNKNOWN";
            DriverPackageState candidate = ReadPublishedPackageIdentity(
                Path.GetFileName(infPath),
                observedHardwareId,
                ParseDriverVersion(text));
            candidates.Add(candidate);
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        DriverPackageState? conflicting = candidates.FirstOrDefault(candidate =>
            !string.Equals(
                candidate.HardwareId,
                hardwareId,
                StringComparison.OrdinalIgnoreCase));
        if (conflicting is not null)
        {
            return conflicting;
        }
        return candidates.Count == 1
            ? candidates[0]
            : candidates[0] with { HardwareId = "ROOT\\AMBIGUOUS" };
    }

    private static DriverMachineState MarkAmbiguous(DriverMachineState state) =>
        new(
            state.Package with { HardwareId = "ROOT\\AMBIGUOUS" },
            state.Device with { HardwareId = "ROOT\\AMBIGUOUS" });

    private static DriverPackageState ReadPublishedPackageIdentity(
        string infPathOrName,
        string hardwareId,
        Version version)
    {
        string publishedInfName = Path.GetFileName(infPathOrName);
        string windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        string publishedInfPath = Path.Combine(
            windowsDirectory,
            "INF",
            publishedInfName);
        string driverStoreInf = ResolveDriverStoreInf(publishedInfPath);
        string driverStoreDirectory = Path.GetDirectoryName(driverStoreInf)
            ?? throw new InvalidDataException(
                "The driver-store INF directory is unavailable.");
        string catalogPath = Path.Combine(
            driverStoreDirectory,
            "EMKE.VirtualAudio.cat");
        if (!File.Exists(catalogPath))
        {
            throw new InvalidDataException(
                "The installed EMKE catalog is unavailable.");
        }
        string catalogSha256;
        using (FileStream catalog = new(
            catalogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            catalogSha256 = Convert.ToHexStringLower(SHA256.HashData(catalog));
        }
        string? signerSubject = ReadSignerSubject(catalogPath);
        bool kernelTrustValid = VerifyKernelCatalogTrust(catalogPath);
        return new DriverPackageState(
            Present: true,
            publishedInfName,
            hardwareId,
            version,
            catalogSha256,
            signerSubject,
            kernelTrustValid);
    }

    private static DriverRegistryIdentity? TryReadDriverRegistry(
        nint deviceSet,
        ref DeviceInfoData device)
    {
        nint key = SetupDiOpenDevRegKey(
            deviceSet,
            ref device,
            DicsFlagGlobal,
            hardwareProfile: 0,
            DiregDrv,
            KeyRead);
        if (key == InvalidHandleValue)
        {
            return null;
        }
        try
        {
            string infPath = ReadRegistryString(key, "InfPath");
            string driverVersion = ReadRegistryString(key, "DriverVersion");
            string providerName = ReadRegistryString(key, "ProviderName");
            return new DriverRegistryIdentity(
                infPath,
                Version.Parse(driverVersion),
                providerName);
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        finally
        {
            _ = RegCloseKey(key);
        }
    }

    private static string StageInf(string sourceInfPath)
    {
        char[] destination = new char[MaximumPathCharacters];
        if (!SetupCopyOemInf(
                sourceInfPath,
                sourceMediaLocation: null,
                SposPath,
                copyStyle: 0,
                destination,
                checked((uint)destination.Length),
                out _,
                nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        string published = Path.GetFileName(BufferToString(destination));
        if (string.IsNullOrWhiteSpace(published))
        {
            throw new InvalidDataException(
                "SetupAPI did not return a published INF identity.");
        }
        return published;
    }

    private static string ResolveDriverStoreInf(string publishedInfPath)
    {
        char[] buffer = new char[MaximumPathCharacters];
        if (!SetupGetInfDriverStoreLocation(
                publishedInfPath,
                nint.Zero,
                localeName: null,
                buffer,
                checked((uint)buffer.Length),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return BufferToString(buffer);
    }

    private static string[] ReadDeviceMultiString(
        nint deviceSet,
        ref DeviceInfoData device,
        uint property)
    {
        _ = SetupDiGetDeviceRegistryProperty(
            deviceSet,
            ref device,
            property,
            out _,
            propertyBuffer: null,
            propertyBufferSize: 0,
            out uint requiredSize);
        int error = Marshal.GetLastPInvokeError();
        if (requiredSize == 0)
        {
            return [];
        }
        if (error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }
        byte[] buffer = new byte[requiredSize];
        if (!SetupDiGetDeviceRegistryProperty(
                deviceSet,
                ref device,
                property,
                out _,
                buffer,
                checked((uint)buffer.Length),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return Encoding.Unicode.GetString(buffer)
            .Split(
                '\0',
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries);
    }

    private static string ReadDeviceInstanceId(
        nint deviceSet,
        ref DeviceInfoData device)
    {
        char[] buffer = new char[MaximumPathCharacters];
        if (!SetupDiGetDeviceInstanceId(
                deviceSet,
                ref device,
                buffer,
                checked((uint)buffer.Length),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return BufferToString(buffer);
    }

    private static string ReadRegistryString(nint key, string valueName)
    {
        uint size = 0;
        int first = RegQueryValueEx(
            key,
            valueName,
            nint.Zero,
            out uint registryType,
            data: null,
            ref size);
        if (first != 0 || size == 0)
        {
            throw new Win32Exception(first);
        }
        if (registryType is not (RegSz or RegExpandSz))
        {
            throw new InvalidDataException(
                "The driver registry identity is not a string.");
        }
        byte[] bytes = new byte[size];
        int second = RegQueryValueEx(
            key,
            valueName,
            nint.Zero,
            out registryType,
            bytes,
            ref size);
        if (second != 0)
        {
            throw new Win32Exception(second);
        }
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static Version ParseDriverVersion(string infText)
    {
        Match match = DriverVersionPattern.Match(infText);
        return match.Success
            ? Version.Parse(match.Groups["version"].Value)
            : new Version(0, 0, 0, 0);
    }

#pragma warning disable SYSLIB0057 // No loader replacement exposes a signed catalog's embedded signer.
    private static string? ReadSignerSubject(string catalogPath)
    {
        using X509Certificate signer = X509Certificate.CreateFromSignedFile(
            catalogPath);
        using X509Certificate2 certificate = new(signer);
        return certificate.Subject;
    }
#pragma warning restore SYSLIB0057

    private static string BufferToString(char[] buffer)
    {
        int terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
    }

    private static bool VerifyKernelCatalogTrust(string catalogPath)
    {
        nint path = Marshal.StringToCoTaskMemUni(catalogPath);
        nint fileInfoPointer = nint.Zero;
        nint trustDataPointer = nint.Zero;
        try
        {
            WinTrustFileInfo fileInfo = new()
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = path,
            };
            fileInfoPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            WinTrustData trustData = NewWinTrustData(
                fileInfoPointer,
                WinTrustStateActionVerify);
            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
            Guid action = DriverActionVerify;
            int status = WinVerifyTrust(nint.Zero, ref action, trustDataPointer);

            trustData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
            trustData.StateAction = WinTrustStateActionClose;
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: true);
            _ = WinVerifyTrust(nint.Zero, ref action, trustDataPointer);
            return status == 0;
        }
        finally
        {
            if (trustDataPointer != nint.Zero)
            {
                Marshal.FreeHGlobal(trustDataPointer);
            }
            if (fileInfoPointer != nint.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }
            Marshal.FreeCoTaskMem(path);
        }
    }

    private static WinTrustData NewWinTrustData(
        nint fileInfo,
        uint stateAction) => new()
        {
            Size = checked((uint)Marshal.SizeOf<WinTrustData>()),
            UiChoice = WinTrustNoUi,
            RevocationChecks = WinTrustRevokeWholeChain,
            UnionChoice = WinTrustChoiceFile,
            FileInfo = fileInfo,
            StateAction = stateAction,
            ProviderFlags = WinTrustProviderRevocationCheckChainExcludeRoot,
        };

    private static void VerifyLeaseHash(VerifiedSetupPayload payload)
    {
        using Stream input = payload.Lease.OpenReadView();
        string actual = Convert.ToHexStringLower(SHA256.HashData(input));
        if (!string.Equals(actual, payload.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The INF changed after payload verification.");
        }
    }

    private static DeviceInfoData NewDeviceInfoData() => new()
    {
        Size = checked((uint)Marshal.SizeOf<DeviceInfoData>()),
    };

    private static DriverNativeInstallResult NativeFailure(
        string failureCode,
        string? publishedInfName,
        string? deviceInstanceId) => new(
            Succeeded: false,
            RebootRequired: false,
            publishedInfName,
            deviceInstanceId,
            failureCode);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Driver installation requires Windows.");
        }
    }

    private sealed record DriverRegistryIdentity(
        string InfPath,
        Version DriverVersion,
        string ProviderName);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetupDiGetClassDevs(
        nint classGuid,
        string? enumerator,
        nint parentWindow,
        uint flags);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiEnumDeviceInfo",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet,
        uint memberIndex,
        ref DeviceInfoData deviceInfoData);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceRegistryPropertyW",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        uint property,
        out uint propertyRegistryDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInstanceIdW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        [Out] char[] deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiOpenDevRegKey",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetupDiOpenDevRegKey(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        uint scope,
        uint hardwareProfile,
        uint keyType,
        uint samDesired);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiDestroyDeviceInfoList",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupCopyOEMInfW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupCopyOemInf(
        string sourceInfFileName,
        string? sourceMediaLocation,
        uint mediaType,
        uint copyStyle,
        [Out] char[] destinationInfFileName,
        uint destinationInfFileNameSize,
        out uint requiredSize,
        nint destinationInfFileNameComponent);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupGetInfDriverStoreLocationW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetInfDriverStoreLocation(
        string fileName,
        nint alternatePlatformInfo,
        string? localeName,
        [Out] char[] returnBuffer,
        uint returnBufferSize,
        out uint requiredSize);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetINFClassW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetInfClass(
        string infName,
        out Guid classGuid,
        [Out] char[] className,
        uint classNameSize,
        out uint requiredSize);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiCreateDeviceInfoList",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetupDiCreateDeviceInfoList(
        ref Guid classGuid,
        nint parentWindow);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiCreateDeviceInfoW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCreateDeviceInfo(
        nint deviceInfoSet,
        string deviceName,
        ref Guid classGuid,
        string? deviceDescription,
        nint parentWindow,
        uint creationFlags,
        ref DeviceInfoData deviceInfoData);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiSetDeviceRegistryPropertyW",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetDeviceRegistryProperty(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        uint property,
        byte[] propertyBuffer,
        uint propertyBufferSize);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiCallClassInstaller",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(
        uint installFunction,
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupUninstallOEMInfW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupUninstallOemInf(
        string infFileName,
        uint flags,
        nint reserved);

    [DllImport(
        "newdev.dll",
        EntryPoint = "UpdateDriverForPlugAndPlayDevicesW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        nint parentWindow,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [DllImport(
        "newdev.dll",
        EntryPoint = "DiUninstallDevice",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DiUninstallDevice(
        nint parentWindow,
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "RegQueryValueExW",
        CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueEx(
        nint key,
        string valueName,
        nint reserved,
        out uint type,
        byte[]? data,
        ref uint dataSize);

    [DllImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegCloseKey(nint key);

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(
        nint window,
        ref Guid actionId,
        nint trustData);
}
