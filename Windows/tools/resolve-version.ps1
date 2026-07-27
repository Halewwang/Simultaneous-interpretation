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

    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

$resolvedVersionFile = (Resolve-Path -LiteralPath $VersionFile).Path
$windowsRoot = Split-Path -Parent $resolvedVersionFile
$version = Read-JsonFile -Path $resolvedVersionFile
$channels = Read-JsonFile -Path (
    Join-Path $windowsRoot 'packaging/channels.json'
)
$compatibility = Read-JsonFile -Path (
    Join-Path $windowsRoot "packaging/compatibility.$($version.channel).json"
)

$channelSettings = $channels.channels.PSObject.Properties[
    [string]$version.channel
].Value
if ($null -eq $channelSettings) {
    throw "No package settings exist for channel '$($version.channel)'."
}

$expectedTag = "windows-v$($version.productVersion)"
if ([string]$version.expectedTag -cne $expectedTag) {
    throw (
        "Version metadata expectedTag '$($version.expectedTag)' does not match " +
        "derived tag '$expectedTag'."
    )
}

if ($PSBoundParameters.ContainsKey('RequireTag') -and $RequireTag -cne $expectedTag) {
    throw "Expected tag '$expectedTag', received '$RequireTag'."
}

if ([string]$compatibility.appVersion -cne [string]$version.productVersion) {
    throw 'Compatibility appVersion must match productVersion.'
}
if ([string]$compatibility.channel -cne [string]$version.channel) {
    throw 'Compatibility channel must match the selected version channel.'
}

$compatibilityPropertyNames = $compatibility.PSObject.Properties.Name
$hasDriverPackageLocation = (
    $compatibilityPropertyNames -contains 'driverPackageUrl'
) -or (
    $compatibilityPropertyNames -contains 'driverPackageSha256'
)
if (
    $compatibility.driverPackageAvailable -eq $false -and
    $hasDriverPackageLocation
) {
    throw (
        'driverPackageAvailable=false cannot be combined with ' +
        'driverPackageUrl or driverPackageSha256.'
    )
}

[pscustomobject]@{
    ProductVersion = [string]$version.productVersion
    PackageVersion = [string]$version.packageVersion
    ExpectedTag = $expectedTag
    PackageIdentity = [string]$channelSettings.packageIdentity
    Publisher = [string]$channelSettings.publisher
    Channel = [string]$version.channel
    Architecture = [string]$version.architecture
    MinimumWindowsBuild = [int]$version.minimumWindowsBuild
    CredentialTarget = [string]$channelSettings.credentialTarget
    AppInstallerPath = [string]$channelSettings.appInstallerPath
    DriverFeedPath = $channelSettings.driverFeedPath
}
