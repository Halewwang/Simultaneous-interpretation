[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$toolsDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$importer = Join-Path $toolsDirectory "import-microsoft-signed-driver.ps1"
$expectedNames = @(
    "EMKE.VirtualAudio.inf",
    "EMKE.VirtualAudio.sys",
    "EMKE.VirtualAudio.cat"
)
$sourceCommit = "0123456789abcdef0123456789abcdef01234567"
$submittedCatalogHash = "a" * 64
$temporaryRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("emke-microsoft-driver-import-" + [guid]::NewGuid().ToString("N"))

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

function Copy-CanonicalPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $destination = Join-Path $temporaryRoot $Name
    New-Item -ItemType Directory -Path $destination | Out-Null
    $sourceFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Force)
    foreach ($expectedName in $expectedNames) {
        [object[]]$matches = @($sourceFiles | Where-Object {
            $_.Name -ieq $expectedName
        })
        if ($matches.Count -ne 1) {
            throw "Package fixture is missing '$expectedName'."
        }
        Copy-Item `
            -LiteralPath $matches[0].FullName `
            -Destination (Join-Path $destination $expectedName)
    }
    return $destination
}

function New-SubmissionManifest {
    param(
        [Parameter(Mandatory)]
        [string]$Package
    )

    $manifestPath = Join-Path $temporaryRoot "driver-submission.json"
    $files = @(
        foreach ($name in $expectedNames) {
            $hash = if ($name -ceq "EMKE.VirtualAudio.cat") {
                $submittedCatalogHash
            } else {
                (Get-FileHash `
                    -LiteralPath (Join-Path $Package $name) `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            [ordered]@{
                name = $name
                sha256 = $hash
            }
        }
    )
    $manifest = [ordered]@{
        sourceCommit = $sourceCommit
        driverVersion = "1.0.0.2"
        driverAbiVersion = 1
        minimumWindowsBuild = 19045
        kmdfLibraryVersion = "1.31"
        files = $files
    }
    [IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false)
    )
    return $manifestPath
}

function New-TrustedEvidence {
    param(
        [string]$SignerSubject = (
            "CN=Microsoft Windows Hardware Compatibility Publisher, " +
            "O=Microsoft Corporation, C=US"
        ),
        [bool]$ChainValid = $true,
        [bool]$CertificateExpired = $false,
        [bool]$KernelPolicyValid = $true
    )

    return [pscustomobject]@{
        Status = "Valid"
        SignerSubject = $SignerSubject
        ChainValid = $ChainValid
        CertificateExpired = $CertificateExpired
        KernelPolicyValid = $KernelPolicyValid
        ChainSubjects = @(
            $SignerSubject,
            "CN=Microsoft Windows Third Party Component CA 2012",
            "CN=Microsoft Root Certificate Authority 2010"
        )
    }
}

function Invoke-Import {
    param(
        [Parameter(Mandatory)]
        [string]$Manifest,

        [Parameter(Mandatory)]
        [string]$ReturnedPackage,

        [Parameter(Mandatory)]
        [string]$Name,

        [scriptblock]$EvidenceWriter,

        [scriptblock]$EvidencePublisher,

        [scriptblock]$EvidenceCommitPublisher
    )

    $output = Join-Path $temporaryRoot "$Name-output"
    $evidence = Join-Path $temporaryRoot "$Name-evidence.json"
    $parameters = @{
        SubmissionManifest = $Manifest
        ReturnedPackageDirectory = $ReturnedPackage
        OutputDirectory = $output
        EvidencePath = $evidence
        PortalSubmissionId = "submission-12345"
        PortalStatus = "signed"
    }
    if ($null -ne $EvidenceWriter) {
        $parameters.EvidenceWriter = $EvidenceWriter
    }
    if ($null -ne $EvidencePublisher) {
        $parameters.EvidencePublisher = $EvidencePublisher
    }
    if ($null -ne $EvidenceCommitPublisher) {
        $parameters.EvidenceCommitPublisher = $EvidenceCommitPublisher
    }
    $null = Import-MicrosoftSignedDriverPackage @parameters
    return [pscustomobject]@{
        Output = $output
        Evidence = $evidence
    }
}

function Assert-ImportRejected {
    param(
        [Parameter(Mandatory)]
        [string]$Manifest,

        [Parameter(Mandatory)]
        [string]$ReturnedPackage,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Pattern,

        [string]$Output,

        [string]$Evidence,

        [scriptblock]$EvidenceWriter,

        [scriptblock]$EvidencePublisher,

        [scriptblock]$EvidenceCommitPublisher
    )

    if ([string]::IsNullOrWhiteSpace($Output)) {
        $Output = Join-Path $temporaryRoot "$Name-output"
    }
    if ([string]::IsNullOrWhiteSpace($Evidence)) {
        $Evidence = Join-Path $temporaryRoot "$Name-evidence.json"
    }
    $parameters = @{
        SubmissionManifest = $Manifest
        ReturnedPackageDirectory = $ReturnedPackage
        OutputDirectory = $Output
        EvidencePath = $Evidence
        PortalSubmissionId = "submission-12345"
        PortalStatus = "signed"
    }
    if ($null -ne $EvidenceWriter) {
        $parameters.EvidenceWriter = $EvidenceWriter
    }
    if ($null -ne $EvidencePublisher) {
        $parameters.EvidencePublisher = $EvidencePublisher
    }
    if ($null -ne $EvidenceCommitPublisher) {
        $parameters.EvidenceCommitPublisher = $EvidenceCommitPublisher
    }
    Assert-Throws -Pattern $Pattern -Action {
        Import-MicrosoftSignedDriverPackage @parameters
    }
    foreach ($forbiddenPath in @($Output, $Evidence)) {
        if (Test-Path -LiteralPath $forbiddenPath) {
            throw "Rejected import created output: $forbiddenPath"
        }
    }
}

try {
    if (-not $IsWindows) {
        throw "Microsoft-signed driver import validation requires Windows catalog APIs."
    }
    if (-not (Test-Path -LiteralPath $importer -PathType Leaf)) {
        throw "Microsoft-signed driver importer is missing: $importer"
    }
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $resolvedPackage = (Resolve-Path -LiteralPath $PackageDirectory).Path
    . $importer

    $script:TrustEvidence = New-TrustedEvidence
    $script:MutationAfterTrustName = $null
    function Get-MicrosoftCatalogTrustEvidence {
        param([string]$CatalogPath)
        if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
            throw "Trust evidence received a missing catalog."
        }
        if ((Split-Path -Leaf (Split-Path -Parent $CatalogPath)) -notlike
            ".emke-driver-snapshot-*") {
            throw "Trust evidence must verify only the protected driver snapshot."
        }
        $evidence = $script:TrustEvidence
        if (-not [string]::IsNullOrWhiteSpace(
                $script:MutationAfterTrustName)) {
            [IO.File]::AppendAllText(
                (Join-Path `
                    (Split-Path -Parent $CatalogPath) `
                    $script:MutationAfterTrustName),
                "mutated-after-trust"
            )
        }
        return $evidence
    }

    $validPackage = Copy-CanonicalPackage -Name "valid-returned"
    $manifest = New-SubmissionManifest -Package $validPackage
    $success = Invoke-Import `
        -Manifest $manifest `
        -ReturnedPackage $validPackage `
        -Name "valid"
    [string[]]$promotedNames = @(
        Get-ChildItem -LiteralPath $success.Output -File -Force |
            ForEach-Object Name
    )
    if (@(Compare-Object $expectedNames $promotedNames).Count -ne 0) {
        throw "Promoted package does not contain the canonical three files."
    }
    foreach ($name in @("EMKE.VirtualAudio.inf", "EMKE.VirtualAudio.sys")) {
        $returnedHash = (Get-FileHash `
            -LiteralPath (Join-Path $validPackage $name) `
            -Algorithm SHA256).Hash
        $promotedHash = (Get-FileHash `
            -LiteralPath (Join-Path $success.Output $name) `
            -Algorithm SHA256).Hash
        if ($promotedHash -cne $returnedHash) {
            throw "Promoted '$name' bytes changed."
        }
    }
    $record = Get-Content -LiteralPath $success.Evidence -Raw |
        ConvertFrom-Json
    $returnedCatalogHash = (Get-FileHash `
        -LiteralPath (Join-Path $validPackage "EMKE.VirtualAudio.cat") `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($record.portalSubmissionId -cne "submission-12345" -or
        $record.portalStatus -cne "signed" -or
        $record.promotionState -cne "committed" -or
        [string]$record.transactionId -cnotmatch "^[0-9a-f]{32}$" -or
        $record.sourceCommit -cne $sourceCommit -or
        $record.submittedCatalogSha256 -cne $submittedCatalogHash -or
        $record.returnedCatalogSha256 -cne $returnedCatalogHash -or
        [int]$record.verificationHostBuild -le 0 -or
        @($record.signerChain).Count -ne 3) {
        throw "Promotion evidence is incomplete or changed."
    }

    $writeFailure = Copy-CanonicalPackage -Name "evidence-write-failure"
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $writeFailure `
        -Name "evidence-write-failure" `
        -Pattern "simulated evidence write failure" `
        -EvidenceWriter {
            param([string]$Path, [string]$Content)
            [IO.File]::WriteAllText($Path, $Content)
            throw "simulated evidence write failure"
        }

    $publishFailure = Copy-CanonicalPackage -Name "evidence-publish-failure"
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $publishFailure `
        -Name "evidence-publish-failure" `
        -Pattern "simulated evidence publish failure" `
        -EvidencePublisher {
            param(
                [string]$StagingPath,
                [string]$FinalPath,
                [psobject]$State
            )
            [IO.File]::Copy($StagingPath, $FinalPath, $false)
            $State.EvidencePublished = $true
            throw "simulated evidence publish failure"
        }

    $commitFailure = Copy-CanonicalPackage -Name "evidence-commit-failure"
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $commitFailure `
        -Name "evidence-commit-failure" `
        -Pattern "simulated evidence commit failure" `
        -EvidenceCommitPublisher {
            param(
                [string]$StagingPath,
                [string]$FinalPath,
                [psobject]$State
            )
            [IO.File]::Move($StagingPath, $FinalPath, $true)
            $State.EvidenceCommitted = $true
            throw "simulated evidence commit failure"
        }

    $foreignPackage = Copy-CanonicalPackage -Name "foreign-evidence-package"
    $foreignOutput = Join-Path $temporaryRoot "foreign-evidence-output"
    $foreignEvidence = Join-Path $temporaryRoot "foreign-evidence.json"
    $foreignContent = '{"transactionId":"foreign","promotionState":"committed"}'
    Assert-Throws -Pattern "simulated concurrent publisher failure" -Action {
        Import-MicrosoftSignedDriverPackage `
            -SubmissionManifest $manifest `
            -ReturnedPackageDirectory $foreignPackage `
            -OutputDirectory $foreignOutput `
            -EvidencePath $foreignEvidence `
            -PortalSubmissionId "submission-12345" `
            -PortalStatus "signed" `
            -EvidencePublisher {
                param(
                    [string]$StagingPath,
                    [string]$FinalPath,
                    [psobject]$State
                )
                [IO.File]::WriteAllText($FinalPath, $foreignContent)
                throw "simulated concurrent publisher failure"
            }
    }
    if (Test-Path -LiteralPath $foreignOutput) {
        throw "Concurrent publisher failure created promoted output."
    }
    if ((Get-Content -LiteralPath $foreignEvidence -Raw) -cne
        $foreignContent) {
        throw "Failed transaction deleted or changed foreign evidence."
    }
    Remove-Item -LiteralPath $foreignEvidence -Force

    foreach ($name in $expectedNames) {
        $caseName = "mutated-after-trust-" +
            [IO.Path]::GetExtension($name).TrimStart(".")
        $mutationPackage = Copy-CanonicalPackage -Name $caseName
        $sourceHash = (Get-FileHash `
            -LiteralPath (Join-Path $mutationPackage $name) `
            -Algorithm SHA256).Hash
        $script:MutationAfterTrustName = $name
        Assert-ImportRejected `
            -Manifest $manifest `
            -ReturnedPackage $mutationPackage `
            -Name $caseName `
            -Pattern "baseline|snapshot|verification|changed"
        $script:MutationAfterTrustName = $null
        if ((Get-FileHash `
                -LiteralPath (Join-Path $mutationPackage $name) `
                -Algorithm SHA256).Hash -cne $sourceHash) {
            throw "Snapshot verification mutated source '$name'."
        }
    }

    $overlapOutput = Copy-CanonicalPackage -Name "overlap-output"
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $overlapOutput `
        -Name "overlap-output" `
        -Output (Join-Path $overlapOutput "promoted") `
        -Pattern "overlap|disjoint"

    $overlapEvidence = Copy-CanonicalPackage -Name "overlap-evidence"
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $overlapEvidence `
        -Name "overlap-evidence" `
        -Evidence (Join-Path $overlapEvidence "promotion-evidence.json") `
        -Pattern "overlap|disjoint"

    $reparseTarget = Join-Path $temporaryRoot "reparse-ancestor-target"
    New-Item -ItemType Directory -Path $reparseTarget | Out-Null
    $reparseReturned = Copy-CanonicalPackage -Name (
        "reparse-ancestor-target\returned"
    )
    $reparseLink = Join-Path $temporaryRoot "reparse-ancestor-link"
    New-Item `
        -ItemType SymbolicLink `
        -Path $reparseLink `
        -Target $reparseTarget | Out-Null
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage (Join-Path $reparseLink "returned") `
        -Name "reparse-returned-ancestor" `
        -Pattern "reparse|symbolic|path component"

    $manifestParentTarget = Join-Path $temporaryRoot "manifest-parent-target"
    New-Item -ItemType Directory -Path $manifestParentTarget | Out-Null
    Copy-Item `
        -LiteralPath $manifest `
        -Destination (Join-Path $manifestParentTarget "submission.json")
    $manifestParentLink = Join-Path $temporaryRoot "manifest-parent-link"
    New-Item `
        -ItemType SymbolicLink `
        -Path $manifestParentLink `
        -Target $manifestParentTarget | Out-Null
    $reparseManifestPackage = Copy-CanonicalPackage `
        -Name "reparse-manifest-package"
    Assert-ImportRejected `
        -Manifest (Join-Path $manifestParentLink "submission.json") `
        -ReturnedPackage $reparseManifestPackage `
        -Name "reparse-manifest-parent" `
        -Pattern "reparse|symbolic|path component"

    $outputParentTarget = Join-Path $temporaryRoot "output-parent-target"
    New-Item -ItemType Directory -Path $outputParentTarget | Out-Null
    $outputParentLink = Join-Path $temporaryRoot "output-parent-link"
    New-Item `
        -ItemType SymbolicLink `
        -Path $outputParentLink `
        -Target $outputParentTarget | Out-Null
    $reparseOutputPackage = Copy-CanonicalPackage -Name "reparse-output-package"
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $reparseOutputPackage `
        -Name "reparse-output-parent" `
        -Output (Join-Path $outputParentLink "promoted") `
        -Pattern "reparse|symbolic|path component"

    $evidenceParentTarget = Join-Path $temporaryRoot "evidence-parent-target"
    New-Item -ItemType Directory -Path $evidenceParentTarget | Out-Null
    $evidenceParentLink = Join-Path $temporaryRoot "evidence-parent-link"
    New-Item `
        -ItemType SymbolicLink `
        -Path $evidenceParentLink `
        -Target $evidenceParentTarget | Out-Null
    $reparseEvidencePackage = Copy-CanonicalPackage `
        -Name "reparse-evidence-package"
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $reparseEvidencePackage `
        -Name "reparse-evidence-parent" `
        -Evidence (Join-Path $evidenceParentLink "promotion.json") `
        -Pattern "reparse|symbolic|path component"

    $missing = Copy-CanonicalPackage -Name "missing"
    Remove-Item -LiteralPath (Join-Path $missing "EMKE.VirtualAudio.sys")
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $missing `
        -Name "missing" `
        -Pattern "exact|missing|inventory"

    $extra = Copy-CanonicalPackage -Name "extra"
    Set-Content -LiteralPath (Join-Path $extra "extra.pdb") -Value "forbidden"
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $extra `
        -Name "extra" `
        -Pattern "exact|extra|inventory"

    foreach ($name in @("EMKE.VirtualAudio.inf", "EMKE.VirtualAudio.sys")) {
        $caseName = "changed-" + [IO.Path]::GetExtension($name).TrimStart(".")
        $changed = Copy-CanonicalPackage -Name $caseName
        [IO.File]::AppendAllText((Join-Path $changed $name), "changed")
        Assert-ImportRejected `
            -Manifest $manifest `
            -ReturnedPackage $changed `
            -Name $caseName `
            -Pattern "hash|submitted|changed"
    }

    $referentDirectory = Join-Path $temporaryRoot "link-referents"
    New-Item -ItemType Directory -Path $referentDirectory | Out-Null
    foreach ($name in $expectedNames) {
        $caseName = "reparse-" + [IO.Path]::GetExtension($name).TrimStart(".")
        $linked = Copy-CanonicalPackage -Name $caseName
        $linkPath = Join-Path $linked $name
        $referent = Join-Path $referentDirectory $name
        Copy-Item -LiteralPath $linkPath -Destination $referent
        Remove-Item -LiteralPath $linkPath
        New-Item `
            -ItemType SymbolicLink `
            -Path $linkPath `
            -Target $referent | Out-Null
        $before = (Get-FileHash -LiteralPath $referent -Algorithm SHA256).Hash
        Assert-ImportRejected `
            -Manifest $manifest `
            -ReturnedPackage $linked `
            -Name $caseName `
            -Pattern "reparse|symbolic|real file"
        $after = (Get-FileHash -LiteralPath $referent -Algorithm SHA256).Hash
        if ($after -cne $before) {
            throw "Rejected reparse import changed '$name' referent bytes."
        }
    }

    $trustCases = @(
        [pscustomobject]@{
            Name = "expired"
            Evidence = New-TrustedEvidence -CertificateExpired $true
        },
        [pscustomobject]@{
            Name = "untrusted-chain"
            Evidence = New-TrustedEvidence -ChainValid $false
        },
        [pscustomobject]@{
            Name = "test-signer"
            Evidence = New-TrustedEvidence -SignerSubject "CN=EMKE Internal Test"
        },
        [pscustomobject]@{
            Name = "non-microsoft"
            Evidence = New-TrustedEvidence `
                -SignerSubject "CN=Other Hardware Publisher, O=Other Corporation"
        },
        [pscustomobject]@{
            Name = "kernel-policy-invalid"
            Evidence = New-TrustedEvidence -KernelPolicyValid $false
        }
    )
    foreach ($case in $trustCases) {
        $script:TrustEvidence = $case.Evidence
        $returned = Copy-CanonicalPackage -Name $case.Name
        Assert-ImportRejected `
            -Manifest $manifest `
            -ReturnedPackage $returned `
            -Name $case.Name `
            -Pattern "Microsoft|signer|chain|expired|kernel|trust"
    }

    $script:TrustEvidence = New-TrustedEvidence
    $changedCatalog = Copy-CanonicalPackage -Name "changed-catalog"
    [IO.File]::AppendAllText(
        (Join-Path $changedCatalog "EMKE.VirtualAudio.cat"),
        "changed"
    )
    Assert-ImportRejected `
        -Manifest $manifest `
        -ReturnedPackage $changedCatalog `
        -Name "changed-catalog" `
        -Pattern "catalog|member|reference|open"

    Write-Host (
        "Microsoft-signed driver import validation passed: canonical bytes; " +
        "exact hashes and catalog members; strict Microsoft trust; zero-output rejection."
    )
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
