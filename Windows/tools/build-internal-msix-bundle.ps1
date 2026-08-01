[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CertificatePath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$InstallScriptPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UninstallScriptPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AllowedOutputRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$SourceCommit,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+$')]
    [string]$WorkflowRunId,

    [Parameter(Mandatory)]
    [ValidateSet('EMKE.Translation.Internal')]
    [string]$PackageIdentity,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$releaseMetadata = & (Join-Path $PSScriptRoot 'resolve-version.ps1') `
    -VersionFile (Join-Path $repositoryRoot 'Windows/version.json')
if (
    $null -eq $releaseMetadata -or
    $releaseMetadata.Channel -cne 'internal' -or
    $releaseMetadata.PackageIdentity -cne $PackageIdentity
) {
    throw 'Internal bundle metadata resolution failed.'
}
$packageBaseName = "EMKE-Translation-Windows-$($releaseMetadata.ProductVersion)-internal-$($releaseMetadata.Architecture)"
$expectedInputs = [ordered]@{
    "$packageBaseName.msix" = $PackagePath
    "$packageBaseName.cer" = $CertificatePath
    'Install-EMKE-Translation-Internal.ps1' = $InstallScriptPath
    'Uninstall-EMKE-Translation-Internal.ps1' = $UninstallScriptPath
}

function Assert-NoReparsePathChain {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [switch]$AllowMissingLeaf
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $currentPath = $fullPath
    while ($true) {
        if (Test-Path -LiteralPath $currentPath) {
            $currentItem = Get-Item -LiteralPath $currentPath -Force
            $linkProperty = $currentItem.PSObject.Properties['LinkType']
            $linkType = if ($null -eq $linkProperty) {
                $null
            } else {
                $linkProperty.Value
            }
            if (
                $null -ne $linkType -or
                ($currentItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw 'Bundle paths must not contain reparse points.'
            }
        } elseif (-not $AllowMissingLeaf) {
            throw 'Bundle path chain validation failed.'
        }

        $parent = [IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) {
            break
        }
        $currentPath = $parent.FullName
    }

    return $fullPath
}

function Resolve-ExactInputFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ExpectedName
    )

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw "Bundle input path must be absolute: $ExpectedName"
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Bundle input file is unavailable: $ExpectedName"
    }

    $resolvedPath = Assert-NoReparsePathChain -Path $Path
    $item = Get-Item -LiteralPath $resolvedPath -Force
    if (
        $item.Name -cne $ExpectedName -or
        $null -ne $item.LinkType -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "Bundle input validation failed: $ExpectedName"
    }

    return $item.FullName
}

function Get-FileEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path -Force
    [ordered]@{
        name = $item.Name
        size = [int64]$item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).
            Hash.ToUpperInvariant()
    }
}

$resolvedInputs = [ordered]@{}
foreach ($entry in $expectedInputs.GetEnumerator()) {
    $resolvedInputs[$entry.Key] = Resolve-ExactInputFile `
        -Path $entry.Value `
        -ExpectedName $entry.Key
}

if (-not [IO.Path]::IsPathFullyQualified($AllowedOutputRoot)) {
    throw 'Bundle allowed output root validation failed.'
}
$resolvedAllowedOutputRoot = [IO.Path]::GetFullPath($AllowedOutputRoot)
if (
    -not (
        Test-Path `
            -LiteralPath $resolvedAllowedOutputRoot `
            -PathType Container
    )
) {
    throw 'Bundle allowed output root validation failed.'
}
$resolvedAllowedOutputRoot =
    Assert-NoReparsePathChain -Path $AllowedOutputRoot
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    throw 'Bundle output directory must be inside the allowed output root.'
}
$resolvedOutput = Assert-NoReparsePathChain `
    -Path $OutputDirectory `
    -AllowMissingLeaf
$pathComparison = if ($IsWindows) {
    [StringComparison]::OrdinalIgnoreCase
} else {
    [StringComparison]::Ordinal
}
$allowedPrefix =
    $resolvedAllowedOutputRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutput.StartsWith($allowedPrefix, $pathComparison)) {
    throw 'Bundle output directory must be inside the allowed output root.'
}
if (Test-Path -LiteralPath $resolvedOutput) {
    $outputItem = Get-Item -LiteralPath $resolvedOutput -Force
    if (
        -not $outputItem.PSIsContainer -or
        $null -ne $outputItem.LinkType -or
        ($outputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'Bundle output directory validation failed.'
    }
    if (@(Get-ChildItem -LiteralPath $resolvedOutput -Force).Count -ne 0) {
        throw 'Bundle output directory must be empty.'
    }
} else {
    [IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
}
$resolvedOutput = Assert-NoReparsePathChain -Path $resolvedOutput

foreach ($entry in $resolvedInputs.GetEnumerator()) {
    [IO.File]::Copy(
        $entry.Value,
        (Join-Path $resolvedOutput $entry.Key),
        $false
    )
}

$hashLines = foreach ($name in $resolvedInputs.Keys) {
    $hash = (Get-FileHash `
        -LiteralPath (Join-Path $resolvedOutput $name) `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    "$hash  $name"
}
$hashPath = Join-Path $resolvedOutput 'SHA256SUMS.txt'
[IO.File]::WriteAllText(
    $hashPath,
    (($hashLines -join "`n") + "`n"),
    [Text.UTF8Encoding]::new($false)
)

$handoffNames = @(
    $resolvedInputs.Keys
    'SHA256SUMS.txt'
)
$handoffPaths = @(
    $handoffNames |
        ForEach-Object { Join-Path $resolvedOutput $_ }
)
$zipPath = Join-Path $resolvedOutput "$packageBaseName.zip"
Compress-Archive `
    -LiteralPath $handoffPaths `
    -DestinationPath $zipPath `
    -CompressionLevel Optimal

$handoffEvidence = @(
    $handoffNames |
        ForEach-Object {
            Get-FileEvidence -Path (Join-Path $resolvedOutput $_)
        }
)
$provenance = [ordered]@{
    schemaVersion = 1
    sourceCommit = $SourceCommit.ToLowerInvariant()
    workflowRunId = $WorkflowRunId
    packageIdentity = $PackageIdentity
    certificateThumbprint = $CertificateThumbprint.ToUpperInvariant()
    handoffFiles = $handoffEvidence
    zip = Get-FileEvidence -Path $zipPath
}
$provenancePath = Join-Path $resolvedOutput "$packageBaseName.provenance.json"
[IO.File]::WriteAllText(
    $provenancePath,
    (($provenance | ConvertTo-Json -Depth 5) + "`n"),
    [Text.UTF8Encoding]::new($false)
)

Write-Output "Bundle: $zipPath"
Write-Output "Bundle SHA-256: $($provenance.zip.sha256)"
Write-Output "Provenance: $provenancePath"
