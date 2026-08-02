[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$toolsDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$collectorScript = Join-Path $toolsDirectory "collect-audio-evidence.ps1"
$script:failures = [Collections.Generic.List[string]]::new()
$script:privacyCanary = "ENDPOINT-CANARY-7B2-DO-NOT-LEAK"

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

function New-TestReleaseMetadata {
    return [pscustomobject]@{
        MinimumWindowsBuild = 19045
        Architecture = "x64"
        DriverPackageVersion = "1.0.0.2"
        DriverAbiVersion = 1
        DriverHardwareId = "ROOT\EMKEVIRTUALAUDIO"
        DriverModelSection = "EMKE.NTamd64.10.0...19045"
        DriverEndpointRoles = @(
            "emke.meeting-speaker.render",
            "emke.app-speaker.capture",
            "emke.app-microphone.render",
            "emke.meeting-microphone.capture"
        )
    }
}

function New-TestHostInfo {
    return [pscustomobject]@{
        OsBuild = 19045
        Architecture = "x64"
        ProductType = 1
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
    try {
        & $Action
    } catch {
        $caught = $_
    }
    if ($null -eq $caught) {
        throw "Expected private failure matching '$Pattern'."
    }
    if ($caught.Exception.Message -notmatch $Pattern) {
        throw "Failure did not match the expected fixed category."
    }
    foreach ($value in $Forbidden) {
        if (-not [string]::IsNullOrEmpty($value) -and
            $caught.Exception.ToString().Contains(
                $value,
                [StringComparison]::Ordinal
            )) {
            throw "Failure disclosed forbidden input."
        }
    }
}

function New-TestRoot {
    $path = Join-Path `
        $PSScriptRoot `
        (".audio-evidence-" + [guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
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

function Get-ValidInfText {
    return @'
[Version]
Signature="$Windows NT$"
Class=MEDIA
Provider=%ProviderName%
DriverVer=08/01/2026,1.0.0.2
CatalogFile=EMKE.VirtualAudio.cat

[Manufacturer]
%ManufacturerName%=EMKE,NTamd64.10.0...19045

[EMKE.NTamd64.10.0...19045]
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

function New-TestPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    Write-Utf8Text `
        -Path (Join-Path $Directory "EMKE.VirtualAudio.inf") `
        -Text (Get-ValidInfText)
    Write-Utf8Text `
        -Path (Join-Path $Directory "EMKE.VirtualAudio.sys") `
        -Text "synthetic-sys-bytes"
    Write-Utf8Text `
        -Path (Join-Path $Directory "EMKE.VirtualAudio.cat") `
        -Text "synthetic-cat-bytes"
}

function Get-ReferencePackageSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    $inf = Get-Item -LiteralPath (
        Join-Path $Directory "EMKE.VirtualAudio.inf"
    )
    $sys = Get-Item -LiteralPath (
        Join-Path $Directory "EMKE.VirtualAudio.sys"
    )
    $cat = Get-Item -LiteralPath (
        Join-Path $Directory "EMKE.VirtualAudio.cat"
    )
    $manifest = (
        "EMKE-DRIVER-PACKAGE-SHA256-V1`n" +
        "INF=$((Get-FileHash -LiteralPath $inf.FullName -Algorithm SHA256).Hash.ToUpperInvariant())`n" +
        "SYS=$((Get-FileHash -LiteralPath $sys.FullName -Algorithm SHA256).Hash.ToUpperInvariant())`n" +
        "CAT=$((Get-FileHash -LiteralPath $cat.FullName -Algorithm SHA256).Hash.ToUpperInvariant())`n"
    )
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($manifest)
        )
    )
}

function New-ValidObservation {
    param(
        [ValidateSet("passed", "failed", "pending")]
        [string]$ExternalObservation = "passed"
    )

    $endpoints = @(
        [ordered]@{
            role = "emke.app-speaker.capture"
            opaqueEndpointId = $script:privacyCanary
        },
        [ordered]@{
            role = "emke.meeting-microphone.capture"
            opaqueEndpointId = "$($script:privacyCanary)-4"
        },
        [ordered]@{
            role = "emke.meeting-speaker.render"
            opaqueEndpointId = "$($script:privacyCanary)-1"
        },
        [ordered]@{
            role = "emke.app-microphone.render"
            opaqueEndpointId = "$($script:privacyCanary)-3"
        }
    )
    $common = @{
        startedAtUtc = "2026-07-27T01:00:00.000Z"
        completedAtUtc = "2026-07-27T01:00:01.000Z"
        exitCode = 0
        discovery = "ready"
        externalObservation = $ExternalObservation
    }
    $scenarios = @(
        [ordered]@{
            name = "outbound-failure"
            startedAtUtc = $common.startedAtUtc
            completedAtUtc = $common.completedAtUtc
            exitCode = 0
            discovery = "ready"
            result = "completed"
            inboundRoute = 1
            outboundRoute = 4
            outboundUnderruns = 0
            droppedFrames = 0
            externalObservation = $ExternalObservation
        },
        [ordered]@{
            name = "enumerate"
            startedAtUtc = $common.startedAtUtc
            completedAtUtc = $common.completedAtUtc
            exitCode = 0
            discovery = "ready"
            result = "ready"
            externalObservation = $ExternalObservation
        },
        [ordered]@{
            name = "crash-after-mic-open"
            startedAtUtc = $common.startedAtUtc
            completedAtUtc = $common.completedAtUtc
            exitCode = 23
            discovery = "ready"
            result = "crashingAfterMicOpen"
            externalObservation = $ExternalObservation
        },
        [ordered]@{
            name = "inbound-original"
            startedAtUtc = $common.startedAtUtc
            completedAtUtc = $common.completedAtUtc
            exitCode = 0
            discovery = "ready"
            result = "completed"
            inboundRoute = 3
            outboundRoute = 1
            outboundUnderruns = 0
            droppedFrames = 0
            externalObservation = $ExternalObservation
        },
        [ordered]@{
            name = "outbound-underrun"
            startedAtUtc = $common.startedAtUtc
            completedAtUtc = $common.completedAtUtc
            exitCode = 0
            discovery = "ready"
            result = "completed"
            inboundRoute = 1
            outboundRoute = 4
            outboundUnderruns = 0
            droppedFrames = 0
            externalObservation = $ExternalObservation
        },
        [ordered]@{
            name = "inbound-translated"
            startedAtUtc = $common.startedAtUtc
            completedAtUtc = $common.completedAtUtc
            exitCode = 0
            discovery = "ready"
            result = "completed"
            inboundRoute = 1
            outboundRoute = 1
            outboundUnderruns = 0
            droppedFrames = 0
            externalObservation = $ExternalObservation
        },
        [ordered]@{
            name = "inbound-failure"
            startedAtUtc = $common.startedAtUtc
            completedAtUtc = $common.completedAtUtc
            exitCode = 0
            discovery = "ready"
            result = "completed"
            inboundRoute = 2
            outboundRoute = 1
            outboundUnderruns = 0
            droppedFrames = 0
            externalObservation = $ExternalObservation
        },
        [ordered]@{
            name = "outbound-translated"
            startedAtUtc = $common.startedAtUtc
            completedAtUtc = $common.completedAtUtc
            exitCode = 0
            discovery = "ready"
            result = "completed"
            inboundRoute = 1
            outboundRoute = 1
            outboundUnderruns = 0
            droppedFrames = 0
            externalObservation = $ExternalObservation
        }
    )
    return [ordered]@{
        schemaVersion = 1
        observedAtUtc = "2026-07-27T01:05:00.000Z"
        endpoints = $endpoints
        scenarios = $scenarios
        operatorNotesDigest = "a" * 64
    }
}

function Copy-JsonObject {
    param(
        [Parameter(Mandatory)]
        [object]$Value
    )

    return $Value |
        ConvertTo-Json -Depth 12 -Compress |
        ConvertFrom-Json -Depth 12
}

function Write-Observation {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Observation
    )

    Write-Utf8Text `
        -Path $Path `
        -Text ($Observation | ConvertTo-Json -Depth 12 -Compress)
}

Import-CollectorFunctions

Invoke-Case -Name "local exact paths reject UNC reparse and existing output" -Action {
    $root = New-TestRoot
    try {
        $inputPath = Join-Path $root "input.json"
        Write-Utf8Text -Path $inputPath -Text "{}"
        if ((Resolve-CollectorInputPath -Path $inputPath -Type Leaf) -cne
            [IO.Path]::GetFullPath($inputPath)) {
            throw "Exact input path was not normalized."
        }
        Assert-PrivateFailure -Pattern "collector input path" -Action {
            Resolve-CollectorInputPath `
                -Path "\\server\share\observation.json" `
                -Type Leaf
        }

        $existingOutput = Join-Path $root "existing.json"
        Write-Utf8Text -Path $existingOutput -Text "preserve-me"
        Assert-PrivateFailure -Pattern "collector output path" -Action {
            Resolve-CollectorOutputPath -Path $existingOutput
        }
        if ([IO.File]::ReadAllText($existingOutput) -cne "preserve-me") {
            throw "Existing output was changed."
        }

        Set-TestFunction -Name Get-CollectorPathItem -Body {
            [pscustomobject]@{
                FullName = "synthetic-reparse"
                Attributes = [IO.FileAttributes]::ReparsePoint
                Parent = $null
            }
        }
        Assert-PrivateFailure -Pattern "collector input path" -Action {
            Resolve-CollectorInputPath -Path $inputPath -Type Leaf
        }
        Import-CollectorFunctions
    } finally {
        if (Test-Path -LiteralPath $root) {
            [IO.Directory]::Delete($root, $true)
        }
    }
}

Invoke-Case -Name "flat package digest and INF metadata are exact" -Action {
    $root = New-TestRoot
    try {
        $packagePath = Join-Path $root "package"
        New-TestPackage -Directory $packagePath
        $package = Get-StrictCollectorPackage -Directory $packagePath
        $expected = Get-ReferencePackageSha256 -Directory $packagePath
        $actual = Get-CollectorPackageSha256 `
            -InfBytes ([IO.File]::ReadAllBytes($package.Inf.FullName)) `
            -SysBytes ([IO.File]::ReadAllBytes($package.Sys.FullName)) `
            -CatBytes ([IO.File]::ReadAllBytes($package.Cat.FullName))
        if ($actual -cne $expected -or
            -not (Test-CollectorSha256Equal `
                -Expected $expected.ToLowerInvariant() `
                -Actual $actual)) {
            throw "Package V1 digest diverged from the reference."
        }
        $metadata = Get-CollectorInfMetadata `
            -Text ([IO.File]::ReadAllText($package.Inf.FullName)) `
            -ReleaseMetadata (New-TestReleaseMetadata) `
            -HostInfo (New-TestHostInfo)
        if ($metadata.Version -cne "1.0.0.2" -or
            $metadata.Provider -cne "EMKE" -or
            $metadata.HardwareId -cne "ROOT\EMKEVIRTUALAUDIO" -or
            $metadata.Abi -ne 1) {
            throw "Valid INF metadata was not exact."
        }

        Write-Utf8Text `
            -Path (Join-Path $packagePath "extra.txt") `
            -Text "extra"
        Assert-PrivateFailure -Pattern "collector package" -Action {
            Get-StrictCollectorPackage -Directory $packagePath
        }
        [IO.File]::Delete((Join-Path $packagePath "extra.txt"))

        $badInf = (Get-ValidInfText).Replace(
            'ProviderName="EMKE"',
            'ProviderName="OTHER"'
        )
        Write-Utf8Text -Path $package.Inf.FullName -Text $badInf
        Assert-PrivateFailure -Pattern "collector INF" -Action {
            Get-CollectorInfMetadata `
                -Text ([IO.File]::ReadAllText($package.Inf.FullName)) `
                -ReleaseMetadata (New-TestReleaseMetadata) `
            -HostInfo (New-TestHostInfo)
        }
    } finally {
        if (Test-Path -LiteralPath $root) {
            [IO.Directory]::Delete($root, $true)
        }
    }
}

Invoke-Case -Name "INF parser follows only the active x64 AddReg chain" -Action {
    # Exercise the Windows checkout form on every host.
    $valid = (Get-ValidInfText).Replace("`r`n", "`n")
    $valid = $valid.Replace("`r", "`n").Replace("`n", "`r`n")
    $metadata = Get-CollectorInfMetadata `
        -Text $valid `
        -ReleaseMetadata (New-TestReleaseMetadata) `
            -HostInfo (New-TestHostInfo)
    if ($metadata.Version -cne "1.0.0.2" -or
        $metadata.Provider -cne "EMKE" -or
        $metadata.HardwareId -cne "ROOT\EMKEVIRTUALAUDIO" -or
        $metadata.Abi -ne 1 -or
        $metadata.ModelSection -cne
        "EMKE.NTamd64.10.0...19045" -or
        $metadata.InstallSection -cne "EMKE_Install.NT") {
        throw "Valid active INF chain metadata was not exact."
    }

    $windowsDirectory = [IO.Path]::GetFullPath(
        (Join-Path $toolsDirectory "..")
    )
    $sourceInf = Join-Path `
        (Join-Path `
            (Join-Path $windowsDirectory "driver") `
            "EMKE.VirtualAudio") `
        "EMKE.VirtualAudio.inf"
    $realMetadata = Get-CollectorInfMetadata `
        -Text ([IO.File]::ReadAllText($sourceInf)) `
        -ReleaseMetadata (New-TestReleaseMetadata) `
            -HostInfo (New-TestHostInfo)
    if ($realMetadata.Version -cne "1.0.0.2" -or
        $realMetadata.Abi -ne 1) {
        throw "Real INF did not traverse the frozen active chain."
    }

    $missingInstallSectionInf = $valid.Replace(
        (
            "[EMKE_Install.NT]`r`n" +
            "AddReg=EMKE.Device.AddReg`r`n`r`n"
        ),
        ""
    )
    if ($missingInstallSectionInf -ceq $valid) {
        throw "CRLF missing-install-section mutation did not change the INF."
    }

    $cases = @(
        @{
            Name = "inactive Models section"
            Text = (
                $valid +
                "`n[EMKE.NTamd64]`n" +
                "%DeviceDescription%=Evil_Install,ROOT\EVIL`n"
            )
        },
        @{
            Name = "comment hardware bait"
            Text = $valid.Replace(
                "EMKE_Install,ROOT\EMKEVIRTUALAUDIO",
                (
                    "EMKE_Install,ROOT\EVIL`n" +
                    "; EMKE_Install,ROOT\EMKEVIRTUALAUDIO"
                )
            )
        },
        @{
            Name = "Strings hardware bait"
            Text = $valid.Replace(
                "EMKE_Install,ROOT\EMKEVIRTUALAUDIO",
                "EMKE_Install,ROOT\EVIL"
            ).Replace(
                'ProviderName="EMKE"',
                (
                    'ProviderName="EMKE"' + "`n" +
                    "HardwareBait=EMKE_Install,ROOT\EMKEVIRTUALAUDIO"
                )
            )
        },
        @{
            Name = "inactive AddReg ABI bait"
            Text = $valid.Replace(
                "HKR,,DriverAbi,0x00010001,0x00000001",
                "HKR,,DriverAbi,0x00010001,0x00000002"
            ) + (
                "`n[Inactive.AddReg]`n" +
                "HKR,,DriverAbi,0x00010001,0x00000001`n"
            )
        },
        @{
            Name = "duplicate section"
            Text = (
                $valid + "`n[Version]`n" +
                'Signature="$Windows NT$"' + "`n"
            )
        },
        @{
            Name = "duplicate Version key"
            Text = $valid.Replace(
                "Class=MEDIA",
                "Class=MEDIA`nClass=MEDIA"
            )
        },
        @{
            Name = "wrong architecture decoration"
            Text = $valid.Replace("NTamd64", "NTarm64")
        },
        @{
            Name = "wrong driver version"
            Text = $valid.Replace("1.0.0.2", "1.0.0.1")
        },
        @{
            Name = "extra Manufacturer path"
            Text = $valid.Replace(
                "%ManufacturerName%=EMKE,NTamd64.10.0...19045",
                (
                    "%ManufacturerName%=EMKE,NTamd64.10.0...19045`r`n" +
                    "%ManufacturerName%=EMKE,NTamd64"
                )
            )
        },
        @{
            Name = "old minimum build"
            Text = $valid.Replace("19045", "19044")
        },
        @{
            Name = "inactive future build"
            Text = $valid.Replace("19045", "19046")
        },
        @{
            Name = "wrong Version provider"
            Text = $valid.Replace(
                "Provider=%ProviderName%",
                "Provider=%OtherProvider%"
            )
        },
        @{
            Name = "wrong Strings provider"
            Text = $valid.Replace(
                'ProviderName="EMKE"',
                'ProviderName="OTHER"'
            )
        },
        @{
            Name = "missing install section"
            Text = $missingInstallSectionInf
        },
        @{
            Name = "wrong AddReg chain"
            Text = $valid.Replace(
                "AddReg=EMKE.Device.AddReg",
                "AddReg=Inactive.AddReg"
            )
        }
    )
    foreach ($case in $cases) {
        Assert-PrivateFailure `
            -Pattern "collector INF" `
            -Forbidden @("ROOT\EVIL", "Inactive.AddReg") `
            -Action {
            Get-CollectorInfMetadata `
                -Text $case.Text `
                -ReleaseMetadata (New-TestReleaseMetadata) `
            -HostInfo (New-TestHostInfo)
        }
    }
}

Invoke-Case -Name "package bytes bind and Windows mutation is denied" -Action {
    $root = New-TestRoot
    $packagePath = Join-Path $root "package"
    $originalPackageBytes = $null
    try {
        New-TestPackage -Directory $packagePath
        $expected = Get-ReferencePackageSha256 -Directory $packagePath
        $script:transactionPaths = @(
            (Join-Path $packagePath "EMKE.VirtualAudio.inf"),
            (Join-Path $packagePath "EMKE.VirtualAudio.sys"),
            (Join-Path $packagePath "EMKE.VirtualAudio.cat")
        )
        $originalPackageBytes = [object[]]::new(3)
        for ($index = 0; $index -lt 3; $index += 1) {
            $originalPackageBytes[$index] =
                [IO.File]::ReadAllBytes(
                    $script:transactionPaths[$index]
                )
        }
        $script:transactionWriteAttempts = 0
        $script:transactionBlockedWrites = 0
        $script:transactionDeleteAttempts = 0
        $script:transactionBlockedDeletes = 0
        $script:transactionSignatureCalls = 0
        Set-TestFunction -Name Get-AuthenticodeSignature -Body {
            param([string]$LiteralPath)
            $script:transactionSignatureCalls += 1
            foreach ($path in $script:transactionPaths) {
                $script:transactionWriteAttempts += 1
                try {
                    [IO.File]::WriteAllText(
                        $path,
                        "mixed-version-bait",
                        [Text.UTF8Encoding]::new($false)
                    )
                } catch {
                    $script:transactionBlockedWrites += 1
                }
                $script:transactionDeleteAttempts += 1
                try {
                    [IO.File]::Delete($path)
                } catch {
                    $script:transactionBlockedDeletes += 1
                }
            }
            $certificate = [pscustomobject]@{}
            $certificate | Add-Member -MemberType ScriptMethod `
                -Name GetCertHashString `
                -Value { param($Algorithm) "B" * 64 }
            return [pscustomobject]@{
                Status = "Valid"
                SignerCertificate = $certificate
            }
        }

        $evidence = Get-CollectorPackageEvidence `
            -Directory $packagePath `
            -ExpectedPackageSha256 $expected `
            -ReleaseMetadata (New-TestReleaseMetadata) `
            -HostInfo (New-TestHostInfo)
        if ($evidence.PackageSha256 -cne $expected -or
            $evidence.DriverMetadata.Version -cne "1.0.0.2" -or
            $evidence.CatalogMetadata.Status -cne "Valid" -or
            $script:transactionSignatureCalls -ne 1 -or
            $script:transactionWriteAttempts -ne 3 -or
            $script:transactionDeleteAttempts -ne 3) {
            throw (
                "Package transaction accepted mixed file versions " +
                "(signatureCalls=$script:transactionSignatureCalls; " +
                "blockedWrites=$script:transactionBlockedWrites; " +
                "blockedDeletes=$script:transactionBlockedDeletes)."
            )
        }
        if ($IsWindows -and (
            $script:transactionBlockedWrites -ne 3 -or
            $script:transactionBlockedDeletes -ne 3 -or
            @($script:transactionPaths | Where-Object {
                -not [IO.File]::Exists($_)
            }).Count -ne 0 -or
            (Get-ReferencePackageSha256 -Directory $packagePath) -cne
            $expected
        )) {
            throw "Windows package handles allowed write or delete sharing."
        }
        if (-not $IsWindows) {
            for ($index = 0; $index -lt 3; $index += 1) {
                [IO.File]::WriteAllBytes(
                    $script:transactionPaths[$index],
                    [byte[]]$originalPackageBytes[$index]
                )
            }
            if ((Get-ReferencePackageSha256 -Directory $packagePath) -cne
                $expected) {
                throw "Portable package fixture restoration was not exact."
            }
        }

        $script:transactionSignatureCalls = 0
        Assert-PrivateFailure `
            -Pattern "collector package digest" `
            -Forbidden @($packagePath) `
            -Action {
            Get-CollectorPackageEvidence `
                -Directory $packagePath `
                -ExpectedPackageSha256 ("f" * 64) `
                -ReleaseMetadata (New-TestReleaseMetadata) `
            -HostInfo (New-TestHostInfo)
        }
        if ($script:transactionSignatureCalls -ne 0) {
            throw "Digest mismatch reached catalog signature validation."
        }

        [IO.File]::AppendAllText(
            $script:transactionPaths[1],
            "-released",
            [Text.UTF8Encoding]::new($false)
        )
        if (-not [IO.File]::ReadAllText(
            $script:transactionPaths[1]
        ).EndsWith("-released", [StringComparison]::Ordinal)) {
            throw "Package transaction did not release its read handles."
        }
    } finally {
        if ($null -ne $originalPackageBytes -and
            [IO.Directory]::Exists($packagePath)) {
            for ($index = 0; $index -lt 3; $index += 1) {
                [IO.File]::WriteAllBytes(
                    $script:transactionPaths[$index],
                    [byte[]]$originalPackageBytes[$index]
                )
            }
        }
        Import-CollectorFunctions
        if (Test-Path -LiteralPath $root) {
            [IO.Directory]::Delete($root, $true)
        }
    }
}

Invoke-Case -Name "observation schema and Smoke ABI semantics are strict" -Action {
    $root = New-TestRoot
    try {
        $observationPath = Join-Path $root "observation.json"
        $valid = New-ValidObservation
        Write-Observation -Path $observationPath -Observation $valid
        $parsed = (
            Read-CollectorObservation -Path $observationPath
        ).Observation
        if ($parsed.Endpoints.Count -ne 4 -or
            $parsed.Scenarios.Count -ne 8 -or
            $parsed.OperatorNotesDigest -cne ("a" * 64)) {
            throw "Valid observation did not preserve strict sanitized values."
        }

        $mutations = @(
            @{
                Name = "top extra"
                Apply = {
                    param($value)
                    $value | Add-Member -NotePropertyName secret -NotePropertyValue (
                        $script:privacyCanary
                    )
                }
            },
            @{
                Name = "duplicate role"
                Apply = {
                    param($value)
                    $value.endpoints[1].role = $value.endpoints[0].role
                }
            },
            @{
                Name = "duplicate scenario"
                Apply = {
                    param($value)
                    $value.scenarios[1].name = $value.scenarios[0].name
                }
            },
            @{
                Name = "non UTC time"
                Apply = {
                    param($value)
                    $value.scenarios[0].startedAtUtc =
                        "2026-07-27T09:00:00+08:00"
                }
            },
            @{
                Name = "reversed time"
                Apply = {
                    param($value)
                    $value.scenarios[0].completedAtUtc =
                        "2026-07-27T00:59:59.000Z"
                }
            },
            @{
                Name = "unsafe counter"
                Apply = {
                    param($value)
                    $value.scenarios[0].outboundUnderruns = 9007199254740992
                }
            },
            @{
                Name = "wrong route"
                Apply = {
                    param($value)
                    $target = @($value.scenarios | Where-Object {
                        $_.name -ceq "inbound-original"
                    })[0]
                    $target.inboundRoute = 1
                }
            },
            @{
                Name = "dropped frames"
                Apply = {
                    param($value)
                    $value.scenarios[0].droppedFrames = 1
                }
            },
            @{
                Name = "scenario extra"
                Apply = {
                    param($value)
                    $value.scenarios[0] |
                        Add-Member `
                            -NotePropertyName rawDetail `
                            -NotePropertyValue $script:privacyCanary
                }
            }
        )
        foreach ($mutation in $mutations) {
            $invalid = Copy-JsonObject -Value $valid
            & $mutation.Apply $invalid
            Write-Observation -Path $observationPath -Observation $invalid
            Assert-PrivateFailure `
                -Pattern "collector observation" `
                -Forbidden @($script:privacyCanary, $observationPath) `
                -Action {
                Read-CollectorObservation -Path $observationPath
            }
        }
    } finally {
        if (Test-Path -LiteralPath $root) {
            [IO.Directory]::Delete($root, $true)
        }
    }
}

Invoke-Case -Name "observation bytes bind strict JSON and raw digest once" -Action {
    $root = New-TestRoot
    try {
        $observationPath = Join-Path $root "observation.json"
        $original = New-ValidObservation
        $originalJson = $original | ConvertTo-Json -Depth 12 -Compress
        Write-Utf8Text -Path $observationPath -Text $originalJson
        $script:snapshotOriginalBytes =
            [IO.File]::ReadAllBytes($observationPath)
        $expectedSha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData(
                $script:snapshotOriginalBytes
            )
        )
        $replacement = Copy-JsonObject -Value $original
        $replacement.observedAtUtc = "2026-07-27T03:05:00.000Z"
        $script:snapshotReplacementBytes = [Text.Encoding]::UTF8.GetBytes(
            ($replacement | ConvertTo-Json -Depth 12 -Compress)
        )
        $script:snapshotReadCount = 0
        Set-TestFunction -Name Read-CollectorObservationBytes -Body {
            param([string]$Path)
            $script:snapshotReadCount += 1
            [IO.File]::WriteAllBytes(
                $Path,
                $script:snapshotReplacementBytes
            )
            return $script:snapshotOriginalBytes
        }

        $snapshot = Read-CollectorObservation -Path $observationPath
        if ($script:snapshotReadCount -ne 1 -or
            $snapshot.RawSha256 -cne $expectedSha256 -or
            $snapshot.Observation.ObservedAtUtc -cne
            "2026-07-27T01:05:00.000Z" -or
            [IO.File]::ReadAllText($observationPath) -notmatch
            "2026-07-27T03:05:00.000Z") {
            throw "Observation parse and raw digest were not one byte snapshot."
        }
        Import-CollectorFunctions

        $validJson = $original |
            ConvertTo-Json -Depth 12 -Compress
        $strictCases = @(
            @{
                Name = "duplicate top key"
                Json = $validJson.Replace(
                    '"schemaVersion":1',
                    '"schemaVersion":1,"schemaVersion":1'
                )
            },
            @{
                Name = "duplicate endpoint key"
                Json = $validJson.Replace(
                    '"role":"emke.app-speaker.capture"',
                    (
                        '"role":"emke.app-speaker.capture",' +
                        '"role":"emke.app-speaker.capture"'
                    )
                )
            },
            @{
                Name = "case-different endpoint key"
                Json = $validJson.Replace(
                    '"role":"emke.app-speaker.capture"',
                    (
                        '"role":"emke.app-speaker.capture",' +
                        '"Role":"emke.app-speaker.capture"'
                    )
                )
            },
            @{
                Name = "duplicate scenario key"
                Json = $validJson.Replace(
                    '"name":"outbound-failure"',
                    (
                        '"name":"outbound-failure",' +
                        '"name":"outbound-failure"'
                    )
                )
            },
            @{
                Name = "JSON comment"
                Json = $validJson.Insert(1, "/*strict-comment*/")
            },
            @{
                Name = "trailing comma"
                Json = $validJson.Insert($validJson.Length - 1, ",")
            }
        )
        foreach ($case in $strictCases) {
            Write-Utf8Text -Path $observationPath -Text $case.Json
            Assert-PrivateFailure `
                -Pattern "collector observation" `
                -Forbidden @($observationPath, $script:privacyCanary) `
                -Action {
                Read-CollectorObservation -Path $observationPath
            }
        }
    } finally {
        Import-CollectorFunctions
        if (Test-Path -LiteralPath $root) {
            [IO.Directory]::Delete($root, $true)
        }
    }
}

Invoke-Case -Name "salt and endpoint role hashes are exact and private" -Action {
    $root = New-TestRoot
    try {
        $saltPath = Join-Path $root "salt.bin"
        $salt = [byte[]](0..31)
        [IO.File]::WriteAllBytes($saltPath, $salt)
        $loaded = Read-CollectorSalt -Path $saltPath
        if ($loaded.Count -ne 32) {
            throw "Valid salt length changed."
        }
        $lockedDigestFile = [IO.FileStream]::new(
            $saltPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::None
        )
        try {
            Assert-PrivateFailure `
                -Pattern "collector input digest" `
                -Forbidden @($saltPath) `
                -Action {
                Get-CollectorFileSha256 -Path $saltPath
            }
        } finally {
            $lockedDigestFile.Dispose()
        }
        $expected = @{
            "emke.meeting-speaker.render" =
                "F61A76468B4337E64F3CD0F5CFCDEB6A85B60D3DB02B23B8A5F35852BEE146AF"
            "emke.app-speaker.capture" =
                "D1268B3A56A720B26EDFD2CB9986DF5719DE95E4FCB809E5249AD77D72E109AF"
            "emke.app-microphone.render" =
                "385DF785EB101FFAE382548BA76F03ABD3EEFAB47FA75E0A343A9BC102F26810"
            "emke.meeting-microphone.capture" =
                "6E371659188370459881B6939F37475203DA5DB6D325F09F2637B87EAFBEBE71"
        }
        foreach ($role in $expected.Keys) {
            $actual = Get-EndpointRoleSha256 `
                -Role $role `
                -OpaqueEndpointId $script:privacyCanary `
                -Salt $salt
            if ($actual -cne $expected[$role]) {
                throw "Endpoint role hash diverged from the frozen vector."
            }
        }

        [IO.File]::WriteAllBytes($saltPath, [byte[]](0..30))
        Assert-PrivateFailure `
            -Pattern "collector salt" `
            -Forbidden @($saltPath) `
            -Action {
            Read-CollectorSalt -Path $saltPath
        }
        Assert-PrivateFailure `
            -Pattern "collector observation" `
            -Forbidden @($script:privacyCanary) `
            -Action {
            Get-EndpointRoleSha256 `
                -Role "emke.meeting-speaker.render" `
                -OpaqueEndpointId "$($script:privacyCanary)`u{0001}" `
                -Salt $salt
        }
    } finally {
        if (Test-Path -LiteralPath $root) {
            [IO.Directory]::Delete($root, $true)
        }
    }
}

Invoke-Case -Name "acceptance output order and proof boundaries are deterministic" -Action {
    $salt = [byte[]](0..31)
    $driver = [pscustomobject]@{
        Version = "1.0.0.2"
        Abi = 1
    }
    $catalog = [pscustomobject]@{
        Status = "Valid"
        SigningCertificateSha256 = "B" * 64
        SignatureProofBoundary =
            "host Authenticode only; Microsoft/WHQL not established"
    }
    $expectations = @{
        passed = "passed"
        failed = "failed"
        pending = "pending"
    }
    foreach ($external in $expectations.Keys) {
        $observation = Read-CollectorObservationObject `
            -InputObject (New-ValidObservation -ExternalObservation $external)
        $acceptance = Get-LabAcceptance -Scenarios $observation.Scenarios
        if ($acceptance -cne $expectations[$external]) {
            throw "Acceptance precedence is incorrect."
        }
        $record = New-AudioEvidenceRecord `
            -SourceCommit ("c" * 40) `
            -CollectedAtUtc "2026-07-27T02:00:00.000Z" `
            -OsBuild 19045 `
            -Architecture "x64" `
            -DriverMetadata $driver `
            -PackageSha256 ("A" * 64) `
            -CatalogMetadata $catalog `
            -Observation $observation `
            -Salt $salt `
            -RawObservationSha256 ("D" * 64) `
            -RecordingBundleSha256 ("E" * 64)
        $json = ConvertTo-CollectorJson -Evidence $record
        foreach ($secret in @(
            $script:privacyCanary,
            "ENDPOINT-CANARY",
            "salt.bin",
            "observation.json"
        )) {
            if ($json.Contains($secret, [StringComparison]::Ordinal)) {
                throw "Evidence JSON disclosed sensitive input."
            }
        }
        $fieldOrder = @(
            '"schemaVersion"',
            '"evidenceKind"',
            '"sourceCommit"',
            '"collectedAtUtc"',
            '"observedAtUtc"',
            '"osBuild"',
            '"architecture"',
            '"driver"',
            '"endpoints"',
            '"scenarios"',
            '"rawObservationSha256"',
            '"recordingBundleSha256"',
            '"labAcceptance"',
            '"proofBoundary"'
        )
        $previous = -1
        foreach ($field in $fieldOrder) {
            $offset = $json.IndexOf($field, [StringComparison]::Ordinal)
            if ($offset -le $previous) {
                throw "Evidence JSON field order is not deterministic."
            }
            $previous = $offset
        }
        $decoded = $json | ConvertFrom-Json -Depth 12
        if ($decoded.labAcceptance -cne $external -or
            $decoded.proofBoundary.driverInstalled -cne "notEstablished" -or
            $decoded.proofBoundary.liveEndpoints -cne
            "observationProvided") {
            throw "Evidence acceptance or endpoint proof boundary is wrong."
        }
        $meetingBoundary = if ($external -ceq "passed") {
            "observationProvided"
        } else {
            "notEstablished"
        }
        if ($decoded.proofBoundary.liveMeeting -cne $meetingBoundary -or
            $decoded.proofBoundary.humanListening -cne $meetingBoundary) {
            throw "Meeting proof boundary overclaimed supplied observation."
        }
    }
}

Invoke-Case -Name "atomic JSON output is new no-BOM and cleans owned temp" -Action {
    $root = New-TestRoot
    try {
        $output = Join-Path $root "evidence.json"
        $json = '{"schemaVersion":1}'
        Write-AtomicEvidenceFile -OutputPath $output -JsonText $json
        $bytes = [IO.File]::ReadAllBytes($output)
        if ($bytes.Length -lt 2 -or
            ($bytes.Length -ge 3 -and
                $bytes[0] -eq 0xEF -and
                $bytes[1] -eq 0xBB -and
                $bytes[2] -eq 0xBF) -or
            [Text.Encoding]::UTF8.GetString($bytes) -cne $json) {
            throw "Atomic output encoding changed."
        }
        Assert-PrivateFailure -Pattern "collector output path" -Action {
            Write-AtomicEvidenceFile -OutputPath $output -JsonText "{}"
        }

        [IO.File]::Delete($output)
        Set-TestFunction -Name Move-CollectorOutputFile -Body {
            throw "synthetic atomic rename failure"
        }
        Assert-PrivateFailure -Pattern "collector output commit" -Action {
            Write-AtomicEvidenceFile -OutputPath $output -JsonText $json
        }
        if (Test-Path -LiteralPath $output) {
            throw "Atomic failure created the target."
        }
        $leftovers = @(Get-ChildItem -LiteralPath $root -Force)
        if ($leftovers.Count -ne 0) {
            throw "Atomic failure retained an owned temporary file."
        }
        Import-CollectorFunctions
    } finally {
        if (Test-Path -LiteralPath $root) {
            [IO.Directory]::Delete($root, $true)
        }
    }
}

if ($script:failures.Count -ne 0) {
    throw (
        "Audio evidence collector validation tests failed:`n" +
        ($script:failures -join [Environment]::NewLine)
    )
}

Write-Host (
    "Audio evidence collector validation tests passed without device, " +
    "certificate, driver, or Git mutation."
)
