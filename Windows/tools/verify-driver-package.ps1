[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-SinglePackageFile {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Files,

        [Parameter(Mandatory)]
        [string]$Extension,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $matches = @($Files | Where-Object { $_.Extension -ieq $Extension })
    if ($matches.Count -ne 1) {
        throw "Package must contain exactly one $Description; found $($matches.Count)."
    }
    return $matches[0]
}

function Assert-ContainsExactlyOnce {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Literal
    )

    $count = ([regex]::Matches(
        $Text,
        [regex]::Escape($Literal),
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )).Count
    if ($count -ne 1) {
        throw "Expected exactly one '$Literal' declaration; found $count."
    }
}

if (-not $IsWindows) {
    throw "Driver package verification requires Windows catalog APIs."
}

if ($null -eq ("Emke.DriverPackage.CatalogMembership" -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @"
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Emke.DriverPackage
{
    public sealed class CatalogMember
    {
        public string FileName { get; set; }
        public string ReferenceTag { get; set; }
    }

    public static class CatalogMembership
    {
        private const uint CryptCatVersion2 = 0x200;

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptAttributeBlob
        {
            public uint DataLength;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CryptCatalogMember
        {
            public uint StructSize;
            public IntPtr ReferenceTag;
            public IntPtr FileName;
            public Guid SubjectType;
            public uint MemberFlags;
            public IntPtr IndirectData;
            public uint CertificateVersion;
            public uint Reserved;
            public IntPtr ReservedHandle;
            public CryptAttributeBlob EncodedIndirectData;
            public CryptAttributeBlob EncodedMemberInfo;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CryptCATOpen(
            string fileName,
            uint openFlags,
            IntPtr provider,
            uint publicVersion,
            uint encodingType);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr CryptCATEnumerateMember(
            IntPtr catalog,
            IntPtr previousMember);

        [DllImport("wintrust.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATClose(IntPtr catalog);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminAcquireContext2(
            out IntPtr catalogAdmin,
            IntPtr subsystem,
            string hashAlgorithm,
            IntPtr strongHashPolicy,
            uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle2(
            IntPtr catalogAdmin,
            IntPtr file,
            ref uint hashSize,
            byte[] hash,
            uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminReleaseContext(
            IntPtr catalogAdmin,
            uint flags);

        public static CatalogMember[] Enumerate(string catalogPath)
        {
            IntPtr catalog = CryptCATOpen(
                catalogPath,
                0,
                IntPtr.Zero,
                CryptCatVersion2,
                0);
            if (catalog == new IntPtr(-1))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "CryptCATOpen could not open the driver catalog.");
            }

            try
            {
                var members = new List<CatalogMember>();
                IntPtr current = IntPtr.Zero;
                while (true)
                {
                    current = CryptCATEnumerateMember(catalog, current);
                    if (current == IntPtr.Zero)
                    {
                        break;
                    }
                    CryptCatalogMember native =
                        Marshal.PtrToStructure<CryptCatalogMember>(current);
                    members.Add(new CatalogMember
                    {
                        FileName = Marshal.PtrToStringUni(native.FileName) ?? "",
                        ReferenceTag =
                            Marshal.PtrToStringUni(native.ReferenceTag) ?? "",
                    });
                }
                return members.ToArray();
            }
            finally
            {
                CryptCATClose(catalog);
            }
        }

        public static string CalculateSha256CatalogHash(string filePath)
        {
            IntPtr catalogAdmin;
            if (!CryptCATAdminAcquireContext2(
                out catalogAdmin,
                IntPtr.Zero,
                "SHA256",
                IntPtr.Zero,
                0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "CryptCATAdminAcquireContext2 failed.");
            }

            try
            {
                using (FileStream stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    uint hashSize = 0;
                    if (!CryptCATAdminCalcHashFromFileHandle2(
                        catalogAdmin,
                        stream.SafeFileHandle.DangerousGetHandle(),
                        ref hashSize,
                        null,
                        0))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Catalog hash-size calculation failed.");
                    }
                    byte[] hash = new byte[hashSize];
                    if (!CryptCATAdminCalcHashFromFileHandle2(
                        catalogAdmin,
                        stream.SafeFileHandle.DangerousGetHandle(),
                        ref hashSize,
                        hash,
                        0))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Catalog hash calculation failed.");
                    }
                    return BitConverter.ToString(hash).Replace("-", "");
                }
            }
            finally
            {
                CryptCATAdminReleaseContext(catalogAdmin, 0);
            }
        }
    }
}
"@
}

$resolvedPackage = Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop
if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Container)) {
    throw "Package directory does not exist: $PackageDirectory"
}
if ($resolvedPackage.Path -match "(?:^|[\\/])Debug(?:[\\/]|$)") {
    throw "Debug directories are forbidden in a distributable driver package."
}

$directories = @(Get-ChildItem -LiteralPath $resolvedPackage -Directory -Force)
if ($directories.Count -ne 0) {
    throw "Driver package must be flat; nested directories are forbidden."
}
$files = @(Get-ChildItem -LiteralPath $resolvedPackage -File -Force)
$inf = Get-SinglePackageFile -Files $files -Extension ".inf" -Description "INF"
$sys = Get-SinglePackageFile -Files $files -Extension ".sys" -Description "SYS"
$cat = Get-SinglePackageFile -Files $files -Extension ".cat" -Description "CAT"

if ($files.Count -ne 3) {
    throw "Driver package must contain only one INF, one SYS, and one CAT."
}
if (@($files | Where-Object { $_.Extension -ieq ".pdb" }).Count -ne 0) {
    throw "PDB files are forbidden in the distributable package."
}
if (@($files | Where-Object { $_.Name -match "Debug" }).Count -ne 0) {
    throw "Debug binaries are forbidden in the distributable package."
}

$infText = Get-Content -LiteralPath $inf.FullName -Raw
if ($infText -match '\$[A-Za-z_][A-Za-z0-9_]*\$') {
    throw "Artifact INF contains an unresolved WDK stamp token."
}
if ($infText -notmatch "ROOT\\EMKEVIRTUALAUDIO") {
    throw "INF does not declare ROOT\EMKEVIRTUALAUDIO."
}

$roles = @(
    "emke.meeting-speaker.render",
    "emke.app-speaker.capture",
    "emke.app-microphone.render",
    "emke.meeting-microphone.capture"
)
foreach ($role in $roles) {
    Assert-ContainsExactlyOnce -Text $infText -Literal $role
}

if ($infText -notmatch "DriverAbi\s*,\s*0x00010001\s*,\s*0x00000001") {
    throw "INF driver ABI must equal 1."
}
if ($infText -notmatch "DriverVer\s*=\s*[^,]+,\s*(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)") {
    throw "INF DriverVer is missing a four-part version."
}
$infVersion = [version]$Matches["version"]

$versionInfo = (Get-Item -LiteralPath $sys.FullName).VersionInfo
if ($versionInfo.FileVersion -notmatch "(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)") {
    throw "Driver file version is missing or is not a four-part version."
}
$fileVersion = [version]$Matches["version"]
if ($infVersion -ne $fileVersion) {
    throw "DriverVer $infVersion does not agree with FileVersion $fileVersion."
}

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".." "..")
)
$contractValidator = Join-Path $PSScriptRoot "validate-driver-contract.mjs"
$sharedContract = Join-Path $repositoryRoot "Windows" "shared" "emke_endpoint_contract.h"
& node $contractValidator --header $sharedContract --inf $inf.FullName
if ($LASTEXITCODE -ne 0) {
    throw "Packaged INF diverges from the shared native endpoint contract."
}

$catalogSignature = Get-AuthenticodeSignature -LiteralPath $cat.FullName
if ($catalogSignature.Status -notin @("NotSigned", "Valid")) {
    throw "Catalog is malformed or has an invalid signature status: $($catalogSignature.Status)."
}

$catalogMembers = @(
    [Emke.DriverPackage.CatalogMembership]::Enumerate($cat.FullName)
)
if ($catalogMembers.Count -ne 2) {
    throw "Catalog must contain exactly the packaged INF and SYS."
}

foreach ($packageFile in @($inf, $sys)) {
    $catalogHash = [Emke.DriverPackage.CatalogMembership]::CalculateSha256CatalogHash(
        $packageFile.FullName
    )
    $matchingMembers = @(
        $catalogMembers | Where-Object {
            [System.IO.Path]::GetFileName($_.FileName) -ieq $packageFile.Name -and
            $_.ReferenceTag -ieq $catalogHash
        }
    )
    if ($matchingMembers.Count -ne 1) {
        throw "Catalog does not contain the exact packaged bytes for $($packageFile.Name)."
    }
}

Write-Host "Driver package verification passed."
Write-Host "Catalog membership: exact INF and SYS SHA-256 members; signature status: $($catalogSignature.Status) (no signing claim)."
Write-Host "Driver version: $fileVersion"
