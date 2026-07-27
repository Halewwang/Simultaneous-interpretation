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

function Get-ProductionFunctionBody {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors
    )
    if ($errors.Count -ne 0) {
        throw "Production script has parser errors: $($errors[0].Message)"
    }
    $definition = @($ast.FindAll(
        {
            param($candidate)
            $candidate -is
                [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $candidate.Name -ceq $Name
        },
        $false
    ))
    if ($definition.Count -ne 1) {
        throw "Expected exactly one production function '$Name'."
    }
    $bodyText = $definition[0].Body.Extent.Text
    return [scriptblock]::Create(
        $bodyText.Substring(1, $bodyText.Length - 2)
    )
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
    $directory = "C:\Package With Spaces"
    return [pscustomobject]@{
        Directory = $directory
        Inf = [pscustomobject]@{
            FullName = "$directory\Driver Name.inf"
        }
        Sys = [pscustomobject]@{
            FullName = "$directory\Driver Name.sys"
        }
        Cat = [pscustomobject]@{
            FullName = "$directory\Driver Name.cat"
        }
    }
}

function New-TemporaryInputSet {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $packageDirectory = Join-Path $Root "input package"
    [IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
    foreach ($entry in @(
        @{ Name = "EMKE.VirtualAudio.inf"; Content = "INF" }
        @{ Name = "EMKE.VirtualAudio.sys"; Content = "SYS" }
        @{ Name = "EMKE.VirtualAudio.cat"; Content = "CAT" }
    )) {
        [IO.File]::WriteAllText(
            (Join-Path $packageDirectory $entry.Name),
            $entry.Content,
            [Text.UTF8Encoding]::new($false)
        )
    }
    $smoke = Join-Path $Root "ready-looking smoke.exe"
    [IO.File]::WriteAllText(
        $smoke,
        "discovery=ready`nresult=ready",
        [Text.UTF8Encoding]::new($false)
    )
    return [pscustomobject]@{
        Package = Get-StrictDriverPackage -Directory $packageDirectory
        Smoke = Get-Item -LiteralPath $smoke
    }
}

function New-TestDirectoryReparsePoint {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Target
    )

    if ($IsWindows) {
        New-Item `
            -ItemType Junction `
            -Path $Path `
            -Target $Target `
            -ErrorAction Stop |
            Out-Null
        return
    }
    [IO.Directory]::CreateSymbolicLink($Path, $Target) | Out-Null
}

function New-WorkspaceTestRootPath {
    param(
        [Parameter(Mandatory)]
        [string]$Prefix
    )

    return Join-Path `
        $PSScriptRoot `
        (".$Prefix-" + [guid]::NewGuid().ToString("N"))
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
                FullName = "C:\Smoke\EMKE.AudioSmoke.exe"
            }
            PackageSha256 = "A" * 64
            SmokeSha256 = "E" * 64
        }
    }
    Set-TestFunction -Name Assert-StagedInputsUnchanged -Body {}
    Set-TestFunction -Name Remove-ProtectedStagingDirectory -Body {}
    Set-TestFunction -Name Invoke-DriverPackageVerifier -Body {}
    Set-TestFunction -Name Get-DriverPackageSha256 -Body { "A" * 64 }
    Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
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
            DriverVersion = "1.0.0.1"
            ProviderName = "EMKE"
            ModelSection = "EMKE.NTamd64.10.0...26200"
            InstallSection = "EMKE_Install"
            HardwareId = "ROOT\EMKEVIRTUALAUDIO"
        }
    }
    Set-TestFunction -Name Assert-InstalledDriverPackageIdentity -Body {
        [pscustomobject]@{
            InfName = "oem42.inf"
            DriverVersion = "1.0.0.1"
            ProviderName = "EMKE"
        }
    }
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        @()
    }
    Set-TestFunction -Name New-RootDevnodeFromInf -Body {
        "ROOT\EMKEVIRTUALAUDIO\0000"
    }
    Set-TestFunction -Name Wait-TargetDevnode -Body {
        New-Devnode
    }
    Set-TestFunction -Name Remove-ExactCreatedRootDevnode -Body {}
    Set-TestFunction -Name Wait-TargetDevnodeAbsent -Body {}
    Set-TestFunction -Name Invoke-SmokeEnumeration -Body {}
}

Import-LifecycleFunctions -Path $installScript

Invoke-Case -Name "local input rejects UNC and reparse paths" -Action {
    $testRoot = New-WorkspaceTestRootPath -Prefix "emke-input-path"
    try {
        [IO.Directory]::CreateDirectory($testRoot) | Out-Null
        $realPackage = Join-Path $testRoot "real"
        [IO.Directory]::CreateDirectory($realPackage) | Out-Null
        $accepted = Assert-LocalNonReparsePath `
            -Path $realPackage `
            -ExpectedType Container
        if ([IO.Path]::GetFullPath($accepted) -cne
            [IO.Path]::GetFullPath($realPackage)) {
            throw "Local non-reparse path was altered."
        }
        Assert-Throws -Pattern "UNC" -Action {
            Assert-LocalNonReparsePath `
                -Path "\\server\share\package" `
                -ExpectedType Container
        }
        $linkedPackage = Join-Path $testRoot "linked"
        New-TestDirectoryReparsePoint `
            -Path $linkedPackage `
            -Target $realPackage
        Assert-Throws -Pattern "reparse" -Action {
            Assert-LocalNonReparsePath `
                -Path $linkedPackage `
                -ExpectedType Container
        }
    } finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}

Invoke-Case -Name "protected staging ACL contract and cleanup guard" -Action {
    $expectedSddl =
        "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"
    if ((Get-ProtectedStagingSddl) -cne $expectedSddl) {
        throw "Protected staging SDDL was not the exact SYSTEM/admin contract."
    }
    if ($IsWindows) {
        Assert-ProtectedStagingSecurityDescriptor -Sddl $expectedSddl
    }

    $testRoot = New-WorkspaceTestRootPath -Prefix "emke-cleanup"
    try {
        [IO.Directory]::CreateDirectory($testRoot) | Out-Null
        $token = [guid]::NewGuid().ToString("N")
        $staging = Join-Path $testRoot $token
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $staging "owned.txt"),
            "owned"
        )
        Set-TestFunction -Name Get-SystemStagingRoot -Body {
            $script:testStagingRoot
        }
        Set-TestFunction -Name Assert-ProtectedStagingAcl -Body {}
        $script:testStagingRoot = $testRoot
        Remove-ProtectedStagingDirectory -Path $staging -Token $token
        if (Test-Path -LiteralPath $staging) {
            throw "Exact owned staging directory was not removed."
        }

        $outside = Join-Path $testRoot "outside"
        [IO.Directory]::CreateDirectory($outside) | Out-Null
        Assert-Throws -Pattern "owned staging|GUID|exact" -Action {
            Remove-ProtectedStagingDirectory `
                -Path $outside `
                -Token ([guid]::NewGuid().ToString("N"))
        }

        $linkedToken = [guid]::NewGuid().ToString("N")
        $linked = Join-Path $testRoot $linkedToken
        New-TestDirectoryReparsePoint -Path $linked -Target $outside
        Assert-Throws -Pattern "reparse" -Action {
            Remove-ProtectedStagingDirectory `
                -Path $linked `
                -Token $linkedToken
        }
        if (-not (Test-Path -LiteralPath $outside -PathType Container)) {
            throw "Reparse cleanup guard touched the referent."
        }
    } finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}

Invoke-Case -Name "staged inputs detect package and smoke replacement" -Action {
    $testRoot = New-WorkspaceTestRootPath -Prefix "emke-staged-input"
    try {
        [IO.Directory]::CreateDirectory($testRoot) | Out-Null
        $inputs = New-TemporaryInputSet -Root $testRoot
        $stage = Join-Path $testRoot "stage"
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        $stage2 = Join-Path $testRoot "stage2"
        [IO.Directory]::CreateDirectory($stage2) | Out-Null
        $staged = Copy-InstallInputsToStaging `
            -Package $inputs.Package `
            -SmokeFile $inputs.Smoke `
            -StagingRoot $stage2
        Assert-StagedInputsUnchanged -StagedInputs $staged

        [IO.File]::AppendAllText($staged.Smoke.FullName, "CHANGED")
        Assert-Throws -Pattern "Smoke|SHA-256|changed" -Action {
            Assert-StagedInputsUnchanged -StagedInputs $staged
        }

        $staged = Copy-InstallInputsToStaging `
            -Package $inputs.Package `
            -SmokeFile $inputs.Smoke `
            -StagingRoot $stage
        [IO.File]::AppendAllText($staged.Package.Sys.FullName, "CHANGED")
        Assert-Throws -Pattern "package|SHA-256|changed" -Action {
            Assert-StagedInputsUnchanged -StagedInputs $staged
        }
    } finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}

Invoke-Case -Name "expected Smoke digest blocks ready-looking replacement" -Action {
    if (-not (Get-Command Invoke-SmokeEnumeration).Parameters.ContainsKey(
        "ExpectedSmokeSha256"
    )) {
        throw "Invoke-SmokeEnumeration has no expected Smoke digest gate."
    }
    $testRoot = New-WorkspaceTestRootPath -Prefix "emke-smoke-digest"
    try {
        [IO.Directory]::CreateDirectory($testRoot) | Out-Null
        $smoke = Join-Path $testRoot "ready-looking.exe"
        [IO.File]::WriteAllText(
            $smoke,
            "discovery=ready`nresult=ready",
            [Text.UTF8Encoding]::new($false)
        )
        $script:processCalls = 0
        Set-TestFunction -Name Invoke-CapturedProcess -Body {
            $script:processCalls += 1
            [pscustomobject]@{
                ExitCode = 0
                OutputLines = @("discovery=ready", "result=ready")
            }
        }
        Assert-Throws `
            -Pattern "Smoke SHA-256 does not match the trusted expected value" `
            -Action {
            Invoke-SmokeEnumeration `
                -SmokePath $smoke `
                -ExpectedSmokeSha256 ("F" * 64)
        }
        if ($script:processCalls -ne 0) {
            throw "Untrusted ready-looking Smoke program reached execution."
        }
    } finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}

Invoke-Case -Name "install orchestrator uses only protected staged copies" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-InstallOrchestratorExternalBoundaries
    $stagingRoot =
        "C:\ProgramData\EMKE\DriverLabStaging\" + ("1" * 32)
    $stagedPackage = [pscustomobject]@{
        Directory = "$stagingRoot\package"
        Inf = [pscustomobject]@{
            FullName = "$stagingRoot\package\EMKE.VirtualAudio.inf"
        }
        Sys = [pscustomobject]@{
            FullName = "$stagingRoot\package\EMKE.VirtualAudio.sys"
        }
        Cat = [pscustomobject]@{
            FullName = "$stagingRoot\package\EMKE.VirtualAudio.cat"
        }
    }
    $stagedSmoke = [pscustomobject]@{
        FullName = "$stagingRoot\smoke\EMKE.AudioSmoke.exe"
    }
    Set-TestFunction -Name New-ProtectedStagingDirectory -Body ({
        [pscustomobject]@{
            Path = $stagingRoot
            Token = ("1" * 32)
        }
    }.GetNewClosure())
    Set-TestFunction -Name Copy-InstallInputsToStaging -Body ({
        [pscustomobject]@{
            Package = $stagedPackage
            Smoke = $stagedSmoke
            PackageSha256 = "A" * 64
            SmokeSha256 = "E" * 64
        }
    }.GetNewClosure())
    $script:stagedChecks = 0
    Set-TestFunction -Name Assert-StagedInputsUnchanged -Body {
        $script:stagedChecks += 1
    }
    $script:cleanupCalls = 0
    Set-TestFunction -Name Remove-ProtectedStagingDirectory -Body {
        $script:cleanupCalls += 1
    }
    $script:installInf = $null
    Set-TestFunction -Name Invoke-PnpUtilInstall -Body {
        param($InfPath)
        $script:installInf = $InfPath
    }
    $script:smokePath = $null
    Set-TestFunction -Name Invoke-SmokeEnumeration -Body {
        param($SmokePath)
        $script:smokePath = $SmokePath
    }
    $script:identityChecks = 0
    Set-TestFunction -Name Assert-InstalledDriverPackageIdentity -Body {
        param($Devnode, $InfMetadata)
        $script:identityChecks += 1
        if ($Devnode.PNPDeviceID -cne
            "ROOT\EMKEVIRTUALAUDIO\0000" -or
            $InfMetadata.DriverVersion -cne "1.0.0.1") {
            throw "Package identity received the wrong binding inputs."
        }
        [pscustomobject]@{
            InfName = "oem42.inf"
            DriverVersion = "1.0.0.1"
            ProviderName = "EMKE"
        }
    }
    $installOutput = @(Invoke-InstallTestDriver `
        -PackagePath "C:\Untrusted Downloads\package" `
        -ExpectedPackageSha256 ("A" * 64) `
        -SmokePath "C:\Untrusted Downloads\Smoke.exe" `
        -ExpectedSmokeSha256 ("E" * 64) `
        -ConfirmInstall *>&1)
    if ($script:installInf -cne $stagedPackage.Inf.FullName -or
        $script:smokePath -cne $stagedSmoke.FullName) {
        throw "Install or Smoke boundary received an unstaged input path."
    }
    if ($script:stagedChecks -lt 2) {
        throw "Staged inputs were not rechecked before mutation and Smoke."
    }
    if ($script:cleanupCalls -ne 1) {
        throw "Protected staging cleanup did not run exactly once."
    }
    if ($script:identityChecks -ne 1) {
        throw "Install did not prove the exact devnode package identity."
    }
    if (@($installOutput | Where-Object {
        [string]$_ -match (
            "host Authenticode validation only; " +
            "Microsoft/WHQL not established"
        )
    }).Count -ne 1) {
        throw "Install output overstated the host Authenticode proof boundary."
    }
}

Invoke-Case -Name "actual INF Models parser rejects inactive-section bait" -Action {
    Import-LifecycleFunctions -Path $installScript
    if (-not (Get-Command Get-DriverInfMetadata).Parameters.ContainsKey(
        "WindowsBuild"
    )) {
        throw "INF metadata parser does not select Models for a Windows build."
    }
    $windowsDirectory = [IO.Path]::GetFullPath(
        (Join-Path $toolsDirectory "..")
    )
    $sourceInf = Join-Path `
        (Join-Path `
            (Join-Path $windowsDirectory "driver") `
            "EMKE.VirtualAudio") `
        "EMKE.VirtualAudio.inf"
    $actual = Get-DriverInfMetadata `
        -Inf ([IO.FileInfo]::new($sourceInf)) `
        -WindowsBuild 26200
    if ($actual.ProviderName -cne "EMKE" -or
        $actual.DriverVersion -cne "1.0.0.1" -or
        $actual.ModelSection -cne "EMKE.NTamd64.10.0...26200" -or
        $actual.InstallSection -cne "EMKE_Install" -or
        $actual.HardwareId -cne "ROOT\EMKEVIRTUALAUDIO") {
        throw "Actual INF effective Models metadata was parsed incorrectly."
    }

    $testRoot = New-WorkspaceTestRootPath -Prefix "emke-inf-parser"
    try {
        [IO.Directory]::CreateDirectory($testRoot) | Out-Null
        $original = [IO.File]::ReadAllText($sourceInf)
        $cases = @(
            [pscustomobject]@{
                Name = "inactive-model"
                Text = (
                    $original +
                    "`n[EMKE.NTamd64]`n" +
                    "%DeviceDescription%=Evil_Install,ROOT\EVIL`n"
                )
            },
            [pscustomobject]@{
                Name = "extra-compatible-id"
                Text = $original.Replace(
                    "EMKE_Install,ROOT\EMKEVIRTUALAUDIO",
                    "EMKE_Install,ROOT\EMKEVIRTUALAUDIO,ROOT\EVIL"
                )
            },
            [pscustomobject]@{
                Name = "duplicate-active-model"
                Text = $original.Replace(
                    (
                        "%DeviceDescription%=EMKE_Install," +
                        "ROOT\EMKEVIRTUALAUDIO"
                    ),
                    (
                        "%DeviceDescription%=EMKE_Install," +
                        "ROOT\EMKEVIRTUALAUDIO`n" +
                        "%DeviceDescription%=EMKE_Install," +
                        "ROOT\EMKEVIRTUALAUDIO"
                    )
                )
            }
        )
        foreach ($case in $cases) {
            $path = Join-Path $testRoot "$($case.Name).inf"
            [IO.File]::WriteAllText(
                $path,
                $case.Text,
                [Text.UTF8Encoding]::new($false)
            )
            Assert-Throws `
                -Pattern "Models|model|hardware|compatible|section" `
                -Action {
                Get-DriverInfMetadata `
                    -Inf (Get-Item -LiteralPath $path) `
                    -WindowsBuild 26200
            }
        }
    } finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}

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

Invoke-Case -Name "catalog certificate digest is exactly SHA256" -Action {
    foreach ($digest in @(
        ("B" * 63),
        ("B" * 65),
        ("G" * 64)
    )) {
        Assert-Throws -Pattern "SHA-256" -Action {
            Assert-CatalogSignatureValid -Metadata ([pscustomobject]@{
                Status = "Valid"
                Certificate = [pscustomobject]@{ Subject = "CN=Test" }
                SummarySha256 = $digest
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
    foreach ($invalidCode in @($null, "not-an-integer")) {
        Assert-Throws `
            -Pattern "ConfigManagerErrorCode must be an integer" `
            -Action {
            Assert-InstalledDevnodeHealthy -Devnodes @(
                [pscustomobject]@{
                    PNPDeviceID = "ROOT\EMKEVIRTUALAUDIO\0000"
                    HardwareID = @("ROOT\EMKEVIRTUALAUDIO")
                    Present = $true
                    ConfigManagerErrorCode = $invalidCode
                }
            )
        }
    }
}

Invoke-Case -Name "installed package identity matches exact devnode" -Action {
    Import-LifecycleFunctions -Path $installScript
    $devnode = New-Devnode
    $metadata = [pscustomobject]@{
        DriverVersion = "1.0.0.1"
        ProviderName = "EMKE"
    }
    $script:signedDriverRows = @(
        [pscustomobject]@{
            DeviceID = "ROOT\OTHER\0000"
            InfName = "oem9.inf"
            DriverVersion = "9.9.9.9"
            ProviderName = "Other"
        },
        [pscustomobject]@{
            DeviceID = $devnode.PNPDeviceID
            InfName = "oem42.inf"
            DriverVersion = "1.0.0.1"
            ProviderName = "EMKE"
        }
    )
    Set-TestFunction -Name Get-CimInstance -Body {
        @($script:signedDriverRows)
    }
    $identity = Assert-InstalledDriverPackageIdentity `
        -Devnode $devnode `
        -InfMetadata $metadata
    if ($identity.InfName -cne "oem42.inf") {
        throw "Exact devnode package identity selected the wrong row."
    }

    $invalidRows = @(
        ,([object[]]@())
        ,([object[]]@(
            $script:signedDriverRows[1],
            $script:signedDriverRows[1]
        ))
        ,([object[]]@([pscustomobject]@{
            DeviceID = $devnode.PNPDeviceID
            InfName = "emke.inf"
            DriverVersion = "1.0.0.1"
            ProviderName = "EMKE"
        }))
        ,([object[]]@([pscustomobject]@{
            DeviceID = $devnode.PNPDeviceID
            InfName = "oem42.inf"
            DriverVersion = "2.0.0.0"
            ProviderName = "EMKE"
        }))
        ,([object[]]@([pscustomobject]@{
            DeviceID = $devnode.PNPDeviceID
            InfName = "oem42.inf"
            DriverVersion = "1.0.0.1"
            ProviderName = "Other"
        }))
    )
    foreach ($rows in $invalidRows) {
        $script:signedDriverRows = $rows
        Assert-Throws `
            -Pattern "package identity|published INF|version|provider" `
            -Action {
            Assert-InstalledDriverPackageIdentity `
                -Devnode $devnode `
                -InfMetadata $metadata
        }
    }
}

Invoke-Case -Name "embedded SetupAPI helper compiles without mutation" -Action {
    Import-LifecycleFunctions -Path $installScript
    Initialize-RootDevnodeSetupApi
    $helper = "Emke.DriverLab.RootDevnodeSetupApi" -as [type]
    if ($null -eq $helper) {
        throw "Embedded SetupAPI helper type was not compiled."
    }
    $publicMethods = @($helper.GetMethods() |
        ForEach-Object { $_.Name })
    foreach ($required in @("Create", "RemoveExact")) {
        if ($publicMethods -notcontains $required) {
            throw "Embedded SetupAPI helper is missing '$required'."
        }
    }
}

Invoke-Case -Name "root create bind package identity state machine" -Action {
    Import-LifecycleFunctions -Path $installScript
    $script:stateSequence = [Collections.Generic.List[string]]::new()
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        [void]$script:stateSequence.Add("preflight-empty")
        @()
    }
    Set-TestFunction -Name New-RootDevnodeFromInf -Body {
        param($InfPath, $HardwareId)
        [void]$script:stateSequence.Add("create:$HardwareId")
        "ROOT\EMKEVIRTUALAUDIO\0007"
    }
    $script:waitCount = 0
    Set-TestFunction -Name Wait-TargetDevnode -Body {
        param($ExpectedInstanceId)
        $script:waitCount += 1
        [void]$script:stateSequence.Add(
            "wait$($script:waitCount):$ExpectedInstanceId"
        )
        New-Devnode -DeviceID $ExpectedInstanceId
    }
    Set-TestFunction -Name Invoke-PnpUtilInstall -Body {
        [void]$script:stateSequence.Add("bind")
    }
    Set-TestFunction -Name Assert-InstalledDriverPackageIdentity -Body {
        [void]$script:stateSequence.Add("identity")
        [pscustomobject]@{
            InfName = "oem42.inf"
            DriverVersion = "1.0.0.1"
            ProviderName = "EMKE"
        }
    }
    Set-TestFunction -Name Remove-ExactCreatedRootDevnode -Body {
        throw "Successful bind must not invoke cleanup."
    }
    $result = Invoke-CreateAndBindRootDevnode `
        -StagedInf ([pscustomobject]@{
            FullName = "C:\Protected\EMKE.VirtualAudio.inf"
        }) `
        -InfMetadata ([pscustomobject]@{
            DriverVersion = "1.0.0.1"
            ProviderName = "EMKE"
        })
    $expected = @(
        "preflight-empty",
        "create:ROOT\EMKEVIRTUALAUDIO",
        "wait1:ROOT\EMKEVIRTUALAUDIO\0007",
        "bind",
        "wait2:ROOT\EMKEVIRTUALAUDIO\0007",
        "identity"
    )
    if ([string]::Join("`n", $script:stateSequence) -cne
        [string]::Join("`n", $expected) -or
        $result.Devnode.PNPDeviceID -cne
        "ROOT\EMKEVIRTUALAUDIO\0007") {
        throw "Root create/bind/package-identity sequence was not exact."
    }
}

Invoke-Case -Name "preexisting target blocks root creation" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        @(New-Devnode)
    }
    $script:createCalls = 0
    Set-TestFunction -Name New-RootDevnodeFromInf -Body {
        $script:createCalls += 1
    }
    Assert-Throws -Pattern "already exists|pre-existing" -Action {
        Invoke-CreateAndBindRootDevnode `
            -StagedInf ([pscustomobject]@{
                FullName = "C:\Protected\EMKE.VirtualAudio.inf"
            }) `
            -InfMetadata ([pscustomobject]@{
                DriverVersion = "1.0.0.1"
                ProviderName = "EMKE"
            })
    }
    if ($script:createCalls -ne 0) {
        throw "Pre-existing target reached root-devnode creation."
    }
}

Invoke-Case -Name "bind failure reports partial state and exact cleanup" -Action {
    foreach ($cleanupSucceeds in @($true, $false)) {
        Import-LifecycleFunctions -Path $installScript
        Set-TestFunction -Name Get-TargetDevnodes -Body { @() }
        Set-TestFunction -Name New-RootDevnodeFromInf -Body {
            "ROOT\EMKEVIRTUALAUDIO\0042"
        }
        Set-TestFunction -Name Wait-TargetDevnode -Body {
            New-Devnode -DeviceID "ROOT\EMKEVIRTUALAUDIO\0042"
        }
        Set-TestFunction -Name Invoke-PnpUtilInstall -Body {
            throw "simulated bind failure"
        }
        $cleanupIds = [Collections.Generic.List[string]]::new()
        Set-TestFunction -Name Remove-ExactCreatedRootDevnode -Body ({
            param($InstanceId)
            [void]$cleanupIds.Add($InstanceId)
            if (-not $cleanupSucceeds) {
                throw "simulated cleanup failure"
            }
        }.GetNewClosure())
        Set-TestFunction -Name Wait-TargetDevnodeAbsent -Body {}
        $pattern = if ($cleanupSucceeds) {
            "Partial state.*confirmed absent"
        } else {
            "Partial state.*state uncertain"
        }
        Assert-Throws -Pattern $pattern -Action {
            Invoke-CreateAndBindRootDevnode `
                -StagedInf ([pscustomobject]@{
                    FullName = "C:\Protected\EMKE.VirtualAudio.inf"
                }) `
                -InfMetadata ([pscustomobject]@{
                    DriverVersion = "1.0.0.1"
                    ProviderName = "EMKE"
                })
        }
        if ($cleanupIds.Count -ne 1 -or
            $cleanupIds[0] -cne
            "ROOT\EMKEVIRTUALAUDIO\0042") {
            throw "Bind failure cleanup was not limited to the created instance."
        }
    }
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
            -ExpectedSmokeSha256 ("E" * 64) `
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
        Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
        $script:smokeLines = $lines
        Set-TestFunction -Name Invoke-CapturedProcess -Body {
            [pscustomobject]@{
                ExitCode = 0
                OutputLines = @($script:smokeLines)
            }
        }
        Assert-Throws -Pattern "exactly one|status" -Action {
            Invoke-SmokeEnumeration `
                -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
                -ExpectedSmokeSha256 ("E" * 64)
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
        Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
        $script:smokeLines = $lines
        Set-TestFunction -Name Invoke-CapturedProcess -Body {
            [pscustomobject]@{
                ExitCode = 0
                OutputLines = @($script:smokeLines)
            }
        }
        Assert-Throws -Pattern "exactly one|status" -Action {
            Invoke-SmokeEnumeration `
                -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
                -ExpectedSmokeSha256 ("E" * 64)
        }
    }
}

Invoke-Case -Name "smoke raw detail remains suppressed" -Action {
    Import-LifecycleFunctions -Path $installScript
    Set-TestFunction -Name Resolve-RequiredFile -Body {
        param($Path)
        $Path
    }
    Set-TestFunction -Name Get-FileSha256 -Body { "E" * 64 }
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
        -SmokePath "C:\Smoke\EMKE.AudioSmoke.exe" `
        -ExpectedSmokeSha256 ("E" * 64))
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
        -ExpectedSmokeSha256 ("E" * 64) `
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
    Set-TestFunction -Name Assert-NoOtherPublishedInfReferences -Body {}
    Set-TestFunction -Name Wait-TargetDevnodeAbsent -Body {
        throw "The exact target devnode is still present after remove-device."
    }
    Set-TestFunction -Name Wait-PublishedInfUnreferenced -Body {}
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
    Assert-Throws -Pattern "still present" -Action {
        Invoke-UninstallTestDriver -ConfirmUninstall
    }
    $expected = @(
        "/remove-device",
        "ROOT\EMKEVIRTUALAUDIO\0000"
    )
    if ($script:processCalls.Count -ne 1 -or
        [string]::Join("`n", $script:processCalls[0].Arguments) -cne
        [string]::Join("`n", $expected)) {
        throw "Present-after-remove path crossed the wrong process boundary."
    }
}

Invoke-Case -Name "shared published INF blocks every deletion" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    Set-TestFunction -Name Assert-LabMachinePrerequisites -Body {}
    $target = New-Devnode
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        @($script:targetDevnode)
    }
    Set-TestFunction -Name Get-PublishedInfForDevnode -Body { "oem42.inf" }
    $script:targetDevnode = $target
    $script:signedDriverRows = @(
        [pscustomobject]@{
            DeviceID = $target.PNPDeviceID
            InfName = "oem42.inf"
        },
        [pscustomobject]@{
            DeviceID = "ROOT\OTHER\0000"
            InfName = "oem42.inf"
        }
    )
    Set-TestFunction -Name Get-CimInstance -Body {
        @($script:signedDriverRows)
    }
    $script:processCalls = 0
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        $script:processCalls += 1
        throw "Shared-reference preflight reached a process boundary."
    }
    Assert-Throws -Pattern "shared|other|reference" -Action {
        Invoke-UninstallTestDriver -ConfirmUninstall
    }
    if ($script:processCalls -ne 0) {
        throw "Shared published INF reached remove-device or delete-driver."
    }
}

Invoke-Case -Name "uninstall orchestrator exact process boundary" -Action {
    Import-LifecycleFunctions -Path $uninstallScript
    Set-TestFunction -Name Assert-SupportedWindowsHost -Body {}
    Set-TestFunction -Name Assert-LabMachinePrerequisites -Body {}
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        @((New-Devnode))
    }
    Set-TestFunction -Name Get-PublishedInfForDevnode -Body { "oem42.inf" }
    $script:uninstallSequence =
        [Collections.Generic.List[string]]::new()
    Set-TestFunction -Name Assert-NoOtherPublishedInfReferences -Body {
        [void]$script:uninstallSequence.Add("preflight-unshared")
    }
    Set-TestFunction -Name Wait-TargetDevnodeAbsent -Body {
        [void]$script:uninstallSequence.Add("absent")
    }
    Set-TestFunction -Name Wait-PublishedInfUnreferenced -Body {
        [void]$script:uninstallSequence.Add("unreferenced")
    }
    Set-TestFunction -Name Resolve-SystemPnpUtil -Body {
        "C:\Windows\System32\pnputil.exe"
    }
    $script:processCalls = [Collections.Generic.List[object]]::new()
    Set-TestFunction -Name Invoke-CapturedProcess -Body {
        param($Executable, $Arguments)
        [void]$script:uninstallSequence.Add($Arguments[0])
        [void]$script:processCalls.Add([pscustomobject]@{
            Executable = $Executable
            Arguments = @($Arguments)
        })
        [pscustomobject]@{ ExitCode = 0; OutputLines = @() }
    }
    Invoke-UninstallTestDriver -ConfirmUninstall
    if ($script:processCalls.Count -ne 2) {
        throw "Uninstall orchestrator did not reach exactly two process boundaries."
    }
    $expectedCalls = @(
        ,@("/remove-device", "ROOT\EMKEVIRTUALAUDIO\0000")
        ,@("/delete-driver", "oem42.inf", "/uninstall", "/force")
    )
    for ($index = 0; $index -lt $expectedCalls.Count; $index += 1) {
        if ($script:processCalls[$index].Executable -cne
            "C:\Windows\System32\pnputil.exe" -or
            [string]::Join(
                "`n",
                $script:processCalls[$index].Arguments
            ) -cne
            [string]::Join("`n", $expectedCalls[$index])) {
            throw "Uninstall orchestrator process boundary was not exact."
        }
    }
    $expectedSequence = @(
        "preflight-unshared",
        "/remove-device",
        "absent",
        "unreferenced",
        "/delete-driver"
    )
    if ([string]::Join("`n", $script:uninstallSequence) -cne
        [string]::Join("`n", $expectedSequence)) {
        throw "Uninstall remove/unreference/delete sequence was not exact."
    }
}

Invoke-Case `
    -Name "captured process timeout kills and reports uncertain state" `
    -Action {
        $invokeCaptured = Get-ProductionFunctionBody `
            -Path $installScript `
            -Name "Invoke-CapturedProcess"
        $executableName = if ($IsWindows) { "pwsh.exe" } else { "pwsh" }
        $pwsh = Join-Path $PSHOME $executableName
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $caught = $null
        try {
            & $invokeCaptured `
                -Executable $pwsh `
                -Arguments @(
                    "-NoProfile",
                    "-Command",
                    "Start-Sleep -Seconds 20"
                ) `
                -TimeoutSeconds 1
        } catch {
            $caught = $_
        } finally {
            $stopwatch.Stop()
        }
        if ($null -eq $caught) {
            throw "Long-running safe child process did not time out."
        }
        if ($caught.Exception.Message -notmatch
            "timed out.*state uncertain.*read-only inventory") {
            throw (
                "Timeout did not report the uncertain read-only boundary: " +
                $caught.Exception.Message
            )
        }
        if ($caught.Exception.Data["StateUncertain"] -ne $true) {
            throw "Timeout exception did not carry StateUncertain metadata."
        }
        if ($stopwatch.Elapsed.TotalSeconds -gt 6) {
            throw "Timed-out process was not killed and reaped promptly."
        }
    }

Invoke-Case -Name "bounded polling reaches completion and timeout" -Action {
    Import-LifecycleFunctions -Path $installScript
    $script:pollActionCalls = 0
    $script:pollDelayCalls = 0
    Set-TestFunction -Name Invoke-PollDelay -Body {
        param($DelayMilliseconds)
        $script:pollDelayCalls += 1
    }
    $result = Invoke-BoundedPoll `
        -Action {
            $script:pollActionCalls += 1
            $script:pollActionCalls
        } `
        -IsComplete {
            param($Value)
            $Value -eq 3
        } `
        -Description "safe synthetic readiness" `
        -MaxAttempts 3 `
        -DelayMilliseconds 1
    if ($result -ne 3 -or
        $script:pollActionCalls -ne 3 -or
        $script:pollDelayCalls -ne 2) {
        throw "Bounded poll did not stop at the exact successful attempt."
    }

    $script:pollActionCalls = 0
    $script:pollDelayCalls = 0
    Assert-Throws `
        -Pattern "Timed out.*state uncertain.*read-only inventory" `
        -Action {
            Invoke-BoundedPoll `
                -Action {
                    $script:pollActionCalls += 1
                    $false
                } `
                -IsComplete {
                    param($Value)
                    $Value
                } `
                -Description "safe synthetic timeout" `
                -MaxAttempts 3 `
                -DelayMilliseconds 1
        }
    if ($script:pollActionCalls -ne 3 -or
        $script:pollDelayCalls -ne 2) {
        throw "Bounded poll exceeded its exact attempt or delay budget."
    }
}

Invoke-Case `
    -Name "devnode and published INF polling is exact and bounded" `
    -Action {
        Import-LifecycleFunctions -Path $installScript
        Set-TestFunction -Name Invoke-PollDelay -Body {}
        $expectedInstanceId = "ROOT\EMKEVIRTUALAUDIO\0042"
        $script:devnodePollCalls = 0
        Set-TestFunction -Name Get-TargetDevnodes -Body {
            $script:devnodePollCalls += 1
            if ($script:devnodePollCalls -eq 1) {
                return @()
            }
            return @(New-Devnode -DeviceID $script:expectedInstanceId)
        }
        $script:expectedInstanceId = $expectedInstanceId
        $found = Wait-TargetDevnode `
            -ExpectedInstanceId $expectedInstanceId `
            -MaxAttempts 3 `
            -DelayMilliseconds 1
        if ($found.PNPDeviceID -cne $expectedInstanceId -or
            $script:devnodePollCalls -ne 2) {
            throw "Devnode readiness poll did not return the exact instance."
        }

        $script:devnodePollCalls = 0
        Set-TestFunction -Name Get-TargetDevnodes -Body {
            $script:devnodePollCalls += 1
            if ($script:devnodePollCalls -eq 1) {
                return @(
                    New-Devnode -DeviceID $script:expectedInstanceId
                )
            }
            return @()
        }
        Wait-TargetDevnodeAbsent `
            -ExpectedInstanceId $expectedInstanceId `
            -MaxAttempts 3 `
            -DelayMilliseconds 1
        if ($script:devnodePollCalls -ne 2) {
            throw "Devnode absence poll did not observe exact disappearance."
        }

        Import-LifecycleFunctions -Path $uninstallScript
        Set-TestFunction -Name Invoke-PollDelay -Body {}
        $script:publishedPollCalls = 0
        Set-TestFunction -Name Get-CimInstance -Body {
            $script:publishedPollCalls += 1
            if ($script:publishedPollCalls -eq 1) {
                return @([pscustomobject]@{
                    DeviceID = "ROOT\EMKEVIRTUALAUDIO\0042"
                    InfName = "oem42.inf"
                })
            }
            return @()
        }
        Wait-PublishedInfUnreferenced `
            -PublishedInf "oem42.inf" `
            -MaxAttempts 3 `
            -DelayMilliseconds 1
        if ($script:publishedPollCalls -ne 2) {
            throw "Published INF poll did not observe exact unreference."
        }
    }

Invoke-Case -Name "process timeout permits only read-only inventory" -Action {
    Import-LifecycleFunctions -Path $installScript
    $script:inventoryCalls = 0
    Set-TestFunction -Name Get-TargetDevnodes -Body {
        $script:inventoryCalls += 1
        @()
    }
    Set-TestFunction -Name New-RootDevnodeFromInf -Body {
        "ROOT\EMKEVIRTUALAUDIO\0042"
    }
    Set-TestFunction -Name Wait-TargetDevnode -Body {
        New-Devnode -DeviceID "ROOT\EMKEVIRTUALAUDIO\0042"
    }
    Set-TestFunction -Name Invoke-PnpUtilInstall -Body {
        $exception = [TimeoutException]::new(
            "Process timed out; state uncertain; perform read-only inventory."
        )
        $exception.Data["StateUncertain"] = $true
        throw $exception
    }
    $script:cleanupMutations = 0
    Set-TestFunction -Name Remove-ExactCreatedRootDevnode -Body {
        $script:cleanupMutations += 1
    }
    Set-TestFunction -Name Wait-TargetDevnodeAbsent -Body {
        throw "Timeout path must not claim absence."
    }
    $caught = $null
    try {
        Invoke-CreateAndBindRootDevnode `
            -StagedInf ([pscustomobject]@{
                FullName = "C:\Protected\EMKE.VirtualAudio.inf"
            }) `
            -InfMetadata ([pscustomobject]@{
                DriverVersion = "1.0.0.1"
                ProviderName = "EMKE"
            })
    } catch {
        $caught = $_
    }
    if ($null -eq $caught -or
        $caught.Exception.Message -notmatch
        "Partial state.*state uncertain.*read-only inventory") {
        throw "Timeout path did not preserve the uncertain partial state."
    }
    if ($caught.Exception.Message -match "rollback") {
        throw "Timeout path made a rollback claim."
    }
    if ($script:cleanupMutations -ne 0 -or
        $script:inventoryCalls -lt 2) {
        throw "Timeout path mutated state or skipped read-only inventory."
    }
}

if ($script:failures.Count -ne 0) {
    throw (
        "Lifecycle validation tests failed:`n" +
        ($script:failures -join [Environment]::NewLine)
    )
}

Write-Host "Lifecycle validation tests passed without device or certificate mutation."
