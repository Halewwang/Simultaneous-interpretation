[CmdletBinding()]
param(
    [string]$SubmissionManifest,
    [string]$ReturnedPackageDirectory,
    [string]$OutputDirectory,
    [string]$EvidencePath,
    [string]$PortalSubmissionId,
    [string]$PortalStatus
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:PackageFileNames = @(
    "EMKE.VirtualAudio.inf",
    "EMKE.VirtualAudio.sys",
    "EMKE.VirtualAudio.cat"
)

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
        throw "$Description must be a real directory, not a reparse point: $Path"
    }
    return $item.FullName
}

function Assert-RealFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must be a real file, not a symbolic link or reparse point: $Path"
    }
    return $item.FullName
}

function Assert-NoReparsePathComponents {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $current = [IO.Path]::GetFullPath($Path)
    while (-not (Test-Path -LiteralPath $current)) {
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) {
            break
        }
        $current = $parent.FullName
    }
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description contains a symbolic link or reparse path component: $current"
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) {
            break
        }
        $current = $parent.FullName
    }
}

function Get-ComparableFullPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Length -gt $root.Length) {
        return $fullPath.TrimEnd([char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        ))
    }
    return $fullPath
}

function Test-PathsOverlap {
    param(
        [Parameter(Mandatory)]
        [string]$Left,

        [Parameter(Mandatory)]
        [string]$Right
    )

    $leftPath = Get-ComparableFullPath -Path $Left
    $rightPath = Get-ComparableFullPath -Path $Right
    if ($leftPath.Equals($rightPath, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $separator = [IO.Path]::DirectorySeparatorChar
    return $leftPath.StartsWith(
            $rightPath + $separator,
            [StringComparison]::OrdinalIgnoreCase) -or
        $rightPath.StartsWith(
            $leftPath + $separator,
            [StringComparison]::OrdinalIgnoreCase)
}

function Assert-DisjointPromotionPaths {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [Parameter(Mandatory)]
        [string]$ReturnedPackagePath,

        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [string]$EvidenceFile
    )

    $paths = @(
        [pscustomobject]@{ Name = "manifest"; Path = $ManifestPath },
        [pscustomobject]@{ Name = "returned package"; Path = $ReturnedPackagePath },
        [pscustomobject]@{ Name = "promoted output"; Path = $OutputPath },
        [pscustomobject]@{ Name = "promotion evidence"; Path = $EvidenceFile }
    )
    for ($left = 0; $left -lt $paths.Count; $left++) {
        for ($right = $left + 1; $right -lt $paths.Count; $right++) {
            if (Test-PathsOverlap `
                    -Left $paths[$left].Path `
                    -Right $paths[$right].Path) {
                throw (
                    "Promotion paths must be disjoint; " +
                    "$($paths[$left].Name) overlaps $($paths[$right].Name)."
                )
            }
        }
    }
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

function Get-JsonString {
    param(
        [Parameter(Mandatory)]
        [psobject]$Property,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $value = $Property.Value
    if ($null -eq $value -or
        $value.PSObject.BaseObject -isnot [string]) {
        throw "$Description must be a JSON string."
    }
    return $value.PSObject.BaseObject
}

function Get-JsonInteger {
    param(
        [Parameter(Mandatory)]
        [psobject]$Property,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $value = $Property.Value
    if ($null -eq $value) {
        throw "$Description must be a JSON integer."
    }
    $baseValue = $value.PSObject.BaseObject
    if ($baseValue -isnot [int] -and $baseValue -isnot [long]) {
        throw "$Description must be a JSON integer."
    }
    return [long]$baseValue
}

function Get-LowercaseSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ExactPackageInventory {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [bool]$CaseSensitive,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $directories = @(Get-ChildItem -LiteralPath $Directory -Directory -Force)
    if ($directories.Count -ne 0) {
        throw "$Description must be flat; nested directories are forbidden."
    }
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Force)
    if ($files.Count -ne $script:PackageFileNames.Count) {
        throw "$Description must contain the exact immutable inventory."
    }
    foreach ($expectedName in $script:PackageFileNames) {
        [object[]]$matches = @($files | Where-Object {
            if ($CaseSensitive) {
                $_.Name -ceq $expectedName
            } else {
                $_.Name -ieq $expectedName
            }
        })
        if ($matches.Count -ne 1) {
            throw "$Description must contain the exact immutable inventory."
        }
        Assert-RealFile `
            -Path $matches[0].FullName `
            -Description "$Description file '$expectedName'" | Out-Null
    }
}

function Read-SubmissionManifest {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $manifestPath = Assert-RealFile `
        -Path $Path `
        -Description "Driver submission manifest"
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

    $sourceCommit = Get-JsonString `
        -Property $manifest.PSObject.Properties["sourceCommit"] `
        -Description "Driver submission source commit"
    $driverVersion = Get-JsonString `
        -Property $manifest.PSObject.Properties["driverVersion"] `
        -Description "Driver submission driver version"
    $driverAbiVersion = Get-JsonInteger `
        -Property $manifest.PSObject.Properties["driverAbiVersion"] `
        -Description "Driver submission ABI version"
    $minimumWindowsBuild = Get-JsonInteger `
        -Property $manifest.PSObject.Properties["minimumWindowsBuild"] `
        -Description "Driver submission minimum Windows build"
    $kmdfLibraryVersion = Get-JsonString `
        -Property $manifest.PSObject.Properties["kmdfLibraryVersion"] `
        -Description "Driver submission KMDF library version"
    if ($sourceCommit -cnotmatch "^[0-9a-f]{40}$" -or
        $driverVersion -cne "1.0.0.2" -or
        $driverAbiVersion -ne 1 -or
        $minimumWindowsBuild -ne 19045 -or
        $kmdfLibraryVersion -cne "1.31") {
        throw "Driver submission manifest release metadata is invalid."
    }

    $filesValue = $manifest.PSObject.Properties["files"].Value
    if ($null -eq $filesValue -or
        $filesValue.PSObject.BaseObject -isnot [Array]) {
        throw "Driver submission manifest files must be a JSON array."
    }
    [object[]]$files = @($filesValue.PSObject.BaseObject)
    if ($files.Count -ne $script:PackageFileNames.Count) {
        throw "Driver submission manifest must list exactly three package files."
    }

    $hashes = [ordered]@{}
    foreach ($file in $files) {
        Assert-ExactPropertyNames `
            -Object $file `
            -Expected @("name", "sha256") `
            -Description "Driver submission file entry"
        $name = Get-JsonString `
            -Property $file.PSObject.Properties["name"] `
            -Description "Driver submission file name"
        $hash = Get-JsonString `
            -Property $file.PSObject.Properties["sha256"] `
            -Description "Driver submission file SHA-256"
        if ($name -cnotin $script:PackageFileNames -or
            $hash -cnotmatch "^[0-9a-f]{64}$" -or
            $hashes.Contains($name)) {
            throw "Driver submission file inventory or hash is invalid."
        }
        $hashes[$name] = $hash
    }
    foreach ($name in $script:PackageFileNames) {
        if (-not $hashes.Contains($name)) {
            throw "Driver submission file inventory is incomplete."
        }
    }

    return [pscustomobject]@{
        SourceCommit = $sourceCommit
        Hashes = $hashes
    }
}

function Test-MicrosoftPublisherSubject {
    param(
        [Parameter(Mandatory)]
        [string]$Subject
    )

    $components = @($Subject.Split(',') | ForEach-Object Trim)
    return @($components | Where-Object {
            $_ -ieq "CN=Microsoft Windows Hardware Compatibility Publisher"
        }).Count -eq 1 -and
        @($components | Where-Object {
            $_ -ieq "O=Microsoft Corporation"
        }).Count -eq 1
}

function Resolve-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    [object[]]$matches = @(
        Get-ChildItem `
            -LiteralPath $kitsRoot `
            -Filter signtool.exe `
            -File `
            -Recurse `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -ieq "x64" } |
            Sort-Object FullName -Descending
    )
    if ($matches.Count -eq 0) {
        throw "signtool.exe is required for kernel policy verification."
    }
    return $matches[0].FullName
}

function Test-CatalogKernelPolicy {
    param(
        [Parameter(Mandatory)]
        [string]$CatalogPath
    )

    $signTool = Resolve-SignTool
    & $signTool verify /kp /all /v $CatalogPath | Out-Host
    return $LASTEXITCODE -eq 0
}

function Get-MicrosoftCatalogTrustEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$CatalogPath
    )

    $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature `
        -LiteralPath $CatalogPath
    $certificate = $signature.SignerCertificate
    if ($null -eq $certificate) {
        throw "Returned catalog has no signing certificate."
    }

    $now = [DateTimeOffset]::Now
    $expired = $now -lt $certificate.NotBefore -or
        $now -gt $certificate.NotAfter
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode =
            [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag =
            [Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
        $chain.ChainPolicy.VerificationFlags =
            [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.VerificationTime = [DateTime]::Now
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(30)
        $chain.ChainPolicy.DisableCertificateDownloads = $false
        $chainValid = $chain.Build($certificate)
        [string[]]$chainSubjects = @(
            $chain.ChainElements |
                ForEach-Object { $_.Certificate.Subject }
        )
    } finally {
        $chain.Dispose()
    }

    return [pscustomobject]@{
        Status = [string]$signature.Status
        SignerSubject = $certificate.Subject
        ChainValid = $chainValid
        CertificateExpired = $expired
        KernelPolicyValid = Test-CatalogKernelPolicy -CatalogPath $CatalogPath
        ChainSubjects = $chainSubjects
    }
}

function Assert-CatalogMembership {
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory
    )

    $verifier = Join-Path $PSScriptRoot "verify-driver-package.ps1"
    & $verifier $PackageDirectory
}

function Assert-TrustedMicrosoftCatalog {
    param(
        [Parameter(Mandatory)]
        [psobject]$TrustEvidence
    )

    if ([string]$TrustEvidence.Status -cne "Valid") {
        throw "Returned catalog signature is not valid."
    }
    if (-not [bool]$TrustEvidence.ChainValid) {
        throw "Returned catalog signer chain is untrusted."
    }
    if ([bool]$TrustEvidence.CertificateExpired) {
        throw "Returned catalog signer certificate is expired or not yet valid."
    }
    if (-not [bool]$TrustEvidence.KernelPolicyValid) {
        throw "Returned catalog does not satisfy Windows kernel signing policy."
    }
    $signerSubject = [string]$TrustEvidence.SignerSubject
    if (-not (Test-MicrosoftPublisherSubject -Subject $signerSubject)) {
        throw "Returned catalog signer is not the Microsoft hardware publisher."
    }
    [string[]]$chainSubjects = @($TrustEvidence.ChainSubjects)
    if ($chainSubjects.Count -eq 0 -or
        @($chainSubjects | Where-Object {
            [string]::IsNullOrWhiteSpace($_)
        }).Count -ne 0) {
        throw "Returned catalog signer chain evidence is incomplete."
    }
}

function Assert-SafeEvidenceText {
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value.Length -gt 128 -or
        $Value -match "[\x00-\x1F\x7F]") {
        throw "$Description is missing or invalid."
    }
}

function Write-PromotionEvidenceStaging {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Content
    )

    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content)
    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None
    )
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
}

function Publish-PromotionEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$StagingPath,

        [Parameter(Mandatory)]
        [string]$FinalPath,

        [Parameter(Mandatory)]
        [psobject]$State
    )

    [IO.File]::Move($StagingPath, $FinalPath)
    $State.EvidencePublished = $true
}

function Commit-PromotionEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$StagingPath,

        [Parameter(Mandatory)]
        [string]$FinalPath,

        [Parameter(Mandatory)]
        [psobject]$State
    )

    [IO.File]::Move($StagingPath, $FinalPath, $true)
    $State.EvidenceCommitted = $true
}

function Import-MicrosoftSignedDriverPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SubmissionManifest,

        [Parameter(Mandatory)]
        [string]$ReturnedPackageDirectory,

        [Parameter(Mandatory)]
        [string]$OutputDirectory,

        [Parameter(Mandatory)]
        [string]$EvidencePath,

        [Parameter(Mandatory)]
        [string]$PortalSubmissionId,

        [Parameter(Mandatory)]
        [string]$PortalStatus,

        [scriptblock]$EvidenceWriter,

        [scriptblock]$EvidencePublisher,

        [scriptblock]$EvidenceCommitPublisher
    )

    if (-not $IsWindows) {
        throw "Microsoft-signed driver import requires Windows catalog APIs."
    }
    Assert-SafeEvidenceText `
        -Value $PortalSubmissionId `
        -Description "Portal submission ID"
    Assert-SafeEvidenceText `
        -Value $PortalStatus `
        -Description "Portal status"

    if ($null -eq $EvidenceWriter) {
        $EvidenceWriter = ${function:Write-PromotionEvidenceStaging}
    }
    if ($null -eq $EvidencePublisher) {
        $EvidencePublisher = ${function:Publish-PromotionEvidence}
    }
    if ($null -eq $EvidenceCommitPublisher) {
        $EvidenceCommitPublisher = ${function:Commit-PromotionEvidence}
    }

    $manifestCandidate = [IO.Path]::GetFullPath($SubmissionManifest)
    Assert-NoReparsePathComponents `
        -Path $manifestCandidate `
        -Description "Driver submission manifest"
    $manifestPath = Assert-RealFile `
        -Path $manifestCandidate `
        -Description "Driver submission manifest"
    $returnedCandidate = [IO.Path]::GetFullPath($ReturnedPackageDirectory)
    Assert-NoReparsePathComponents `
        -Path $returnedCandidate `
        -Description "Returned driver package"
    $returnedPath = Assert-RealDirectory `
        -Path $returnedCandidate `
        -Description "Returned driver package"
    $outputPath = [IO.Path]::GetFullPath($OutputDirectory)
    $evidenceFile = [IO.Path]::GetFullPath($EvidencePath)
    Assert-DisjointPromotionPaths `
        -ManifestPath $manifestPath `
        -ReturnedPackagePath $returnedPath `
        -OutputPath $outputPath `
        -EvidenceFile $evidenceFile
    if (Test-Path -LiteralPath $outputPath) {
        throw "Promoted output must not already exist: $outputPath"
    }
    if (Test-Path -LiteralPath $evidenceFile) {
        throw "Promotion evidence must not already exist: $evidenceFile"
    }
    $outputParent = Split-Path -Parent $outputPath
    $evidenceParent = Split-Path -Parent $evidenceFile
    if ([string]::IsNullOrWhiteSpace($outputParent) -or
        [string]::IsNullOrWhiteSpace($evidenceParent)) {
        throw "Promotion output and evidence must have parent directories."
    }
    $outputParent = Assert-RealDirectory `
        -Path $outputParent `
        -Description "Promoted output parent"
    $evidenceParent = Assert-RealDirectory `
        -Path $evidenceParent `
        -Description "Promotion evidence parent"
    Assert-NoReparsePathComponents `
        -Path $outputParent `
        -Description "Promoted output parent"
    Assert-NoReparsePathComponents `
        -Path $evidenceParent `
        -Description "Promotion evidence parent"

    $manifest = Read-SubmissionManifest -Path $manifestPath
    $snapshotPath = Join-Path `
        $outputParent `
        (".emke-driver-snapshot-" + [guid]::NewGuid().ToString("N"))
    $transactionId = [guid]::NewGuid().ToString("N")
    $pendingEvidenceStaging = Join-Path `
        $evidenceParent `
        (".emke-evidence-staging-pending-" + $transactionId + ".json")
    $committedEvidenceStaging = Join-Path `
        $evidenceParent `
        (".emke-evidence-staging-committed-" + $transactionId + ".json")
    $publishState = [pscustomobject]@{
        TransactionId = $transactionId
        EvidencePublished = $false
        EvidenceCommitted = $false
    }
    $ownsOutput = $false
    try {
        New-Item -ItemType Directory -Path $snapshotPath | Out-Null
        Assert-NoReparsePathComponents `
            -Path $snapshotPath `
            -Description "Protected driver snapshot"
        [object[]]$sourceEntries = @(
            Get-ChildItem -LiteralPath $returnedPath -Force
        )
        foreach ($entry in $sourceEntries) {
            if ($entry.PSIsContainer) {
                throw "Returned driver package must be flat."
            }
            Assert-RealFile `
                -Path $entry.FullName `
                -Description "Returned driver package file '$($entry.Name)'" |
                Out-Null
            [object[]]$canonicalMatches = @(
                $script:PackageFileNames | Where-Object {
                    $_ -ieq $entry.Name
                }
            )
            $destinationName = if ($canonicalMatches.Count -eq 1) {
                $canonicalMatches[0]
            } else {
                $entry.Name
            }
            [IO.File]::Copy(
                $entry.FullName,
                (Join-Path $snapshotPath $destinationName),
                $false
            )
        }
        Assert-ExactPackageInventory `
            -Directory $snapshotPath `
            -CaseSensitive $true `
            -Description "Protected driver snapshot"

        $snapshotFiles = @{}
        $snapshotBaselineHashes = @{}
        foreach ($name in $script:PackageFileNames) {
            $snapshotFiles[$name] = Join-Path $snapshotPath $name
            $snapshotBaselineHashes[$name] = Get-LowercaseSha256 `
                -Path $snapshotFiles[$name]
        }
        foreach ($name in @("EMKE.VirtualAudio.inf", "EMKE.VirtualAudio.sys")) {
            if ($snapshotBaselineHashes[$name] -cne $manifest.Hashes[$name]) {
                throw "Returned '$name' hash changed from the submitted bytes."
            }
        }

        Assert-CatalogMembership -PackageDirectory $snapshotPath
        $catalogPath = $snapshotFiles["EMKE.VirtualAudio.cat"]
        $trust = Get-MicrosoftCatalogTrustEvidence -CatalogPath $catalogPath
        Assert-TrustedMicrosoftCatalog -TrustEvidence $trust

        foreach ($name in $script:PackageFileNames) {
            $verifiedHash = Get-LowercaseSha256 -Path $snapshotFiles[$name]
            if ($verifiedHash -cne $snapshotBaselineHashes[$name]) {
                throw "Protected snapshot '$name' changed during verification."
            }
        }

        $promotedFiles = @()
        foreach ($name in $script:PackageFileNames) {
            $promotedFiles += [ordered]@{
                name = $name
                sha256 = $snapshotBaselineHashes[$name]
            }
        }

        $hostBuild = [Environment]::OSVersion.Version.Build
        if ($hostBuild -le 0) {
            throw "Verification host build is unavailable."
        }
        $record = [ordered]@{
            schemaVersion = 2
            transactionId = $transactionId
            promotionState = "pending"
            portalSubmissionId = $PortalSubmissionId
            portalStatus = $PortalStatus
            sourceCommit = $manifest.SourceCommit
            verificationHostBuild = $hostBuild
            signerSubject = [string]$trust.SignerSubject
            signerChain = @($trust.ChainSubjects)
            submittedCatalogSha256 = $manifest.Hashes["EMKE.VirtualAudio.cat"]
            returnedCatalogSha256 =
                $snapshotBaselineHashes["EMKE.VirtualAudio.cat"]
            files = $promotedFiles
        }
        & $EvidenceWriter `
            -Path $pendingEvidenceStaging `
            -Content ($record | ConvertTo-Json -Depth 6)
        & $EvidencePublisher `
            -StagingPath $pendingEvidenceStaging `
            -FinalPath $evidenceFile `
            -State $publishState
        [IO.Directory]::Move($snapshotPath, $outputPath)
        $ownsOutput = $true
        Assert-ExactPackageInventory `
            -Directory $outputPath `
            -CaseSensitive $true `
            -Description "Promoted driver package"
        foreach ($name in $script:PackageFileNames) {
            $promotedHash = Get-LowercaseSha256 `
                -Path (Join-Path $outputPath $name)
            if ($promotedHash -cne $snapshotBaselineHashes[$name]) {
                throw "Promoted '$name' bytes changed during publication."
            }
        }
        $record["promotionState"] = "committed"
        & $EvidenceWriter `
            -Path $committedEvidenceStaging `
            -Content ($record | ConvertTo-Json -Depth 6)
        & $EvidenceCommitPublisher `
            -StagingPath $committedEvidenceStaging `
            -FinalPath $evidenceFile `
            -State $publishState
        if (-not $publishState.EvidenceCommitted) {
            throw "Promotion evidence did not reach committed state."
        }
    } catch {
        $failure = $_
        if (($publishState.EvidencePublished -or
                $publishState.EvidenceCommitted) -and
            (Test-Path -LiteralPath $evidenceFile)) {
            Remove-Item -LiteralPath $evidenceFile -Force
        }
        if ($ownsOutput -and (Test-Path -LiteralPath $outputPath)) {
            Remove-Item -LiteralPath $outputPath -Recurse -Force
        }
        throw $failure
    } finally {
        if (Test-Path -LiteralPath $pendingEvidenceStaging) {
            Remove-Item -LiteralPath $pendingEvidenceStaging -Force
        }
        if (Test-Path -LiteralPath $committedEvidenceStaging) {
            Remove-Item -LiteralPath $committedEvidenceStaging -Force
        }
        if (Test-Path -LiteralPath $snapshotPath) {
            Remove-Item -LiteralPath $snapshotPath -Recurse -Force
        }
    }

    Write-Host "Microsoft-signed driver package promoted: $outputPath"
    Write-Host "Promotion evidence: $evidenceFile"
    Write-Host "Portal submission: $PortalSubmissionId ($PortalStatus)"
}

if ($MyInvocation.InvocationName -cne ".") {
    foreach ($required in @{
        SubmissionManifest = $SubmissionManifest
        ReturnedPackageDirectory = $ReturnedPackageDirectory
        OutputDirectory = $OutputDirectory
        EvidencePath = $EvidencePath
        PortalSubmissionId = $PortalSubmissionId
        PortalStatus = $PortalStatus
    }.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$required.Value)) {
            throw "Required importer argument is missing: $($required.Key)"
        }
    }
    Import-MicrosoftSignedDriverPackage `
        -SubmissionManifest $SubmissionManifest `
        -ReturnedPackageDirectory $ReturnedPackageDirectory `
        -OutputDirectory $OutputDirectory `
        -EvidencePath $EvidencePath `
        -PortalSubmissionId $PortalSubmissionId `
        -PortalStatus $PortalStatus
}
