[CmdletBinding()]
param(
    [string]$VersionFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'version.json'),
    [string]$RequireTag
)

$ErrorActionPreference = 'Stop'

function Read-JsonFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required metadata file does not exist: $Path"
    }

    $jsonText = Get-Content -LiteralPath $Path -Raw
    $parsedJson = ConvertFrom-Json -InputObject $jsonText -NoEnumerate
    if (
        $null -eq $parsedJson -or
        $parsedJson.GetType() -ne
            [System.Management.Automation.PSCustomObject]
    ) {
        throw "JSON root must be an object: $Path"
    }

    $parsedJson
}

function Get-RequiredProperty {
    param(
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Context
    )

    if ($null -eq $Object) {
        throw "$Context must be a JSON object."
    }

    $matchedProperty = $null
    foreach ($property in $Object.PSObject.Properties) {
        if ($property.Name -ceq $Name) {
            $matchedProperty = $property
            break
        }
    }

    if ($null -eq $matchedProperty) {
        throw "$Context is missing required property '$Name'."
    }

    $matchedProperty.Value
}

function Test-ExactPropertyExists {
    param(
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory)]
        [string]$Name,
        [switch]$IgnoreCase
    )

    if ($null -eq $Object) {
        return $false
    }

    foreach ($property in $Object.PSObject.Properties) {
        if (
            ($IgnoreCase -and $property.Name -ieq $Name) -or
            (-not $IgnoreCase -and $property.Name -ceq $Name)
        ) {
            return $true
        }
    }

    return $false
}

function Assert-NonEmptyString {
    param(
        [AllowNull()]
        [object]$Value,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if (
        -not ($Value -is [string]) -or
        [string]::IsNullOrWhiteSpace($Value)
    ) {
        throw "$Name must be a non-empty string."
    }
}

function Assert-JsonInteger {
    param(
        [AllowNull()]
        [object]$Value,
        [Parameter(Mandatory)]
        [string]$Name
    )

    $isInteger = (
        $Value -is [sbyte] -or
        $Value -is [byte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64] -or
        $Value -is [uint64]
    )
    if (-not $isInteger) {
        throw "$Name must be a JSON integer."
    }
}

function Assert-ThreePartVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value,
        [Parameter(Mandatory)]
        [string]$Name
    )

    $numericPart = '(?:0|[1-9][0-9]*)'
    if (-not [regex]::IsMatch(
        $Value,
        "^$numericPart\.$numericPart\.$numericPart$"
    )) {
        throw "$Name must contain exactly three non-negative integer parts."
    }
}

function Assert-PackageVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value,
        [Parameter(Mandatory)]
        [string]$ProductVersion
    )

    $numericPart = '(?:0|[1-9][0-9]*)'
    if (-not [regex]::IsMatch(
        $Value,
        "^$numericPart\.$numericPart\.$numericPart\.$numericPart$"
    )) {
        throw 'packageVersion must contain exactly four integer parts.'
    }

    $segments = $Value.Split('.')
    foreach ($segment in $segments) {
        [int]$parsedSegment = 0
        if (
            -not [int]::TryParse($segment, [ref]$parsedSegment) -or
            $parsedSegment -lt 0 -or
            $parsedSegment -gt 65535
        ) {
            throw 'Every packageVersion part must be between 0 and 65535.'
        }
    }

    if (($segments[0..2] -join '.') -cne $ProductVersion) {
        throw 'The first three packageVersion parts must match productVersion.'
    }
}

$resolvedVersionFile = (Resolve-Path -LiteralPath $VersionFile).Path
$windowsRoot = Split-Path -Parent $resolvedVersionFile
$version = Read-JsonFile -Path $resolvedVersionFile

$productVersion = Get-RequiredProperty `
    -Object $version `
    -Name 'productVersion' `
    -Context 'version metadata'
$packageVersion = Get-RequiredProperty `
    -Object $version `
    -Name 'packageVersion' `
    -Context 'version metadata'
$metadataExpectedTag = Get-RequiredProperty `
    -Object $version `
    -Name 'expectedTag' `
    -Context 'version metadata'
$contractVersion = Get-RequiredProperty `
    -Object $version `
    -Name 'contractVersion' `
    -Context 'version metadata'
$settingsSchemaVersion = Get-RequiredProperty `
    -Object $version `
    -Name 'settingsSchemaVersion' `
    -Context 'version metadata'
$driverAbiVersion = Get-RequiredProperty `
    -Object $version `
    -Name 'driverAbiVersion' `
    -Context 'version metadata'
$minimumWindowsBuild = Get-RequiredProperty `
    -Object $version `
    -Name 'minimumWindowsBuild' `
    -Context 'version metadata'
$minimumWindowsApiContract = Get-RequiredProperty `
    -Object $version `
    -Name 'minimumWindowsApiContract' `
    -Context 'version metadata'
$maximumVersionTested = Get-RequiredProperty `
    -Object $version `
    -Name 'maximumVersionTested' `
    -Context 'version metadata'
$architecture = Get-RequiredProperty `
    -Object $version `
    -Name 'architecture' `
    -Context 'version metadata'
$channel = Get-RequiredProperty `
    -Object $version `
    -Name 'channel' `
    -Context 'version metadata'

Assert-NonEmptyString -Value $productVersion -Name 'productVersion'
Assert-NonEmptyString -Value $packageVersion -Name 'packageVersion'
Assert-NonEmptyString -Value $metadataExpectedTag -Name 'expectedTag'
Assert-NonEmptyString -Value $architecture -Name 'architecture'
Assert-NonEmptyString -Value $channel -Name 'channel'
Assert-NonEmptyString `
    -Value $minimumWindowsApiContract `
    -Name 'minimumWindowsApiContract'
Assert-NonEmptyString `
    -Value $maximumVersionTested `
    -Name 'maximumVersionTested'
Assert-JsonInteger -Value $contractVersion -Name 'contractVersion'
Assert-JsonInteger `
    -Value $settingsSchemaVersion `
    -Name 'settingsSchemaVersion'
Assert-JsonInteger -Value $driverAbiVersion -Name 'driverAbiVersion'
Assert-JsonInteger `
    -Value $minimumWindowsBuild `
    -Name 'minimumWindowsBuild'
if ($contractVersion -le 0) {
    throw 'contractVersion must be greater than zero.'
}
if ($settingsSchemaVersion -le 0) {
    throw 'settingsSchemaVersion must be greater than zero.'
}
if ($driverAbiVersion -le 0) {
    throw 'driverAbiVersion must be greater than zero.'
}
Assert-ThreePartVersion `
    -Value $productVersion `
    -Name 'productVersion'
Assert-PackageVersion `
    -Value $packageVersion `
    -ProductVersion $productVersion

if ($minimumWindowsBuild -ne 19045) {
    throw 'minimumWindowsBuild must be 19045 for Windows 0.2.0.'
}
if ($minimumWindowsApiContract -ne '10.0.19041.0') {
    throw 'minimumWindowsApiContract must be 10.0.19041.0.'
}
if ($maximumVersionTested -ne '10.0.26200.0') {
    throw 'maximumVersionTested must be 10.0.26200.0.'
}
if ($architecture -cne 'x64') {
    throw "Unsupported architecture '$architecture'; expected 'x64'."
}
if (-not [regex]::IsMatch($channel, '^[a-z0-9]+(?:-[a-z0-9]+)*$')) {
    throw 'channel must be a lowercase alphanumeric identifier with optional hyphens.'
}

$expectedTag = "windows-v$productVersion"
if ($metadataExpectedTag -cne $expectedTag) {
    throw (
        "Version metadata expectedTag '$metadataExpectedTag' does not match " +
        "derived tag '$expectedTag'."
    )
}

if ($PSBoundParameters.ContainsKey('RequireTag') -and $RequireTag -cne $expectedTag) {
    throw "Expected tag '$expectedTag', received '$RequireTag'."
}

$channels = Read-JsonFile -Path (
    Join-Path $windowsRoot 'packaging/channels.json'
)
$channelMap = Get-RequiredProperty `
    -Object $channels `
    -Name 'channels' `
    -Context 'channels metadata'
$channelProperty = $null
foreach ($property in $channelMap.PSObject.Properties) {
    if ($property.Name -ceq $channel) {
        $channelProperty = $property
        break
    }
}
if ($null -eq $channelProperty) {
    throw "No exact package settings exist for channel '$channel'."
}
$channelSettings = $channelProperty.Value

$packageIdentity = Get-RequiredProperty `
    -Object $channelSettings `
    -Name 'packageIdentity' `
    -Context "channel '$channel'"
$publisher = Get-RequiredProperty `
    -Object $channelSettings `
    -Name 'publisher' `
    -Context "channel '$channel'"
$credentialTarget = Get-RequiredProperty `
    -Object $channelSettings `
    -Name 'credentialTarget' `
    -Context "channel '$channel'"
$appInstallerPath = Get-RequiredProperty `
    -Object $channelSettings `
    -Name 'appInstallerPath' `
    -Context "channel '$channel'"

Assert-NonEmptyString -Value $packageIdentity -Name 'packageIdentity'
Assert-NonEmptyString -Value $publisher -Name 'publisher'
Assert-NonEmptyString -Value $credentialTarget -Name 'credentialTarget'
Assert-NonEmptyString -Value $appInstallerPath -Name 'appInstallerPath'

$driverFeedPath = $null
if (Test-ExactPropertyExists -Object $channelSettings -Name 'driverFeedPath') {
    $driverFeedPath = Get-RequiredProperty `
        -Object $channelSettings `
        -Name 'driverFeedPath' `
        -Context "channel '$channel'"
    if ($null -ne $driverFeedPath) {
        Assert-NonEmptyString -Value $driverFeedPath -Name 'driverFeedPath'
    }
}

$compatibility = Read-JsonFile -Path (
    Join-Path $windowsRoot "packaging/compatibility.$channel.json"
)

$compatibilityAppVersion = Get-RequiredProperty `
    -Object $compatibility `
    -Name 'appVersion' `
    -Context 'compatibility metadata'
$compatibilityContractVersion = Get-RequiredProperty `
    -Object $compatibility `
    -Name 'contractVersion' `
    -Context 'compatibility metadata'
$compatibilitySettingsSchemaVersion = Get-RequiredProperty `
    -Object $compatibility `
    -Name 'settingsSchemaVersion' `
    -Context 'compatibility metadata'
$compatibilityDriverAbiVersion = Get-RequiredProperty `
    -Object $compatibility `
    -Name 'driverAbiVersion' `
    -Context 'compatibility metadata'
$minimumDriverVersion = Get-RequiredProperty `
    -Object $compatibility `
    -Name 'minimumDriverVersion' `
    -Context 'compatibility metadata'
$recommendedDriverVersion = Get-RequiredProperty `
    -Object $compatibility `
    -Name 'recommendedDriverVersion' `
    -Context 'compatibility metadata'
$driverPackageAvailable = Get-RequiredProperty `
    -Object $compatibility `
    -Name 'driverPackageAvailable' `
    -Context 'compatibility metadata'
$compatibilityChannel = Get-RequiredProperty `
    -Object $compatibility `
    -Name 'channel' `
    -Context 'compatibility metadata'

Assert-NonEmptyString `
    -Value $compatibilityAppVersion `
    -Name 'compatibility appVersion'
Assert-NonEmptyString `
    -Value $compatibilityChannel `
    -Name 'compatibility channel'
Assert-NonEmptyString `
    -Value $minimumDriverVersion `
    -Name 'minimumDriverVersion'
Assert-NonEmptyString `
    -Value $recommendedDriverVersion `
    -Name 'recommendedDriverVersion'
Assert-JsonInteger `
    -Value $compatibilityContractVersion `
    -Name 'compatibility contractVersion'
Assert-JsonInteger `
    -Value $compatibilitySettingsSchemaVersion `
    -Name 'compatibility settingsSchemaVersion'
Assert-JsonInteger `
    -Value $compatibilityDriverAbiVersion `
    -Name 'compatibility driverAbiVersion'
if ($compatibilityContractVersion -le 0) {
    throw 'Compatibility contractVersion must be greater than zero.'
}
if ($compatibilitySettingsSchemaVersion -le 0) {
    throw 'Compatibility settingsSchemaVersion must be greater than zero.'
}
if ($compatibilityDriverAbiVersion -le 0) {
    throw 'Compatibility driverAbiVersion must be greater than zero.'
}
Assert-PackageVersion `
    -Value $minimumDriverVersion `
    -ProductVersion '1.0.0'
Assert-PackageVersion `
    -Value $recommendedDriverVersion `
    -ProductVersion '1.0.0'

if (-not ($driverPackageAvailable -is [bool])) {
    throw 'driverPackageAvailable must be a JSON Boolean.'
}
if ($compatibilityAppVersion -cne $productVersion) {
    throw 'Compatibility appVersion must match productVersion.'
}
if ($compatibilityChannel -cne $channel) {
    throw 'Compatibility channel must match the selected version channel.'
}
if ($compatibilityContractVersion -ne $contractVersion) {
    throw 'Compatibility contractVersion must match version metadata.'
}
if ($compatibilitySettingsSchemaVersion -ne $settingsSchemaVersion) {
    throw 'Compatibility settingsSchemaVersion must match version metadata.'
}
if ($compatibilityDriverAbiVersion -ne $driverAbiVersion) {
    throw 'Compatibility driverAbiVersion must match version metadata.'
}
if ($minimumDriverVersion -cne '1.0.0.2') {
    throw 'minimumDriverVersion must be 1.0.0.2 for Windows 0.2.0.'
}
if ($recommendedDriverVersion -cne '1.0.0.2') {
    throw 'recommendedDriverVersion must be 1.0.0.2 for Windows 0.2.0.'
}

$hasDriverPackageUrl = Test-ExactPropertyExists `
    -Object $compatibility `
    -Name 'driverPackageUrl' `
    -IgnoreCase
$hasDriverPackageSha256 = Test-ExactPropertyExists `
    -Object $compatibility `
    -Name 'driverPackageSha256' `
    -IgnoreCase
if (-not $driverPackageAvailable -and (
    $hasDriverPackageUrl -or
    $hasDriverPackageSha256
)) {
    throw (
        'driverPackageAvailable=false cannot be combined with ' +
        'driverPackageUrl or driverPackageSha256.'
    )
}
if ($driverPackageAvailable) {
    throw 'driverPackageAvailable=true is unsupported for this Internal milestone.'
}

[pscustomobject]@{
    ProductVersion = $productVersion
    PackageVersion = $packageVersion
    ExpectedTag = $expectedTag
    PackageIdentity = $packageIdentity
    Publisher = $publisher
    Channel = $channel
    Architecture = $architecture
    MinimumWindowsBuild = $minimumWindowsBuild
    MinimumWindowsApiContract = $minimumWindowsApiContract
    MaximumVersionTested = $maximumVersionTested
    CredentialTarget = $credentialTarget
    AppInstallerPath = $appInstallerPath
    DriverFeedPath = $driverFeedPath
}
