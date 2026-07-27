[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [string]$Verifier
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Verifier {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [bool]$ExpectSuccess,

        [Parameter(Mandatory)]
        [string]$Description
    )

    & pwsh -NoProfile -File $Verifier $Directory
    $exitCode = $LASTEXITCODE
    if ($ExpectSuccess -and $exitCode -ne 0) {
        throw "$Description unexpectedly failed with exit code $exitCode."
    }
    if (-not $ExpectSuccess -and $exitCode -eq 0) {
        throw "$Description unexpectedly accepted mismatched package bytes."
    }
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackageDirectory).Path
$resolvedVerifier = (Resolve-Path -LiteralPath $Verifier).Path
$testRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    ("emke-package-verifier-" + [guid]::NewGuid().ToString("N"))

try {
    Invoke-Verifier `
        -Directory $resolvedPackage `
        -ExpectSuccess $true `
        -Description "original catalog membership"

    foreach ($extension in @(".sys", ".inf")) {
        $caseDirectory = Join-Path $testRoot $extension.TrimStart(".")
        New-Item -ItemType Directory -Path $caseDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $resolvedPackage -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $caseDirectory
        }
        $target = Get-ChildItem -LiteralPath $caseDirectory -File |
            Where-Object { $_.Extension -ieq $extension } |
            Select-Object -First 1
        if ($null -eq $target) {
            throw "Mutation fixture is missing $extension."
        }
        [System.IO.File]::AppendAllText($target.FullName, "`r`nEMKE-MUTATION")
        Invoke-Verifier `
            -Directory $caseDirectory `
            -ExpectSuccess $false `
            -Description "mutated $extension catalog membership"
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host "Package verifier integration tests passed: original valid; mutated INF/SYS rejected."
