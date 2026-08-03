using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Platform.Security;

internal enum WindowsHandleTrustStatus
{
    Trusted,
    ChainOnly,
    Invalid,
}

internal sealed record WindowsHandleTrustEvidence(
    WindowsHandleTrustStatus Status,
    int NativeStatus,
    byte[]? SignerCertificate);

internal static partial class WindowsHandleAuthenticodeTrust
{
    private const int TrustSuccess = 0;
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const uint MaximumSignerCertificateBytes = 1024 * 1024;
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckChain = 0x00000040;
    private static readonly nint InvalidHandleValue = new(-1);

    internal static readonly Guid GenericVerifyV2 = new(
        "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    internal static readonly Guid DriverActionVerify = new(
        "F750E6C3-38EE-11D1-85E5-00C04FC295EE");

    public static WindowsHandleTrustEvidence Verify(
        SafeFileHandle handle,
        string displayPath,
        Guid actionId)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Handle-bound Authenticode trust is available only on Windows.");
        }
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException(
                "Handle-bound Authenticode trust requires an open file handle.",
                nameof(handle));
        }

        bool referenceAdded = false;
        try
        {
            handle.DangerousAddRef(ref referenceAdded);
            nint rawHandle = handle.DangerousGetHandle();
            if (rawHandle == nint.Zero || rawHandle == InvalidHandleValue)
            {
                throw new ArgumentException(
                    "Handle-bound Authenticode trust requires a valid file handle.",
                    nameof(handle));
            }

            return VerifyBorrowedHandle(rawHandle, displayPath, actionId);
        }
        finally
        {
            if (referenceAdded)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static unsafe WindowsHandleTrustEvidence VerifyBorrowedHandle(
        nint handle,
        string displayPath,
        Guid actionId)
    {
        fixed (char* displayPathPointer = displayPath)
        {
            WinTrustFileInfo fileInfo = new()
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = (nint)displayPathPointer,
                File = handle,
            };
            WinTrustData trustData = new()
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                UnionInfo = (nint)(&fileInfo),
                StateAction = WtdStateActionVerify,
                ProviderFlags = WtdRevocationCheckChain,
            };
            Guid action = actionId;
            int nativeStatus;
            byte[]? signerCertificate;
            try
            {
                nativeStatus = WinVerifyTrust(
                    InvalidHandleValue,
                    ref action,
                    ref trustData);
                signerCertificate = CopySignerCertificate(trustData.StateData);
            }
            finally
            {
                trustData.StateAction = WtdStateActionClose;
                _ = WinVerifyTrust(
                    InvalidHandleValue,
                    ref action,
                    ref trustData);
            }

            return new WindowsHandleTrustEvidence(
                MapStatus(nativeStatus),
                nativeStatus,
                signerCertificate);
        }
    }

    private static WindowsHandleTrustStatus MapStatus(int nativeStatus)
    {
        return nativeStatus switch
        {
            TrustSuccess => WindowsHandleTrustStatus.Trusted,
            CertEUntrustedRoot or CertEChaining =>
                WindowsHandleTrustStatus.ChainOnly,
            _ => WindowsHandleTrustStatus.Invalid,
        };
    }

    private static byte[]? CopySignerCertificate(nint stateData)
    {
        if (stateData == nint.Zero)
        {
            return null;
        }

        nint providerData = WTHelperProvDataFromStateData(stateData);
        if (providerData == nint.Zero)
        {
            return null;
        }

        nint providerSigner = WTHelperGetProvSignerFromChain(
            providerData,
            signerIndex: 0,
            counterSigner: false,
            counterSignerIndex: 0);
        if (providerSigner == nint.Zero)
        {
            return null;
        }

        nint providerCertificate = WTHelperGetProvCertFromChain(
            providerSigner,
            certificateIndex: 0);
        if (providerCertificate == nint.Zero)
        {
            return null;
        }

        CryptProviderCertificate provider =
            Marshal.PtrToStructure<CryptProviderCertificate>(
                providerCertificate);
        if (provider.Size < checked(
                (uint)Marshal.SizeOf<CryptProviderCertificate>())
            || provider.CertificateContext == nint.Zero)
        {
            return null;
        }

        CertificateContext certificate =
            Marshal.PtrToStructure<CertificateContext>(
                provider.CertificateContext);
        if (certificate.EncodedCertificate == nint.Zero
            || certificate.EncodedCertificateSize is 0
            || certificate.EncodedCertificateSize
                > MaximumSignerCertificateBytes)
        {
            return null;
        }

        byte[] copiedCertificate = new byte[
            checked((int)certificate.EncodedCertificateSize)];
        Marshal.Copy(
            certificate.EncodedCertificate,
            copiedCertificate,
            startIndex: 0,
            copiedCertificate.Length);
        return copiedCertificate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
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
        public nint UnionInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        public uint Size;
        public nint CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        public uint EncodingType;
        public nint EncodedCertificate;
        public uint EncodedCertificateSize;
        public nint CertificateInfo;
        public nint CertificateStore;
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WinVerifyTrust(
        nint window,
        ref Guid actionId,
        ref WinTrustData trustData);

    [LibraryImport(
        "wintrust.dll",
        EntryPoint = "WTHelperProvDataFromStateData")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport(
        "wintrust.dll",
        EntryPoint = "WTHelperGetProvSignerFromChain")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [LibraryImport(
        "wintrust.dll",
        EntryPoint = "WTHelperGetProvCertFromChain")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint WTHelperGetProvCertFromChain(
        nint providerSigner,
        uint certificateIndex);
}
