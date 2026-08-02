[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$toolsDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$creator = Join-Path $toolsDirectory "create-driver-submission.ps1"
$sourceCommit = "0123456789abcdef0123456789abcdef01234567"
$expectedNames = @(
    "EMKE.VirtualAudio.inf",
    "EMKE.VirtualAudio.sys",
    "EMKE.VirtualAudio.cat"
)
$temporaryRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("emke-driver-submission-" + [guid]::NewGuid().ToString("N"))

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

function Copy-PackageFixture {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $destination = Join-Path $temporaryRoot $Name
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($fileName in $expectedNames) {
        Copy-Item `
            -LiteralPath (Join-Path $PackageDirectory $fileName) `
            -Destination (Join-Path $destination $fileName)
    }
    return $destination
}

function Invoke-Create {
    param(
        [Parameter(Mandatory)]
        [string]$InputDirectory,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $output = Join-Path $temporaryRoot "$Name-output"
    $archive = Join-Path $temporaryRoot "$Name.zip"
    $evidence = Join-Path $temporaryRoot "$Name-evidence.json"
    & $creator `
        -PackageDirectory $InputDirectory `
        -OutputDirectory $output `
        -SourceCommit $sourceCommit `
        -ArchivePath $archive `
        -EvidencePath $evidence `
        -WorkflowRunId "123456789" `
        -WdkVersion "10.0.28000.2526" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Submission creator failed with exit code $LASTEXITCODE."
    }
    return [pscustomobject]@{
        Output = $output
        Archive = $archive
        Evidence = $evidence
    }
}

function Assert-CreateRejected {
    param(
        [Parameter(Mandatory)]
        [string]$InputDirectory,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Pattern
    )

    Assert-Throws -Pattern $Pattern -Action {
        Invoke-Create -InputDirectory $InputDirectory -Name $Name | Out-Null
    }
}

try {
    if (-not (Test-Path -LiteralPath $creator -PathType Leaf)) {
        throw "Submission creator is missing: $creator"
    }
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $resolvedPackage = (Resolve-Path -LiteralPath $PackageDirectory).Path

    $first = Invoke-Create -InputDirectory $resolvedPackage -Name "valid-a"
    $second = Invoke-Create -InputDirectory $resolvedPackage -Name "valid-b"
    $firstArchiveHash = (Get-FileHash -LiteralPath $first.Archive -Algorithm SHA256).Hash
    $secondArchiveHash = (Get-FileHash -LiteralPath $second.Archive -Algorithm SHA256).Hash
    if ($firstArchiveHash -cne $secondArchiveHash) {
        throw "Identical package inputs did not produce a deterministic archive."
    }

    $manifest = Get-Content `
        -LiteralPath (Join-Path $first.Output "driver-submission.json") `
        -Raw | ConvertFrom-Json
    if ($manifest.sourceCommit -cne $sourceCommit -or
        $manifest.driverVersion -cne "1.0.0.2" -or
        [int]$manifest.driverAbiVersion -ne 1 -or
        [int]$manifest.minimumWindowsBuild -ne 19045 -or
        $manifest.kmdfLibraryVersion -cne "1.31") {
        throw "Submission provenance metadata does not match the frozen release."
    }
    [string[]]$manifestNames = @($manifest.files | ForEach-Object name)
    if (@(Compare-Object $expectedNames $manifestNames).Count -ne 0) {
        throw "Submission manifest does not contain the exact package inventory."
    }
    foreach ($file in $manifest.files) {
        if (([string]$file.sha256) -cnotmatch "^[0-9a-f]{64}$") {
            throw "Submission manifest contains a non-canonical SHA-256 hash."
        }
    }

    Set-Content -LiteralPath (Join-Path $first.Output "extra.pdb") -Value "forbidden"
    Assert-Throws -Pattern "exact|extra|inventory|PDB" -Action {
        & $creator -ValidateOnly -OutputDirectory $first.Output
    }

    Remove-Item -LiteralPath (Join-Path $second.Output "EMKE.VirtualAudio.sys")
    Set-Content `
        -LiteralPath (Join-Path $second.Output "EMKE.VirtualAudio.sys") `
        -Value "changed"
    Assert-Throws -Pattern "hash|SHA|changed|manifest" -Action {
        & $creator -ValidateOnly -OutputDirectory $second.Output
    }

    $unresolved = Copy-PackageFixture -Name "unresolved-token"
    $unresolvedInf = Join-Path $unresolved "EMKE.VirtualAudio.inf"
    $unresolvedText = Get-Content -LiteralPath $unresolvedInf -Raw
    [IO.File]::WriteAllText(
        $unresolvedInf,
        $unresolvedText.Replace(
            "KmdfLibraryVersion=1.31",
            "KmdfLibraryVersion=`$KMDFVERSION`$"
        )
    )
    Assert-CreateRejected `
        -InputDirectory $unresolved `
        -Name "unresolved-token" `
        -Pattern "unresolved|stamp|token"

    $wrongModel = Copy-PackageFixture -Name "wrong-model"
    $wrongModelInf = Join-Path $wrongModel "EMKE.VirtualAudio.inf"
    $wrongModelText = Get-Content -LiteralPath $wrongModelInf -Raw
    [IO.File]::WriteAllText(
        $wrongModelInf,
        $wrongModelText.Replace(
            "NTamd64.10.0...19045",
            "NTamd64.10.0...26200"
        )
    )
    Assert-CreateRejected `
        -InputDirectory $wrongModel `
        -Name "wrong-model" `
        -Pattern "model|19045|contract|verification"

    $wrongKmdf = Copy-PackageFixture -Name "wrong-kmdf"
    $wrongKmdfInf = Join-Path $wrongKmdf "EMKE.VirtualAudio.inf"
    $wrongKmdfText = Get-Content -LiteralPath $wrongKmdfInf -Raw
    [IO.File]::WriteAllText(
        $wrongKmdfInf,
        $wrongKmdfText.Replace(
            "KmdfLibraryVersion=1.31",
            "KmdfLibraryVersion=1.33"
        )
    )
    Assert-CreateRejected `
        -InputDirectory $wrongKmdf `
        -Name "wrong-kmdf" `
        -Pattern "KMDF|1.31|contract|verification"

    $badCatalogMembership = Copy-PackageFixture -Name "bad-cat-membership"
    [IO.File]::AppendAllText(
        (Join-Path $badCatalogMembership "EMKE.VirtualAudio.sys"),
        "catalog-member-mutation"
    )
    Assert-CreateRejected `
        -InputDirectory $badCatalogMembership `
        -Name "bad-cat-membership" `
        -Pattern "catalog|reference|hash|verification"

    Write-Host (
        "Driver submission validation passed: deterministic archive; " +
        "exact inventory; destination hashes; INF and CAT mutations rejected."
    )
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
