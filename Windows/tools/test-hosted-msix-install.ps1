[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CertificatePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$ExpectedCertificateThumbprint,

    [ValidateSet('EMKE.Translation.Internal')]
    [string]$ExpectedPackageIdentity = 'EMKE.Translation.Internal',

    [ValidateSet('CN=EMKE Internal Test')]
    [string]$ExpectedPublisher = 'CN=EMKE Internal Test',

    [Parameter(Mandatory)]
    [ValidatePattern('^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$')]
    [string]$ExpectedVersion,

    [ValidateSet('X64')]
    [string]$ExpectedArchitecture = 'X64',

    [ValidateSet('driverMissing', 'unsupportedWindowsProductType')]
    [string]$ExpectedSmokeStatus = 'driverMissing',

    [ValidateSet('EMKE.Windows.App.exe')]
    [string]$SmokeExecutableRelativePath = 'EMKE.Windows.App.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Hosted MSIX installation validation requires Windows.'
}

function Assert-NoReparsePathChain {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $currentPath = $fullPath
    while ($true) {
        if (-not (Test-Path -LiteralPath $currentPath)) {
            throw 'Hosted MSIX input path chain validation failed.'
        }
        $currentItem = Get-Item -LiteralPath $currentPath -Force
        $linkProperty = $currentItem.PSObject.Properties['LinkType']
        $linkType = if ($null -eq $linkProperty) {
            $null
        } else {
            $linkProperty.Value
        }
        if (
            $null -ne $linkType -or
            ($currentItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw 'Hosted MSIX input paths must not contain reparse points.'
        }

        $parent = [IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) {
            break
        }
        $currentPath = $parent.FullName
    }

    return $fullPath
}

function Resolve-ExactLeafFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ExpectedExtension
    )

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw 'Hosted MSIX input paths must be absolute.'
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Hosted MSIX input file is unavailable.'
    }
    $resolvedPath = Assert-NoReparsePathChain -Path $Path
    $item = Get-Item -LiteralPath $resolvedPath -Force
    if (
        $item.Extension -cne $ExpectedExtension -or
        $null -ne $item.LinkType -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'Hosted MSIX input file validation failed.'
    }
    return $item.FullName
}

function Get-ExactInstalledPackage {
    $packages = @(
        Get-AppxPackage -Name $ExpectedPackageIdentity |
            Where-Object {
                $_.Name -ceq $ExpectedPackageIdentity
            }
    )
    if ($packages.Count -ne 1) {
        throw (
            "Expected exactly one installed package named " +
            "'$ExpectedPackageIdentity'; found $($packages.Count)."
        )
    }
    return $packages[0]
}

function Assert-DriverMissingSmokeRecord {
    param(
        [Parameter(Mandatory)]
        [string]$Json,

        [Parameter(Mandatory)]
        [ValidateSet('driverMissing', 'unsupportedWindowsProductType')]
        [string]$ExpectedStatus
    )

    try {
        $smoke = ConvertFrom-Json -InputObject $Json -NoEnumerate
    } catch {
        throw 'Driver-missing smoke output is not valid JSON.'
    }

    if ($smoke -isnot [pscustomobject]) {
        throw 'Driver-missing smoke output must be one JSON object.'
    }
    $requiredProperties = @(
        'status',
        'translationStartAllowed',
        'networkOpenCount',
        'audioStartCount'
    )
    foreach ($propertyName in $requiredProperties) {
        if ($null -eq $smoke.PSObject.Properties[$propertyName]) {
            throw "Driver-missing smoke is missing '$propertyName'."
        }
    }
    if (
        $smoke.status -isnot [string] -or
        $smoke.status -cne $ExpectedStatus -or
        $smoke.translationStartAllowed -isnot [bool] -or
        $smoke.translationStartAllowed -ne $false -or
        $smoke.networkOpenCount -isnot [long] -or
        $smoke.networkOpenCount -ne 0 -or
        $smoke.audioStartCount -isnot [long] -or
        $smoke.audioStartCount -ne 0
    ) {
        throw (
            'Driver-missing smoke must block translation with zero ' +
            'network opens and zero audio starts.'
        )
    }
}

function Invoke-DriverMissingSmoke {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath,

        [Parameter(Mandatory)]
        [ValidateSet('driverMissing', 'unsupportedWindowsProductType')]
        [string]$ExpectedStatus
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw 'Installed smoke executable is unavailable.'
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('--hosted-driver-missing-smoke')

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Driver-missing smoke process did not start.'
        }
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'Driver-missing smoke process exceeded 30 seconds.'
        }
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if ($process.ExitCode -ne 0) {
            throw "Driver-missing smoke exited with code $($process.ExitCode)."
        }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            throw 'Driver-missing smoke emitted standard-error output.'
        }

        $lines = @(
            $stdout -split '\r?\n' |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
        if ($lines.Count -ne 1) {
            throw 'Driver-missing smoke must emit exactly one JSON record.'
        }
        Assert-DriverMissingSmokeRecord `
            -Json $lines[0] `
            -ExpectedStatus $ExpectedStatus
    } finally {
        $process.Dispose()
    }
}

$resolvedPackagePath = Resolve-ExactLeafFile `
    -Path $PackagePath `
    -ExpectedExtension '.msix'
$resolvedCertificatePath = Resolve-ExactLeafFile `
    -Path $CertificatePath `
    -ExpectedExtension '.cer'
$expectedThumbprint = $ExpectedCertificateThumbprint.ToUpperInvariant()
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    [IO.File]::ReadAllBytes($resolvedCertificatePath)
)
$trustedPeopleStore = $null
$certificateAdded = $false
$installationAttempted = $false
$installedPackage = $null

try {
    if (
        $certificate.HasPrivateKey -or
        $certificate.Subject -cne $ExpectedPublisher -or
        $certificate.Thumbprint.ToUpperInvariant() -cne $expectedThumbprint
    ) {
        throw 'Hosted public certificate validation failed.'
    }

    if (@(Get-AppxPackage -Name $ExpectedPackageIdentity).Count -ne 0) {
        throw 'The exact Internal package was already installed on the runner.'
    }
    if (
        Test-Path -LiteralPath (
            "Cert:\LocalMachine\TrustedPeople\$expectedThumbprint"
        )
    ) {
        throw 'The exact Internal certificate was already trusted on the runner.'
    }

    $trustedPeopleStore =
        [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
        )
    $trustedPeopleStore.Open(
        [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
    )
    $trustedPeopleStore.Add($certificate)
    $certificateAdded = $true

    $installationAttempted = $true
    Add-AppxPackage -Path $resolvedPackagePath -ErrorAction Stop
    $installedPackage = Get-ExactInstalledPackage

    if (
        $installedPackage.Publisher -cne $ExpectedPublisher -or
        $installedPackage.Version.ToString() -cne $ExpectedVersion -or
        $installedPackage.Architecture.ToString().ToUpperInvariant() -cne
            $ExpectedArchitecture
    ) {
        throw 'Installed package identity metadata validation failed.'
    }

    $smokePath = Join-Path `
        $installedPackage.InstallLocation `
        $SmokeExecutableRelativePath
    Invoke-DriverMissingSmoke `
        -ExecutablePath $smokePath `
        -ExpectedStatus $ExpectedSmokeStatus
    Write-Output (
        'Hosted MSIX install check: identity=passed; ' +
        "smokeStatus=$ExpectedSmokeStatus; networkOpenCount=0; audioStartCount=0"
    )
} finally {
    try {
        if ($installationAttempted) {
            $packagesToRemove = @()
            if ($null -ne $installedPackage) {
                $packagesToRemove = @($installedPackage)
            } else {
                $packagesToRemove = @(
                    Get-AppxPackage -Name $ExpectedPackageIdentity |
                        Where-Object {
                            $_.Name -ceq $ExpectedPackageIdentity
                        }
                )
            }
            foreach ($package in $packagesToRemove) {
                Remove-AppxPackage `
                    -Package $package.PackageFullName `
                    -ErrorAction Stop
            }
        }
    } finally {
        try {
            if ($certificateAdded) {
                if ($null -eq $trustedPeopleStore) {
                    $trustedPeopleStore =
                        [Security.Cryptography.X509Certificates.X509Store]::new(
                            [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
                            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
                        )
                    $trustedPeopleStore.Open(
                        [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
                    )
                }
                $matches = @(
                    $trustedPeopleStore.Certificates |
                        Where-Object {
                            $_.Thumbprint.ToUpperInvariant() -ceq
                                $expectedThumbprint
                        }
                )
                foreach ($match in $matches) {
                    $trustedPeopleStore.Remove($match)
                }
            }
        } finally {
            if ($null -ne $trustedPeopleStore) {
                $trustedPeopleStore.Close()
                $trustedPeopleStore.Dispose()
            }
            $certificate.Dispose()
        }
    }
}
