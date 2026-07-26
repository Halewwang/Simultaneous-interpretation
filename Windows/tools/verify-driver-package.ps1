[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-SinglePackageFile {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Files,

        [Parameter(Mandatory)]
        [string]$Extension,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $matches = @($Files | Where-Object { $_.Extension -ieq $Extension })
    if ($matches.Count -ne 1) {
        throw "Package must contain exactly one $Description; found $($matches.Count)."
    }
    return $matches[0]
}

function Assert-ContainsExactlyOnce {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Literal
    )

    $count = ([regex]::Matches(
        $Text,
        [regex]::Escape($Literal),
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )).Count
    if ($count -ne 1) {
        throw "Expected exactly one '$Literal' declaration; found $count."
    }
}

function Get-NormalizedCatalogMemberNames {
    param(
        [Parameter(Mandatory)]
        [object]$Items,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($null -eq $Items.Keys) {
        throw "Detailed catalog validation did not return $Description keys."
    }
    return @(
        $Items.Keys |
            ForEach-Object {
                [System.IO.Path]::GetFileName([string]$_).ToLowerInvariant()
            } |
            Sort-Object
    )
}

if (-not $IsWindows) {
    throw "Driver package verification requires Windows catalog APIs."
}

$resolvedPackage = Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop
if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Container)) {
    throw "Package directory does not exist: $PackageDirectory"
}
if ($resolvedPackage.Path -match "(?:^|[\\/])Debug(?:[\\/]|$)") {
    throw "Debug directories are forbidden in a distributable driver package."
}

$directories = @(Get-ChildItem -LiteralPath $resolvedPackage -Directory -Force)
if ($directories.Count -ne 0) {
    throw "Driver package must be flat; nested directories are forbidden."
}
$files = @(Get-ChildItem -LiteralPath $resolvedPackage -File -Force)
$inf = Get-SinglePackageFile -Files $files -Extension ".inf" -Description "INF"
$sys = Get-SinglePackageFile -Files $files -Extension ".sys" -Description "SYS"
$cat = Get-SinglePackageFile -Files $files -Extension ".cat" -Description "CAT"

if ($files.Count -ne 3) {
    throw "Driver package must contain only one INF, one SYS, and one CAT."
}
if (@($files | Where-Object { $_.Extension -ieq ".pdb" }).Count -ne 0) {
    throw "PDB files are forbidden in the distributable package."
}
if (@($files | Where-Object { $_.Name -match "Debug" }).Count -ne 0) {
    throw "Debug binaries are forbidden in the distributable package."
}

$infText = Get-Content -LiteralPath $inf.FullName -Raw
if ($infText -notmatch "ROOT\\EMKEVIRTUALAUDIO") {
    throw "INF does not declare ROOT\EMKEVIRTUALAUDIO."
}

$roles = @(
    "emke.meeting-speaker.render",
    "emke.app-speaker.capture",
    "emke.app-microphone.render",
    "emke.meeting-microphone.capture"
)
foreach ($role in $roles) {
    Assert-ContainsExactlyOnce -Text $infText -Literal $role
}

if ($infText -notmatch "DriverAbi\s*,\s*0x00010001\s*,\s*0x00000001") {
    throw "INF driver ABI must equal 1."
}
if ($infText -notmatch "DriverVer\s*=\s*[^,]+,\s*(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)") {
    throw "INF DriverVer is missing a four-part version."
}
$infVersion = [version]$Matches["version"]

$versionInfo = (Get-Item -LiteralPath $sys.FullName).VersionInfo
if ($versionInfo.FileVersion -notmatch "(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)") {
    throw "Driver file version is missing or is not a four-part version."
}
$fileVersion = [version]$Matches["version"]
if ($infVersion -ne $fileVersion) {
    throw "DriverVer $infVersion does not agree with FileVersion $fileVersion."
}

$catalogSignature = Get-AuthenticodeSignature -LiteralPath $cat.FullName
if ($catalogSignature.Status -notin @("NotSigned", "Valid")) {
    throw "Catalog is malformed or has an invalid signature status: $($catalogSignature.Status)."
}

$catalogValidation = Test-FileCatalog `
    -CatalogFilePath $cat.FullName `
    -Path @($inf.FullName, $sys.FullName) `
    -Detailed `
    -ErrorAction Stop
if ($null -eq $catalogValidation -or
    $catalogValidation.Status.ToString() -ne "Valid") {
    $status = if ($null -eq $catalogValidation) {
        "NoResult"
    } else {
        $catalogValidation.Status.ToString()
    }
    throw "Catalog validation failed for the packaged INF and SYS: $status."
}
if ($catalogValidation.HashAlgorithm.ToString() -ne "SHA256") {
    throw "Catalog must validate packaged files with SHA-256."
}

$expectedMemberNames = @(
    $inf.Name.ToLowerInvariant(),
    $sys.Name.ToLowerInvariant()
) | Sort-Object
$catalogMemberNames = Get-NormalizedCatalogMemberNames `
    -Items $catalogValidation.CatalogItems `
    -Description "CatalogItems"
$pathMemberNames = Get-NormalizedCatalogMemberNames `
    -Items $catalogValidation.PathItems `
    -Description "PathItems"

$catalogDifference = @(
    Compare-Object `
        -ReferenceObject $expectedMemberNames `
        -DifferenceObject $catalogMemberNames
)
$pathDifference = @(
    Compare-Object `
        -ReferenceObject $expectedMemberNames `
        -DifferenceObject $pathMemberNames
)
if ($catalogMemberNames.Count -ne 2 -or $catalogDifference.Count -ne 0) {
    throw "Catalog must contain exactly the packaged INF and SYS."
}
if ($pathMemberNames.Count -ne 2 -or $pathDifference.Count -ne 0) {
    throw "Catalog validation must test exactly the packaged INF and SYS."
}

Write-Host "Driver package verification passed."
Write-Host "Catalog status: $($catalogValidation.Status); signature status: $($catalogSignature.Status) (no signing claim)."
Write-Host "Driver version: $fileVersion"
