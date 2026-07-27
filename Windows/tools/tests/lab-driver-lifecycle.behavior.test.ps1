[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$toolsDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..")
)
$installScript = Join-Path $toolsDirectory "install-test-driver.ps1"
$uninstallScript = Join-Path $toolsDirectory "uninstall-test-driver.ps1"
$script:failures = [System.Collections.Generic.List[string]]::new()
$script:TargetHardwareId = "ROOT\EMKEVIRTUALAUDIO"
$script:MinimumWindowsBuild = 26200

function Import-LifecycleFunctions {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Lifecycle script is missing: $Path"
    }
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
    if ($definitions.Count -eq 0) {
        throw "Lifecycle script does not expose testable production functions."
    }
    foreach ($definition in $definitions) {
        $bodyText = $definition.Body.Extent.Text
        $bodyText = $bodyText.Substring(1, $bodyText.Length - 2)
        Set-Item `
            -LiteralPath "Function:\global:$($definition.Name)" `
            -Value ([scriptblock]::Create($bodyText)) `
            -Force
    }
    Set-Item `
        -LiteralPath "Function:\global:Invoke-CapturedProcess" `
        -Value {
            throw (
                "REAL PROCESS EXECUTION IS FORBIDDEN IN " +
                "LIFECYCLE BEHAVIOR TESTS."
            )
        } `
        -Force
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
            throw "Expected error '$Pattern'; received '$($_.Exception.Message)'."
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

function New-InstallPackageRecord {
    $inf = [System.IO.FileInfo]::new(
        "C:\EMKE lab package; Write-Output INJECTED\Driver Name.inf"
    )
    $sys = [System.IO.FileInfo]::new(
        "C:\EMKE lab package; Write-Output INJECTED\Driver Name.sys"
    )
    $cat = [System.IO.FileInfo]::new(
        "C:\EMKE lab package; Write-Output INJECTED\Driver Name.cat"
    )
    return [pscustomobject]@{
        Directory = "C:\EMKE lab package; Write-Output INJECTED"
        Inf = $inf
        Sys = $sys
        Cat = $cat
    }
}

function Set-SafeInstallDefaults {
    param(
        [switch]$UseRealPrerequisites
    )

    $script:pnpCalls = [System.Collections.Generic.List[object]]::new()
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    if (-not $UseRealPrerequisites) {
        Set-TestFunction -Name Assert-LabMachinePrerequisites -Body {}
    }
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Get-StrictDriverPackage -Body {
        New-InstallPackageRecord
    }
    Set-TestFunction -Name Invoke-DriverPackageVerifier -Body {}
    Set-TestFunction -Name Get-DriverPackageSha256 -Body {
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
    }
    Set-TestFunction -Name Get-CatalogSignatureMetadata -Body {
        [pscustomobject]@{
            Status = "Valid"
            Certificate = [pscustomobject]@{
                Subject = "CN=EMKE Internal Test"
                Thumbprint = "0123456789ABCDEF"
            }
            SummarySha256 =
                "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
        }
    }
    Set-TestFunction -Name Get-DriverInfMetadata -Body {
        [pscustomobject]@{
            DriverVer = "07/26/2026,1.0.0.1"
            HardwareId = "ROOT\EMKEVIRTUALAUDIO"
        }
    }
    Set-TestFunction -Name Invoke-PnpUtilInstall -Body {
        param($InfPath)
        $script:pnpCalls.Add(@($InfPath))
    }
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        @([pscustomobject]@{
            PNPDeviceID = "ROOT\EMKEVIRTUALAUDIO\0000"
            HardwareID = @("ROOT\EMKEVIRTUALAUDIO")
            Present = $true
            ConfigManagerErrorCode = 0
        })
    }
    Set-TestFunction -Name Assert-InstalledDevnodeHealthy -Body {}
    Set-TestFunction -Name Invoke-SmokeEnumeration -Body {}
}

function Invoke-InstallWithDefaults {
    param(
        [bool]$Confirm = $true,
        [string]$Digest =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
    )

    Invoke-InstallTestDriver `
        -PackagePath "C:\EMKE lab package; Write-Output INJECTED" `
        -ExpectedPackageSha256 $Digest `
        -SmokePath "C:\Smoke Tools\EMKE.AudioSmoke.exe" `
        -ConfirmInstall:$Confirm
}

Import-LifecycleFunctions -Path $installScript

Invoke-Case -Name "missing install confirmation" -Action {
    Set-SafeInstallDefaults
    Assert-Throws -Pattern "ConfirmInstall" -Action {
        Invoke-InstallWithDefaults -Confirm $false
    }
    if ($script:pnpCalls.Count -ne 0) {
        throw "pnputil boundary was reached without confirmation."
    }
}

Invoke-Case -Name "package digest mismatch" -Action {
    Set-SafeInstallDefaults
    Assert-Throws -Pattern "SHA-256" -Action {
        Invoke-InstallWithDefaults -Digest (
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"
        )
    }
    if ($script:pnpCalls.Count -ne 0) {
        throw "pnputil boundary was reached after a digest mismatch."
    }
}

Invoke-Case -Name "invalid or unsigned catalog" -Action {
    foreach ($status in @("NotSigned", "HashMismatch")) {
        Set-SafeInstallDefaults
        Set-TestFunction `
            -Name Get-CatalogSignatureMetadata `
            -Body ({
                [pscustomobject]@{
                    Status = $status
                    Certificate = $null
                    SummarySha256 = ""
                }
            }.GetNewClosure())
        Assert-Throws -Pattern "catalog|signature" -Action {
            Invoke-InstallWithDefaults
        }
        if ($script:pnpCalls.Count -ne 0) {
            throw "pnputil boundary was reached for CAT status $status."
        }
    }
}

Invoke-Case -Name "unsupported OS build" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-SafeInstallDefaults -UseRealPrerequisites
    Set-TestFunction -Name Get-WindowsBuildNumber -Body { 26199 }
    Set-TestFunction -Name Test-IsAdministrator -Body { $true }
    Assert-Throws -Pattern "26200" -Action {
        Invoke-InstallWithDefaults
    }
    if ($script:pnpCalls.Count -ne 0) {
        throw "pnputil boundary was reached on an unsupported OS build."
    }
}

Invoke-Case -Name "non-administrator" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-SafeInstallDefaults -UseRealPrerequisites
    Set-TestFunction -Name Get-WindowsBuildNumber -Body { 26200 }
    Set-TestFunction -Name Test-IsAdministrator -Body { $false }
    Assert-Throws -Pattern "administrator|elevat" -Action {
        Invoke-InstallWithDefaults
    }
    if ($script:pnpCalls.Count -ne 0) {
        throw "pnputil boundary was reached without elevation."
    }
}

Invoke-Case -Name "space and metacharacter INF path" -Action {
    Set-SafeInstallDefaults
    Invoke-InstallWithDefaults
    if ($script:pnpCalls.Count -ne 1) {
        throw "Expected one install boundary call."
    }
    $expected = "C:\EMKE lab package; Write-Output INJECTED\Driver Name.inf"
    if ($script:pnpCalls[0][0] -cne $expected) {
        throw "INF path was split or altered."
    }
}

Invoke-Case -Name "one exact install command" -Action {
    Import-LifecycleFunctions -Path $installScript
    $script:processCall = $null
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        param($Executable, $Arguments)
        $script:processCall = [pscustomobject]@{
            Executable = $Executable
            Arguments = @($Arguments)
        }
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    $infPath = "C:\Package With Spaces\EMKE Virtual Audio.inf"
    Invoke-PnpUtilInstall -InfPath $infPath
    $expectedExecutable = "C:\Windows\System32\pnputil.exe"
    if ($script:processCall.Executable -cne $expectedExecutable) {
        throw "Install executable was not fixed to the system pnputil.exe."
    }
    $expected = @("/add-driver", $infPath, "/install")
    if ([string]::Join("`n", $script:processCall.Arguments) -cne
        [string]::Join("`n", $expected)) {
        throw "Install arguments were not the one exact allow-listed command."
    }
}

Invoke-Case -Name "smoke nonzero exit" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{
            ExitCode = 7
            OutputLines = @("discovery=ready", "result=ready")
        }
    }
    Assert-Throws -Pattern "exit code" -Action {
        Invoke-SmokeEnumeration -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe"
    }
}

Invoke-Case -Name "smoke missing discovery" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{ ExitCode = 0; OutputLines = @("result=ready") }
    }
    Assert-Throws -Pattern "discovery=ready" -Action {
        Invoke-SmokeEnumeration -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe"
    }
}

Invoke-Case -Name "smoke missing result" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{ ExitCode = 0; OutputLines = @("discovery=ready") }
    }
    Assert-Throws -Pattern "result=ready" -Action {
        Invoke-SmokeEnumeration -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe"
    }
}

Invoke-Case -Name "smoke driverMissing" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{
            ExitCode = 0
            OutputLines = @("discovery=driverMissing", "result=ready")
        }
    }
    Assert-Throws -Pattern "driverMissing|discovery=ready" -Action {
        Invoke-SmokeEnumeration -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe"
    }
}

Import-LifecycleFunctions -Path $uninstallScript

Invoke-Case -Name "published INF allow-list" -Action {
    foreach ($accepted in @("oem0.inf", "oem42.inf", "OEM999.INF")) {
        Assert-PublishedInfName -PublishedInf $accepted
    }
    foreach ($rejected in @(
        "emke.inf",
        "oem.inf",
        "oem1.inf.bak",
        "oem1*.inf",
        "..\oem1.inf",
        "oem1.inf /force"
    )) {
        Assert-Throws -Pattern "published INF" -Action {
            Assert-PublishedInfName -PublishedInf $rejected
        }
    }
}

Invoke-Case -Name "missing uninstall confirmation" -Action {
    $script:pnpCalls = 0
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    Set-TestFunction -Name Assert-LabMachinePrerequisites -Body {}
    Set-TestFunction -Name Get-TargetDevnodes -Body { @() }
    Set-TestFunction -Name Invoke-PnpUtilUninstall -Body {
        $script:pnpCalls += 1
    }
    Assert-Throws -Pattern "ConfirmUninstall" -Action {
        Invoke-UninstallTestDriver -ConfirmUninstall:$false
    }
    if ($script:pnpCalls -ne 0) {
        throw "pnputil boundary was reached without uninstall confirmation."
    }
}

Invoke-Case -Name "one exact uninstall command" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    $script:processCall = $null
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        param($Executable, $Arguments)
        $script:processCall = [pscustomobject]@{
            Executable = $Executable
            Arguments = @($Arguments)
        }
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    Invoke-PnpUtilUninstall -PublishedInf "oem42.inf"
    $expectedExecutable = "C:\Windows\System32\pnputil.exe"
    if ($script:processCall.Executable -cne $expectedExecutable) {
        throw "Uninstall executable was not fixed to the system pnputil.exe."
    }
    $expected = @("/delete-driver", "oem42.inf", "/uninstall", "/force")
    if ([string]::Join("`n", $script:processCall.Arguments) -cne
        [string]::Join("`n", $expected)) {
        throw "Uninstall arguments were not the one exact allow-listed command."
    }
}

Set-TestFunction -Name Invoke-CapturedProcess -Body {
    throw "REAL PROCESS EXECUTION IS FORBIDDEN IN LIFECYCLE BEHAVIOR TESTS."
}

if ($script:failures.Count -ne 0) {
    throw (
        "Lifecycle behavior tests failed:`n" +
        ($script:failures -join [Environment]::NewLine)
    )
}

Write-Host "Lifecycle behavior tests passed without device or certificate mutation."
