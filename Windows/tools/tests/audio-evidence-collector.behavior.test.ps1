[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$toolsDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$collectorScript = Join-Path $toolsDirectory "collect-audio-evidence.ps1"
$script:failures = [Collections.Generic.List[string]]::new()
$script:privacyCanary = "PRIVATE-ENDPOINT-7B2-BEHAVIOR-CANARY"

function Set-TestFunction {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Body
    )

    Set-Item -LiteralPath "Function:\global:$Name" -Value $Body -Force
}

function Import-CollectorFunctions {
    if (-not (Test-Path -LiteralPath $collectorScript -PathType Leaf)) {
        throw "Collector script is missing."
    }
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $collectorScript,
        [ref]$tokens,
        [ref]$errors
    )
    if ($errors.Count -ne 0) {
        throw "Collector script has parser errors."
    }
    $definitions = @($ast.FindAll(
        {
            param($candidate)
            $candidate -is
                [Management.Automation.Language.FunctionDefinitionAst]
        },
        $false
    ))
    foreach ($definition in $definitions) {
        $body = $definition.Body.Extent.Text
        $body = $body.Substring(1, $body.Length - 2)
        Set-TestFunction `
            -Name $definition.Name `
            -Body ([scriptblock]::Create($body))
    }
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

function Assert-PrivateFailure {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Pattern,

        [string[]]$Forbidden = @()
    )

    $caught = $null
    $emitted = [Collections.Generic.List[string]]::new()
    try {
        & $Action *>&1 |
            ForEach-Object { [void]$emitted.Add([string]$_) }
    } catch {
        $caught = $_
    }
    if ($null -eq $caught -or
        $caught.Exception.Message -notmatch $Pattern) {
        throw "Expected private failure category '$Pattern'."
    }
    $combined = $caught.Exception.ToString() + "`n" + ($emitted -join "`n")
    foreach ($value in $Forbidden) {
        if (-not [string]::IsNullOrEmpty($value) -and
            $combined.Contains($value, [StringComparison]::Ordinal)) {
            throw "Collector failure disclosed forbidden input."
        }
    }
}

function Write-Utf8Text {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Text
    )

    [IO.File]::WriteAllText(
        $Path,
        $Text,
        [Text.UTF8Encoding]::new($false)
    )
}

function Get-BehaviorInfText {
    return @'
[Version]
Signature="$Windows NT$"
Class=MEDIA
Provider=%ProviderName%
DriverVer=07/26/2026,1.0.0.1
CatalogFile=EMKE.VirtualAudio.cat

[Manufacturer]
%ManufacturerName%=EMKE,NTamd64.10.0...26200

[EMKE.NTamd64.10.0...26200]
%DeviceDescription%=EMKE_Install,ROOT\EMKEVIRTUALAUDIO

[EMKE_Install.NT]
AddReg=EMKE.Device.AddReg

[EMKE.Device.AddReg]
HKR,,DriverAbi,0x00010001,0x00000001

[Strings]
ProviderName="EMKE"
ManufacturerName="EMKE"
DeviceDescription="EMKE Virtual Audio Bridge"
'@
}

function New-BehaviorObservation {
    $started = "2026-07-27T01:00:00.000Z"
    $completed = "2026-07-27T01:00:01.000Z"
    $external = "passed"
    $audio = {
        param($name, $inbound, $outbound, $underruns)
        return [ordered]@{
            name = $name
            startedAtUtc = $started
            completedAtUtc = $completed
            exitCode = 0
            discovery = "ready"
            result = "completed"
            inboundRoute = $inbound
            outboundRoute = $outbound
            outboundUnderruns = $underruns
            droppedFrames = 0
            externalObservation = $external
        }
    }
    return [ordered]@{
        schemaVersion = 1
        observedAtUtc = "2026-07-27T01:05:00.000Z"
        endpoints = @(
            [ordered]@{
                role = "emke.meeting-speaker.render"
                opaqueEndpointId = "$($script:privacyCanary)-1"
            },
            [ordered]@{
                role = "emke.app-speaker.capture"
                opaqueEndpointId = "$($script:privacyCanary)-2"
            },
            [ordered]@{
                role = "emke.app-microphone.render"
                opaqueEndpointId = "$($script:privacyCanary)-3"
            },
            [ordered]@{
                role = "emke.meeting-microphone.capture"
                opaqueEndpointId = "$($script:privacyCanary)-4"
            }
        )
        scenarios = @(
            [ordered]@{
                name = "enumerate"
                startedAtUtc = $started
                completedAtUtc = $completed
                exitCode = 0
                discovery = "ready"
                result = "ready"
                externalObservation = $external
            },
            (& $audio "inbound-original" 3 1 0),
            (& $audio "inbound-translated" 1 1 0),
            (& $audio "outbound-translated" 1 1 0),
            (& $audio "outbound-underrun" 1 4 0),
            (& $audio "inbound-failure" 2 1 0),
            (& $audio "outbound-failure" 1 4 0),
            [ordered]@{
                name = "crash-after-mic-open"
                startedAtUtc = $started
                completedAtUtc = $completed
                exitCode = 23
                discovery = "ready"
                result = "crashingAfterMicOpen"
                externalObservation = $external
            }
        )
    }
}

function Get-ReferencePackageSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    $parts = @{}
    foreach ($extension in @("inf", "sys", "cat")) {
        $file = Get-ChildItem `
            -LiteralPath $Directory `
            -File |
            Where-Object { $_.Extension -ieq ".$extension" }
        $parts[$extension] = (Get-FileHash `
            -LiteralPath $file.FullName `
            -Algorithm SHA256).Hash.ToUpperInvariant()
    }
    $manifest = (
        "EMKE-DRIVER-PACKAGE-SHA256-V1`n" +
        "INF=$($parts.inf)`n" +
        "SYS=$($parts.sys)`n" +
        "CAT=$($parts.cat)`n"
    )
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($manifest)
        )
    )
}

function New-BehaviorFixture {
    $root = Join-Path `
        $PSScriptRoot `
        (".audio-evidence-behavior-" + [guid]::NewGuid().ToString("N"))
    $repository = Join-Path $root "repository"
    $package = Join-Path $root "package"
    [IO.Directory]::CreateDirectory($repository) | Out-Null
    [IO.Directory]::CreateDirectory($package) | Out-Null
    Write-Utf8Text `
        -Path (Join-Path $package "EMKE.VirtualAudio.inf") `
        -Text (Get-BehaviorInfText)
    Write-Utf8Text `
        -Path (Join-Path $package "EMKE.VirtualAudio.sys") `
        -Text "behavior-sys"
    Write-Utf8Text `
        -Path (Join-Path $package "EMKE.VirtualAudio.cat") `
        -Text "behavior-cat"
    $observationPath = Join-Path $root "observation.json"
    Write-Utf8Text `
        -Path $observationPath `
        -Text ((New-BehaviorObservation) |
            ConvertTo-Json -Depth 12 -Compress)
    $saltPath = Join-Path $root "salt.bin"
    [IO.File]::WriteAllBytes($saltPath, [byte[]](0..31))
    $recordingPath = Join-Path $root "recording.bundle"
    Write-Utf8Text -Path $recordingPath -Text "private-recording-content"
    return [pscustomobject]@{
        Root = $root
        Repository = $repository
        Package = $package
        Observation = $observationPath
        Salt = $saltPath
        Output = (Join-Path $root "evidence.json")
        Recording = $recordingPath
        Commit = "c" * 40
        PackageSha256 = Get-ReferencePackageSha256 -Directory $package
    }
}

function Remove-BehaviorFixture {
    param(
        [Parameter(Mandatory)]
        [psobject]$Fixture
    )

    if (Test-Path -LiteralPath $Fixture.Root) {
        [IO.Directory]::Delete($Fixture.Root, $true)
    }
}

function Set-SafeCollectorSeams {
    $script:testHostBuild = 26200
    $script:testHostArchitecture = "x64"
    $script:testRepositoryHead = "c" * 40
    Set-TestFunction -Name Get-CollectorHostInfo -Body {
        [pscustomobject]@{
            OsBuild = $script:testHostBuild
            Architecture = $script:testHostArchitecture
        }
    }
    Set-TestFunction -Name Get-CollectorRepositoryHead -Body {
        $script:testRepositoryHead
    }
    Set-TestFunction -Name Get-CollectorUtcNow -Body {
        "2026-07-27T02:00:00.000Z"
    }
    Set-TestFunction -Name Get-AuthenticodeSignature -Body {
        $certificate = [pscustomobject]@{}
        $certificate | Add-Member -MemberType ScriptMethod `
            -Name GetCertHashString `
            -Value { param($Algorithm) "B" * 64 }
        [pscustomobject]@{
            Status = "Valid"
            SignerCertificate = $certificate
        }
    }
}

function Invoke-FixtureCollection {
    param(
        [Parameter(Mandatory)]
        [psobject]$Fixture,

        [switch]$IncludeRecording,

        [switch]$Confirm = $true,

        [string]$ExpectedCommit = $Fixture.Commit,

        [string]$ExpectedPackageSha256 = $Fixture.PackageSha256
    )

    $parameters = @{
        RepositoryPath = $Fixture.Repository
        ExpectedSourceCommit = $ExpectedCommit
        PackagePath = $Fixture.Package
        ExpectedPackageSha256 = $ExpectedPackageSha256
        ObservationPath = $Fixture.Observation
        SaltPath = $Fixture.Salt
        OutputPath = $Fixture.Output
        ConfirmCollect = $Confirm
    }
    if ($IncludeRecording) {
        $parameters.RecordingBundlePath = $Fixture.Recording
    }
    Invoke-CollectAudioEvidence @parameters
}

Invoke-Case -Name "dot-source rejects before caller state and functions" -Action {
    $parameters = @{
        RepositoryPath = "C:\synthetic\repository"
        ExpectedSourceCommit = "c" * 40
        PackagePath = "C:\synthetic\package"
        ExpectedPackageSha256 = "a" * 64
        ObservationPath = "C:\synthetic\observation.json"
        SaltPath = "C:\synthetic\salt.bin"
        OutputPath = "C:\synthetic\evidence.json"
    }
    & {
        $originalPreference = $ErrorActionPreference
        $caught = $null
        try {
            . $collectorScript @parameters
        } catch {
            $caught = $_
        }
        if ($null -eq $caught -or
            $caught.Exception.Message -notmatch "dot-source") {
            throw "Collector did not reject dot-source."
        }
        if ($ErrorActionPreference -cne $originalPreference) {
            throw "Dot-source changed caller error behavior."
        }
        foreach ($name in @(
            "Invoke-CollectAudioEvidence",
            "Read-CollectorObservation",
            "Write-AtomicEvidenceFile"
        )) {
            if ($null -ne (Get-Command `
                -Name $name `
                -CommandType Function `
                -ErrorAction SilentlyContinue)) {
                throw "Dot-source leaked collector functions."
            }
        }
    }
}

Import-CollectorFunctions

Invoke-Case -Name "confirmation and host gates run before input reads" -Action {
    Import-CollectorFunctions
    $script:forbiddenReads = 0
    Set-TestFunction -Name Get-CollectorHostInfo -Body {
        $script:forbiddenReads += 1
        throw "host reached"
    }
    Set-TestFunction -Name Resolve-CollectorInputPath -Body {
        $script:forbiddenReads += 1
        throw "input reached"
    }
    $synthetic = [pscustomobject]@{
        Repository = "C:\private\repository"
        Package = "C:\private\package"
        Observation = "C:\private\observation.json"
        Salt = "C:\private\salt.bin"
        Output = "C:\private\evidence.json"
        Commit = "c" * 40
        PackageSha256 = "a" * 64
    }
    Assert-PrivateFailure `
        -Pattern "ConfirmCollect" `
        -Forbidden @("C:\private") `
        -Action {
        Invoke-FixtureCollection -Fixture $synthetic -Confirm:$false
    }
    if ($script:forbiddenReads -ne 0) {
        throw "Missing confirmation reached host or input reads."
    }

    foreach ($hostCase in @(
        @{ Build = 26199; Architecture = "x64"; Pattern = "host" },
        @{ Build = 26200; Architecture = "arm64"; Pattern = "host" }
    )) {
        Import-CollectorFunctions
        Set-SafeCollectorSeams
        $script:testHostBuild = $hostCase.Build
        $script:testHostArchitecture = $hostCase.Architecture
        Assert-PrivateFailure `
            -Pattern $hostCase.Pattern `
            -Forbidden @("C:\private") `
            -Action {
            Invoke-FixtureCollection -Fixture $synthetic
        }
    }
}

Invoke-Case -Name "commit package signature and input failures stay private" -Action {
    $fixture = New-BehaviorFixture
    try {
        Import-CollectorFunctions
        Set-SafeCollectorSeams
        $script:testRepositoryHead = "d" * 40
        Assert-PrivateFailure `
            -Pattern "source commit" `
            -Forbidden @($fixture.Repository, $fixture.Commit) `
            -Action {
            Invoke-FixtureCollection -Fixture $fixture
        }

        Import-CollectorFunctions
        Set-SafeCollectorSeams
        Assert-PrivateFailure `
            -Pattern "package digest" `
            -Forbidden @($fixture.Package) `
            -Action {
            Invoke-FixtureCollection `
                -Fixture $fixture `
                -ExpectedPackageSha256 ("f" * 64)
        }

        Import-CollectorFunctions
        Set-SafeCollectorSeams
        Set-TestFunction -Name Get-AuthenticodeSignature -Body {
            [pscustomobject]@{
                Status = "NotSigned"
                SignerCertificate = $null
            }
        }
        Assert-PrivateFailure `
            -Pattern "catalog signature" `
            -Forbidden @($fixture.Package) `
            -Action {
            Invoke-FixtureCollection -Fixture $fixture
        }

        Import-CollectorFunctions
        Set-SafeCollectorSeams
        $invalid = New-BehaviorObservation
        $invalid | Add-Member `
            -NotePropertyName privateDetail `
            -NotePropertyValue $script:privacyCanary
        Write-Utf8Text `
            -Path $fixture.Observation `
            -Text ($invalid | ConvertTo-Json -Depth 12 -Compress)
        Assert-PrivateFailure `
            -Pattern "collector observation" `
            -Forbidden @(
                $fixture.Observation,
                $script:privacyCanary
            ) `
            -Action {
            Invoke-FixtureCollection -Fixture $fixture
        }

        Write-Utf8Text `
            -Path $fixture.Observation `
            -Text ((New-BehaviorObservation) |
                ConvertTo-Json -Depth 12 -Compress)
        Write-Utf8Text -Path $fixture.Salt -Text "weak-salt"
        Assert-PrivateFailure `
            -Pattern "collector salt" `
            -Forbidden @($fixture.Salt) `
            -Action {
            Invoke-FixtureCollection -Fixture $fixture
        }
    } finally {
        Remove-BehaviorFixture -Fixture $fixture
    }
}

Invoke-Case -Name "happy path emits deterministic sanitized evidence" -Action {
    $fixture = New-BehaviorFixture
    try {
        Import-CollectorFunctions
        Set-SafeCollectorSeams
        $emitted = @(Invoke-FixtureCollection `
            -Fixture $fixture `
            -IncludeRecording *>&1)
        if (-not (Test-Path -LiteralPath $fixture.Output -PathType Leaf)) {
            throw "Collector did not create evidence."
        }
        $json = [IO.File]::ReadAllText($fixture.Output)
        $combined = ($emitted -join "`n") + "`n" + $json
        foreach ($secret in @(
            $script:privacyCanary,
            $fixture.Repository,
            $fixture.Package,
            $fixture.Observation,
            $fixture.Salt,
            $fixture.Recording,
            "private-recording-content"
        )) {
            if ($combined.Contains($secret, [StringComparison]::Ordinal)) {
                throw "Happy path disclosed private input."
            }
        }
        $evidence = $json | ConvertFrom-Json -Depth 12
        $rawHash = (Get-FileHash `
            -LiteralPath $fixture.Observation `
            -Algorithm SHA256).Hash.ToUpperInvariant()
        $recordingHash = (Get-FileHash `
            -LiteralPath $fixture.Recording `
            -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($evidence.sourceCommit -cne $fixture.Commit -or
            $evidence.rawObservationSha256 -cne $rawHash -or
            $evidence.recordingBundleSha256 -cne $recordingHash -or
            $evidence.labAcceptance -cne "passed" -or
            $evidence.proofBoundary.driverInstalled -cne "notEstablished") {
            throw "Happy evidence content was not exact."
        }
    } finally {
        Remove-BehaviorFixture -Fixture $fixture
    }
}

Invoke-Case -Name "existing output and atomic failure never overwrite target" -Action {
    $fixture = New-BehaviorFixture
    try {
        Import-CollectorFunctions
        Set-SafeCollectorSeams
        Write-Utf8Text -Path $fixture.Output -Text "preserve-existing"
        Assert-PrivateFailure `
            -Pattern "collector output path" `
            -Forbidden @($fixture.Output) `
            -Action {
            Invoke-FixtureCollection -Fixture $fixture
        }
        if ([IO.File]::ReadAllText($fixture.Output) -cne "preserve-existing") {
            throw "Existing evidence was overwritten."
        }
        [IO.File]::Delete($fixture.Output)

        Import-CollectorFunctions
        Set-SafeCollectorSeams
        Set-TestFunction -Name Move-CollectorOutputFile -Body {
            throw "synthetic rename failure"
        }
        Assert-PrivateFailure `
            -Pattern "collector output commit" `
            -Forbidden @($fixture.Output) `
            -Action {
            Invoke-FixtureCollection -Fixture $fixture
        }
        if (Test-Path -LiteralPath $fixture.Output) {
            throw "Atomic failure created the output."
        }
        $leftovers = @(Get-ChildItem `
            -LiteralPath $fixture.Root `
            -File `
            -Force |
            Where-Object { $_.Name -like ".evidence.json.*.tmp" })
        if ($leftovers.Count -ne 0) {
            throw "Atomic failure retained owned temporary output."
        }
    } finally {
        Remove-BehaviorFixture -Fixture $fixture
    }
}

if ($script:failures.Count -ne 0) {
    throw (
        "Audio evidence collector behavior tests failed:`n" +
        ($script:failures -join [Environment]::NewLine)
    )
}

Write-Host (
    "Audio evidence collector behavior tests passed without device, " +
    "certificate, driver, or Git mutation."
)
