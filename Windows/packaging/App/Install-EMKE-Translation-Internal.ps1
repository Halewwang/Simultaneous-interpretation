[CmdletBinding(DefaultParameterSetName = "Install")]
param(
    [Parameter(Mandatory, ParameterSetName = "Install")]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath,

    [Parameter(Mandatory, ParameterSetName = "Install")]
    [ValidateNotNullOrEmpty()]
    [string]$CertificatePath,

    [Parameter(Mandatory, ParameterSetName = "Install")]
    [ValidateNotNullOrEmpty()]
    [string]$ChecksumsPath,

    [Parameter(ParameterSetName = "Install")]
    [switch]$ConfirmTrust,

    [Parameter(Mandatory, ParameterSetName = "ImportCertificateChild")]
    [switch]$ImportCertificateChild,

    [Parameter(Mandatory, ParameterSetName = "ImportCertificateChild")]
    [ValidateNotNullOrEmpty()]
    [string]$VerifiedCertificatePath,

    [Parameter(Mandatory, ParameterSetName = "ImportCertificateChild")]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedCertificateSha256,

    [Parameter(Mandatory, ParameterSetName = "ImportCertificateChild")]
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
$script:ExpectedVersion = "0.1.0.0"
$script:ExpectedArchitecture = "x64"
$script:ExpectedCertificateSubject = "CN=EMKE Internal Test"

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

function Assert-SupportedInstallParent {
    Assert-PowerShell7Windows
    if (Test-IsAdministrator) {
        throw (
            "Run this helper from a non-elevated PowerShell session. " +
            "Only the certificate import child may request elevation."
        )
    }
}

function Assert-SupportedCertificateChild {
    Assert-PowerShell7Windows
    if (-not (Test-IsAdministrator)) {
        throw "The certificate import child requires elevation."
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
        [ValidateSet(".msix", ".cer", ".txt")]
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

    if ($Paths.Count -lt 2) {
        throw "At least two bundle inputs are required."
    }
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

function Get-RawCertificateSha256 {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $digest = [Security.Cryptography.SHA256]::HashData($Certificate.RawData)
    return [Convert]::ToHexString($digest)
}

function Add-ExactTrustedPeopleCertificate {
    param(
        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$ExpectedThumbprint
    )

    $certificate =
        [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [IO.File]::ReadAllBytes($CertificatePath)
        )
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
    )
    try {
        if (-not (Test-FixedThumbprintEqual `
            -Expected $ExpectedThumbprint `
            -Actual $certificate.Thumbprint)) {
            throw "Certificate import thumbprint validation failed."
        }
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
        )
        $existing = @($store.Certificates | Where-Object {
            Test-FixedThumbprintEqual `
                -Expected $ExpectedThumbprint `
                -Actual $_.Thumbprint
        })
        if ($existing.Count -gt 1) {
            throw "Trusted People contains duplicate exact certificates."
        }
        if ($existing.Count -eq 0) {
            $store.Add($certificate)
        }
    } finally {
        $store.Close()
        $store.Dispose()
        $certificate.Dispose()
    }
}

function Assert-TrustedPeopleCertificate {
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedThumbprint,

        [Parameter(Mandatory)]
        [string]$ExpectedRawSha256
    )

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
    )
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly
        )
        $matches = @($store.Certificates | Where-Object {
            Test-FixedThumbprintEqual `
                -Expected $ExpectedThumbprint `
                -Actual $_.Thumbprint
        })
        if ($matches.Count -ne 1) {
            throw "Exact Trusted People certificate verification failed."
        }
        if (-not [string]::Equals(
            $matches[0].Subject,
            $script:ExpectedCertificateSubject,
            [StringComparison]::Ordinal
        )) {
            throw "Trusted People certificate subject validation failed."
        }
        $rawSha256 = Get-RawCertificateSha256 -Certificate $matches[0]
        if (-not (Test-FixedSha256Equal `
            -Expected $ExpectedRawSha256 `
            -Actual $rawSha256)) {
            throw "Trusted People certificate byte validation failed."
        }
    } finally {
        $store.Close()
        $store.Dispose()
    }
}

function Invoke-ImportCertificateChild {
    param(
        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        [string]$ExpectedThumbprint
    )

    Assert-SupportedCertificateChild
    $resolvedCertificate = Resolve-ExactBundleInput `
        -Path $CertificatePath `
        -ExpectedExtension ".cer"
    Assert-FileSha256 `
        -Path $resolvedCertificate `
        -ExpectedSha256 $ExpectedSha256
    $evidence = Get-InternalCertificateEvidence `
        -Path $resolvedCertificate
    Assert-CertificateEvidenceExpected `
        -Evidence $evidence `
        -ExpectedSha256 $ExpectedSha256 `
        -ExpectedThumbprint $ExpectedThumbprint
    Add-ExactTrustedPeopleCertificate `
        -CertificatePath $resolvedCertificate `
        -ExpectedThumbprint $ExpectedThumbprint
    Assert-TrustedPeopleCertificate `
        -ExpectedThumbprint $ExpectedThumbprint `
        -ExpectedRawSha256 $ExpectedSha256
}

function Resolve-TrustedPowerShell {
    $candidate = Join-Path $PSHOME "pwsh.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Trusted PowerShell 7 executable is unavailable."
    }
    return $candidate
}

function Invoke-ElevatedCertificateImport {
    param(
        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$ExpectedCertificateSha256,

        [Parameter(Mandatory)]
        [string]$ExpectedCertificateThumbprint
    )

    $powerShellPath = Resolve-TrustedPowerShell
    $arguments = @(
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-File",
        $PSCommandPath,
        "-ImportCertificateChild",
        "-VerifiedCertificatePath",
        $CertificatePath,
        "-ExpectedCertificateSha256",
        $ExpectedCertificateSha256,
        "-ExpectedCertificateThumbprint",
        $ExpectedCertificateThumbprint
    )
    $process = Start-Process `
        -FilePath $powerShellPath `
        -ArgumentList $arguments `
        -Verb RunAs `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Elevated certificate import failed with a stable child error."
    }
}

function Invoke-AddExactAppxPackage {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    Add-AppxPackage -Path $PackagePath -ErrorAction Stop
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
    return $matches[0]
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

function Assert-InstalledInternalPackage {
    $package = Get-ExactInstalledInternalPackage
    Assert-InternalPackageIdentity -Package $package
    return $package
}

function Write-CertificateInstallRecord {
    param(
        [Parameter(Mandatory)]
        [psobject]$CertificateEvidence
    )

    $recordPath = "HKCU:\Software\EMKE\Translation\Internal"
    $null = New-Item -Path $recordPath -Force
    $values = @{
        PackageName = $script:PackageName
        CertificateSubject = $CertificateEvidence.Subject
        CertificateThumbprint = $CertificateEvidence.Thumbprint
        CertificateSha256 = $CertificateEvidence.Sha256
    }
    foreach ($entry in $values.GetEnumerator()) {
        $null = New-ItemProperty `
            -Path $recordPath `
            -Name $entry.Key `
            -Value $entry.Value `
            -PropertyType String `
            -Force
    }
}

function Invoke-InstallInternalMsix {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$ChecksumsPath,

        [switch]$ConfirmTrust
    )

    if (-not $ConfirmTrust) {
        throw (
            "Certificate trust requires the explicit -ConfirmTrust switch."
        )
    }
    Assert-SupportedInstallParent

    $resolvedPackage = Resolve-ExactBundleInput `
        -Path $PackagePath `
        -ExpectedExtension ".msix"
    $resolvedCertificate = Resolve-ExactBundleInput `
        -Path $CertificatePath `
        -ExpectedExtension ".cer"
    $resolvedChecksums = Resolve-ExactBundleInput `
        -Path $ChecksumsPath `
        -ExpectedExtension ".txt"
    Assert-SameBundleDirectory `
        -Paths @(
            $resolvedPackage,
            $resolvedCertificate,
            $resolvedChecksums
        )

    $packageSha256 = Read-ExpectedSha256 `
        -ChecksumsPath $resolvedChecksums `
        -FilePath $resolvedPackage
    $certificateSha256 = Read-ExpectedSha256 `
        -ChecksumsPath $resolvedChecksums `
        -FilePath $resolvedCertificate
    Assert-FileSha256 `
        -Path $resolvedPackage `
        -ExpectedSha256 $packageSha256
    Assert-FileSha256 `
        -Path $resolvedCertificate `
        -ExpectedSha256 $certificateSha256
    $certificateEvidence = Get-InternalCertificateEvidence `
        -Path $resolvedCertificate
    Assert-CertificateEvidenceExpected `
        -Evidence $certificateEvidence `
        -ExpectedSha256 $certificateSha256 `
        -ExpectedThumbprint $certificateEvidence.Thumbprint

    Invoke-ElevatedCertificateImport `
        -CertificatePath $resolvedCertificate `
        -ExpectedCertificateSha256 $certificateSha256 `
        -ExpectedCertificateThumbprint $certificateEvidence.Thumbprint

    Assert-FileSha256 `
        -Path $resolvedPackage `
        -ExpectedSha256 $packageSha256
    Assert-FileSha256 `
        -Path $resolvedCertificate `
        -ExpectedSha256 $certificateSha256
    $postElevationEvidence = Get-InternalCertificateEvidence `
        -Path $resolvedCertificate
    Assert-CertificateEvidenceExpected `
        -Evidence $postElevationEvidence `
        -ExpectedSha256 $certificateSha256 `
        -ExpectedThumbprint $certificateEvidence.Thumbprint
    Write-CertificateInstallRecord `
        -CertificateEvidence $postElevationEvidence

    Invoke-AddExactAppxPackage -PackagePath $resolvedPackage
    $null = Assert-InstalledInternalPackage
    Write-Output "Installed package: $($script:PackageName)"
}

try {
    if ($PSCmdlet.ParameterSetName -ceq "ImportCertificateChild") {
        Invoke-ImportCertificateChild `
            -CertificatePath $VerifiedCertificatePath `
            -ExpectedSha256 $ExpectedCertificateSha256 `
            -ExpectedThumbprint $ExpectedCertificateThumbprint
        exit 0
    }

    Invoke-InstallInternalMsix `
        -PackagePath $PackagePath `
        -CertificatePath $CertificatePath `
        -ChecksumsPath $ChecksumsPath `
        -ConfirmTrust:$ConfirmTrust
} catch {
    if ($PSCmdlet.ParameterSetName -ceq "ImportCertificateChild") {
        Write-Error "Elevated certificate import failed."
        exit 21
    }
    Write-Error $_
    exit 1
}
