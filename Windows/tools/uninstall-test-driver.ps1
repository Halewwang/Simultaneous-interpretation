[CmdletBinding()]
param(
    [switch]$ConfirmUninstall
)

if ($MyInvocation.InvocationName -ceq ".") {
    throw "Dot-source invocation is forbidden for this lifecycle script."
}

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
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [ValidateRange(1, 900)]
        [int]$TimeoutSeconds
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
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $terminationDetail = "process-tree termination was attempted"
            try {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) {
                    $terminationDetail = (
                        "process-tree termination was attempted but bounded " +
                        "reaping did not complete"
                    )
                }
            } catch {
                $terminationDetail = (
                    "process-tree termination failed: " +
                    $_.Exception.Message
                )
            }
            $exception = [TimeoutException]::new(
                "Process timed out after $TimeoutSeconds seconds; " +
                "state uncertain; perform read-only inventory before any " +
                "further mutation; $terminationDetail."
            )
            $exception.Data["StateUncertain"] = $true
            throw $exception
        }
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

function Invoke-PollDelay {
    param(
        [Parameter(Mandatory)]
        [ValidateRange(1, 60000)]
        [int]$DelayMilliseconds
    )

    Start-Sleep -Milliseconds $DelayMilliseconds
}

function Invoke-BoundedPoll {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [scriptblock]$IsComplete,

        [Parameter(Mandatory)]
        [string]$Description,

        [ValidateRange(1, 300)]
        [int]$MaxAttempts = 30,

        [ValidateRange(1, 60000)]
        [int]$DelayMilliseconds = 1000
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt += 1) {
        $value = & $Action
        if (& $IsComplete $value) {
            return $value
        }
        if ($attempt -lt $MaxAttempts) {
            Invoke-PollDelay -DelayMilliseconds $DelayMilliseconds
        }
    }
    $exception = [TimeoutException]::new(
        "Timed out waiting for $Description after $MaxAttempts attempts; " +
        "state uncertain; perform read-only inventory before any further " +
        "mutation."
    )
    $exception.Data["StateUncertain"] = $true
    throw $exception
}

function Assert-CurrentTargetDevnode {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
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
    if ($matching.Count -ne 1) {
        throw (
            "Expected one signed-driver metadata match for the exact " +
            "target devnode; found $($matching.Count)."
        )
    }
    $publishedInf = [string]$matching[0].InfName
    if ([string]::IsNullOrWhiteSpace($publishedInf)) {
        throw "The exact target devnode has no published INF metadata."
    }
    Assert-PublishedInfName -PublishedInf $publishedInf
    return $publishedInf
}

function Assert-ExactTargetInstanceId {
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId
    )

    if ($InstanceId -notmatch "^ROOT\\EMKEVIRTUALAUDIO\\[^\\]+$") {
        throw "Refusing a non-exact EMKE target instance ID."
    }
}

function Invoke-PnpUtilRemoveDevice {
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId
    )

    Assert-ExactTargetInstanceId -InstanceId $InstanceId
    $pnpUtil = Resolve-SystemPnpUtil
    $result = Invoke-CapturedProcess `
        -Executable $pnpUtil `
        -Arguments @("/remove-device", $InstanceId) `
        -TimeoutSeconds 120
    if ($result.ExitCode -ne 0) {
        throw (
            "pnputil exact device removal failed with exit code " +
            "$($result.ExitCode)."
        )
    }
}

function Invoke-PnpUtilDeleteDriver {
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
            $PublishedInf
        ) `
        -TimeoutSeconds 120
    if ($result.ExitCode -ne 0) {
        throw (
            "Published package deletion failed with exit code " +
            "$($result.ExitCode); the package remains or its package state " +
            "is unproven. A new reference may have raced the prior " +
            "read-only check; no forced uninstall or device mutation was " +
            "requested."
        )
    }
}

function Assert-NoOtherPublishedInfReferences {
    param(
        [Parameter(Mandatory)]
        [psobject]$TargetDevnode,

        [Parameter(Mandatory)]
        [string]$PublishedInf
    )

    Assert-PublishedInfName -PublishedInf $PublishedInf
    $signedDrivers = @(Get-CimInstance -ClassName Win32_PnPSignedDriver)
    $otherReferences = @($signedDrivers | Where-Object {
        [string]$_.InfName -ieq $PublishedInf -and
        [string]$_.DeviceID -ine [string]$TargetDevnode.PNPDeviceID
    })
    if ($otherReferences.Count -ne 0) {
        throw (
            "Published INF '$PublishedInf' is shared by another signed-driver " +
            "reference; refusing every deletion."
        )
    }
}

function Assert-TargetDevnodeAbsent {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Devnodes
    )

    $present = @($Devnodes | Where-Object { $_.Present -eq $true })
    if ($present.Count -ne 0) {
        throw "The exact target devnode is still present after driver removal."
    }
}

function Wait-TargetDevnodeAbsent {
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedInstanceId,

        [ValidateRange(1, 300)]
        [int]$MaxAttempts = 30,

        [ValidateRange(1, 60000)]
        [int]$DelayMilliseconds = 1000
    )

    [void](Invoke-BoundedPoll `
        -Action {
            @(Get-TargetDevnodes)
        } `
        -IsComplete {
            param($Value)
            $null -eq $Value -or @($Value).Count -eq 0
        } `
        -Description "absence of exact target devnode '$ExpectedInstanceId'" `
        -MaxAttempts $MaxAttempts `
        -DelayMilliseconds $DelayMilliseconds)
}

function Wait-PublishedInfUnreferenced {
    param(
        [Parameter(Mandatory)]
        [string]$PublishedInf,

        [ValidateRange(1, 300)]
        [int]$MaxAttempts = 30,

        [ValidateRange(1, 60000)]
        [int]$DelayMilliseconds = 1000
    )

    Assert-PublishedInfName -PublishedInf $PublishedInf
    [void](Invoke-BoundedPoll `
        -Action {
            @(
                Get-CimInstance -ClassName Win32_PnPSignedDriver |
                Where-Object {
                    [string]$_.InfName -ieq $PublishedInf
                }
            )
        } `
        -IsComplete {
            param($Value)
            $null -eq $Value -or @($Value).Count -eq 0
        } `
        -Description "published INF '$PublishedInf' to become unreferenced" `
        -MaxAttempts $MaxAttempts `
        -DelayMilliseconds $DelayMilliseconds)
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
    Assert-NoOtherPublishedInfReferences `
        -TargetDevnode $devnode `
        -PublishedInf $publishedInf

    Write-Host "Hardware ID: $script:TargetHardwareId"
    Write-Host "Target instance: $($devnode.PNPDeviceID)"
    Write-Host "Published INF: $publishedInf"

    Invoke-PnpUtilRemoveDevice -InstanceId $devnode.PNPDeviceID
    Wait-TargetDevnodeAbsent -ExpectedInstanceId $devnode.PNPDeviceID
    Write-Host "Exact target devnode is no longer present."

    Wait-PublishedInfUnreferenced -PublishedInf $publishedInf
    Invoke-PnpUtilDeleteDriver -PublishedInf $publishedInf
    Write-Host "Unreferenced published INF was removed."
}

Invoke-UninstallTestDriver @PSBoundParameters
