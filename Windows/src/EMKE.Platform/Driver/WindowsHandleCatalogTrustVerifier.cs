using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EMKE.Platform.Security;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Platform.Driver;

internal sealed record WindowsCatalogHandleMember(
    string LogicalName,
    string DisplayPath,
    SafeFileHandle Handle);

internal sealed record WindowsHandleCatalogEvidence(
    string? SignerSubject,
    bool KernelPolicyValid,
    bool CatalogEntriesMatch,
    bool MemberTrustValid,
    bool Allowed,
    string Reason);

internal sealed class WindowsHandleCatalogTrustVerifier
{
    private const int TrustSuccess = 0;
    private const int MaximumCatalogBytes = 64 * 1024 * 1024;
    private const uint MaximumCatalogEntries = 1_000_000;
    private const uint MaximumMemberHashBytes = 1024;
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckChain = 0x00000040;
    private static readonly nint InvalidHandleValue = new(-1);
    private readonly IDriverCatalogTrustPolicy _trustPolicy;

    public static WindowsHandleCatalogTrustVerifier Instance { get; } = new(
        MicrosoftDriverCatalogTrustPolicy.Instance);

    internal WindowsHandleCatalogTrustVerifier(
        IDriverCatalogTrustPolicy trustPolicy)
    {
        _trustPolicy = trustPolicy
            ?? throw new ArgumentNullException(nameof(trustPolicy));
    }

#pragma warning disable CA1031 // Every incomplete native evidence path fails closed.
    public WindowsHandleCatalogEvidence Verify(
        string catalogDisplayPath,
        SafeFileHandle catalogHandle,
        IReadOnlyList<WindowsCatalogHandleMember> members)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDisplayPath);
        ArgumentNullException.ThrowIfNull(catalogHandle);
        ArgumentNullException.ThrowIfNull(members);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Handle-bound catalog trust is available only on Windows.");
        }
        if (!HasExactLogicalMembers(members))
        {
            return Untrusted("catalogMemberSetInvalid");
        }

        try
        {
            using FileHandleBorrow catalogBorrow = new(catalogHandle);
            using FileHandleBorrow firstMemberBorrow = new(members[0].Handle);
            using FileHandleBorrow secondMemberBorrow = new(members[1].Handle);

            WindowsHandleTrustEvidence catalogTrust =
                WindowsHandleAuthenticodeTrust.Verify(
                    catalogHandle,
                    catalogDisplayPath,
                    WindowsHandleAuthenticodeTrust.DriverActionVerify);
            string? signerSubject = ReadSignerSubject(
                catalogTrust.SignerCertificate);
            bool kernelPolicyValid =
                catalogTrust.Status == WindowsHandleTrustStatus.Trusted
                && !string.IsNullOrWhiteSpace(signerSubject);

            byte[] catalogBytes = ReadBoundedCatalog(catalogHandle);
            using SafeCtlContextHandle? ctlContext = DecodeCatalog(catalogBytes);
            if (ctlContext is null || ctlContext.IsInvalid)
            {
                return new WindowsHandleCatalogEvidence(
                    signerSubject,
                    kernelPolicyValid,
                    CatalogEntriesMatch: false,
                    MemberTrustValid: false,
                    Allowed: false,
                    Reason: "catalogDecodeInvalid");
            }

            using SafeCatalogAdminHandle? catalogAdmin =
                SafeCatalogAdminHandle.Acquire();
            if (catalogAdmin is null || catalogAdmin.IsInvalid)
            {
                return new WindowsHandleCatalogEvidence(
                    signerSubject,
                    kernelPolicyValid,
                    CatalogEntriesMatch: false,
                    MemberTrustValid: false,
                    Allowed: false,
                    Reason: "catalogAdminUnavailable");
            }

            bool ctlReferenceAdded = false;
            bool adminReferenceAdded = false;
            try
            {
                ctlContext.DangerousAddRef(ref ctlReferenceAdded);
                catalogAdmin.DangerousAddRef(ref adminReferenceAdded);
                nint rawCtlContext = ctlContext.DangerousGetHandle();
                nint rawCatalogAdmin = catalogAdmin.DangerousGetHandle();

                MemberCatalogEvidence[] memberEvidence =
                    new MemberCatalogEvidence[members.Count];
                for (int index = 0; index < members.Count; index++)
                {
                    WindowsCatalogHandleMember member = members[index];
                    byte[]? memberHash = CalculateMemberHash(
                        rawCatalogAdmin,
                        member.Handle);
                    bool catalogEntryMatches = memberHash is not null
                        && CatalogContainsHash(rawCtlContext, memberHash);
                    bool memberTrustValid = memberHash is not null
                        && VerifyCatalogMember(
                            catalogDisplayPath,
                            rawCtlContext,
                            rawCatalogAdmin,
                            member,
                            memberHash) == TrustSuccess;
                    memberEvidence[index] = new MemberCatalogEvidence(
                        catalogEntryMatches,
                        memberTrustValid);
                }

                bool catalogEntriesMatch = memberEvidence.All(
                    evidence => evidence.CatalogEntryMatches);
                bool memberTrustValid = memberEvidence.All(
                    evidence => evidence.MemberTrustValid);
                DriverCatalogTrustDecision decision = _trustPolicy.Evaluate(
                    signerSubject ?? string.Empty,
                    kernelPolicyValid,
                    catalogEntriesMatch && memberTrustValid);
                return new WindowsHandleCatalogEvidence(
                    signerSubject,
                    kernelPolicyValid,
                    catalogEntriesMatch,
                    memberTrustValid,
                    decision.Allowed,
                    decision.Reason);
            }
            finally
            {
                if (adminReferenceAdded)
                {
                    catalogAdmin.DangerousRelease();
                }
                if (ctlReferenceAdded)
                {
                    ctlContext.DangerousRelease();
                }
            }
        }
        catch (Exception)
        {
            return Untrusted("catalogVerificationFailed");
        }
    }
#pragma warning restore CA1031

    private static bool HasExactLogicalMembers(
        IReadOnlyList<WindowsCatalogHandleMember> members)
    {
        if (members.Count != 2)
        {
            return false;
        }

        bool hasInf = false;
        bool hasSys = false;
        foreach (WindowsCatalogHandleMember member in members)
        {
            if (member is null
                || string.IsNullOrWhiteSpace(member.DisplayPath)
                || member.Handle is null
                || member.Handle.IsInvalid
                || member.Handle.IsClosed)
            {
                return false;
            }

            if (string.Equals(
                    member.LogicalName,
                    "driver-inf",
                    StringComparison.Ordinal))
            {
                if (hasInf)
                {
                    return false;
                }
                hasInf = true;
            }
            else if (string.Equals(
                         member.LogicalName,
                         "driver-sys",
                         StringComparison.Ordinal))
            {
                if (hasSys)
                {
                    return false;
                }
                hasSys = true;
            }
            else
            {
                return false;
            }
        }

        return hasInf && hasSys;
    }

    private static byte[] ReadBoundedCatalog(SafeFileHandle catalogHandle)
    {
        long length = RandomAccess.GetLength(catalogHandle);
        if (length is <= 0 or > MaximumCatalogBytes)
        {
            throw new InvalidDataException(
                "Catalog bytes are outside the verified bound.");
        }

        byte[] bytes = new byte[checked((int)length)];
        long offset = 0;
        while (offset < length)
        {
            int read = RandomAccess.Read(
                catalogHandle,
                bytes.AsSpan(checked((int)offset)),
                offset);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Catalog handle ended before its verified length.");
            }
            offset += read;
        }

        return bytes;
    }

    private static unsafe SafeCtlContextHandle? DecodeCatalog(byte[] bytes)
    {
        fixed (byte* bytesPointer = bytes)
        {
            CryptDataBlob blob = new()
            {
                Size = checked((uint)bytes.Length),
                Data = (nint)bytesPointer,
            };
            if (!WindowsHandleCatalogNativeMethods.CryptQueryObject(
                    WindowsHandleCatalogNativeMethods.CertQueryObjectBlob,
                    (nint)(&blob),
                    WindowsHandleCatalogNativeMethods.CertQueryContentFlagCtl,
                    WindowsHandleCatalogNativeMethods.CertQueryFormatFlagBinary,
                    flags: 0,
                    encodingType: nint.Zero,
                    contentType: nint.Zero,
                    formatType: nint.Zero,
                    certificateStore: nint.Zero,
                    message: nint.Zero,
                    out nint ctlContext)
                || ctlContext == nint.Zero)
            {
                return null;
            }

            return new SafeCtlContextHandle(ctlContext);
        }
    }

    private static string? ReadSignerSubject(byte[]? certificateBytes)
    {
        if (certificateBytes is null)
        {
            return null;
        }

        try
        {
            using X509Certificate2 certificate = X509CertificateLoader
                .LoadCertificate(certificateBytes);
            return certificate.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[]? CalculateMemberHash(
        nint catalogAdmin,
        SafeFileHandle memberHandle)
    {
        uint hashSize = 0;
        if (!WindowsCatalogNativeMethods.CryptCATAdminCalcHashFromFileHandle2(
                catalogAdmin,
                memberHandle,
                ref hashSize,
                hash: null,
                flags: 0)
            || hashSize is 0 or > MaximumMemberHashBytes)
        {
            return null;
        }

        byte[] hash = new byte[checked((int)hashSize)];
        if (!WindowsCatalogNativeMethods.CryptCATAdminCalcHashFromFileHandle2(
                catalogAdmin,
                memberHandle,
                ref hashSize,
                hash,
                flags: 0)
            || hashSize is 0 or > MaximumMemberHashBytes
            || hashSize > checked((uint)hash.Length))
        {
            return null;
        }

        return hashSize == checked((uint)hash.Length)
            ? hash
            : hash.AsSpan(0, checked((int)hashSize)).ToArray();
    }

    private static bool CatalogContainsHash(
        nint rawCtlContext,
        byte[] expectedHash)
    {
        CtlContext context = Marshal.PtrToStructure<CtlContext>(rawCtlContext);
        if (context.CtlInfo == nint.Zero)
        {
            return false;
        }

        CtlInfo info = Marshal.PtrToStructure<CtlInfo>(context.CtlInfo);
        if (info.EntryCount is 0 or > MaximumCatalogEntries
            || info.Entries == nint.Zero)
        {
            return false;
        }

        int entrySize = Marshal.SizeOf<CtlEntry>();
        for (uint index = 0; index < info.EntryCount; index++)
        {
            nint entryPointer = info.Entries + checked(
                (nint)((long)index * entrySize));
            CtlEntry entry = Marshal.PtrToStructure<CtlEntry>(entryPointer);
            if (entry.SubjectIdentifier.Size
                    != checked((uint)expectedHash.Length)
                || entry.SubjectIdentifier.Data == nint.Zero)
            {
                continue;
            }

            byte[] subjectIdentifier = new byte[expectedHash.Length];
            Marshal.Copy(
                entry.SubjectIdentifier.Data,
                subjectIdentifier,
                startIndex: 0,
                subjectIdentifier.Length);
            if (CryptographicOperations.FixedTimeEquals(
                    subjectIdentifier,
                    expectedHash))
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe int VerifyCatalogMember(
        string catalogDisplayPath,
        nint ctlContext,
        nint catalogAdmin,
        WindowsCatalogHandleMember member,
        byte[] memberHash)
    {
        string memberTag = Convert.ToHexString(memberHash);
        fixed (char* catalogPathPointer = catalogDisplayPath)
        fixed (char* memberPathPointer = member.DisplayPath)
        fixed (char* memberTagPointer = memberTag)
        fixed (byte* memberHashPointer = memberHash)
        {
            WindowsCatalogNativeMethods.WinTrustCatalogInfo catalogInfo = new()
            {
                Size = checked((uint)Marshal.SizeOf<
                    WindowsCatalogNativeMethods.WinTrustCatalogInfo>()),
                CatalogFilePath = (nint)catalogPathPointer,
                MemberTag = (nint)memberTagPointer,
                MemberFilePath = (nint)memberPathPointer,
                MemberFile = member.Handle.DangerousGetHandle(),
                CalculatedFileHash = (nint)memberHashPointer,
                CalculatedFileHashSize = checked((uint)memberHash.Length),
                CatalogContext = ctlContext,
                CatalogAdmin = catalogAdmin,
            };
            WindowsCatalogNativeMethods.WinTrustData trustData = new()
            {
                Size = checked((uint)Marshal.SizeOf<
                    WindowsCatalogNativeMethods.WinTrustData>()),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceCatalog,
                UnionInfo = (nint)(&catalogInfo),
                StateAction = WtdStateActionVerify,
                ProviderFlags = WtdRevocationCheckChain,
            };
            Guid action = WindowsHandleAuthenticodeTrust.GenericVerifyV2;
            try
            {
                return WindowsCatalogNativeMethods.WinVerifyTrust(
                    InvalidHandleValue,
                    ref action,
                    ref trustData);
            }
            finally
            {
                trustData.StateAction = WtdStateActionClose;
                _ = WindowsCatalogNativeMethods.WinVerifyTrust(
                    InvalidHandleValue,
                    ref action,
                    ref trustData);
            }
        }
    }

    private static WindowsHandleCatalogEvidence Untrusted(string reason)
    {
        return new WindowsHandleCatalogEvidence(
            SignerSubject: null,
            KernelPolicyValid: false,
            CatalogEntriesMatch: false,
            MemberTrustValid: false,
            Allowed: false,
            Reason: reason);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptDataBlob
    {
        public uint Size;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlContext
    {
        public uint EncodingType;
        public nint EncodedCtl;
        public uint EncodedCtlSize;
        public nint CtlInfo;
        public nint CertificateStore;
        public nint Message;
        public nint EncodedContent;
        public uint EncodedContentSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlUsage
    {
        public uint IdentifierCount;
        public nint Identifiers;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAlgorithmIdentifier
    {
        public nint ObjectId;
        public CryptDataBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlInfo
    {
        public uint Version;
        public CtlUsage SubjectUsage;
        public CryptDataBlob ListIdentifier;
        public CryptDataBlob SequenceNumber;
        public FileTime ThisUpdate;
        public FileTime NextUpdate;
        public CryptAlgorithmIdentifier SubjectAlgorithm;
        public uint EntryCount;
        public nint Entries;
        public uint ExtensionCount;
        public nint Extensions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlEntry
    {
        public CryptDataBlob SubjectIdentifier;
        public uint AttributeCount;
        public nint Attributes;
    }

    private sealed record MemberCatalogEvidence(
        bool CatalogEntryMatches,
        bool MemberTrustValid);

    private sealed class FileHandleBorrow : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private bool _referenceAdded;

        public FileHandleBorrow(SafeFileHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (handle.IsInvalid || handle.IsClosed)
            {
                throw new ArgumentException(
                    "Catalog trust requires an open payload handle.",
                    nameof(handle));
            }

            _handle = handle;
            try
            {
                handle.DangerousAddRef(ref _referenceAdded);
                nint rawHandle = handle.DangerousGetHandle();
                if (rawHandle == nint.Zero || rawHandle == InvalidHandleValue)
                {
                    throw new ArgumentException(
                        "Catalog trust requires a valid payload handle.",
                        nameof(handle));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_referenceAdded)
            {
                _handle.DangerousRelease();
                _referenceAdded = false;
            }
        }
    }

    private sealed class SafeCtlContextHandle
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeCtlContextHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return WindowsHandleCatalogNativeMethods.CertFreeCtlContext(handle);
        }
    }

    private sealed class SafeCatalogAdminHandle
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeCatalogAdminHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        public static SafeCatalogAdminHandle? Acquire()
        {
            Guid subsystem = WindowsHandleAuthenticodeTrust.DriverActionVerify;
            if (!WindowsCatalogNativeMethods.CryptCATAdminAcquireContext2(
                    out nint catalogAdmin,
                    ref subsystem,
                    hashAlgorithm: null,
                    strongHashPolicy: nint.Zero,
                    flags: 0)
                || catalogAdmin == nint.Zero)
            {
                return null;
            }

            return new SafeCatalogAdminHandle(catalogAdmin);
        }

        protected override bool ReleaseHandle()
        {
            return WindowsCatalogNativeMethods.CryptCATAdminReleaseContext(
                handle,
                flags: 0);
        }
    }
}

internal static partial class WindowsHandleCatalogNativeMethods
{
    internal const uint CertQueryObjectBlob = 2;
    internal const uint CertQueryContentFlagCtl = 1 << 2;
    internal const uint CertQueryFormatFlagBinary = 1 << 1;

    [LibraryImport(
        "crypt32.dll",
        EntryPoint = "CryptQueryObject",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptQueryObject(
        uint objectType,
        nint objectValue,
        uint expectedContentTypeFlags,
        uint expectedFormatTypeFlags,
        uint flags,
        nint encodingType,
        nint contentType,
        nint formatType,
        nint certificateStore,
        nint message,
        out nint context);

    [LibraryImport(
        "crypt32.dll",
        EntryPoint = "CertFreeCTLContext",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertFreeCtlContext(nint ctlContext);
}
