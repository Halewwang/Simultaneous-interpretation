[CmdletBinding(DefaultParameterSetName = "Create")]
param(
    [Parameter(Mandatory, ParameterSetName = "Create")]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter(Mandatory, ParameterSetName = "Create")]
    [ValidatePattern("^[0-9a-f]{40}$")]
    [string]$SourceCommit,

    [Parameter(Mandatory, ParameterSetName = "Create")]
    [string]$ArchivePath,

    [Parameter(Mandatory, ParameterSetName = "Create")]
    [string]$EvidencePath,

    [Parameter(Mandatory, ParameterSetName = "Create")]
    [ValidatePattern("^[0-9]+$")]
    [string]$WorkflowRunId,

    [Parameter(Mandatory, ParameterSetName = "Create")]
    [ValidatePattern("^10\.0\.28000\.2526$")]
    [string]$WdkVersion,

    [Parameter(Mandatory, ParameterSetName = "Validate")]
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:PackageFileNames = @(
    "EMKE.VirtualAudio.inf",
    "EMKE.VirtualAudio.sys",
    "EMKE.VirtualAudio.cat"
)
$script:ManifestFileName = "driver-submission.json"
$script:ExpectedOutputNames = @(
    $script:PackageFileNames + $script:ManifestFileName
)
$script:FixedArchiveTimestamp = [DateTimeOffset]::Parse(
    "1980-01-01T00:00:00Z",
    [Globalization.CultureInfo]::InvariantCulture
)

function Get-NormalizedFullPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
}

function Test-PathEqualOrWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Candidate,

        [Parameter(Mandatory)]
        [string]$Root
    )

    $candidatePath = Get-NormalizedFullPath -Path $Candidate
    $rootPath = Get-NormalizedFullPath -Path $Root
    if ($candidatePath.Equals(
        $rootPath,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        return $true
    }
    $prefix = $rootPath + [IO.Path]::DirectorySeparatorChar
    return $candidatePath.StartsWith(
        $prefix,
        [StringComparison]::OrdinalIgnoreCase
    )
}

function Assert-RealDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must be a real directory: $Path"
    }
    return $item.FullName
}

function Assert-ExactPropertyNames {
    param(
        [Parameter(Mandatory)]
        [psobject]$Object,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Description
    )

    [string[]]$actual = @($Object.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) {
        throw "$Description has an unexpected property inventory."
    }
    foreach ($name in $Expected) {
        if (@($actual | Where-Object { $_ -ceq $name }).Count -ne 1) {
            throw "$Description has an unexpected property inventory."
        }
    }
}

function Get-LowercaseSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256
    return $hash.Hash.ToLowerInvariant()
}

function Assert-ExactFlatInventory {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [string[]]$ExpectedNames,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $directories = @(Get-ChildItem -LiteralPath $Directory -Directory -Force)
    if ($directories.Count -ne 0) {
        throw "$Description must be flat; nested directories are forbidden."
    }
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Force)
    if ($files.Count -ne $ExpectedNames.Count) {
        throw "$Description must contain the exact immutable inventory."
    }
    foreach ($expectedName in $ExpectedNames) {
        if (@($files | Where-Object {
            $_.Name -ceq $expectedName
        }).Count -ne 1) {
            throw "$Description must contain the exact immutable inventory."
        }
    }
}

function Assert-ExactSourcePackageInventory {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    $directories = @(Get-ChildItem -LiteralPath $Directory -Directory -Force)
    if ($directories.Count -ne 0) {
        throw "Verified driver package must be flat; nested directories are forbidden."
    }
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Force)
    if ($files.Count -ne $script:PackageFileNames.Count) {
        throw "Verified driver package must contain the exact immutable inventory."
    }
    foreach ($expectedName in $script:PackageFileNames) {
        if (@($files | Where-Object {
            $_.Name -ieq $expectedName
        }).Count -ne 1) {
            throw "Verified driver package must contain the exact immutable inventory."
        }
    }
}

function Test-DriverSubmissionDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    $resolvedDirectory = Assert-RealDirectory `
        -Path $Directory `
        -Description "Driver submission directory"
    Assert-ExactFlatInventory `
        -Directory $resolvedDirectory `
        -ExpectedNames $script:ExpectedOutputNames `
        -Description "Driver submission directory"

    $manifestPath = Join-Path $resolvedDirectory $script:ManifestFileName
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw |
            ConvertFrom-Json
    } catch {
        throw "Driver submission manifest is not valid JSON."
    }
    Assert-ExactPropertyNames `
        -Object $manifest `
        -Expected @(
            "sourceCommit",
            "driverVersion",
            "driverAbiVersion",
            "minimumWindowsBuild",
            "kmdfLibraryVersion",
            "files"
        ) `
        -Description "Driver submission manifest"
    if (([string]$manifest.sourceCommit) -cnotmatch "^[0-9a-f]{40}$") {
        throw "Driver submission source commit must be 40 lowercase hex characters."
    }
    if ([string]$manifest.driverVersion -cne "1.0.0.2" -or
        [int]$manifest.driverAbiVersion -ne 1 -or
        [int]$manifest.minimumWindowsBuild -ne 19045 -or
        [string]$manifest.kmdfLibraryVersion -cne "1.31") {
        throw "Driver submission manifest release metadata is invalid."
    }

    [object[]]$manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -ne $script:PackageFileNames.Count) {
        throw "Driver submission manifest must list exactly three package files."
    }
    foreach ($expectedName in $script:PackageFileNames) {
        [object[]]$matches = @(
            $manifestFiles |
                Where-Object { [string]$_.name -ceq $expectedName }
        )
        if ($matches.Count -ne 1) {
            throw "Driver submission manifest package inventory is invalid."
        }
        Assert-ExactPropertyNames `
            -Object $matches[0] `
            -Expected @("name", "sha256") `
            -Description "Driver submission file entry"
        $expectedHash = [string]$matches[0].sha256
        if ($expectedHash -cnotmatch "^[0-9a-f]{64}$") {
            throw "Driver submission manifest SHA-256 must be lowercase hex."
        }
        $actualHash = Get-LowercaseSha256 `
            -Path (Join-Path $resolvedDirectory $expectedName)
        if ($actualHash -cne $expectedHash) {
            throw "Driver submission destination hash changed for $expectedName."
        }
    }
    return $manifest
}

function Write-DeterministicSubmissionArchive {
    param(
        [Parameter(Mandatory)]
        [string]$SubmissionDirectory,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    $parent = Split-Path -Parent $destinationPath
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "Archive destination must have a parent directory."
    }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path -LiteralPath $destinationPath) {
        if (-not (Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
            throw "Archive destination is not a file: $destinationPath"
        }
        Remove-Item -LiteralPath $destinationPath -Force
    }

    $archiveStream = [IO.File]::Open(
        $destinationPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None
    )
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $archiveStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false
        )
        try {
            foreach ($name in $script:ExpectedOutputNames) {
                $entry = $archive.CreateEntry(
                    $name,
                    [IO.Compression.CompressionLevel]::NoCompression
                )
                $entry.LastWriteTime = $script:FixedArchiveTimestamp
                $entry.ExternalAttributes = 0
                $entryStream = $entry.Open()
                try {
                    $sourceStream = [IO.File]::OpenRead(
                        (Join-Path $SubmissionDirectory $name)
                    )
                    try {
                        $sourceStream.CopyTo($entryStream)
                    } finally {
                        $sourceStream.Dispose()
                    }
                } finally {
                    $entryStream.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
    } finally {
        $archiveStream.Dispose()
    }
    return $destinationPath
}

function New-DriverSubmission {
    param(
        [Parameter(Mandatory)]
        [string]$InputDirectory,

        [Parameter(Mandatory)]
        [string]$DestinationDirectory,

        [Parameter(Mandatory)]
        [string]$Commit,

        [Parameter(Mandatory)]
        [string]$DestinationArchive,

        [Parameter(Mandatory)]
        [string]$DestinationEvidence,

        [Parameter(Mandatory)]
        [string]$RunId,

        [Parameter(Mandatory)]
        [string]$WdkPackageVersion
    )

    $resolvedInput = Assert-RealDirectory `
        -Path $InputDirectory `
        -Description "Verified driver package"
    $outputPath = [IO.Path]::GetFullPath($DestinationDirectory)
    $archivePath = [IO.Path]::GetFullPath($DestinationArchive)
    $evidencePath = [IO.Path]::GetFullPath($DestinationEvidence)
    if ((Test-PathEqualOrWithin `
            -Candidate $outputPath `
            -Root $resolvedInput) -or
        (Test-PathEqualOrWithin `
            -Candidate $resolvedInput `
            -Root $outputPath)) {
        throw "Submission output and verified package directories must be disjoint."
    }
    if ((Test-PathEqualOrWithin `
            -Candidate $archivePath `
            -Root $outputPath) -or
        (Test-PathEqualOrWithin `
            -Candidate $evidencePath `
            -Root $outputPath)) {
        throw "Archive and evidence files must be outside the submission directory."
    }

    $verifier = Join-Path $PSScriptRoot "verify-driver-package.ps1"
    & $verifier $resolvedInput
    if ($LASTEXITCODE -ne 0) {
        throw "Verified driver package gate failed with exit code $LASTEXITCODE."
    }
    Assert-ExactSourcePackageInventory `
        -Directory $resolvedInput

    $sourceHashes = [ordered]@{}
    foreach ($name in $script:PackageFileNames) {
        $sourceHashes[$name] = Get-LowercaseSha256 `
            -Path (Join-Path $resolvedInput $name)
    }

    if (Test-Path -LiteralPath $outputPath) {
        Assert-RealDirectory `
            -Path $outputPath `
            -Description "Existing submission output" | Out-Null
        Remove-Item -LiteralPath $outputPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

    $manifestFiles = @()
    foreach ($name in $script:PackageFileNames) {
        $destinationFile = Join-Path $outputPath $name
        [IO.File]::Copy(
            (Join-Path $resolvedInput $name),
            $destinationFile,
            $false
        )
        $destinationHash = Get-LowercaseSha256 -Path $destinationFile
        if ($destinationHash -cne $sourceHashes[$name]) {
            throw "Copied driver package bytes changed for $name."
        }
        $manifestFiles += [ordered]@{
            name = $name
            sha256 = $destinationHash
        }
    }

    $manifest = [ordered]@{
        sourceCommit = $Commit
        driverVersion = "1.0.0.2"
        driverAbiVersion = 1
        minimumWindowsBuild = 19045
        kmdfLibraryVersion = "1.31"
        files = $manifestFiles
    }
    $manifestPath = Join-Path $outputPath $script:ManifestFileName
    [IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false)
    )
    $validatedManifest = Test-DriverSubmissionDirectory -Directory $outputPath
    $writtenArchive = Write-DeterministicSubmissionArchive `
        -SubmissionDirectory $outputPath `
        -Destination $archivePath
    $archiveHash = Get-LowercaseSha256 -Path $writtenArchive

    $evidenceParent = Split-Path -Parent $evidencePath
    New-Item -ItemType Directory -Path $evidenceParent -Force | Out-Null
    $evidence = [ordered]@{
        schemaVersion = 1
        workflowRunId = $RunId
        sourceCommit = $Commit
        wdkVersion = $WdkPackageVersion
        archive = [ordered]@{
            name = [IO.Path]::GetFileName($writtenArchive)
            sha256 = $archiveHash
        }
        files = $validatedManifest.files
        signingBoundary = "external-hardware-dev-center"
    }
    [IO.File]::WriteAllText(
        $evidencePath,
        ($evidence | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false)
    )

    Write-Host "Driver submission created: $outputPath"
    Write-Host "Portal-ready archive: $writtenArchive"
    Write-Host "Archive SHA-256: $archiveHash"
    Write-Host "Signing boundary: Microsoft Hardware Dev Center is external."
}

if ($ValidateOnly) {
    Test-DriverSubmissionDirectory -Directory $OutputDirectory | Out-Null
    Write-Host "Driver submission validation passed: $OutputDirectory"
    return
}

New-DriverSubmission `
    -InputDirectory $PackageDirectory `
    -DestinationDirectory $OutputDirectory `
    -Commit $SourceCommit `
    -DestinationArchive $ArchivePath `
    -DestinationEvidence $EvidencePath `
    -RunId $WorkflowRunId `
    -WdkPackageVersion $WdkVersion
