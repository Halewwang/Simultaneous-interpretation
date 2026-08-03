using System.Runtime.InteropServices;
using EMKE.Platform.Driver;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class WindowsHandleCatalogTrustTests
{
    private const string UnsignedCatalogFixtureVariable =
        "EMKE_SETUP_UNSIGNED_CAT_FIXTURE";
    private const string UnsignedInfFixtureVariable =
        "EMKE_SETUP_UNSIGNED_INF_FIXTURE";
    private const string UnsignedSysFixtureVariable =
        "EMKE_SETUP_UNSIGNED_SYS_FIXTURE";
    private const int CatalogInfoPathCharacters = 260;
    private static readonly Guid DriverActionVerify = new(
        "F750E6C3-38EE-11D1-85E5-00C04FC295EE");
    private static readonly string[] InboxMemberCandidates =
        ["null.sys", "cng.sys", "disk.sys", "partmgr.sys"];

    [TestMethod]
    public void RegisteredMicrosoftInboxCatalogPassesHandleBoundTrust()
    {
        Assert.IsTrue(
            OperatingSystem.IsWindows(),
            "Inbox catalog trust evidence requires Windows.");
        InboxCatalogFixture fixture = ResolveInboxCatalogFixture();
        using SafeFileHandle catalogHandle = OpenRestrictive(fixture.CatalogPath);
        using SafeFileHandle infMemberHandle = OpenRestrictive(fixture.MemberPath);
        using SafeFileHandle sysMemberHandle = OpenRestrictive(fixture.MemberPath);

        WindowsHandleCatalogEvidence evidence =
            WindowsHandleCatalogTrustVerifier.Instance.Verify(
                fixture.CatalogPath,
                catalogHandle,
                [
                    new WindowsCatalogHandleMember(
                        "driver-inf",
                        fixture.MemberPath,
                        infMemberHandle),
                    new WindowsCatalogHandleMember(
                        "driver-sys",
                        fixture.MemberPath,
                        sysMemberHandle),
                ]);

        Assert.IsTrue(evidence.KernelPolicyValid, evidence.Reason);
        Assert.IsTrue(evidence.CatalogEntriesMatch, evidence.Reason);
        Assert.IsTrue(evidence.MemberTrustValid, evidence.Reason);
        Assert.IsTrue(evidence.Allowed, evidence.Reason);
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidence.SignerSubject));
    }

    [TestMethod]
    [TestCategory("WindowsSetupUnsignedEmkeCatalog")]
    public void UnsignedEmkeCatalogIsDecodedForExactMembersButFailsKernelPolicy()
    {
        string catalogPath = RequireFixture(UnsignedCatalogFixtureVariable);
        string infPath = RequireFixture(UnsignedInfFixtureVariable);
        string sysPath = RequireFixture(UnsignedSysFixtureVariable);
        using SafeFileHandle catalogHandle = OpenRestrictive(catalogPath);
        using SafeFileHandle infHandle = OpenRestrictive(infPath);
        using SafeFileHandle sysHandle = OpenRestrictive(sysPath);

        WindowsHandleCatalogEvidence evidence =
            WindowsHandleCatalogTrustVerifier.Instance.Verify(
                catalogPath,
                catalogHandle,
                [
                    new WindowsCatalogHandleMember(
                        "driver-inf",
                        infPath,
                        infHandle),
                    new WindowsCatalogHandleMember(
                        "driver-sys",
                        sysPath,
                        sysHandle),
                ]);

        Assert.IsTrue(evidence.CatalogEntriesMatch, evidence.Reason);
        Assert.IsFalse(evidence.MemberTrustValid, evidence.Reason);
        Assert.IsFalse(evidence.KernelPolicyValid, evidence.Reason);
        Assert.IsFalse(evidence.Allowed, evidence.Reason);
    }

    private static InboxCatalogFixture ResolveInboxCatalogFixture()
    {
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            Assert.Fail("SystemRoot is required to resolve an inbox catalog.");
        }

        foreach (string candidateName in InboxMemberCandidates)
        {
            string memberPath = Path.Combine(
                systemRoot,
                "System32",
                "drivers",
                candidateName);
            if (!File.Exists(memberPath))
            {
                continue;
            }

            using SafeFileHandle memberHandle = OpenRestrictive(memberPath);
            if (TryResolveRegisteredCatalog(memberHandle, out string catalogPath))
            {
                return new InboxCatalogFixture(catalogPath, memberPath);
            }
        }

        Assert.Fail(
            "No registered Microsoft inbox catalog was found for null.sys, "
            + "cng.sys, disk.sys, or partmgr.sys.");
        throw new InvalidOperationException("Assert.Fail should have thrown.");
    }

    private static bool TryResolveRegisteredCatalog(
        SafeFileHandle memberHandle,
        out string catalogPath)
    {
        catalogPath = string.Empty;
        Guid subsystem = DriverActionVerify;
        if (!InboxCatalogNativeMethods.CryptCATAdminAcquireContext2(
                out nint catalogAdmin,
                ref subsystem,
                hashAlgorithm: null,
                strongHashPolicy: nint.Zero,
                flags: 0))
        {
            return false;
        }

        nint catalogContext = nint.Zero;
        try
        {
            uint hashSize = 0;
            if (!InboxCatalogNativeMethods.CryptCATAdminCalcHashFromFileHandle2(
                    catalogAdmin,
                    memberHandle,
                    ref hashSize,
                    hash: null,
                    flags: 0)
                || hashSize == 0)
            {
                return false;
            }

            byte[] hash = new byte[checked((int)hashSize)];
            if (!InboxCatalogNativeMethods.CryptCATAdminCalcHashFromFileHandle2(
                    catalogAdmin,
                    memberHandle,
                    ref hashSize,
                    hash,
                    flags: 0))
            {
                return false;
            }

            nint previousCatalog = nint.Zero;
            catalogContext = InboxCatalogNativeMethods
                .CryptCATAdminEnumCatalogFromHash(
                    catalogAdmin,
                    hash,
                    hashSize,
                    flags: 0,
                    ref previousCatalog);
            if (catalogContext == nint.Zero)
            {
                return false;
            }

            string? resolvedPath = ReadCatalogPath(catalogContext);
            if (string.IsNullOrWhiteSpace(resolvedPath)
                || !File.Exists(resolvedPath))
            {
                return false;
            }

            catalogPath = resolvedPath;
            return true;
        }
        finally
        {
            if (catalogContext != nint.Zero)
            {
                _ = InboxCatalogNativeMethods.CryptCATAdminReleaseCatalogContext(
                    catalogAdmin,
                    catalogContext,
                    flags: 0);
            }

            _ = InboxCatalogNativeMethods.CryptCATAdminReleaseContext(
                catalogAdmin,
                flags: 0);
        }
    }

    private static string? ReadCatalogPath(nint catalogContext)
    {
        int structureBytes = checked(
            sizeof(uint) + (CatalogInfoPathCharacters * sizeof(char)));
        nint catalogInfo = Marshal.AllocHGlobal(structureBytes);
        try
        {
            Marshal.WriteInt32(catalogInfo, structureBytes);
            if (!InboxCatalogNativeMethods.CryptCATCatalogInfoFromContext(
                    catalogContext,
                    catalogInfo,
                    flags: 0))
            {
                return null;
            }

            return Marshal.PtrToStringUni(catalogInfo + sizeof(uint));
        }
        finally
        {
            Marshal.FreeHGlobal(catalogInfo);
        }
    }

    private static SafeFileHandle OpenRestrictive(string path)
    {
        return File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
    }

    private static string RequireFixture(string variableName)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            Assert.Fail($"{variableName} must name an unsigned EMKE fixture.");
        }

        string fullPath = Path.GetFullPath(configuredPath);
        Assert.IsTrue(
            File.Exists(fullPath),
            $"The unsigned EMKE fixture does not exist: {fullPath}");
        return fullPath;
    }

    private sealed record InboxCatalogFixture(
        string CatalogPath,
        string MemberPath);
}

internal static partial class InboxCatalogNativeMethods
{
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
        EntryPoint = "CryptCATAdminEnumCatalogFromHash",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint CryptCATAdminEnumCatalogFromHash(
        nint catalogAdmin,
        [In] byte[] hash,
        uint hashLength,
        uint flags,
        ref nint previousCatalog);

    [LibraryImport(
        "wintrust.dll",
        EntryPoint = "CryptCATCatalogInfoFromContext",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATCatalogInfoFromContext(
        nint catalogContext,
        nint catalogInfo,
        uint flags);

    [LibraryImport(
        "wintrust.dll",
        EntryPoint = "CryptCATAdminReleaseCatalogContext",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminReleaseCatalogContext(
        nint catalogAdmin,
        nint catalogContext,
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

#pragma warning restore CA1515
