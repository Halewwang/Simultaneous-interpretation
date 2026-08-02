[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RepositoryPath,

    [Parameter(Mandatory)]
    [ValidatePattern("^[0-9A-Fa-f]{40}$")]
    [string]$ExpectedSourceCommit,

    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedPackageSha256,

    [Parameter(Mandatory)]
    [string]$ObservationPath,

    [Parameter(Mandatory)]
    [string]$SaltPath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$RecordingBundlePath,

    [switch]$ConfirmCollect
)

if ($MyInvocation.InvocationName -ceq ".") {
    throw "Dot-source invocation is forbidden for this collector."
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-DriverReleaseMetadata {
    $resolver = Join-Path $PSScriptRoot "resolve-version.ps1"
    if (-not (Test-Path -LiteralPath $resolver -PathType Leaf)) {
        throw "Required release metadata resolver is missing."
    }
    $resolved = @(& $resolver)
    if ($resolved.Count -ne 1) {
        throw "Release metadata resolver must return exactly one object."
    }
    $release = $resolved[0]
    foreach ($name in @(
        "MinimumWindowsBuild",
        "Architecture",
        "DriverPackageVersion",
        "DriverAbiVersion",
        "DriverHardwareId",
        "DriverModelSection"
    )) {
        if ($null -eq $release.PSObject.Properties[$name]) {
            throw "Release metadata is missing required property '$name'."
        }
    }
    return $release
}

function Get-CollectorHostInfo {
    param(
        [Parameter(Mandatory)]
        [psobject]$ReleaseMetadata
    )

    if ($PSVersionTable.PSVersion.Major -ne 7) {
        throw "Collector host is unsupported."
    }
    if (-not $IsWindows) {
        throw "Collector host is unsupported."
    }
    $architecture =
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    if ($ReleaseMetadata.Architecture -cne "x64" -or
        $architecture -ne [Runtime.InteropServices.Architecture]::X64) {
        throw "Collector host is unsupported."
    }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt [int]$ReleaseMetadata.MinimumWindowsBuild) {
        throw "Collector host is unsupported."
    }
    $productType = Get-ItemPropertyValue `
        -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions" `
        -Name ProductType
    if ([string]$productType -cne "WinNT") {
        throw "Collector host is unsupported."
    }
    return [pscustomobject]@{
        OsBuild = $build
        Architecture = "x64"
        ProductType = 1
    }
}

function Get-CollectorPathItem {
    param([string]$Path)
    return Get-Item -LiteralPath $Path -Force
}

function Resolve-CollectorInputPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet("Container", "Leaf")]
        [string]$Type
    )

    try {
        if ([string]::IsNullOrWhiteSpace($Path) -or
            $Path -match "^(\\\\|//)" -or
            -not [IO.Path]::IsPathFullyQualified($Path)) {
            throw [InvalidOperationException]::new()
        }
        $fullPath = [IO.Path]::GetFullPath($Path)
        $pathType = if ($Type -ceq "Leaf") {
            "Leaf"
        } else {
            "Container"
        }
        if (-not (Test-Path `
            -LiteralPath $fullPath `
            -PathType $pathType)) {
            throw [InvalidOperationException]::new()
        }
        $current = Get-CollectorPathItem -Path $fullPath
        while ($null -ne $current) {
            if (($current.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [InvalidOperationException]::new()
            }
            $current = if ($current -is [IO.FileInfo]) {
                $current.Directory
            } else {
                $current.Parent
            }
        }
        return $fullPath
    } catch {
        throw "Collector input path is invalid."
    }
}

function Resolve-CollectorOutputPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        if ([string]::IsNullOrWhiteSpace($Path) -or
            $Path -match "^(\\\\|//)" -or
            -not [IO.Path]::IsPathFullyQualified($Path)) {
            throw [InvalidOperationException]::new()
        }
        $fullPath = [IO.Path]::GetFullPath($Path)
        if (Test-Path -LiteralPath $fullPath) {
            throw [InvalidOperationException]::new()
        }
        $parent = [IO.Path]::GetDirectoryName($fullPath)
        $fileName = [IO.Path]::GetFileName($fullPath)
        if ([string]::IsNullOrWhiteSpace($parent) -or
            [string]::IsNullOrWhiteSpace($fileName)) {
            throw [InvalidOperationException]::new()
        }
        [void](Resolve-CollectorInputPath `
            -Path $parent `
            -Type Container)
        return $fullPath
    } catch {
        throw "Collector output path is invalid."
    }
}

function Get-SingleCollectorPackageFile {
    param(
        [Parameter(Mandatory)]
        [IO.FileInfo[]]$Files,

        [Parameter(Mandatory)]
        [string]$Extension
    )

    $matches = @($Files | Where-Object {
        $_.Extension -ieq $Extension
    })
    if ($matches.Count -ne 1) {
        throw "Collector package is invalid."
    }
    return $matches[0]
}

function Get-StrictCollectorPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    try {
        $resolved = Resolve-CollectorInputPath `
            -Path $Directory `
            -Type Container
        $entries = @(Get-ChildItem -LiteralPath $resolved -Force)
        $files = @($entries | Where-Object { $_ -is [IO.FileInfo] })
        if ($entries.Count -ne 3 -or $files.Count -ne 3) {
            throw [InvalidOperationException]::new()
        }
        foreach ($file in $files) {
            [void](Resolve-CollectorInputPath `
                -Path $file.FullName `
                -Type Leaf)
        }
        return [pscustomobject]@{
            Directory = $resolved
            Inf = Get-SingleCollectorPackageFile `
                -Files $files `
                -Extension ".inf"
            Sys = Get-SingleCollectorPackageFile `
                -Files $files `
                -Extension ".sys"
            Cat = Get-SingleCollectorPackageFile `
                -Files $files `
                -Extension ".cat"
        }
    } catch {
        throw "Collector package is invalid."
    }
}

function Get-CollectorPackageSha256 {
    param(
        [Parameter(Mandatory)]
        [byte[]]$InfBytes,

        [Parameter(Mandatory)]
        [byte[]]$SysBytes,

        [Parameter(Mandatory)]
        [byte[]]$CatBytes
    )

    try {
        $infHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($InfBytes)
        )
        $sysHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($SysBytes)
        )
        $catHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($CatBytes)
        )
        $manifest = (
            "EMKE-DRIVER-PACKAGE-SHA256-V1`n" +
            "INF=$infHash`n" +
            "SYS=$sysHash`n" +
            "CAT=$catHash`n"
        )
        return [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData(
                [Text.Encoding]::UTF8.GetBytes($manifest)
            )
        )
    } catch {
        throw "Collector package digest is invalid."
    }
}

function Test-CollectorSha256Equal {
    param(
        [string]$Expected,
        [string]$Actual
    )
    if ($Expected -notmatch "^[0-9A-Fa-f]{64}$" -or
        $Actual -notmatch "^[0-9A-Fa-f]{64}$") {
        return $false
    }
    return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
        [Convert]::FromHexString($Expected),
        [Convert]::FromHexString($Actual)
    )
}

function ConvertTo-CollectorInfSectionMap {
    param(
        [Parameter(Mandatory)]
        [string]$Text
    )

    $sections =
        [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )
    $current = $null
    foreach ($rawLine in ($Text -split "\r?\n")) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or
            $line.StartsWith(";", [StringComparison]::Ordinal)) {
            continue
        }
        if ($line -match "^\[(?<name>[^\[\]]+)\]$") {
            $name = $Matches["name"].Trim()
            if ([string]::IsNullOrWhiteSpace($name) -or
                $sections.ContainsKey($name)) {
                throw [InvalidOperationException]::new()
            }
            $current = [Collections.Generic.List[string]]::new()
            $sections.Add($name, $current)
            continue
        }
        if ($null -eq $current) {
            throw [InvalidOperationException]::new()
        }
        $current.Add($line)
    }
    return $sections
}

function ConvertTo-CollectorInfKeyValueMap {
    param(
        [Parameter(Mandatory)]
        [object]$Lines
    )

    $values =
        [Collections.Generic.Dictionary[string, string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )
    foreach ($line in $Lines) {
        $separator = $line.IndexOf("=")
        if ($separator -le 0) {
            throw [InvalidOperationException]::new()
        }
        $key = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if ([string]::IsNullOrWhiteSpace($key) -or
            [string]::IsNullOrWhiteSpace($value) -or
            $values.ContainsKey($key)) {
            throw [InvalidOperationException]::new()
        }
        $values.Add($key, $value)
    }
    return $values
}

function ConvertFrom-CollectorInfString {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    if ($Value -match '^"(?<value>[^"]*)"$') {
        return $Matches["value"]
    }
    return $Value
}

function Get-CollectorInfMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [psobject]$ReleaseMetadata,

        [Parameter(Mandatory)]
        [psobject]$HostInfo
    )

    try {
        $sections = ConvertTo-CollectorInfSectionMap -Text $Text
        foreach ($required in @(
            "Version",
            "Manufacturer",
            "Strings"
        )) {
            if (-not $sections.ContainsKey($required)) {
                throw [InvalidOperationException]::new()
            }
        }

        $strings = ConvertTo-CollectorInfKeyValueMap `
            -Lines $sections["Strings"]
        if (-not $strings.ContainsKey("ProviderName") -or
            -not $strings.ContainsKey("ManufacturerName") -or
            (ConvertFrom-CollectorInfString `
                -Value $strings["ProviderName"]) -cne "EMKE" -or
            (ConvertFrom-CollectorInfString `
                -Value $strings["ManufacturerName"]) -cne "EMKE") {
            throw [InvalidOperationException]::new()
        }

        $version = ConvertTo-CollectorInfKeyValueMap `
            -Lines $sections["Version"]
        if (-not $version.ContainsKey("Provider") -or
            $version["Provider"] -cne "%ProviderName%" -or
            -not $version.ContainsKey("DriverVer") -or
            $version["DriverVer"] -notmatch (
                "^(?<date>[0-9]{2}/[0-9]{2}/[0-9]{4})," +
                "(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)$"
            )) {
            throw [InvalidOperationException]::new()
        }
        $driverVer = $version["DriverVer"]
        $driverVersion = ([version]$Matches["version"]).ToString()
        if ($driverVersion -cne
            [string]$ReleaseMetadata.DriverPackageVersion) {
            throw [InvalidOperationException]::new()
        }

        if ([int]$HostInfo.OsBuild -lt
            [int]$ReleaseMetadata.MinimumWindowsBuild) {
            throw [InvalidOperationException]::new()
        }
        $manufacturer = ConvertTo-CollectorInfKeyValueMap `
            -Lines $sections["Manufacturer"]
        if ($manufacturer.Count -ne 1 -or
            -not $manufacturer.ContainsKey("%ManufacturerName%")) {
            throw [InvalidOperationException]::new()
        }
        $manufacturerParts = @(
            $manufacturer["%ManufacturerName%"].Split(",") |
                ForEach-Object { $_.Trim() }
        )
        $modelSection = [string]$ReleaseMetadata.DriverModelSection
        if ($modelSection -notmatch (
            "^EMKE\.NTamd64\.10\.0\.\.\.(?<build>[0-9]+)$"
        ) -or [int]$Matches["build"] -ne
            [int]$ReleaseMetadata.MinimumWindowsBuild) {
            throw [InvalidOperationException]::new()
        }
        $modelDecoration = $modelSection.Substring("EMKE.".Length)
        if ($manufacturerParts.Count -ne 2 -or
            $manufacturerParts[0] -cne "EMKE" -or
            $manufacturerParts[1] -cne $modelDecoration) {
            throw [InvalidOperationException]::new()
        }
        if (-not $sections.ContainsKey($modelSection)) {
            throw [InvalidOperationException]::new()
        }
        foreach ($sectionName in $sections.Keys) {
            if ($sectionName -imatch "^EMKE\.NT" -and
                $sectionName -ine $modelSection) {
                throw [InvalidOperationException]::new()
            }
        }

        $models = ConvertTo-CollectorInfKeyValueMap `
            -Lines $sections[$modelSection]
        if ($models.Count -ne 1 -or
            -not $models.ContainsKey("%DeviceDescription%")) {
            throw [InvalidOperationException]::new()
        }
        $modelParts = @(
            $models["%DeviceDescription%"].Split(",") |
                ForEach-Object { $_.Trim() }
        )
        if ($modelParts.Count -ne 2 -or
            $modelParts[0] -cne "EMKE_Install" -or
            $modelParts[1] -cne $ReleaseMetadata.DriverHardwareId) {
            throw [InvalidOperationException]::new()
        }

        $installSection = "$($modelParts[0]).NT"
        if (-not $sections.ContainsKey($installSection)) {
            throw [InvalidOperationException]::new()
        }
        $install = ConvertTo-CollectorInfKeyValueMap `
            -Lines $sections[$installSection]
        if (-not $install.ContainsKey("AddReg")) {
            throw [InvalidOperationException]::new()
        }
        $addRegParts = @(
            $install["AddReg"].Split(",") |
                ForEach-Object { $_.Trim() }
        )
        if ($addRegParts.Count -ne 1 -or
            $addRegParts[0] -cne "EMKE.Device.AddReg" -or
            -not $sections.ContainsKey($addRegParts[0])) {
            throw [InvalidOperationException]::new()
        }

        $driverAbiEntries = @(
            $sections[$addRegParts[0]] | Where-Object {
                $parts = @($_.Split(",") |
                    ForEach-Object { $_.Trim() })
                $parts.Count -ge 3 -and
                $parts[2] -ieq "DriverAbi"
            }
        )
        if ($driverAbiEntries.Count -ne 1) {
            throw [InvalidOperationException]::new()
        }
        $driverAbiParts = @(
            $driverAbiEntries[0].Split(",") |
                ForEach-Object { $_.Trim() }
        )
        $expectedAbiHex = (
            "0x{0:X8}" -f [int]$ReleaseMetadata.DriverAbiVersion
        )
        if ($driverAbiParts.Count -ne 5 -or
            $driverAbiParts[0] -ine "HKR" -or
            $driverAbiParts[1] -cne "" -or
            $driverAbiParts[2] -ine "DriverAbi" -or
            $driverAbiParts[3] -ine "0x00010001" -or
            $driverAbiParts[4] -ine $expectedAbiHex) {
            throw [InvalidOperationException]::new()
        }

        return [pscustomobject]@{
            DriverVer = $driverVer
            Version = $driverVersion
            Provider = "EMKE"
            HardwareId = [string]$ReleaseMetadata.DriverHardwareId
            Abi = [int]$ReleaseMetadata.DriverAbiVersion
            ModelSection = $modelSection
            InstallSection = $installSection
        }
    } catch {
        throw "Collector INF is invalid."
    }
}

function Get-CollectorCatalogMetadata {
    param(
        [Parameter(Mandatory)]
        [IO.FileInfo]$Catalog
    )

    try {
        $signature = Get-AuthenticodeSignature `
            -LiteralPath $Catalog.FullName
        if ([string]$signature.Status -cne "Valid" -or
            $null -eq $signature.SignerCertificate) {
            throw [InvalidOperationException]::new()
        }
        $certificateSha256 =
            $signature.SignerCertificate.GetCertHashString(
                [Security.Cryptography.HashAlgorithmName]::SHA256
            )
        if ($certificateSha256 -notmatch "^[0-9A-Fa-f]{64}$") {
            throw [InvalidOperationException]::new()
        }
        return [pscustomobject]@{
            Status = "Valid"
            SigningCertificateSha256 =
                $certificateSha256.ToUpperInvariant()
            SignatureProofBoundary = (
                "host Authenticode only; " +
                "Microsoft/WHQL not established"
            )
        }
    } catch {
        throw "Collector catalog signature is invalid."
    }
}

function Open-CollectorPackageReadHandle {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        return [IO.FileStream]::new(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read,
            4096,
            [IO.FileOptions]::SequentialScan
        )
    } catch {
        throw "Collector package transaction is invalid."
    }
}

function Read-CollectorPackageHandleBytes {
    param(
        [Parameter(Mandatory)]
        [IO.FileStream]$Stream
    )

    $memory = [IO.MemoryStream]::new()
    try {
        $Stream.Position = 0
        $Stream.CopyTo($memory)
        return ,$memory.ToArray()
    } catch {
        throw "Collector package transaction is invalid."
    } finally {
        $memory.Dispose()
    }
}

function Get-CollectorPackageEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{64}$")]
        [string]$ExpectedPackageSha256,

        [Parameter(Mandatory)]
        [psobject]$ReleaseMetadata,

        [Parameter(Mandatory)]
        [psobject]$HostInfo
    )

    $package = Get-StrictCollectorPackage -Directory $Directory
    $handles = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        foreach ($file in @($package.Inf, $package.Sys, $package.Cat)) {
            $handle = Open-CollectorPackageReadHandle `
                -Path $file.FullName
            $handles.Add($handle)
        }
        $infBytes = Read-CollectorPackageHandleBytes `
            -Stream $handles[0]
        $sysBytes = Read-CollectorPackageHandleBytes `
            -Stream $handles[1]
        $catBytes = Read-CollectorPackageHandleBytes `
            -Stream $handles[2]
        $packageSha256 = Get-CollectorPackageSha256 `
            -InfBytes $infBytes `
            -SysBytes $sysBytes `
            -CatBytes $catBytes
        if (-not (Test-CollectorSha256Equal `
            -Expected $ExpectedPackageSha256 `
            -Actual $packageSha256)) {
            throw "Collector package digest does not match."
        }

        try {
            $encoding = [Text.UTF8Encoding]::new($false, $true)
            $infText = $encoding.GetString($infBytes)
        } catch {
            throw "Collector INF is invalid."
        }
        $driverMetadata = Get-CollectorInfMetadata `
            -Text $infText `
            -ReleaseMetadata $ReleaseMetadata `
            -HostInfo $HostInfo
        $catalogMetadata =
            Get-CollectorCatalogMetadata -Catalog $package.Cat
        return [pscustomobject]@{
            PackageSha256 = $packageSha256
            DriverMetadata = $driverMetadata
            CatalogMetadata = $catalogMetadata
        }
    } finally {
        for ($index = $handles.Count - 1; $index -ge 0; $index -= 1) {
            try {
                $handles[$index].Dispose()
            } catch {
            }
        }
    }
}

function Get-CollectorExpectedRoles {
    return @(
        "emke.meeting-speaker.render",
        "emke.app-speaker.capture",
        "emke.app-microphone.render",
        "emke.meeting-microphone.capture"
    )
}

function Get-CollectorExpectedScenarios {
    return @(
        "enumerate",
        "inbound-original",
        "inbound-translated",
        "outbound-translated",
        "outbound-underrun",
        "inbound-failure",
        "outbound-failure",
        "crash-after-mic-open"
    )
}

function Assert-CollectorObjectShape {
    param(
        [Parameter(Mandatory)]
        [object]$InputObject,

        [Parameter(Mandatory)]
        [string[]]$Allowed,

        [Parameter(Mandatory)]
        [string[]]$Required
    )

    if ($null -eq $InputObject -or
        $InputObject -is [string] -or
        $InputObject -is [Collections.IEnumerable] -and
        $InputObject -isnot [Management.Automation.PSCustomObject]) {
        throw [InvalidOperationException]::new()
    }
    $names = @($InputObject.PSObject.Properties.Name)
    foreach ($name in $names) {
        if ($Allowed -cnotcontains $name) {
            throw [InvalidOperationException]::new()
        }
    }
    foreach ($name in $Required) {
        if ($names -cnotcontains $name) {
            throw [InvalidOperationException]::new()
        }
    }
}

function Test-CollectorSafeInteger {
    param(
        [object]$Value,
        [switch]$AllowZero = $true
    )

    $integer = (
        $Value -is [byte] -or
        $Value -is [sbyte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64]
    )
    if (-not $integer) {
        return $false
    }
    $number = [int64]$Value
    $minimum = if ($AllowZero) { 0 } else { 1 }
    return (
        $number -ge $minimum -and
        $number -le 9007199254740991
    )
}

function ConvertFrom-CollectorUtc {
    param(
        [Parameter(Mandatory)]
        [object]$Value
    )

    if ($Value -isnot [string] -or
        $Value -notmatch (
            "^[0-9]{4}-[0-9]{2}-[0-9]{2}T" +
            "[0-9]{2}:[0-9]{2}:[0-9]{2}" +
            "(?:\.[0-9]{1,7})?Z$"
        )) {
        throw [InvalidOperationException]::new()
    }
    $parsed = [DateTimeOffset]::MinValue
    $styles = (
        [Globalization.DateTimeStyles]::AssumeUniversal -bor
        [Globalization.DateTimeStyles]::AdjustToUniversal
    )
    if (-not [DateTimeOffset]::TryParse(
        $Value,
        [Globalization.CultureInfo]::InvariantCulture,
        $styles,
        [ref]$parsed
    ) -or $parsed.Offset -ne [TimeSpan]::Zero) {
        throw [InvalidOperationException]::new()
    }
    return $parsed
}

function Get-CollectorScenarioRule {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $rules = @{
        "inbound-original" = @{ Inbound = 3; Outbound = 1 }
        "inbound-translated" = @{ Inbound = 1; Outbound = 1 }
        "outbound-translated" = @{
            Inbound = 1
            Outbound = 1
            ExactUnderruns = 0
        }
        "outbound-underrun" = @{ Inbound = 1; Outbound = 4 }
        "inbound-failure" = @{ Inbound = 2; Outbound = 1 }
        "outbound-failure" = @{ Inbound = 1; Outbound = 4 }
    }
    if (-not $rules.ContainsKey($Name)) {
        throw [InvalidOperationException]::new()
    }
    return $rules[$Name]
}

function ConvertTo-ValidatedCollectorScenario {
    param(
        [Parameter(Mandatory)]
        [object]$Scenario
    )

    $common = @(
        "name",
        "startedAtUtc",
        "completedAtUtc",
        "exitCode",
        "discovery",
        "result",
        "externalObservation"
    )
    $diagnostics = @(
        "inboundRoute",
        "outboundRoute",
        "outboundUnderruns",
        "droppedFrames"
    )
    Assert-CollectorObjectShape `
        -InputObject $Scenario `
        -Allowed @($common + $diagnostics) `
        -Required $common
    if ($Scenario.name -isnot [string] -or
        (Get-CollectorExpectedScenarios) -cnotcontains $Scenario.name -or
        $Scenario.discovery -isnot [string] -or
        $Scenario.discovery -cne "ready" -or
        $Scenario.result -isnot [string] -or
        @("ready", "completed", "crashingAfterMicOpen") -cnotcontains
        $Scenario.result -or
        $Scenario.externalObservation -isnot [string] -or
        @("passed", "failed", "pending") -cnotcontains
        $Scenario.externalObservation -or
        -not (Test-CollectorSafeInteger -Value $Scenario.exitCode)) {
        throw [InvalidOperationException]::new()
    }
    $started = ConvertFrom-CollectorUtc -Value $Scenario.startedAtUtc
    $completed = ConvertFrom-CollectorUtc -Value $Scenario.completedAtUtc
    if ($completed -lt $started) {
        throw [InvalidOperationException]::new()
    }

    $names = @($Scenario.PSObject.Properties.Name)
    $hasDiagnostics = @($diagnostics | Where-Object {
        $names -ccontains $_
    })
    if ($Scenario.name -ceq "enumerate") {
        if ($Scenario.exitCode -ne 0 -or
            $Scenario.result -cne "ready" -or
            $hasDiagnostics.Count -ne 0) {
            throw [InvalidOperationException]::new()
        }
    } elseif ($Scenario.name -ceq "crash-after-mic-open") {
        if ($Scenario.exitCode -eq 0 -or
            $Scenario.result -cne "crashingAfterMicOpen" -or
            $hasDiagnostics.Count -ne 0) {
            throw [InvalidOperationException]::new()
        }
    } else {
        if ($Scenario.exitCode -ne 0 -or
            $Scenario.result -cne "completed" -or
            $hasDiagnostics.Count -ne 4) {
            throw [InvalidOperationException]::new()
        }
        foreach ($field in $diagnostics) {
            if (-not (Test-CollectorSafeInteger -Value $Scenario.$field)) {
                throw [InvalidOperationException]::new()
            }
        }
        $rule = Get-CollectorScenarioRule -Name $Scenario.name
        if ($Scenario.inboundRoute -ne $rule.Inbound -or
            $Scenario.outboundRoute -ne $rule.Outbound -or
            $Scenario.droppedFrames -ne 0) {
            throw [InvalidOperationException]::new()
        }
        if ($rule.ContainsKey("ExactUnderruns") -and
            $Scenario.outboundUnderruns -ne $rule.ExactUnderruns) {
            throw [InvalidOperationException]::new()
        }
    }

    $sanitized = [ordered]@{
        name = [string]$Scenario.name
        startedAtUtc = [string]$Scenario.startedAtUtc
        completedAtUtc = [string]$Scenario.completedAtUtc
        exitCode = [int64]$Scenario.exitCode
        discovery = [string]$Scenario.discovery
        result = [string]$Scenario.result
    }
    if ($hasDiagnostics.Count -ne 0) {
        $sanitized.inboundRoute = [int64]$Scenario.inboundRoute
        $sanitized.outboundRoute = [int64]$Scenario.outboundRoute
        $sanitized.outboundUnderruns =
            [int64]$Scenario.outboundUnderruns
        $sanitized.droppedFrames = [int64]$Scenario.droppedFrames
    }
    $sanitized.externalObservation =
        [string]$Scenario.externalObservation
    return [pscustomobject]$sanitized
}

function Read-CollectorObservation {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        $resolved = Resolve-CollectorInputPath -Path $Path -Type Leaf
        $bytes = Read-CollectorObservationBytes -Path $resolved
        $inputObject = ConvertFrom-CollectorJsonBytes -Bytes $bytes
        return [pscustomobject]@{
            Observation = Read-CollectorObservationObject `
                -InputObject $inputObject
            RawSha256 = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($bytes)
            )
        }
    } catch {
        throw "Collector observation is invalid."
    }
}

function Read-CollectorObservationBytes {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return [IO.File]::ReadAllBytes($Path)
}

function ConvertFrom-CollectorJsonBytes {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.CommentHandling =
        [Text.Json.JsonCommentHandling]::Disallow
    $options.AllowTrailingCommas = $false
    $memory = [ReadOnlyMemory[byte]]::new($Bytes)
    $document = [Text.Json.JsonDocument]::Parse($memory, $options)
    try {
        return ConvertFrom-CollectorJsonElement `
            -Element $document.RootElement
    } finally {
        $document.Dispose()
    }
}

function ConvertFrom-CollectorJsonElement {
    param(
        [Parameter(Mandatory)]
        [Text.Json.JsonElement]$Element
    )

    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal
        )
        $result = [ordered]@{}
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw [InvalidOperationException]::new()
            }
            $result[$property.Name] =
                ConvertFrom-CollectorJsonElement `
                    -Element $property.Value
        }
        return [pscustomobject]$result
    }
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $result = [object[]]::new($Element.GetArrayLength())
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            $result[$index] =
                ConvertFrom-CollectorJsonElement -Element $item
            $index += 1
        }
        return ,$result
    }
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::String) {
        return $Element.GetString()
    }
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Number) {
        $integer = [int64]0
        if ($Element.TryGetInt64([ref]$integer)) {
            return $integer
        }
        return $Element.GetDouble()
    }
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::True) {
        return $true
    }
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::False) {
        return $false
    }
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Null) {
        return $null
    }
    throw [InvalidOperationException]::new()
}

function Read-CollectorObservationObject {
    param(
        [Parameter(Mandatory)]
        [object]$InputObject
    )

    try {
        if ($InputObject -is [Collections.IDictionary]) {
            $json = $InputObject |
                ConvertTo-Json -Depth 12 -Compress
            $bytes = [Text.Encoding]::UTF8.GetBytes($json)
            $InputObject = ConvertFrom-CollectorJsonBytes -Bytes $bytes
        }
        Assert-CollectorObjectShape `
            -InputObject $InputObject `
            -Allowed @(
                "schemaVersion",
                "observedAtUtc",
                "endpoints",
                "scenarios",
                "operatorNotesDigest"
            ) `
            -Required @(
                "schemaVersion",
                "observedAtUtc",
                "endpoints",
                "scenarios"
            )
        if (-not (Test-CollectorSafeInteger `
            -Value $InputObject.schemaVersion) -or
            $InputObject.schemaVersion -ne 1) {
            throw [InvalidOperationException]::new()
        }
        [void](ConvertFrom-CollectorUtc -Value $InputObject.observedAtUtc)

        $endpointInputs = @($InputObject.endpoints)
        if ($endpointInputs.Count -ne 4) {
            throw [InvalidOperationException]::new()
        }
        $roles = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal
        )
        $endpoints = [Collections.Generic.List[object]]::new()
        foreach ($endpoint in $endpointInputs) {
            Assert-CollectorObjectShape `
                -InputObject $endpoint `
                -Allowed @("role", "opaqueEndpointId") `
                -Required @("role", "opaqueEndpointId")
            if ($endpoint.role -isnot [string] -or
                (Get-CollectorExpectedRoles) -cnotcontains $endpoint.role -or
                -not $roles.Add([string]$endpoint.role) -or
                $endpoint.opaqueEndpointId -isnot [string] -or
                [string]::IsNullOrWhiteSpace($endpoint.opaqueEndpointId) -or
                $endpoint.opaqueEndpointId.Length -gt 512 -or
                $endpoint.opaqueEndpointId -match "[\x00-\x1F\x7F]") {
                throw [InvalidOperationException]::new()
            }
            [void]$endpoints.Add([pscustomobject][ordered]@{
                Role = [string]$endpoint.role
                OpaqueEndpointId = [string]$endpoint.opaqueEndpointId
            })
        }

        $scenarioInputs = @($InputObject.scenarios)
        if ($scenarioInputs.Count -ne 8) {
            throw [InvalidOperationException]::new()
        }
        $scenarioNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal
        )
        $scenarios = [Collections.Generic.List[object]]::new()
        foreach ($scenario in $scenarioInputs) {
            $validated =
                ConvertTo-ValidatedCollectorScenario -Scenario $scenario
            if (-not $scenarioNames.Add($validated.name)) {
                throw [InvalidOperationException]::new()
            }
            [void]$scenarios.Add($validated)
        }
        foreach ($expected in (Get-CollectorExpectedRoles)) {
            if (-not $roles.Contains($expected)) {
                throw [InvalidOperationException]::new()
            }
        }
        foreach ($expected in (Get-CollectorExpectedScenarios)) {
            if (-not $scenarioNames.Contains($expected)) {
                throw [InvalidOperationException]::new()
            }
        }

        $notesDigest = $null
        if ($null -ne
            $InputObject.PSObject.Properties["operatorNotesDigest"]) {
            if ($InputObject.operatorNotesDigest -isnot [string] -or
                $InputObject.operatorNotesDigest -notmatch
                "^[0-9A-Fa-f]{64}$") {
                throw [InvalidOperationException]::new()
            }
            $notesDigest =
                $InputObject.operatorNotesDigest.ToLowerInvariant()
        }
        return [pscustomobject]@{
            ObservedAtUtc = [string]$InputObject.observedAtUtc
            Endpoints = $endpoints.ToArray()
            Scenarios = $scenarios.ToArray()
            OperatorNotesDigest = $notesDigest
        }
    } catch {
        throw "Collector observation is invalid."
    }
}

function Read-CollectorSalt {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        $resolved = Resolve-CollectorInputPath -Path $Path -Type Leaf
        $bytes = [IO.File]::ReadAllBytes($resolved)
        if ($bytes.Length -ne 32) {
            throw [InvalidOperationException]::new()
        }
        return $bytes
    } catch {
        throw "Collector salt is invalid."
    }
}

function Get-EndpointRoleSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Role,

        [Parameter(Mandatory)]
        [string]$OpaqueEndpointId,

        [Parameter(Mandatory)]
        [byte[]]$Salt
    )

    try {
        if ((Get-CollectorExpectedRoles) -cnotcontains $Role -or
            [string]::IsNullOrWhiteSpace($OpaqueEndpointId) -or
            $OpaqueEndpointId.Length -gt 512 -or
            $OpaqueEndpointId -match "[\x00-\x1F\x7F]" -or
            $Salt.Length -ne 32) {
            throw [InvalidOperationException]::new()
        }
        $prefix = [Text.Encoding]::UTF8.GetBytes(
            "EMKE-ENDPOINT-ROLE-HASH-V1`0$Role`0"
        )
        $suffix = [Text.Encoding]::UTF8.GetBytes(
            "`0$OpaqueEndpointId"
        )
        $bytes = [byte[]]::new(
            $prefix.Length + $Salt.Length + $suffix.Length
        )
        [Array]::Copy($prefix, 0, $bytes, 0, $prefix.Length)
        [Array]::Copy(
            $Salt,
            0,
            $bytes,
            $prefix.Length,
            $Salt.Length
        )
        [Array]::Copy(
            $suffix,
            0,
            $bytes,
            $prefix.Length + $Salt.Length,
            $suffix.Length
        )
        return [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($bytes)
        )
    } catch {
        throw "Collector observation is invalid."
    }
}

function Get-LabAcceptance {
    param(
        [Parameter(Mandatory)]
        [object[]]$Scenarios
    )

    if (@($Scenarios | Where-Object {
        $_.externalObservation -ceq "failed"
    }).Count -ne 0) {
        return "failed"
    }
    if (@($Scenarios | Where-Object {
        $_.externalObservation -ceq "pending"
    }).Count -ne 0) {
        return "pending"
    }
    return "passed"
}

function New-AudioEvidenceRecord {
    param(
        [Parameter(Mandatory)]
        [string]$SourceCommit,

        [Parameter(Mandatory)]
        [string]$CollectedAtUtc,

        [Parameter(Mandatory)]
        [int]$OsBuild,

        [Parameter(Mandatory)]
        [string]$Architecture,

        [Parameter(Mandatory)]
        [psobject]$DriverMetadata,

        [Parameter(Mandatory)]
        [string]$PackageSha256,

        [Parameter(Mandatory)]
        [psobject]$CatalogMetadata,

        [Parameter(Mandatory)]
        [psobject]$Observation,

        [Parameter(Mandatory)]
        [byte[]]$Salt,

        [Parameter(Mandatory)]
        [string]$RawObservationSha256,

        [string]$RecordingBundleSha256
    )

    $endpoints = [Collections.Generic.List[object]]::new()
    foreach ($role in (Get-CollectorExpectedRoles)) {
        $matching = @($Observation.Endpoints | Where-Object {
            $_.Role -ceq $role
        })
        if ($matching.Count -ne 1) {
            throw "Collector evidence construction failed."
        }
        [void]$endpoints.Add([pscustomobject][ordered]@{
            role = $role
            endpointIdSha256 = Get-EndpointRoleSha256 `
                -Role $role `
                -OpaqueEndpointId $matching[0].OpaqueEndpointId `
                -Salt $Salt
        })
    }

    $scenarios = [Collections.Generic.List[object]]::new()
    foreach ($name in (Get-CollectorExpectedScenarios)) {
        $matching = @($Observation.Scenarios | Where-Object {
            $_.name -ceq $name
        })
        if ($matching.Count -ne 1) {
            throw "Collector evidence construction failed."
        }
        $scenario = $matching[0]
        $sanitized = [ordered]@{
            name = [string]$scenario.name
            startedAtUtc = [string]$scenario.startedAtUtc
            completedAtUtc = [string]$scenario.completedAtUtc
            exitCode = [int64]$scenario.exitCode
            discovery = [string]$scenario.discovery
            result = [string]$scenario.result
        }
        if ($null -ne $scenario.PSObject.Properties["inboundRoute"]) {
            $sanitized.inboundRoute = [int64]$scenario.inboundRoute
            $sanitized.outboundRoute = [int64]$scenario.outboundRoute
            $sanitized.outboundUnderruns =
                [int64]$scenario.outboundUnderruns
            $sanitized.droppedFrames = [int64]$scenario.droppedFrames
        }
        $sanitized.externalObservation =
            [string]$scenario.externalObservation
        [void]$scenarios.Add([pscustomobject]$sanitized)
    }

    $acceptance = Get-LabAcceptance -Scenarios $Observation.Scenarios
    $evidence = [ordered]@{
        schemaVersion = 1
        evidenceKind = "emke.windows-audio-lab-evidence"
        sourceCommit = $SourceCommit.ToLowerInvariant()
        collectedAtUtc = $CollectedAtUtc
        observedAtUtc = $Observation.ObservedAtUtc
        osBuild = $OsBuild
        architecture = $Architecture
        driver = [ordered]@{
            version = $DriverMetadata.Version
            abi = $DriverMetadata.Abi
            packageSha256 = $PackageSha256.ToUpperInvariant()
            catalogSignatureStatus = $CatalogMetadata.Status
            signingCertificateSha256 = (
                $CatalogMetadata.SigningCertificateSha256.
                    ToUpperInvariant()
            )
            signatureProofBoundary =
                $CatalogMetadata.SignatureProofBoundary
        }
        endpoints = $endpoints.ToArray()
        scenarios = $scenarios.ToArray()
        rawObservationSha256 =
            $RawObservationSha256.ToUpperInvariant()
    }
    if (-not [string]::IsNullOrWhiteSpace($RecordingBundleSha256)) {
        $evidence.recordingBundleSha256 =
            $RecordingBundleSha256.ToUpperInvariant()
    }
    $evidence.labAcceptance = $acceptance
    $meetingEvidence = if ($acceptance -ceq "passed") {
        "observationProvided"
    } else {
        "notEstablished"
    }
    $evidence.proofBoundary = [ordered]@{
        collectorValidated = $true
        driverInstalled = "notEstablished"
        liveEndpoints = "observationProvided"
        liveMeeting = $meetingEvidence
        humanListening = $meetingEvidence
    }
    return [pscustomobject]$evidence
}

function ConvertTo-CollectorJson {
    param(
        [Parameter(Mandatory)]
        [object]$Evidence
    )

    return $Evidence | ConvertTo-Json -Depth 12 -Compress
}

function Get-CollectorFileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        $resolved = Resolve-CollectorInputPath -Path $Path -Type Leaf
        return (Get-FileHash `
            -LiteralPath $resolved `
            -Algorithm SHA256).Hash.ToUpperInvariant()
    } catch {
        throw "Collector input digest is invalid."
    }
}

function Move-CollectorOutputFile {
    param(
        [string]$TemporaryPath,
        [string]$FinalPath
    )
    [IO.File]::Move($TemporaryPath, $FinalPath, $false)
}

function Write-AtomicEvidenceFile {
    param(
        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [string]$JsonText
    )

    $resolvedOutput = Resolve-CollectorOutputPath -Path $OutputPath
    $parent = [IO.Path]::GetDirectoryName($resolvedOutput)
    $name = [IO.Path]::GetFileName($resolvedOutput)
    $temporaryPath = Join-Path `
        $parent `
        (".$name." + [guid]::NewGuid().ToString("N") + ".tmp")
    $created = $false
    try {
        $stream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough
        )
        $created = $true
        try {
            $encoding = [Text.UTF8Encoding]::new($false)
            $bytes = $encoding.GetBytes($JsonText)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        Move-CollectorOutputFile `
            -TemporaryPath $temporaryPath `
            -FinalPath $resolvedOutput
        $created = $false
    } catch {
        if ($created -and [IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
        throw "Collector output commit failed."
    }
}

function Get-CollectorRepositoryHead {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryPath
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "git"
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add("-C")
    $startInfo.ArgumentList.Add($RepositoryPath)
    $startInfo.ArgumentList.Add("rev-parse")
    $startInfo.ArgumentList.Add("--verify")
    $startInfo.ArgumentList.Add("HEAD")
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw [InvalidOperationException]::new()
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
            throw [InvalidOperationException]::new()
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult().Trim()
        [void]$stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or
            $stdout -notmatch "^[0-9A-Fa-f]{40}$") {
            throw [InvalidOperationException]::new()
        }
        return $stdout.ToLowerInvariant()
    } catch {
        throw "Collector source commit is invalid."
    } finally {
        $process.Dispose()
    }
}

function Get-CollectorUtcNow {
    return [DateTimeOffset]::UtcNow.ToString(
        "yyyy-MM-ddTHH:mm:ss.fffZ",
        [Globalization.CultureInfo]::InvariantCulture
    )
}

function Invoke-CollectAudioEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryPath,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{40}$")]
        [string]$ExpectedSourceCommit,

        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{64}$")]
        [string]$ExpectedPackageSha256,

        [Parameter(Mandatory)]
        [string]$ObservationPath,

        [Parameter(Mandatory)]
        [string]$SaltPath,

        [Parameter(Mandatory)]
        [string]$OutputPath,

        [string]$RecordingBundlePath,

        [Parameter(Mandatory)]
        [psobject]$ReleaseMetadata,

        [switch]$ConfirmCollect
    )
    if (-not $ConfirmCollect) {
        throw "Collection requires explicit -ConfirmCollect."
    }
    $hostInfo = Get-CollectorHostInfo `
        -ReleaseMetadata $ReleaseMetadata
    if ($hostInfo.OsBuild -lt $ReleaseMetadata.MinimumWindowsBuild -or
        $hostInfo.Architecture -cne "x64" -or
        [int]$hostInfo.ProductType -ne 1) {
        throw "Collector host is unsupported."
    }

    $repository = Resolve-CollectorInputPath `
        -Path $RepositoryPath `
        -Type Container
    $packageDirectory = Resolve-CollectorInputPath `
        -Path $PackagePath `
        -Type Container
    $observationFile = Resolve-CollectorInputPath `
        -Path $ObservationPath `
        -Type Leaf
    $saltFile = Resolve-CollectorInputPath -Path $SaltPath -Type Leaf
    $recordingFile = $null
    if (-not [string]::IsNullOrWhiteSpace($RecordingBundlePath)) {
        $recordingFile = Resolve-CollectorInputPath `
            -Path $RecordingBundlePath `
            -Type Leaf
    }
    [void](Resolve-CollectorOutputPath -Path $OutputPath)

    $sourceCommit = Get-CollectorRepositoryHead `
        -RepositoryPath $repository
    if ($sourceCommit -cne $ExpectedSourceCommit.ToLowerInvariant()) {
        throw "Collector source commit does not match."
    }
    $packageEvidence = Get-CollectorPackageEvidence `
        -Directory $packageDirectory `
        -ExpectedPackageSha256 $ExpectedPackageSha256 `
        -ReleaseMetadata $ReleaseMetadata `
        -HostInfo $hostInfo
    $observationSnapshot =
        Read-CollectorObservation -Path $observationFile
    $salt = Read-CollectorSalt -Path $saltFile
    $recordingBundleSha256 = $null
    if ($null -ne $recordingFile) {
        $recordingBundleSha256 =
            Get-CollectorFileSha256 -Path $recordingFile
    }
    $evidence = New-AudioEvidenceRecord `
        -SourceCommit $sourceCommit `
        -CollectedAtUtc (Get-CollectorUtcNow) `
        -OsBuild $hostInfo.OsBuild `
        -Architecture $hostInfo.Architecture `
        -DriverMetadata $packageEvidence.DriverMetadata `
        -PackageSha256 $packageEvidence.PackageSha256 `
        -CatalogMetadata $packageEvidence.CatalogMetadata `
        -Observation $observationSnapshot.Observation `
        -Salt $salt `
        -RawObservationSha256 $observationSnapshot.RawSha256 `
        -RecordingBundleSha256 $recordingBundleSha256
    $json = ConvertTo-CollectorJson -Evidence $evidence
    Write-AtomicEvidenceFile -OutputPath $OutputPath -JsonText $json
    Write-Output "Audio evidence collection completed."
}

$releaseMetadata = Get-DriverReleaseMetadata
Invoke-CollectAudioEvidence `
    @PSBoundParameters `
    -ReleaseMetadata $releaseMetadata
