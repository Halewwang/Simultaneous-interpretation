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

    [Parameter(Mandatory, ParameterSetName = "RemoveCertificateChild")]
    [switch]$RemoveCertificateChild,

    [Parameter(Mandatory, ParameterSetName = "RemoveCertificateChild")]
    [ValidateNotNullOrEmpty()]
    [string]$VerifiedCertificatePath,

    [Parameter(Mandatory, ParameterSetName = "RemoveCertificateChild")]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedCertificateSha256,

    [Parameter(Mandatory, ParameterSetName = "RemoveCertificateChild")]
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

function Assert-SupportedUninstallParent {
    Assert-PowerShell7Windows
    if (Test-IsAdministrator) {
        throw (
            "Run this helper from a non-elevated PowerShell session. " +
            "Only the certificate removal child may request elevation."
        )
    }
}

function Assert-SupportedCertificateChild {
    Assert-PowerShell7Windows
    if (-not (Test-IsAdministrator)) {
        throw "The certificate removal child requires elevation."
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
        [ValidateSet(".cer", ".txt")]
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

function Read-CertificateInstallRecord {
    $recordPath = "HKCU:\Software\EMKE\Translation\Internal"
    if (-not (Test-Path -LiteralPath $recordPath)) {
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

function Remove-ExactTrustedPeopleCertificate {
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
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
        )
        $matches = @($store.Certificates | Where-Object {
            Test-FixedThumbprintEqual `
                -Expected $ExpectedThumbprint `
                -Actual $_.Thumbprint
        })
        if ($matches.Count -gt 1) {
            throw "Trusted People contains duplicate exact certificates."
        }
        if ($matches.Count -eq 0) {
            return
        }
        $certificate = $matches[0]
        if (-not [string]::Equals(
            $certificate.Subject,
            $script:ExpectedCertificateSubject,
            [StringComparison]::Ordinal
        )) {
            throw "Exact certificate subject validation failed."
        }
        $rawSha256 = Get-RawCertificateSha256 -Certificate $certificate
        if (-not (Test-FixedSha256Equal `
            -Expected $ExpectedRawSha256 `
            -Actual $rawSha256)) {
            throw "Exact certificate byte validation failed."
        }
        $store.Remove($certificate)

        $remaining = @($store.Certificates | Where-Object {
            Test-FixedThumbprintEqual `
                -Expected $ExpectedThumbprint `
                -Actual $_.Thumbprint
        })
        if ($remaining.Count -ne 0) {
            throw "Exact Trusted People certificate removal failed."
        }
    } finally {
        $store.Close()
        $store.Dispose()
    }
}

function Invoke-RemoveCertificateChild {
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
    Remove-ExactTrustedPeopleCertificate `
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

function Invoke-ElevatedCertificateRemoval {
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
        "-RemoveCertificateChild",
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
        throw "Elevated certificate removal failed with a stable child error."
    }
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

        [string]$ChecksumsPath
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
    if ($RemoveCertificate) {
        if ([string]::IsNullOrWhiteSpace($CertificatePath) -or
            [string]::IsNullOrWhiteSpace($ChecksumsPath)) {
            throw (
                "-CertificatePath and -ChecksumsPath are required when " +
                "removing the certificate."
            )
        }
        $resolvedCertificate = Resolve-ExactBundleInput `
            -Path $CertificatePath `
            -ExpectedExtension ".cer"
        $resolvedChecksums = Resolve-ExactBundleInput `
            -Path $ChecksumsPath `
            -ExpectedExtension ".txt"
        Assert-SameBundleDirectory `
            -Paths @($resolvedCertificate, $resolvedChecksums)
        $certificateSha256 = Read-ExpectedSha256 `
            -ChecksumsPath $resolvedChecksums `
            -FilePath $resolvedCertificate
        Assert-FileSha256 `
            -Path $resolvedCertificate `
            -ExpectedSha256 $certificateSha256
        $certificateEvidence = Get-InternalCertificateEvidence `
            -Path $resolvedCertificate
        Assert-CertificateEvidenceExpected `
            -Evidence $certificateEvidence `
            -ExpectedSha256 $certificateSha256 `
            -ExpectedThumbprint $certificateEvidence.Thumbprint
        $record = Read-CertificateInstallRecord
        Assert-InstallRecordMatchesCertificate `
            -Record $record `
            -Evidence $certificateEvidence
    }

    $package = Get-ExactInstalledInternalPackage
    Invoke-RemoveExactAppxPackage `
        -PackageFullName $package.PackageFullName
    Assert-InternalPackageAbsent

    if ($RemoveCertificate) {
        Invoke-ElevatedCertificateRemoval `
            -CertificatePath $resolvedCertificate `
            -ExpectedCertificateSha256 $certificateSha256 `
            -ExpectedCertificateThumbprint $certificateEvidence.Thumbprint
        Remove-CertificateInstallRecord
    }
    Write-Output "Uninstalled package: $($script:PackageName)"
}

try {
    if ($PSCmdlet.ParameterSetName -ceq "RemoveCertificateChild") {
        Invoke-RemoveCertificateChild `
            -CertificatePath $VerifiedCertificatePath `
            -ExpectedSha256 $ExpectedCertificateSha256 `
            -ExpectedThumbprint $ExpectedCertificateThumbprint
        exit 0
    }

    Invoke-UninstallInternalMsix `
        -RemoveCertificate:$RemoveCertificate `
        -ConfirmRemoveCertificate:$ConfirmRemoveCertificate `
        -CertificatePath $CertificatePath `
        -ChecksumsPath $ChecksumsPath
} catch {
    if ($PSCmdlet.ParameterSetName -ceq "RemoveCertificateChild") {
        Write-Error "Elevated certificate removal failed."
        exit 22
    }
    Write-Error $_
    exit 1
}
