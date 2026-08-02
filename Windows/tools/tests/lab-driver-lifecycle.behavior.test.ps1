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
$script:MinimumWindowsBuild = 19045

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

function New-TestReleaseMetadata {
    return [pscustomobject]@{
        MinimumWindowsBuild = 19045
        Architecture = "x64"
        DriverPackageVersion = "1.0.0.2"
        DriverAbiVersion = 1
        DriverHardwareId = "ROOT\EMKEVIRTUALAUDIO"
        DriverModelSection = "EMKE.NTamd64.10.0...19045"
    }
}

function New-TestHostInfo {
    param(
        [int]$Build = 19045,
        [string]$Architecture = "x64",
        [int]$ProductType = 1
    )
    return [pscustomobject]@{
        Build = $Build
        Architecture = $Architecture
        ProductType = $ProductType
    }
}

function New-InstallPackageRecord {
    $directory = "C:\EMKE lab package; Write-Output INJECTED"
    return [pscustomobject]@{
        Directory = $directory
        Inf = [pscustomobject]@{
            FullName = "$directory\Driver Name.inf"
            Name = "Driver Name.inf"
        }
        Sys = [pscustomobject]@{
            FullName = "$directory\Driver Name.sys"
            Name = "Driver Name.sys"
        }
        Cat = [pscustomobject]@{
            FullName = "$directory\Driver Name.cat"
            Name = "Driver Name.cat"
        }
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
    Set-TestFunction -Name New-ProtectedStagingDirectory -Body {
        [pscustomobject]@{
            Path = "C:\ProgramData\EMKE\DriverLabStaging\" + ("1" * 32)
            Token = "1" * 32
        }
    }
    Set-TestFunction -Name Copy-InstallInputsToStaging -Body {
        [pscustomobject]@{
            Package = (New-InstallPackageRecord)
            Smoke = [pscustomobject]@{
                FullName = "C:\Smoke Tools\EMKE.AudioSmoke.exe"
            }
            PackageSha256 = "A" * 64
            SmokeSha256 = "E" * 64
        }
    }
    Set-TestFunction -Name Assert-StagedInputsUnchanged -Body {}
    Set-TestFunction -Name Remove-ProtectedStagingDirectory -Body {}
    Set-TestFunction -Name Invoke-DriverPackageVerifier -Body {}
    Set-TestFunction -Name Get-DriverPackageSha256 -Body {
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
    }
    Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
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
            DriverVer = "08/01/2026,1.0.0.2"
            DriverVersion = "1.0.0.2"
            ProviderName = "EMKE"
            ModelSection = "EMKE.NTamd64.10.0...19045"
            InstallSection = "EMKE_Install"
            HardwareId = "ROOT\EMKEVIRTUALAUDIO"
        }
    }
    Set-TestFunction -Name Assert-InstalledDriverPackageIdentity -Body {
        param(
            $Devnode,
            $InfMetadata,
            $TrustedPackage,
            $ExpectedPackageSha256
        )
        [pscustomobject]@{
            InfName = "oem42.inf"
            DriverVersion = "1.0.0.2"
            ProviderName = "EMKE"
            PackageSha256 = "A" * 64
        }
    }
    Set-TestFunction -Name Invoke-PnpUtilInstall -Body {
        param($InfPath)
        $script:pnpCalls.Add(@($InfPath))
    }
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        @()
    }
    Set-TestFunction -Name New-RootDevnodeFromInf -Body {
        "ROOT\EMKEVIRTUALAUDIO\0000"
    }
    Set-TestFunction -Name Wait-TargetDevnode -Body {
        @([pscustomobject]@{
            PNPDeviceID = "ROOT\EMKEVIRTUALAUDIO\0000"
            HardwareID = @("ROOT\EMKEVIRTUALAUDIO")
            Present = $true
            ConfigManagerErrorCode = 0
        })[0]
    }
    Set-TestFunction -Name Remove-ExactCreatedRootDevnode -Body {}
    Set-TestFunction -Name Wait-TargetDevnodeAbsent -Body {}
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
        -ExpectedSmokeSha256 ("E" * 64) `
        -ReleaseMetadata (New-TestReleaseMetadata) `
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
    Set-TestFunction -Name Get-WindowsHostInfo -Body {
        New-TestHostInfo -Build 19044
    }
    Set-TestFunction -Name Test-IsAdministrator -Body { $true }
    Assert-Throws -Pattern "19045" -Action {
        Invoke-InstallWithDefaults
    }
    if ($script:pnpCalls.Count -ne 0) {
        throw "pnputil boundary was reached on an unsupported OS build."
    }
}

Invoke-Case -Name "Windows 10 workstation x64 floor matrix" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Test-IsAdministrator -Body { $true }
    $release = New-TestReleaseMetadata
    Assert-Throws -Pattern "19045|build" -Action {
        Assert-LabMachinePrerequisites `
            -ReleaseMetadata $release `
            -HostInfo (New-TestHostInfo -Build 19044)
    }
    Assert-LabMachinePrerequisites `
        -ReleaseMetadata $release `
        -HostInfo (New-TestHostInfo -Build 19045)
}

Invoke-Case -Name "non-workstation and non-x64 hosts" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Test-IsAdministrator -Body { $true }
    $release = New-TestReleaseMetadata
    foreach ($hostInfo in @(
        (New-TestHostInfo -Architecture "x86"),
        (New-TestHostInfo -Architecture "ARM64"),
        (New-TestHostInfo -ProductType 2),
        (New-TestHostInfo -ProductType 3)
    )) {
        Assert-Throws -Pattern "x64|workstation|host" -Action {
            Assert-LabMachinePrerequisites `
                -ReleaseMetadata $release `
                -HostInfo $hostInfo
        }
    }
}

Invoke-Case -Name "non-administrator" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-SafeInstallDefaults -UseRealPrerequisites
    Set-TestFunction -Name Get-WindowsHostInfo -Body {
        New-TestHostInfo -Build 19045
    }
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
        param($Executable, $Arguments, $TimeoutSeconds)
        $script:processCall = [pscustomobject]@{
            Executable = $Executable
            Arguments = @($Arguments)
            TimeoutSeconds = $TimeoutSeconds
        }
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    $infPath = "C:\Package With Spaces\EMKE Virtual Audio.inf"
    Invoke-PnpUtilInstall -InfPath $infPath
    $expectedExecutable = "C:\Windows\System32\pnputil.exe"
    if ($script:processCall.Executable -cne $expectedExecutable) {
        throw "Install executable was not fixed to the system pnputil.exe."
    }
    if ($script:processCall.TimeoutSeconds -ne 120) {
        throw "Install command did not use the bounded PnP timeout."
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
    Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{
            ExitCode = 7
            OutputLines = @("discovery=ready", "result=ready")
        }
    }
    Assert-Throws -Pattern "exit code" -Action {
        Invoke-SmokeEnumeration `
            -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
            -ExpectedSmokeSha256 ("E" * 64)
    }
}

Invoke-Case -Name "smoke missing discovery" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{ ExitCode = 0; OutputLines = @("result=ready") }
    }
    Assert-Throws -Pattern "discovery=ready" -Action {
        Invoke-SmokeEnumeration `
            -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
            -ExpectedSmokeSha256 ("E" * 64)
    }
}

Invoke-Case -Name "smoke missing result" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{ ExitCode = 0; OutputLines = @("discovery=ready") }
    }
    Assert-Throws -Pattern "result=ready" -Action {
        Invoke-SmokeEnumeration `
            -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
            -ExpectedSmokeSha256 ("E" * 64)
    }
}

Invoke-Case -Name "smoke driverMissing" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        [pscustomobject]@{
            ExitCode = 0
            OutputLines = @("discovery=driverMissing", "result=ready")
        }
    }
    Assert-Throws -Pattern "driverMissing|discovery=ready" -Action {
        Invoke-SmokeEnumeration `
            -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
            -ExpectedSmokeSha256 ("E" * 64)
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
    Set-TestFunction -Name Invoke-PnpUtilRemoveDevice -Body {
        $script:pnpCalls += 1
    }
    Set-TestFunction -Name Invoke-PnpUtilDeleteDriver -Body {
        $script:pnpCalls += 1
    }
    Assert-Throws -Pattern "ConfirmUninstall" -Action {
        Invoke-UninstallTestDriver `
            -ReleaseMetadata (New-TestReleaseMetadata) `
            -ConfirmUninstall:$false
    }
    if ($script:pnpCalls -ne 0) {
        throw "pnputil boundary was reached without uninstall confirmation."
    }
}

Invoke-Case -Name "one exact remove-device command" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    $script:processCall = $null
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        param($Executable, $Arguments, $TimeoutSeconds)
        $script:processCall = [pscustomobject]@{
            Executable = $Executable
            Arguments = @($Arguments)
            TimeoutSeconds = $TimeoutSeconds
        }
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    $instanceId = "ROOT\EMKEVIRTUALAUDIO\0000"
    Invoke-PnpUtilRemoveDevice `
        -InstanceId $instanceId `
        -HardwareId $script:TargetHardwareId
    $expectedExecutable = "C:\Windows\System32\pnputil.exe"
    if ($script:processCall.Executable -cne $expectedExecutable) {
        throw "Uninstall executable was not fixed to the system pnputil.exe."
    }
    if ($script:processCall.TimeoutSeconds -ne 120) {
        throw "Remove-device command did not use the bounded PnP timeout."
    }
    $expected = @("/remove-device", $instanceId)
    if ([string]::Join("`n", $script:processCall.Arguments) -cne
        [string]::Join("`n", $expected)) {
        throw "Remove-device arguments were not exact."
    }
}

Invoke-Case -Name "one exact delete-driver command" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    $script:processCall = $null
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        param($Executable, $Arguments, $TimeoutSeconds)
        $script:processCall = [pscustomobject]@{
            Executable = $Executable
            Arguments = @($Arguments)
            TimeoutSeconds = $TimeoutSeconds
        }
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    Invoke-PnpUtilDeleteDriver -PublishedInf "oem42.inf"
    $expected = @(
        "/delete-driver",
        "oem42.inf"
    )
    if ([string]::Join("`n", $script:processCall.Arguments) -cne
        [string]::Join("`n", $expected)) {
        throw "Delete-driver arguments were not exact."
    }
    if ($script:processCall.TimeoutSeconds -ne 120) {
        throw "Delete-driver command did not use the bounded PnP timeout."
    }
}

Invoke-Case -Name "delete-driver failure leaves package unproven" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    $script:processCall = $null
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        param($Executable, $Arguments, $TimeoutSeconds)
        $script:processCall = [pscustomobject]@{
            Arguments = @($Arguments)
            TimeoutSeconds = $TimeoutSeconds
        }
        [pscustomobject]@{ ExitCode = 5; OutputLines = @() }
    }
    Assert-Throws `
        -Pattern "package remains|package state is unproven|new reference" `
        -Action {
        Invoke-PnpUtilDeleteDriver -PublishedInf "oem42.inf"
    }
    $expected = @("/delete-driver", "oem42.inf")
    if ([string]::Join("`n", $script:processCall.Arguments) -cne
        [string]::Join("`n", $expected)) {
        throw "Delete failure path used uninstall or force arguments."
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
