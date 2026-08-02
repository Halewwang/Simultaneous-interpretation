[CmdletBinding(DefaultParameterSetName = "Uninstall")]
param(
    [Parameter(ParameterSetName = "Uninstall")]
    [switch]$RemoveCertificate,

    [Parameter(ParameterSetName = "Uninstall")]
    [switch]$ConfirmRemoveCertificate,

    [Parameter(ParameterSetName = "Uninstall")]
    [ValidateNotNullOrEmpty()]
    [string]$CertificatePath,

    [Parameter(ParameterSetName = "Uninstall")]
    [ValidateNotNullOrEmpty()]
    [string]$ChecksumsPath,

    [Parameter(ParameterSetName = "Uninstall")]
    [ValidatePattern("^[0-9A-Fa-f]{40}$")]
    [string]$ExpectedCertificateThumbprint
)

if ($MyInvocation.InvocationName -ceq ".") {
    throw "Dot-source invocation is forbidden for this lifecycle script."
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:PackageName = "EMKE.Translation.Internal"
$script:ExpectedPublisher = "CN=EMKE Internal Test"
$script:ExpectedVersion = "__EMKE_PACKAGE_VERSION__"
$script:ExpectedArchitecture = "__EMKE_ARCHITECTURE__"
$script:ExpectedCertificateSubject = "CN=EMKE Internal Test"
$script:PackageFileName =
    "__EMKE_PACKAGE_BASE_NAME__.msix"
$script:CertificateFileName =
    "__EMKE_PACKAGE_BASE_NAME__.cer"
$script:InstallScriptFileName =
    "Install-EMKE-Translation-Internal.ps1"
$script:UninstallScriptFileName =
    "Uninstall-EMKE-Translation-Internal.ps1"

function Assert-PowerShell7Windows {
    if ($PSVersionTable.PSVersion.Major -ne 7) {
        throw "This helper requires PowerShell 7."
    }
    if (-not $IsWindows) {
        throw "This helper can only run on Windows."
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Assert-SupportedUninstallParent {
    Assert-PowerShell7Windows
    if (Test-IsAdministrator) {
        throw (
            "Run this helper from a non-elevated PowerShell session. " +
            "Only the certificate removal child may request elevation."
        )
    }
}

function Test-FixedSha256Equal {
    param(
        [Parameter(Mandatory)]
        [string]$Expected,

        [Parameter(Mandatory)]
        [string]$Actual
    )

    if ($Expected -notmatch "^[0-9A-Fa-f]{64}$" -or
        $Actual -notmatch "^[0-9A-Fa-f]{64}$") {
        return $false
    }
    $expectedBytes = [Convert]::FromHexString($Expected)
    $actualBytes = [Convert]::FromHexString($Actual)
    return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
        $expectedBytes,
        $actualBytes
    )
}

function Test-FixedThumbprintEqual {
    param(
        [Parameter(Mandatory)]
        [string]$Expected,

        [Parameter(Mandatory)]
        [string]$Actual
    )

    if ($Expected -notmatch "^[0-9A-Fa-f]{40}$" -or
        $Actual -notmatch "^[0-9A-Fa-f]{40}$") {
        return $false
    }
    $expectedBytes = [Convert]::FromHexString($Expected)
    $actualBytes = [Convert]::FromHexString($Actual)
    return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
        $expectedBytes,
        $actualBytes
    )
}

function Resolve-ExactBundleInput {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet(".msix", ".cer", ".txt", ".ps1")]
        [string]$ExpectedExtension
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not [IO.Path]::IsPathFullyQualified($Path) -or
        $Path -match "^[\\/]{2}") {
        throw "Lifecycle inputs must use an absolute local filesystem path."
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals(
        [IO.Path]::GetExtension($fullPath),
        $ExpectedExtension,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Lifecycle input has an unexpected file extension."
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required lifecycle input is unavailable."
    }

    $current = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($current.PSProvider.Name -cne "FileSystem") {
        throw "Lifecycle inputs must use an absolute local filesystem path."
    }
    if ($IsWindows -and
        -not [string]::IsNullOrEmpty([string]$current.PSDrive.DisplayRoot)) {
        throw "Lifecycle inputs must not use a mapped or remote filesystem."
    }
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne
            0 -or
            -not [string]::IsNullOrEmpty([string]$current.LinkType)) {
            throw (
                "Lifecycle inputs and their parents must not contain a " +
                "reparse point or symbolic link."
            )
        }
        if (-not $IsWindows) {
            break
        }
        $current = if ($current -is [IO.FileInfo]) {
            $current.Directory
        } else {
            $current.Parent
        }
    }

    return $fullPath
}

function Assert-SameBundleDirectory {
    param(
        [Parameter(Mandatory)]
        [string[]]$Paths
    )

    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $expectedDirectory = [IO.Path]::GetDirectoryName(
        [IO.Path]::GetFullPath($Paths[0])
    )
    foreach ($pathItem in $Paths | Select-Object -Skip 1) {
        $directory = [IO.Path]::GetDirectoryName(
            [IO.Path]::GetFullPath($pathItem)
        )
        if (-not [string]::Equals(
            $expectedDirectory,
            $directory,
            $comparison
        )) {
            throw "Lifecycle inputs must be in the same bundle directory."
        }
    }
}

function Read-ExpectedSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$ChecksumsPath,

        [Parameter(Mandatory)]
        [string]$FilePath
    )

    $expectedName = [IO.Path]::GetFileName($FilePath)
    $matches = [Collections.Generic.List[string]]::new()
    foreach ($line in [IO.File]::ReadAllLines($ChecksumsPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $parsed = [regex]::Match(
            $line,
            "^(?<hash>[0-9A-Fa-f]{64})[ `t]+(?:\*)?(?<name>[^`r`n]+)$",
            [Text.RegularExpressions.RegexOptions]::CultureInvariant
        )
        if (-not $parsed.Success) {
            throw "SHA256SUMS contains a malformed entry."
        }
        $name = $parsed.Groups["name"].Value
        if ($name -in @(".", "..") -or
            [IO.Path]::IsPathFullyQualified($name) -or
            $name.Contains("/") -or
            $name.Contains("\")) {
            throw "SHA256SUMS entries must contain exact leaf names only."
        }
        if ([string]::Equals(
            $name,
            $expectedName,
            [StringComparison]::Ordinal
        )) {
            $matches.Add(
                $parsed.Groups["hash"].Value.ToUpperInvariant()
            )
        }
    }
    if ($matches.Count -ne 1) {
        throw (
            "SHA256SUMS must contain exactly one entry for the requested " +
            "bundle file."
        )
    }
    return $matches[0]
}

function Get-CurrentLifecycleScriptPath {
    return [IO.Path]::GetFullPath($PSCommandPath)
}

function Read-ExactChecksumInventory {
    param(
        [Parameter(Mandatory)]
        [string]$ChecksumsPath
    )

    $expectedNames = @(
        $script:PackageFileName,
        $script:CertificateFileName,
        $script:InstallScriptFileName,
        $script:UninstallScriptFileName
    )
    $hashes = [ordered]@{}
    $lines = @(
        [IO.File]::ReadAllLines($ChecksumsPath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($lines.Count -ne 4) {
        throw "SHA256SUMS must contain exactly four handoff entries."
    }
    foreach ($line in $lines) {
        $parsed = [regex]::Match(
            $line,
            "^(?<hash>[0-9A-Fa-f]{64})[ `t]+(?:\*)?(?<name>[^`r`n]+)$",
            [Text.RegularExpressions.RegexOptions]::CultureInvariant
        )
        if (-not $parsed.Success) {
            throw "SHA256SUMS contains a malformed entry."
        }
        $name = $parsed.Groups["name"].Value
        if ($name -cnotin $expectedNames) {
            throw "SHA256SUMS contains an unexpected handoff entry."
        }
        if ($hashes.Contains($name)) {
            throw "SHA256SUMS contains a duplicate handoff entry."
        }
        $hashes[$name] =
            $parsed.Groups["hash"].Value.ToUpperInvariant()
    }
    foreach ($expectedName in $expectedNames) {
        if (-not $hashes.Contains($expectedName)) {
            throw "SHA256SUMS is missing an exact handoff entry."
        }
    }
    return $hashes
}

function Resolve-ExactBundleInventory {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$ChecksumsPath,

        [Parameter(Mandatory)]
        [string]$CurrentScriptPath
    )

    $resolvedPackage = Resolve-ExactBundleInput `
        -Path $PackagePath `
        -ExpectedExtension ".msix"
    $resolvedCertificate = Resolve-ExactBundleInput `
        -Path $CertificatePath `
        -ExpectedExtension ".cer"
    $resolvedChecksums = Resolve-ExactBundleInput `
        -Path $ChecksumsPath `
        -ExpectedExtension ".txt"
    if ([IO.Path]::GetFileName($resolvedPackage) -cne
        $script:PackageFileName -or
        [IO.Path]::GetFileName($resolvedCertificate) -cne
        $script:CertificateFileName -or
        [IO.Path]::GetFileName($resolvedChecksums) -cne "SHA256SUMS.txt") {
        throw "Bundle input names do not match the fixed handoff inventory."
    }

    $bundleDirectory = [IO.Path]::GetDirectoryName($resolvedPackage)
    $resolvedInstall = Resolve-ExactBundleInput `
        -Path (Join-Path $bundleDirectory $script:InstallScriptFileName) `
        -ExpectedExtension ".ps1"
    $resolvedUninstall = Resolve-ExactBundleInput `
        -Path (Join-Path $bundleDirectory $script:UninstallScriptFileName) `
        -ExpectedExtension ".ps1"
    Assert-SameBundleDirectory `
        -Paths @(
            $resolvedPackage,
            $resolvedCertificate,
            $resolvedChecksums,
            $resolvedInstall,
            $resolvedUninstall
        )

    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $resolvedCurrentScript = [IO.Path]::GetFullPath($CurrentScriptPath)
    if (-not [string]::Equals(
        $resolvedCurrentScript,
        $resolvedUninstall,
        $comparison
    )) {
        throw "The running uninstaller is not the exact bundled helper."
    }

    $hashes = Read-ExactChecksumInventory `
        -ChecksumsPath $resolvedChecksums
    $pathsByName = [ordered]@{
        $script:PackageFileName = $resolvedPackage
        $script:CertificateFileName = $resolvedCertificate
        $script:InstallScriptFileName = $resolvedInstall
        $script:UninstallScriptFileName = $resolvedUninstall
    }
    foreach ($entry in $pathsByName.GetEnumerator()) {
        Assert-FileSha256 `
            -Path $entry.Value `
            -ExpectedSha256 $hashes[$entry.Key]
    }

    return [pscustomobject]@{
        PackagePath = $resolvedPackage
        CertificatePath = $resolvedCertificate
        ChecksumsPath = $resolvedChecksums
        InstallScriptPath = $resolvedInstall
        UninstallScriptPath = $resolvedUninstall
        Hashes = $hashes
        PackageSha256 = $hashes[$script:PackageFileName]
        CertificateSha256 = $hashes[$script:CertificateFileName]
    }
}

function Assert-BundleInventoryUnchanged {
    param(
        [Parameter(Mandatory)]
        [psobject]$Inventory
    )

    $pathsByName = [ordered]@{
        $script:PackageFileName = $Inventory.PackagePath
        $script:CertificateFileName = $Inventory.CertificatePath
        $script:InstallScriptFileName = $Inventory.InstallScriptPath
        $script:UninstallScriptFileName = $Inventory.UninstallScriptPath
    }
    foreach ($entry in $pathsByName.GetEnumerator()) {
        Assert-FileSha256 `
            -Path $entry.Value `
            -ExpectedSha256 $Inventory.Hashes[$entry.Key]
    }
}

function Assert-FileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{64}$")]
        [string]$ExpectedSha256
    )

    $actual = (Get-FileHash `
        -LiteralPath $Path `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    if (-not (Test-FixedSha256Equal `
        -Expected $ExpectedSha256 `
        -Actual $actual)) {
        throw "Lifecycle input digest mismatch."
    }
}

function Get-InternalCertificateEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $bytes = [IO.File]::ReadAllBytes($Path)
    $certificate = $null
    try {
        $certificate =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $bytes
            )
        if ($certificate.HasPrivateKey) {
            throw "Internal certificate input must be public-only."
        }
        if (-not [string]::Equals(
            $certificate.Subject,
            $script:ExpectedCertificateSubject,
            [StringComparison]::Ordinal
        )) {
            throw "Internal certificate subject validation failed."
        }
        $thumbprint = $certificate.Thumbprint.ToUpperInvariant()
        if ($thumbprint -notmatch "^[0-9A-F]{40}$") {
            throw "Internal certificate thumbprint validation failed."
        }
        $sha256Bytes = [Security.Cryptography.SHA256]::HashData($bytes)
        return [pscustomobject]@{
            Subject = $certificate.Subject
            Thumbprint = $thumbprint
            Sha256 = [Convert]::ToHexString($sha256Bytes)
        }
    } catch {
        if ($_.Exception.Message -match "^Internal certificate ") {
            throw
        }
        throw "Internal certificate loading failed."
    } finally {
        if ($null -ne $certificate) {
            $certificate.Dispose()
        }
        if ($null -ne $bytes) {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
    }
}

function Assert-CertificateEvidenceExpected {
    param(
        [Parameter(Mandatory)]
        [psobject]$Evidence,

        [Parameter(Mandatory)]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        [string]$ExpectedThumbprint
    )

    if (-not [string]::Equals(
        $Evidence.Subject,
        $script:ExpectedCertificateSubject,
        [StringComparison]::Ordinal
    )) {
        throw "Internal certificate subject validation failed."
    }
    if (-not (Test-FixedSha256Equal `
        -Expected $ExpectedSha256 `
        -Actual $Evidence.Sha256)) {
        throw "Internal certificate byte validation failed."
    }
    if (-not (Test-FixedThumbprintEqual `
        -Expected $ExpectedThumbprint `
        -Actual $Evidence.Thumbprint)) {
        throw "Internal certificate thumbprint validation failed."
    }
}

function Read-CertificateInstallRecord {
    param(
        [switch]$AllowMissing
    )

    $recordPath = "HKCU:\Software\EMKE\Translation\Internal"
    if (-not (Test-Path -LiteralPath $recordPath)) {
        if ($AllowMissing) {
            return $null
        }
        throw "The exact Internal certificate install record is unavailable."
    }
    $record = Get-ItemProperty -LiteralPath $recordPath -ErrorAction Stop
    return [pscustomobject]@{
        PackageName = [string]$record.PackageName
        CertificateSubject = [string]$record.CertificateSubject
        CertificateThumbprint = [string]$record.CertificateThumbprint
        CertificateSha256 = [string]$record.CertificateSha256
    }
}

function Assert-InstallRecordMatchesCertificate {
    param(
        [Parameter(Mandatory)]
        [psobject]$Record,

        [Parameter(Mandatory)]
        [psobject]$Evidence
    )

    if (-not [string]::Equals(
        $Record.PackageName,
        $script:PackageName,
        [StringComparison]::Ordinal
    ) -or -not [string]::Equals(
        $Record.CertificateSubject,
        $script:ExpectedCertificateSubject,
        [StringComparison]::Ordinal
    )) {
        throw "Internal certificate install record identity is invalid."
    }
    if (-not (Test-FixedThumbprintEqual `
        -Expected $Record.CertificateThumbprint `
        -Actual $Evidence.Thumbprint)) {
        throw "Supplied certificate does not match the recorded thumbprint."
    }
    if (-not (Test-FixedSha256Equal `
        -Expected $Record.CertificateSha256 `
        -Actual $Evidence.Sha256)) {
        throw "Supplied certificate does not match the recorded bytes."
    }
}

function Set-ProtectedDirectoryAcl {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $Path,
            [IO.UnixFileMode]::UserRead -bor
                [IO.UnixFileMode]::UserWrite -bor
                [IO.UnixFileMode]::UserExecute
        )
        return
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $userSid = $identity.User
    $systemSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,
        $null
    )
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
        $null
    )
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner($userSid)
    $security.SetAccessRuleProtection($true, $false)
    $inheritance =
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @($userSid, $systemSid, $administratorsSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        )
        $null = $security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}

function New-ProtectedElevatedRequest {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Import", "Remove")]
        [string]$Operation,

        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{64}$")]
        [string]$ExpectedCertificateSha256,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{40}$")]
        [string]$ExpectedCertificateThumbprint
    )

    $baseDirectory = if ($IsWindows) {
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData
        )
    } else {
        [IO.Path]::GetTempPath()
    }
    if ([string]::IsNullOrWhiteSpace($baseDirectory)) {
        throw "Protected elevation staging base is unavailable."
    }
    $requestDirectory = Join-Path `
        $baseDirectory `
        ("emke-msix-elevation-" + [guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($requestDirectory) | Out-Null
    try {
        Set-ProtectedDirectoryAcl -Path $requestDirectory
        $requestPath = Join-Path $requestDirectory "request.json"
        $payload = [ordered]@{
            schemaVersion = 1
            operation = $Operation
            certificatePath = $CertificatePath
            certificateSha256 =
                $ExpectedCertificateSha256.ToUpperInvariant()
            certificateThumbprint =
                $ExpectedCertificateThumbprint.ToUpperInvariant()
            expectedSubject = $script:ExpectedCertificateSubject
        }
        $jsonBytes = [Text.UTF8Encoding]::new($false).GetBytes(
            (($payload | ConvertTo-Json -Compress) + "`n")
        )
        $stream = [IO.FileStream]::new(
            $requestPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None
        )
        try {
            $stream.Write($jsonBytes, 0, $jsonBytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
            [Array]::Clear($jsonBytes, 0, $jsonBytes.Length)
        }
        [IO.File]::SetAttributes(
            $requestPath,
            [IO.FileAttributes]::ReadOnly
        )
        $requestSha256 = (Get-FileHash `
            -LiteralPath $requestPath `
            -Algorithm SHA256).Hash.ToUpperInvariant()
        return [pscustomobject]@{
            DirectoryPath = $requestDirectory
            RequestPath = $requestPath
            RequestSha256 = $requestSha256
        }
    } catch {
        if (Test-Path -LiteralPath $requestDirectory) {
            Remove-Item -LiteralPath $requestDirectory -Recurse -Force
        }
        throw
    }
}

function Assert-ElevatedRequestUnchanged {
    param(
        [Parameter(Mandatory)]
        [psobject]$Request
    )

    $requestItem = Get-Item `
        -LiteralPath $Request.RequestPath `
        -Force `
        -ErrorAction Stop
    if ($requestItem.Name -cne "request.json" -or
        ($requestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne
        0 -or
        ($requestItem.Attributes -band [IO.FileAttributes]::ReadOnly) -eq
        0) {
        throw "Protected elevation request changed before execution."
    }
    Assert-FileSha256 `
        -Path $requestItem.FullName `
        -ExpectedSha256 $Request.RequestSha256
}

function Remove-ProtectedElevatedRequest {
    param(
        [Parameter(Mandatory)]
        [psobject]$Request
    )

    $expectedDirectory = [IO.Path]::GetDirectoryName($Request.RequestPath)
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath($Request.DirectoryPath),
        [IO.Path]::GetFullPath($expectedDirectory),
        [StringComparison]::OrdinalIgnoreCase
    ) -or [IO.Path]::GetFileName($Request.RequestPath) -cne "request.json") {
        throw "Protected elevation request cleanup target is invalid."
    }
    if (Test-Path -LiteralPath $Request.RequestPath -PathType Leaf) {
        [IO.File]::SetAttributes(
            $Request.RequestPath,
            [IO.FileAttributes]::Normal
        )
        [IO.File]::Delete($Request.RequestPath)
    }
    if (Test-Path -LiteralPath $Request.DirectoryPath -PathType Container) {
        if (@(Get-ChildItem `
            -LiteralPath $Request.DirectoryPath `
            -Force).Count -ne 0) {
            throw "Protected elevation request directory is not empty."
        }
        [IO.Directory]::Delete($Request.DirectoryPath)
    }
}

function Get-ElevatedCertificateChildSource {
    return @'
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-FixedHexEqual {
    param([string]$Expected, [string]$Actual, [int]$Length)
    if ($Expected -notmatch "^[0-9A-Fa-f]{$Length}$" -or
        $Actual -notmatch "^[0-9A-Fa-f]{$Length}$") {
        return $false
    }
    return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
        [Convert]::FromHexString($Expected),
        [Convert]::FromHexString($Actual)
    )
}

function Resolve-SafeLeaf {
    param([string]$Path, [string]$Extension)
    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not [IO.Path]::IsPathFullyQualified($Path) -or
        $Path -match "^[\\/]{2}") {
        throw "Unsafe elevated input path."
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetExtension($fullPath) -cne $Extension -or
        -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Unsafe elevated input file."
    }
    $current = Get-Item -LiteralPath $fullPath -Force
    if ($current.PSProvider.Name -cne "FileSystem" -or
        -not [string]::IsNullOrEmpty([string]$current.PSDrive.DisplayRoot)) {
        throw "Unsafe elevated input filesystem."
    }
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne
            0 -or -not [string]::IsNullOrEmpty([string]$current.LinkType)) {
            throw "Unsafe elevated input reparse chain."
        }
        $current = if ($current -is [IO.FileInfo]) {
            $current.Directory
        } else {
            $current.Parent
        }
    }
    return $fullPath
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )) {
        throw "Elevation is required."
    }

    $requestPath = [Environment]::GetEnvironmentVariable(
        "EMKE_ELEVATED_REQUEST_PATH",
        [EnvironmentVariableTarget]::Process
    )
    $requestSha256 = [Environment]::GetEnvironmentVariable(
        "EMKE_ELEVATED_REQUEST_SHA256",
        [EnvironmentVariableTarget]::Process
    )
    $resolvedRequest = Resolve-SafeLeaf -Path $requestPath -Extension ".json"
    $requestItem = Get-Item -LiteralPath $resolvedRequest -Force
    if (($requestItem.Attributes -band [IO.FileAttributes]::ReadOnly) -eq 0) {
        throw "Elevated request is not read-only."
    }
    $requestAcl = Get-Acl -LiteralPath $requestItem.DirectoryName
    if (-not $requestAcl.AreAccessRulesProtected) {
        throw "Elevated request ACL is not protected."
    }
    $requestStream = [IO.FileStream]::new(
        $resolvedRequest,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read
    )
    try {
        $requestMemory = [IO.MemoryStream]::new()
        try {
            $requestStream.CopyTo($requestMemory)
            $requestBytes = $requestMemory.ToArray()
        } finally {
            $requestMemory.Dispose()
        }
    } finally {
        $requestStream.Dispose()
    }
    try {
        $actualRequestSha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($requestBytes)
        )
        if (-not (Test-FixedHexEqual `
            -Expected $requestSha256 `
            -Actual $actualRequestSha256 `
            -Length 64)) {
            throw "Elevated request digest mismatch."
        }
        $request = ConvertFrom-Json `
            -InputObject ([Text.Encoding]::UTF8.GetString($requestBytes)) `
            -NoEnumerate
    } finally {
        [Array]::Clear($requestBytes, 0, $requestBytes.Length)
    }
    $propertyNames = @($request.PSObject.Properties.Name | Sort-Object)
    $expectedProperties = @(
        "certificatePath",
        "certificateSha256",
        "certificateThumbprint",
        "expectedSubject",
        "operation",
        "schemaVersion"
    )
    if (($propertyNames -join "|") -cne
        (($expectedProperties | Sort-Object) -join "|") -or
        [int]$request.schemaVersion -ne 1 -or
        [string]$request.operation -cnotin @("Import", "Remove") -or
        [string]$request.expectedSubject -cne "CN=EMKE Internal Test") {
        throw "Elevated request schema validation failed."
    }

    $certificatePath = Resolve-SafeLeaf `
        -Path ([string]$request.certificatePath) `
        -Extension ".cer"
    $certificateStream = [IO.FileStream]::new(
        $certificatePath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read
    )
    try {
        $certificateMemory = [IO.MemoryStream]::new()
        try {
            $certificateStream.CopyTo($certificateMemory)
            $certificateBytes = $certificateMemory.ToArray()
        } finally {
            $certificateMemory.Dispose()
        }
    } finally {
        $certificateStream.Dispose()
    }
    $certificate = $null
    try {
        $actualCertificateSha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($certificateBytes)
        )
        if (-not (Test-FixedHexEqual `
            -Expected ([string]$request.certificateSha256) `
            -Actual $actualCertificateSha256 `
            -Length 64)) {
            throw "Elevated certificate digest mismatch."
        }
        $certificate =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $certificateBytes
            )
        if ($certificate.HasPrivateKey -or
            $certificate.Subject -cne "CN=EMKE Internal Test" -or
            -not (Test-FixedHexEqual `
                -Expected ([string]$request.certificateThumbprint) `
                -Actual $certificate.Thumbprint `
                -Length 40)) {
            throw "Elevated certificate identity validation failed."
        }
        $store = [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
        )
        $resultExitCode = 0
        try {
            $store.Open(
                [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
            )
            $matches = @($store.Certificates | Where-Object {
                Test-FixedHexEqual `
                    -Expected ([string]$request.certificateThumbprint) `
                    -Actual $_.Thumbprint `
                    -Length 40
            })
            if ($matches.Count -gt 1) {
                throw "Duplicate exact certificate."
            }
            if ($matches.Count -eq 1) {
                $storedSha256 = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData(
                        $matches[0].RawData
                    )
                )
                if ($matches[0].Subject -cne "CN=EMKE Internal Test" -or
                    -not (Test-FixedHexEqual `
                        -Expected ([string]$request.certificateSha256) `
                        -Actual $storedSha256 `
                        -Length 64)) {
                    throw "Stored exact certificate validation failed."
                }
            }
            if ($request.operation -ceq "Import" -and $matches.Count -eq 0) {
                $store.Add($certificate)
            } elseif (
                $request.operation -ceq "Import" -and
                $matches.Count -eq 1
            ) {
                $resultExitCode = 10
            } elseif (
                $request.operation -ceq "Remove" -and
                $matches.Count -eq 1
            ) {
                $store.Remove($matches[0])
            }
            $postMatches = @($store.Certificates | Where-Object {
                Test-FixedHexEqual `
                    -Expected ([string]$request.certificateThumbprint) `
                    -Actual $_.Thumbprint `
                    -Length 40
            })
            if (($request.operation -ceq "Import" -and
                $postMatches.Count -ne 1) -or
                ($request.operation -ceq "Remove" -and
                $postMatches.Count -ne 0)) {
                throw "Certificate store postcondition failed."
            }
            if ($postMatches.Count -eq 1) {
                $postSha256 = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData(
                        $postMatches[0].RawData
                    )
                )
                if ($postMatches[0].Subject -cne
                    "CN=EMKE Internal Test" -or
                    -not (Test-FixedHexEqual `
                        -Expected ([string]$request.certificateSha256) `
                        -Actual $postSha256 `
                        -Length 64)) {
                    throw "Certificate store postcondition identity failed."
                }
            }
        } finally {
            $store.Close()
            $store.Dispose()
        }
    } finally {
        if ($null -ne $certificate) {
            $certificate.Dispose()
        }
        [Array]::Clear(
            $certificateBytes,
            0,
            $certificateBytes.Length
        )
    }
    exit $resultExitCode
} catch {
    Write-Error "Elevated certificate operation failed."
    exit 23
}
'@
}

function Resolve-TrustedPowerShell {
    $candidate = Join-Path $PSHOME "pwsh.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Trusted PowerShell 7 executable is unavailable."
    }
    $item = Get-Item -LiteralPath $candidate -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::IsNullOrEmpty([string]$item.LinkType)) {
        throw "Trusted PowerShell 7 executable validation failed."
    }
    return $candidate
}

function Invoke-ElevatedCertificateOperation {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Import", "Remove")]
        [string]$Operation,

        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$ExpectedCertificateSha256,

        [Parameter(Mandatory)]
        [string]$ExpectedCertificateThumbprint
    )

    $request = New-ProtectedElevatedRequest `
        -Operation $Operation `
        -CertificatePath $CertificatePath `
        -ExpectedCertificateSha256 $ExpectedCertificateSha256 `
        -ExpectedCertificateThumbprint $ExpectedCertificateThumbprint
    $oldRequestPath = [Environment]::GetEnvironmentVariable(
        "EMKE_ELEVATED_REQUEST_PATH",
        [EnvironmentVariableTarget]::Process
    )
    $oldRequestSha256 = [Environment]::GetEnvironmentVariable(
        "EMKE_ELEVATED_REQUEST_SHA256",
        [EnvironmentVariableTarget]::Process
    )
    $requestLock = $null
    $certificateLock = $null
    $operationResult = $null
    try {
        $requestLock = [IO.FileStream]::new(
            $request.RequestPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read
        )
        $certificateLock = [IO.FileStream]::new(
            $CertificatePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read
        )
        Assert-ElevatedRequestUnchanged -Request $request
        $source = Get-ElevatedCertificateChildSource
        $encodedCommand = [Convert]::ToBase64String(
            [Text.Encoding]::Unicode.GetBytes($source)
        )
        $argumentString = (
            "-NoLogo -NoProfile -NonInteractive " +
            "-EncodedCommand $encodedCommand"
        )
        [Environment]::SetEnvironmentVariable(
            "EMKE_ELEVATED_REQUEST_PATH",
            $request.RequestPath,
            [EnvironmentVariableTarget]::Process
        )
        [Environment]::SetEnvironmentVariable(
            "EMKE_ELEVATED_REQUEST_SHA256",
            $request.RequestSha256,
            [EnvironmentVariableTarget]::Process
        )
        $powerShellPath = Resolve-TrustedPowerShell
        $process = Start-Process `
            -FilePath $powerShellPath `
            -ArgumentList $argumentString `
            -Verb RunAs `
            -Wait `
            -PassThru
        if ($Operation -ceq "Import") {
            $operationResult = switch ([int]$process.ExitCode) {
                0 { "Added"; break }
                10 { "AlreadyPresent"; break }
                default {
                    throw (
                        "Elevated certificate operation failed or UAC was " +
                        "cancelled; the exact operation can be retried."
                    )
                }
            }
        } elseif ($process.ExitCode -ne 0) {
            throw (
                "Elevated certificate operation failed or UAC was " +
                "cancelled; the exact operation can be retried."
            )
        }
        Assert-ElevatedRequestUnchanged -Request $request
    } finally {
        if ($null -ne $certificateLock) {
            $certificateLock.Dispose()
        }
        if ($null -ne $requestLock) {
            $requestLock.Dispose()
        }
        [Environment]::SetEnvironmentVariable(
            "EMKE_ELEVATED_REQUEST_PATH",
            $oldRequestPath,
            [EnvironmentVariableTarget]::Process
        )
        [Environment]::SetEnvironmentVariable(
            "EMKE_ELEVATED_REQUEST_SHA256",
            $oldRequestSha256,
            [EnvironmentVariableTarget]::Process
        )
        Remove-ProtectedElevatedRequest -Request $request
    }
    return $operationResult
}

function Invoke-ElevatedCertificateRemoval {
    param(
        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$ExpectedCertificateSha256,

        [Parameter(Mandatory)]
        [string]$ExpectedCertificateThumbprint
    )

    Invoke-ElevatedCertificateOperation `
        -Operation "Remove" `
        -CertificatePath $CertificatePath `
        -ExpectedCertificateSha256 $ExpectedCertificateSha256 `
        -ExpectedCertificateThumbprint $ExpectedCertificateThumbprint
}

function Assert-InternalPackageIdentity {
    param(
        [Parameter(Mandatory)]
        [psobject]$Package
    )

    if (-not [string]::Equals(
        [string]$Package.Name,
        $script:PackageName,
        [StringComparison]::Ordinal
    )) {
        throw "Installed package Name validation failed."
    }
    if (-not [string]::Equals(
        [string]$Package.Publisher,
        $script:ExpectedPublisher,
        [StringComparison]::Ordinal
    )) {
        throw "Installed package Publisher validation failed."
    }
    if (-not [string]::Equals(
        [string]$Package.Version,
        $script:ExpectedVersion,
        [StringComparison]::Ordinal
    )) {
        throw "Installed package Version validation failed."
    }
    if (-not [string]::Equals(
        [string]$Package.Architecture,
        $script:ExpectedArchitecture,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Installed package Architecture validation failed."
    }
}

function Get-ExactInstalledInternalPackage {
    $matches = @(Get-AppxPackage `
        -Name $script:PackageName `
        -ErrorAction Stop | Where-Object {
            [string]::Equals(
                [string]$_.Name,
                $script:PackageName,
                [StringComparison]::Ordinal
            )
        })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one installed Internal package."
    }
    Assert-InternalPackageIdentity -Package $matches[0]
    return $matches[0]
}

function Get-OptionalInstalledInternalPackage {
    $matches = @(Get-AppxPackage `
        -Name $script:PackageName `
        -ErrorAction Stop | Where-Object {
            [string]::Equals(
                [string]$_.Name,
                $script:PackageName,
                [StringComparison]::Ordinal
            )
        })
    if ($matches.Count -gt 1) {
        throw "More than one exact Internal package was returned."
    }
    if ($matches.Count -eq 0) {
        return $null
    }
    return $matches[0]
}

function Invoke-RemoveExactAppxPackage {
    param(
        [Parameter(Mandatory)]
        [string]$PackageFullName
    )

    Remove-AppxPackage -Package $PackageFullName -ErrorAction Stop
}

function Assert-InternalPackageAbsent {
    $remaining = @(Get-AppxPackage `
        -Name $script:PackageName `
        -ErrorAction Stop | Where-Object {
            [string]::Equals(
                [string]$_.Name,
                $script:PackageName,
                [StringComparison]::Ordinal
            )
        })
    if ($remaining.Count -ne 0) {
        throw "Exact Internal package removal verification failed."
    }
}

function Remove-CertificateInstallRecord {
    $recordPath = "HKCU:\Software\EMKE\Translation\Internal"
    if (Test-Path -LiteralPath $recordPath) {
        Remove-Item -LiteralPath $recordPath -Force
    }
}

function Invoke-UninstallInternalMsix {
    param(
        [switch]$RemoveCertificate,

        [switch]$ConfirmRemoveCertificate,

        [string]$CertificatePath,

        [string]$ChecksumsPath,

        [ValidatePattern("^[0-9A-Fa-f]{40}$")]
        [string]$ExpectedCertificateThumbprint
    )

    if ($RemoveCertificate -and -not $ConfirmRemoveCertificate) {
        throw (
            "Certificate removal requires the explicit " +
            "-ConfirmRemoveCertificate switch."
        )
    }
    if (-not $RemoveCertificate -and $ConfirmRemoveCertificate) {
        throw (
            "-ConfirmRemoveCertificate is valid only with " +
            "-RemoveCertificate."
        )
    }
    Assert-SupportedUninstallParent

    $certificateEvidence = $null
    $inventory = $null
    $trustedThumbprint = $null
    if ($RemoveCertificate) {
        if ([string]::IsNullOrWhiteSpace($CertificatePath) -or
            [string]::IsNullOrWhiteSpace($ChecksumsPath) -or
            [string]::IsNullOrWhiteSpace(
                $ExpectedCertificateThumbprint
            )) {
            throw (
                "-CertificatePath, -ChecksumsPath, and the fixed " +
                "-ExpectedCertificateThumbprint are required when removing " +
                "the certificate."
            )
        }
        $trustedThumbprint =
            $ExpectedCertificateThumbprint.ToUpperInvariant()
        $derivedPackagePath = Join-Path `
            ([IO.Path]::GetDirectoryName(
                [IO.Path]::GetFullPath($CertificatePath)
            )) `
            $script:PackageFileName
        $inventory = Resolve-ExactBundleInventory `
            -PackagePath $derivedPackagePath `
            -CertificatePath $CertificatePath `
            -ChecksumsPath $ChecksumsPath `
            -CurrentScriptPath (Get-CurrentLifecycleScriptPath)
        $certificateEvidence = Get-InternalCertificateEvidence `
            -Path $inventory.CertificatePath
        Assert-CertificateEvidenceExpected `
            -Evidence $certificateEvidence `
            -ExpectedSha256 $inventory.CertificateSha256 `
            -ExpectedThumbprint $trustedThumbprint
        $record = Read-CertificateInstallRecord -AllowMissing
        if ($null -ne $record) {
            Assert-InstallRecordMatchesCertificate `
                -Record $record `
                -Evidence $certificateEvidence
        }
    }

    $package = Get-OptionalInstalledInternalPackage
    if ($null -ne $package) {
        Invoke-RemoveExactAppxPackage `
            -PackageFullName $package.PackageFullName
    }
    Assert-InternalPackageAbsent

    if ($RemoveCertificate) {
        Assert-BundleInventoryUnchanged -Inventory $inventory
        Invoke-ElevatedCertificateRemoval `
            -CertificatePath $inventory.CertificatePath `
            -ExpectedCertificateSha256 $inventory.CertificateSha256 `
            -ExpectedCertificateThumbprint $trustedThumbprint
        Remove-CertificateInstallRecord
    }
    Write-Output "Uninstalled package: $($script:PackageName)"
}

try {
    Invoke-UninstallInternalMsix `
        -RemoveCertificate:$RemoveCertificate `
        -ConfirmRemoveCertificate:$ConfirmRemoveCertificate `
        -CertificatePath $CertificatePath `
        -ChecksumsPath $ChecksumsPath `
        -ExpectedCertificateThumbprint $ExpectedCertificateThumbprint
} catch {
    Write-Error $_
    exit 1
}
