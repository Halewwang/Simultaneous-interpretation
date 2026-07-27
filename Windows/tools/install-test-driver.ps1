[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedPackageSha256,

    [Parameter(Mandatory)]
    [string]$SmokePath,

    [switch]$ConfirmInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:TargetHardwareId = "ROOT\EMKEVIRTUALAUDIO"
$script:MinimumWindowsBuild = 26200

function Assert-SupportedWindowsHost {
    if ($PSVersionTable.PSVersion.Major -ne 7) {
        throw "This lab tool requires PowerShell 7."
    }
    if (-not $IsWindows) {
        throw "This lab tool can only run on Windows."
    }
}

function Get-WindowsBuildNumber {
    return [Environment]::OSVersion.Version.Build
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Assert-LabMachinePrerequisites {
    $build = Get-WindowsBuildNumber
    if ($build -lt $script:MinimumWindowsBuild) {
        throw (
            "Windows build $build is unsupported; build " +
            "$($script:MinimumWindowsBuild) or newer is required."
        )
    }
    if (-not (Test-IsAdministrator)) {
        throw "An elevated PowerShell 7 administrator session is required."
    }
}

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Required file does not exist: $Path"
    }
    return $resolved
}

function Resolve-SystemPnpUtil {
    $systemDirectory = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::System
    )
    if ([string]::IsNullOrWhiteSpace($systemDirectory)) {
        throw "Windows system directory could not be resolved."
    }
    $pnpUtil = Join-Path $systemDirectory "pnputil.exe"
    if (-not (Test-Path -LiteralPath $pnpUtil -PathType Leaf)) {
        throw "System pnputil.exe is missing: $pnpUtil"
    }
    return $pnpUtil
}

function Get-SinglePackageFile {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Files,

        [Parameter(Mandatory)]
        [string]$Extension
    )

    $matches = @($Files | Where-Object { $_.Extension -ieq $Extension })
    if ($matches.Count -ne 1) {
        throw (
            "Driver package must contain exactly one $Extension file; " +
            "found $($matches.Count)."
        )
    }
    return $matches[0]
}

function Get-StrictDriverPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    $resolved = (Resolve-Path -LiteralPath $Directory -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Driver package path is not a directory: $Directory"
    }
    $nested = @(Get-ChildItem -LiteralPath $resolved -Directory -Force)
    if ($nested.Count -ne 0) {
        throw "Driver package must be flat."
    }
    $files = @(Get-ChildItem -LiteralPath $resolved -File -Force)
    if ($files.Count -ne 3) {
        throw "Driver package must contain only one INF, one SYS, and one CAT."
    }

    return [pscustomobject]@{
        Directory = $resolved
        Inf = Get-SinglePackageFile -Files $files -Extension ".inf"
        Sys = Get-SinglePackageFile -Files $files -Extension ".sys"
        Cat = Get-SinglePackageFile -Files $files -Extension ".cat"
    }
}

function Invoke-DriverPackageVerifier {
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory
    )

    $verifier = Join-Path $PSScriptRoot "verify-driver-package.ps1"
    if (-not (Test-Path -LiteralPath $verifier -PathType Leaf)) {
        throw "Required driver package verifier is missing: $verifier"
    }
    & $verifier -PackageDirectory $PackageDirectory
}

function Get-DriverPackageSha256 {
    param(
        [Parameter(Mandatory)]
        [psobject]$Package
    )

    $infHash = (Get-FileHash `
        -LiteralPath $Package.Inf.FullName `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    $sysHash = (Get-FileHash `
        -LiteralPath $Package.Sys.FullName `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    $catHash = (Get-FileHash `
        -LiteralPath $Package.Cat.FullName `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    $manifest = (
        "EMKE-DRIVER-PACKAGE-SHA256-V1`n" +
        "INF=$infHash`n" +
        "SYS=$sysHash`n" +
        "CAT=$catHash`n"
    )
    $bytes = [Text.Encoding]::UTF8.GetBytes($manifest)
    $digest = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($digest)
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

function Get-CatalogSignatureMetadata {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$Catalog
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Catalog.FullName
    $certificate = $signature.SignerCertificate
    $summary = if ($null -eq $certificate) {
        ""
    } else {
        $certificate.GetCertHashString(
            [Security.Cryptography.HashAlgorithmName]::SHA256
        )
    }
    return [pscustomobject]@{
        Status = [string]$signature.Status
        Certificate = $certificate
        SummarySha256 = $summary
    }
}

function Assert-CatalogSignatureValid {
    param(
        [Parameter(Mandatory)]
        [psobject]$Metadata
    )

    if ($Metadata.Status -cne "Valid") {
        throw (
            "Driver catalog signature status must be Valid; received " +
            "'$($Metadata.Status)'."
        )
    }
    if ($null -eq $Metadata.Certificate) {
        throw "Driver catalog signature has no signing certificate."
    }
    if ([string]::IsNullOrWhiteSpace($Metadata.SummarySha256) -or
        $Metadata.SummarySha256 -notmatch "^[0-9A-Fa-f]{16,}$") {
        throw "Driver catalog signing certificate has no usable SHA-256 summary."
    }
}

function Get-DriverInfMetadata {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$Inf
    )

    $text = Get-Content -LiteralPath $Inf.FullName -Raw
    $driverVerMatches = [regex]::Matches(
        $text,
        "(?im)^\s*DriverVer\s*=\s*(?<value>[^\r\n]+?)\s*$"
    )
    if ($driverVerMatches.Count -ne 1) {
        throw "Driver package INF must contain exactly one DriverVer."
    }
    $hardwareMatches = [regex]::Matches(
        $text,
        "(?im)^\s*%[^%=\r\n]+%\s*=\s*[^,\r\n]+,\s*" +
            "(?<hardware>ROOT\\EMKEVIRTUALAUDIO)\s*$"
    )
    if ($hardwareMatches.Count -ne 1) {
        throw (
            "Driver package INF must declare the exact hardware ID " +
            "$script:TargetHardwareId exactly once."
        )
    }

    return [pscustomobject]@{
        DriverVer = $driverVerMatches[0].Groups["value"].Value.Trim()
        HardwareId = $hardwareMatches[0].Groups["hardware"].Value
    }
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start required process: $Executable"
        }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $standardOutput.GetAwaiter().GetResult()
        $stderr = $standardError.GetAwaiter().GetResult()
        $combined = $stdout + [Environment]::NewLine + $stderr
        $lines = @($combined -split "\r?\n" | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        })
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            OutputLines = $lines
        }
    } finally {
        $process.Dispose()
    }
}

function Invoke-PnpUtilInstall {
    param(
        [Parameter(Mandatory)]
        [string]$InfPath
    )

    $pnpUtil = Resolve-SystemPnpUtil
    $result = Invoke-CapturedProcess `
        -Executable $pnpUtil `
        -Arguments @("/add-driver", $InfPath, "/install")
    if ($result.ExitCode -ne 0) {
        throw "pnputil driver installation failed with exit code $($result.ExitCode)."
    }
}

function Get-TargetDevnodes {
    $rootDevices = @(Get-CimInstance `
        -ClassName Win32_PnPEntity `
        -Filter "PNPDeviceID LIKE 'ROOT%'")
    return @($rootDevices | Where-Object {
        @($_.HardwareID) -icontains $script:TargetHardwareId
    })
}

function Assert-InstalledDevnodeHealthy {
    param(
        [Parameter(Mandatory)]
        [object[]]$Devnodes
    )

    if ($Devnodes.Count -ne 1) {
        throw (
            "Expected exactly one $script:TargetHardwareId devnode after install; " +
            "found $($Devnodes.Count)."
        )
    }
    $devnode = $Devnodes[0]
    if ($devnode.Present -ne $true) {
        throw "The target driver devnode is not present after installation."
    }
    if ([int]$devnode.ConfigManagerErrorCode -ne 0) {
        throw (
            "The target driver devnode is not healthy; ConfigManagerErrorCode=" +
            "$($devnode.ConfigManagerErrorCode)."
        )
    }
}

function Invoke-SmokeEnumeration {
    param(
        [Parameter(Mandatory)]
        [string]$SmokePath
    )

    $resolvedSmoke = Resolve-RequiredFile -Path $SmokePath
    $result = Invoke-CapturedProcess `
        -Executable $resolvedSmoke `
        -Arguments @("--scenario", "enumerate")
    if ($result.ExitCode -ne 0) {
        throw "Audio smoke enumeration failed with exit code $($result.ExitCode)."
    }
    if ($result.OutputLines -contains "discovery=driverMissing") {
        throw "Audio smoke enumeration reported driverMissing."
    }
    if ($result.OutputLines -notcontains "discovery=ready") {
        throw "Audio smoke enumeration did not report discovery=ready."
    }
    if ($result.OutputLines -notcontains "result=ready") {
        throw "Audio smoke enumeration did not report result=ready."
    }
}

function Invoke-InstallTestDriver {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$ExpectedPackageSha256,

        [Parameter(Mandatory)]
        [string]$SmokePath,

        [switch]$ConfirmInstall
    )

    if (-not $ConfirmInstall) {
        throw "Installation requires the explicit -ConfirmInstall switch."
    }
    Assert-SupportedWindowsHost
    Assert-LabMachinePrerequisites

    $resolvedSmoke = Resolve-RequiredFile -Path $SmokePath

    $package = Get-StrictDriverPackage -Directory $PackagePath
    Invoke-DriverPackageVerifier -PackageDirectory $package.Directory

    $actualPackageSha256 = Get-DriverPackageSha256 -Package $package
    if (-not (Test-FixedSha256Equal `
        -Expected $ExpectedPackageSha256 `
        -Actual $actualPackageSha256)) {
        throw (
            "Driver package SHA-256 does not match the trusted expected value. " +
            "Refusing installation."
        )
    }

    $signature = Get-CatalogSignatureMetadata -Catalog $package.Cat
    Assert-CatalogSignatureValid -Metadata $signature
    $infMetadata = Get-DriverInfMetadata -Inf $package.Inf
    if ($infMetadata.HardwareId -cne $script:TargetHardwareId) {
        throw "Driver INF hardware ID is not the exact target."
    }

    Write-Host "DriverVer: $($infMetadata.DriverVer)"
    Write-Host "Hardware ID: $($infMetadata.HardwareId)"
    Write-Host "Package SHA-256: $actualPackageSha256"
    Write-Host (
        "Catalog signature: Valid; signing certificate SHA-256: " +
        $signature.SummarySha256
    )

    Invoke-PnpUtilInstall -InfPath $package.Inf.FullName
    $devnodes = @(Get-TargetDevnodes)
    Assert-InstalledDevnodeHealthy -Devnodes $devnodes
    Invoke-SmokeEnumeration -SmokePath $resolvedSmoke
    Write-Host (
        "Audio smoke: discovery=ready; result=ready; " +
        "public ABI four-role contract passed."
    )
}

Invoke-InstallTestDriver @PSBoundParameters
