[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$toolsDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$installScript = Join-Path $toolsDirectory "install-test-driver.ps1"
$uninstallScript = Join-Path $toolsDirectory "uninstall-test-driver.ps1"
$script:TargetHardwareId = "ROOT\EMKEVIRTUALAUDIO"
$script:MinimumWindowsBuild = 26200
$script:failures = [Collections.Generic.List[string]]::new()

function Import-LifecycleFunctions {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors
    )
    if ($errors.Count -ne 0) {
        throw "Lifecycle script has parser errors: $($errors[0].Message)"
    }
    $definitions = @($ast.FindAll(
        {
            param($candidate)
            $candidate -is
                [System.Management.Automation.Language.FunctionDefinitionAst]
        },
        $false
    ))
    foreach ($definition in $definitions) {
        $body = $definition.Body.Extent.Text
        $body = $body.Substring(1, $body.Length - 2)
        Set-Item `
            -LiteralPath "Function:\global:$($definition.Name)" `
            -Value ([scriptblock]::Create($body)) `
            -Force
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        throw "REAL PROCESS EXECUTION IS FORBIDDEN IN VALIDATION TESTS."
    }
}

function Set-TestFunction {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Body
    )

    Set-Item -LiteralPath "Function:\global:$Name" -Value $Body -Force
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Pattern
    )

    try {
        & $Action
    } catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected '$Pattern'; received '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected action to throw '$Pattern'."
}

function Invoke-Case {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    try {
        & $Action
        Write-Host "PASS: $Name"
    } catch {
        $script:failures.Add("$Name`: $($_.Exception.Message)")
        Write-Host "FAIL: $Name"
    }
}

function New-Devnode {
    param(
        [string[]]$HardwareID = @("ROOT\EMKEVIRTUALAUDIO"),
        [bool]$Present = $true,
        [int]$ErrorCode = 0,
        [string]$DeviceID = "ROOT\EMKEVIRTUALAUDIO\0000"
    )

    return [pscustomobject]@{
        PNPDeviceID = $DeviceID
        HardwareID = $HardwareID
        Present = $Present
        ConfigManagerErrorCode = $ErrorCode
    }
}

function New-InstallPackageRecord {
    return [pscustomobject]@{
        Directory = "C:\Package With Spaces"
        Inf = [IO.FileInfo]::new("C:\Package With Spaces\Driver Name.inf")
        Sys = [IO.FileInfo]::new("C:\Package With Spaces\Driver Name.sys")
        Cat = [IO.FileInfo]::new("C:\Package With Spaces\Driver Name.cat")
    }
}

function Set-InstallOrchestratorExternalBoundaries {
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    Set-TestFunction -Name Assert-LabMachinePrerequisites -Body {}
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Get-StrictDriverPackage -Body {
        New-InstallPackageRecord
    }
    Set-TestFunction -Name Invoke-DriverPackageVerifier -Body {}
    Set-TestFunction -Name Get-DriverPackageSha256 -Body { "A" * 64 }
    Set-TestFunction -Name Get-CatalogSignatureMetadata -Body {
        [pscustomobject]@{
            Status = "Valid"
            Certificate = [pscustomobject]@{ Subject = "CN=Internal Test" }
            SummarySha256 = "B" * 64
        }
    }
    Set-TestFunction -Name Get-DriverInfMetadata -Body {
        [pscustomobject]@{
            DriverVer = "07/26/2026,1.0.0.1"
            HardwareId = "ROOT\EMKEVIRTUALAUDIO"
        }
    }
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        @(New-Devnode)
    }
    Set-TestFunction -Name Invoke-SmokeEnumeration -Body {}
}

Import-LifecycleFunctions -Path $installScript

Invoke-Case -Name "valid catalog without signer" -Action {
    Assert-Throws -Pattern "signing certificate" -Action {
        Assert-CatalogSignatureValid -Metadata ([pscustomobject]@{
            Status = "Valid"
            Certificate = $null
            SummarySha256 = ""
        })
    }
}

Invoke-Case -Name "untrusted catalog statuses" -Action {
    foreach ($status in @("UnknownError", "NotTrusted")) {
        Assert-Throws -Pattern "must be Valid" -Action {
            Assert-CatalogSignatureValid -Metadata ([pscustomobject]@{
                Status = $status
                Certificate = [pscustomobject]@{ Subject = "CN=Untrusted" }
                SummarySha256 = "C" * 64
            })
        }
    }
}

Invoke-Case -Name "install devnode validation" -Action {
    Assert-Throws -Pattern "exactly one" -Action {
        Assert-InstalledDevnodeHealthy -Devnodes ([object[]]@())
    }
    Assert-Throws -Pattern "exactly one" -Action {
        Assert-InstalledDevnodeHealthy -Devnodes @(
            (New-Devnode),
            (New-Devnode -DeviceID "ROOT\EMKEVIRTUALAUDIO\0001")
        )
    }
    Assert-Throws -Pattern "not present" -Action {
        Assert-InstalledDevnodeHealthy -Devnodes @(
            (New-Devnode -Present $false)
        )
    }
    Assert-Throws -Pattern "ConfigManagerErrorCode=7" -Action {
        Assert-InstalledDevnodeHealthy -Devnodes @(
            (New-Devnode -ErrorCode 7)
        )
    }
    Assert-InstalledDevnodeHealthy -Devnodes @((New-Devnode))
}

Invoke-Case -Name "digest mismatch reports observed digest" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-InstallOrchestratorExternalBoundaries
    $script:processCalls = 0
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        $script:processCalls += 1
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    $output = [Collections.Generic.List[string]]::new()
    $caught = $null
    try {
        Invoke-InstallTestDriver `
            -PackagePath "C:\Package With Spaces" `
            -ExpectedPackageSha256 ("D" * 64) `
            -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
            -ConfirmInstall *>&1 |
            ForEach-Object { [void]$output.Add([string]$_) }
    } catch {
        $caught = $_
    }
    if ($null -eq $caught) {
        throw "Digest mismatch unexpectedly succeeded."
    }
    $observedLine = "Observed package SHA-256: " + ("A" * 64)
    if (@($output | Where-Object {
        [string]$_ -match [regex]::Escape($observedLine)
    }).Count -ne 1) {
        throw "Observed digest was not emitted before mismatch failure."
    }
    if ($script:processCalls -ne 0) {
        throw "Digest mismatch reached the process boundary."
    }
}

Invoke-Case -Name "smoke duplicate status" -Action {
    $statusCases = @(
        ,@("discovery=ready", "discovery=ready", "result=ready")
        ,@("discovery=ready", "result=ready", "result=ready")
    )
    foreach ($lines in $statusCases) {
        Import-LifecycleFunctions -Path $installScript
        Set-TestFunction -Name Resolve-RequiredFile -Body {
            param($Path)
            $Path
        }
        $script:smokeLines = $lines
        Set-TestFunction -Name Invoke-CapturedProcess -Body {
            [pscustomobject]@{
                ExitCode = 0
                OutputLines = @($script:smokeLines)
            }
        }
        Assert-Throws -Pattern "exactly one|status" -Action {
            Invoke-SmokeEnumeration -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe"
        }
    }
}

Invoke-Case -Name "smoke contradictory status" -Action {
    $statusCases = @(
        ,@("discovery=ready", "discovery=partial", "result=ready")
        ,@("discovery=ready", "result=ready", "result=failed")
    )
    foreach ($lines in $statusCases) {
        Import-LifecycleFunctions -Path $installScript
        Set-TestFunction -Name Resolve-RequiredFile -Body {
            param($Path)
            $Path
        }
        $script:smokeLines = $lines
        Set-TestFunction -Name Invoke-CapturedProcess -Body {
            [pscustomobject]@{
                ExitCode = 0
                OutputLines = @($script:smokeLines)
            }
        }
        Assert-Throws -Pattern "exactly one|status" -Action {
            Invoke-SmokeEnumeration -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe"
        }
    }
}

Invoke-Case -Name "smoke raw detail remains suppressed" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{
            ExitCode = 0
            OutputLines = @(
                "discovery=ready",
                "result=ready",
                "rawEndpointId={0.0.0.00000000}.sensitive"
            )
        }
    }
    $emitted = @(Invoke-SmokeEnumeration `
        -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe")
    if ($emitted.Count -ne 0) {
        throw "Smoke wrapper emitted captured process detail."
    }
}

Invoke-Case -Name "install orchestrator exact process boundary" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-InstallOrchestratorExternalBoundaries
    $script:processCalls = [Collections.Generic.List[object]]::new()
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        param($Executable, $Arguments)
        [void]$script:processCalls.Add([pscustomobject]@{
            Executable = $Executable
            Arguments = @($Arguments)
        })
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    Invoke-InstallTestDriver `
        -PackagePath "C:\Package With Spaces" `
        -ExpectedPackageSha256 ("A" * 64) `
        -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
        -ConfirmInstall
    if ($script:processCalls.Count -ne 1) {
        throw "Install orchestrator did not reach exactly one process boundary."
    }
    $expected = @(
        "/add-driver",
        "C:\Package With Spaces\Driver Name.inf",
        "/install"
    )
    if ($script:processCalls[0].Executable -cne
        "C:\Windows\System32\pnputil.exe" -or
        [string]::Join("`n", $script:processCalls[0].Arguments) -cne
        [string]::Join("`n", $expected)) {
        throw "Install orchestrator process boundary was not exact."
    }
}

Import-LifecycleFunctions -Path $uninstallScript

Invoke-Case -Name "published INF exact CIM mapping" -Action {
    $devnode = New-Devnode
    $script:signedDriverRows = @(
        [pscustomobject]@{
            DeviceID = "ROOT\OTHER\0000"
            InfName = "oem9.inf"
        },
        [pscustomobject]@{
            DeviceID = $devnode.PNPDeviceID
            InfName = "oem42.inf"
        }
    )
    Set-TestFunction -Name Get-CimInstance -Body {
        @($script:signedDriverRows)
    }
    $published = Get-PublishedInfForDevnode -Devnode $devnode
    if ($published -cne "oem42.inf") {
        throw "CIM mapping did not select the exact DeviceID."
    }
    Assert-Throws -Pattern "exact target hardware ID" -Action {
        Assert-CurrentTargetDevnode -Devnodes @(
            (New-Devnode -HardwareID @("ROOT\OTHER"))
        )
    }
}

Invoke-Case -Name "published INF zero and multiple matches" -Action {
    $devnode = New-Devnode
    Set-TestFunction -Name Get-CimInstance -Body {
        @($script:signedDriverRows)
    }
    $rowCases = @(
        ,([object[]]@())
        ,([object[]]@(
            [pscustomobject]@{
                DeviceID = $devnode.PNPDeviceID
                InfName = "oem42.inf"
            },
            [pscustomobject]@{
                DeviceID = $devnode.PNPDeviceID
                InfName = "oem42.inf"
            }
        ))
    )
    foreach ($rows in $rowCases) {
        $script:signedDriverRows = $rows
        Assert-Throws -Pattern "one published INF|one signed-driver" -Action {
            Get-PublishedInfForDevnode -Devnode $devnode
        }
    }
    $script:signedDriverRows = @([pscustomobject]@{
        DeviceID = $devnode.PNPDeviceID
        InfName = "oem42.inf /force"
    })
    Assert-Throws -Pattern "published INF" -Action {
        Get-PublishedInfForDevnode -Devnode $devnode
    }
}

Invoke-Case -Name "uninstall unsupported OS build" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    Set-TestFunction -Name Get-WindowsBuildNumber -Body { 26199 }
    Set-TestFunction -Name Test-IsAdministrator -Body { $true }
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        throw "Target query must not run below build 26200."
    }
    $script:processCalls = 0
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        $script:processCalls += 1
    }
    Assert-Throws -Pattern "26200" -Action {
        Invoke-UninstallTestDriver -ConfirmUninstall
    }
    if ($script:processCalls -ne 0) {
        throw "Unsupported OS reached process boundary."
    }
}

Invoke-Case -Name "uninstall non-administrator" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    Set-TestFunction -Name Get-WindowsBuildNumber -Body { 26200 }
    Set-TestFunction -Name Test-IsAdministrator -Body { $false }
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        throw "Target query must not run without elevation."
    }
    $script:processCalls = 0
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        $script:processCalls += 1
    }
    Assert-Throws -Pattern "administrator|elevat" -Action {
        Invoke-UninstallTestDriver -ConfirmUninstall
    }
    if ($script:processCalls -ne 0) {
        throw "Non-administrator path reached process boundary."
    }
}

Invoke-Case -Name "post-uninstall devnode still present" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    Set-TestFunction -Name Assert-LabMachinePrerequisites -Body {}
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        @((New-Devnode))
    }
    Set-TestFunction -Name Get-PublishedInfForDevnode -Body { "oem42.inf" }
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    $script:processCalls = 0
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        $script:processCalls += 1
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    Assert-Throws -Pattern "still present" -Action {
        Invoke-UninstallTestDriver -ConfirmUninstall
    }
    if ($script:processCalls -ne 1) {
        throw "Post-uninstall verification did not follow one process call."
    }
}

Invoke-Case -Name "uninstall orchestrator exact process boundary" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    Set-TestFunction -Name Assert-LabMachinePrerequisites -Body {}
    $script:targetQueryCount = 0
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        $script:targetQueryCount += 1
        if ($script:targetQueryCount -eq 1) {
            return @((New-Devnode))
        }
        return @()
    }
    Set-TestFunction -Name Get-PublishedInfForDevnode -Body { "oem42.inf" }
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    $script:processCalls = [Collections.Generic.List[object]]::new()
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        param($Executable, $Arguments)
        [void]$script:processCalls.Add([pscustomobject]@{
            Executable = $Executable
            Arguments = @($Arguments)
        })
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    Invoke-UninstallTestDriver -ConfirmUninstall
    if ($script:processCalls.Count -ne 1) {
        throw "Uninstall orchestrator did not reach exactly one process boundary."
    }
    $expected = @(
        "/delete-driver",
        "oem42.inf",
        "/uninstall",
        "/force"
    )
    if ($script:processCalls[0].Executable -cne
        "C:\Windows\System32\pnputil.exe" -or
        [string]::Join("`n", $script:processCalls[0].Arguments) -cne
        [string]::Join("`n", $expected)) {
        throw "Uninstall orchestrator process boundary was not exact."
    }
}

if ($script:failures.Count -ne 0) {
    throw (
        "Lifecycle validation tests failed:`n" +
        ($script:failures -join [Environment]::NewLine)
    )
}

Write-Host "Lifecycle validation tests passed without device or certificate mutation."
