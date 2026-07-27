[CmdletBinding()]
param(
    [switch]$ConfirmUninstall
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

function Get-TargetDevnodes {
    $rootDevices = @(Get-CimInstance `
        -ClassName Win32_PnPEntity `
        -Filter "PNPDeviceID LIKE 'ROOT%'")
    return @($rootDevices | Where-Object {
        @($_.HardwareID) -icontains $script:TargetHardwareId
    })
}

function Assert-CurrentTargetDevnode {
    param(
        [Parameter(Mandatory)]
        [object[]]$Devnodes
    )

    $present = @($Devnodes | Where-Object { $_.Present -eq $true })
    if ($present.Count -ne 1) {
        throw (
            "Expected exactly one present $script:TargetHardwareId devnode; " +
            "found $($present.Count)."
        )
    }
    if (@($present[0].HardwareID) -inotcontains $script:TargetHardwareId) {
        throw "The current devnode does not have the exact target hardware ID."
    }
    return $present[0]
}

function Assert-PublishedInfName {
    param(
        [Parameter(Mandatory)]
        [string]$PublishedInf
    )

    if ($PublishedInf -notmatch "^oem[0-9]+\.inf$") {
        throw "Refusing non-allow-listed published INF '$PublishedInf'."
    }
}

function Get-PublishedInfForDevnode {
    param(
        [Parameter(Mandatory)]
        [psobject]$Devnode
    )

    $signedDrivers = @(Get-CimInstance -ClassName Win32_PnPSignedDriver)
    $matching = @($signedDrivers | Where-Object {
        $_.DeviceID -ieq $Devnode.PNPDeviceID
    })
    $publishedInfs = @($matching |
        ForEach-Object { [string]$_.InfName } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
    if ($publishedInfs.Count -ne 1) {
        throw (
            "Expected one published INF for the exact target devnode; " +
            "found $($publishedInfs.Count)."
        )
    }
    $publishedInf = $publishedInfs[0]
    Assert-PublishedInfName -PublishedInf $publishedInf
    return $publishedInf
}

function Invoke-PnpUtilUninstall {
    param(
        [Parameter(Mandatory)]
        [string]$PublishedInf
    )

    Assert-PublishedInfName -PublishedInf $PublishedInf
    $pnpUtil = Resolve-SystemPnpUtil
    $result = Invoke-CapturedProcess `
        -Executable $pnpUtil `
        -Arguments @(
            "/delete-driver",
            $PublishedInf,
            "/uninstall",
            "/force"
        )
    if ($result.ExitCode -ne 0) {
        throw "pnputil driver removal failed with exit code $($result.ExitCode)."
    }
}

function Assert-TargetDevnodeAbsent {
    param(
        [Parameter(Mandatory)]
        [object[]]$Devnodes
    )

    $present = @($Devnodes | Where-Object { $_.Present -eq $true })
    if ($present.Count -ne 0) {
        throw "The exact target devnode is still present after driver removal."
    }
}

function Invoke-UninstallTestDriver {
    param(
        [switch]$ConfirmUninstall
    )

    if (-not $ConfirmUninstall) {
        throw "Removal requires the explicit -ConfirmUninstall switch."
    }
    Assert-SupportedWindowsHost
    Assert-LabMachinePrerequisites

    $devnodes = @(Get-TargetDevnodes)
    $devnode = Assert-CurrentTargetDevnode -Devnodes $devnodes
    $publishedInf = Get-PublishedInfForDevnode -Devnode $devnode
    Assert-PublishedInfName -PublishedInf $publishedInf

    Write-Host "Hardware ID: $script:TargetHardwareId"
    Write-Host "Published INF: $publishedInf"

    Invoke-PnpUtilUninstall -PublishedInf $publishedInf
    $remaining = @(Get-TargetDevnodes)
    Assert-TargetDevnodeAbsent -Devnodes $remaining
    Write-Host "Target devnode is no longer present."
}

Invoke-UninstallTestDriver @PSBoundParameters
