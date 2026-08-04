[CmdletBinding(DefaultParameterSetName = "Setup")]
param(
    [Parameter(Mandatory, ParameterSetName = "Setup")]
    [string]$SetupPath,

    [Parameter(Mandatory, ParameterSetName = "Inventory")]
    [string]$InventoryRoot,

    [Parameter(Mandatory, ParameterSetName = "Inventory")]
    [string]$InventoryManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:PayloadNames = @(
    "EMKE-Translation-Windows-0.2.0-internal-x64.msix",
    "EMKE-Translation-Windows-0.2.0-internal-x64.cer",
    "EMKE.VirtualAudio.inf",
    "EMKE.VirtualAudio.sys",
    "EMKE.VirtualAudio.cat"
)
$script:TopLevelFields = @(
    "schemaVersion",
    "productVersion",
    "packageVersion",
    "channel",
    "architecture",
    "setupSourceCommit",
    "setupWorkflowRun",
    "setupSignerSubject",
    "payloads",
    "inventorySha256"
)
$script:PayloadFields = @(
    "logicalName",
    "fileName",
    "kind",
    "length",
    "sha256",
    "sourceCommit",
    "workflowRun",
    "signerSubject"
)
$script:SetupFileName =
    "EMKE-Translation-Setup-0.2.0-internal-x64.exe"
$script:RecoveryFileName =
    "EMKE-Translation-Setup-Recovery-0.2.0-internal-x64.exe"
$script:EngineeringZipFileName =
    "EMKE-Translation-Setup-Engineering-0.2.0-internal-x64.zip"
$script:AcceptedSignerThumbprint =
    "33E9992B08919BA6522F8A16B95CC2AA5DA6BB98"
$script:CandidateFiles = @(
    $script:SetupFileName,
    "SHA256SUMS.txt",
    "setup-provenance.json",
    $script:RecoveryFileName,
    $script:EngineeringZipFileName
)
$script:ProvenanceFields = @(
    "schemaVersion",
    "productVersion",
    "channel",
    "architecture",
    "setupSourceCommit",
    "setupWorkflowRun",
    "setupSignerSubject",
    "setupSignerThumbprint",
    "setupSha256",
    "recoverySha256",
    "payloadInventorySha256",
    "unknownPublisherBoundary",
    "payloads"
)

function Assert-NoReparsePathChain {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $cursor = $fullPath
    while ($true) {
        if (-not (Test-Path -LiteralPath $cursor)) {
            throw "Setup verification path is missing."
        }
        $item = Get-Item -LiteralPath $cursor -Force
        if (
            $null -ne $item.LinkType -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Setup verification rejects reparse points and links."
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) {
            break
        }
        $cursor = $parent.FullName
    }
    return $fullPath
}

function Assert-ExactFields {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) {
        throw "$Label field inventory is invalid."
    }
    for ($index = 0; $index -lt $Expected.Count; $index += 1) {
        if ($actual[$index] -cne $Expected[$index]) {
            throw "$Label fields are not canonical."
        }
    }
}

function Get-LowerSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.
        ToLowerInvariant()
}

function Get-TextSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Text
    )

    return [Convert]::ToHexStringLower(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($Text)
        )
    )
}

function Assert-CanonicalScalar {
    param(
        [Parameter(Mandatory)]
        [object]$Inventory
    )

    if (
        [int]$Inventory.schemaVersion -ne 1 -or
        $Inventory.productVersion -cne "0.2.0" -or
        $Inventory.packageVersion -cne "0.2.0.0" -or
        $Inventory.channel -cne "internal" -or
        $Inventory.architecture -cne "x64" -or
        $Inventory.setupSourceCommit -cnotmatch "^[0-9a-f]{40}$" -or
        $Inventory.setupWorkflowRun -cnotmatch "^[1-9][0-9]+$" -or
        $Inventory.setupSignerSubject -notin @(
            "CN=EMKE Internal Test",
            "UNSIGNED"
        ) -or
        $Inventory.inventorySha256 -cnotmatch "^[0-9a-f]{64}$"
    ) {
        throw "Setup inventory scalar contract is invalid."
    }
}

function Assert-SetupInventory {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$ManifestPath
    )

    $resolvedRoot = Assert-NoReparsePathChain -Path $Root
    $resolvedManifest = Assert-NoReparsePathChain -Path $ManifestPath
    if (
        -not (Test-Path -LiteralPath $resolvedRoot -PathType Container) -or
        -not (Test-Path -LiteralPath $resolvedManifest -PathType Leaf)
    ) {
        throw "Setup inventory paths are invalid."
    }
    if (@(Get-ChildItem -LiteralPath $resolvedRoot -Directory -Force).Count -ne 0) {
        throw "Setup payload inventory cannot contain nested directories."
    }
    $actualFiles = @(
        Get-ChildItem -LiteralPath $resolvedRoot -File -Force |
            Sort-Object -Property Name |
            ForEach-Object { $_.Name }
    )
    $expectedSorted = @($script:PayloadNames | Sort-Object)
    if (
        $actualFiles.Count -ne $expectedSorted.Count -or
        @(Compare-Object $actualFiles $expectedSorted -CaseSensitive).Count -ne 0
    ) {
        throw "Setup payload inventory contains missing, extra, or forbidden files."
    }

    $raw = [IO.File]::ReadAllText($resolvedManifest)
    $inventory = $raw | ConvertFrom-Json
    Assert-ExactFields `
        -Value $inventory `
        -Expected $script:TopLevelFields `
        -Label "Setup inventory"
    Assert-CanonicalScalar -Inventory $inventory
    if (@($inventory.payloads).Count -ne $script:PayloadNames.Count) {
        throw "Setup payload inventory count is invalid."
    }

    $expectedLogicalNames = @(
        "application-msix",
        "application-certificate",
        "driver-inf",
        "driver-sys",
        "driver-catalog"
    )
    $expectedKinds = @(
        "msix",
        "certificate",
        "driverInf",
        "driverSys",
        "driverCatalog"
    )
    for ($index = 0; $index -lt $script:PayloadNames.Count; $index += 1) {
        $payload = $inventory.payloads[$index]
        Assert-ExactFields `
            -Value $payload `
            -Expected $script:PayloadFields `
            -Label "Setup payload"
        if (
            $payload.logicalName -cne $expectedLogicalNames[$index] -or
            $payload.fileName -cne $script:PayloadNames[$index] -or
            $payload.kind -cne $expectedKinds[$index] -or
            [long]$payload.length -le 0 -or
            $payload.sha256 -cnotmatch "^[0-9a-f]{64}$" -or
            $payload.sourceCommit -cnotmatch "^[0-9a-f]{40}$" -or
            $payload.workflowRun -cnotmatch "^[1-9][0-9]+$" -or
            [string]::IsNullOrWhiteSpace([string]$payload.signerSubject)
        ) {
            throw "Setup payload manifest identity or provenance is invalid."
        }
        $path = Assert-NoReparsePathChain -Path (
            Join-Path $resolvedRoot $payload.fileName
        )
        $item = Get-Item -LiteralPath $path -Force
        if (
            [long]$item.Length -ne [long]$payload.length -or
            (Get-LowerSha256 -Path $path) -cne $payload.sha256
        ) {
            throw "Setup payload length or hash verification failed."
        }
    }

    $unsigned = [ordered]@{
        schemaVersion = [int]$inventory.schemaVersion
        productVersion = [string]$inventory.productVersion
        packageVersion = [string]$inventory.packageVersion
        channel = [string]$inventory.channel
        architecture = [string]$inventory.architecture
        setupSourceCommit = [string]$inventory.setupSourceCommit
        setupWorkflowRun = [string]$inventory.setupWorkflowRun
        setupSignerSubject = [string]$inventory.setupSignerSubject
        payloads = @(
            foreach ($payload in $inventory.payloads) {
                [ordered]@{
                    logicalName = [string]$payload.logicalName
                    fileName = [string]$payload.fileName
                    kind = [string]$payload.kind
                    length = [long]$payload.length
                    sha256 = [string]$payload.sha256
                    sourceCommit = [string]$payload.sourceCommit
                    workflowRun = [string]$payload.workflowRun
                    signerSubject = [string]$payload.signerSubject
                }
            }
        )
    }
    $unsignedJson = $unsigned | ConvertTo-Json -Depth 8 -Compress
    if ((Get-TextSha256 -Text $unsignedJson) -cne $inventory.inventorySha256) {
        throw "Setup inventory manifest or provenance digest is invalid."
    }
    $canonical = [ordered]@{}
    foreach ($entry in $unsigned.GetEnumerator()) {
        $canonical[$entry.Key] = $entry.Value
    }
    $canonical.inventorySha256 = [string]$inventory.inventorySha256
    $canonicalJson = $canonical | ConvertTo-Json -Depth 8 -Compress
    if ($raw -cne $canonicalJson) {
        throw "Setup inventory manifest is not canonical."
    }
}

function Resolve-SignTool {
    $kitsRoot = (
        Get-ItemProperty `
            -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" `
            -ErrorAction Stop
    ).KitsRoot10
    $matches = @(
        Get-ChildItem -LiteralPath (Join-Path $kitsRoot "bin") -Directory |
            Where-Object {
                [version]$parsed = [version]::new()
                [version]::TryParse($_.Name, [ref]$parsed)
            } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object {
                $candidate = Join-Path $_.FullName "x64/SignTool.exe"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $candidate
                }
            }
    )
    if ($matches.Count -eq 0) {
        throw "Windows SDK SignTool.exe is unavailable."
    }
    return $matches[0]
}

function Assert-PinnedSetupSignature {
    param([Parameter(Mandatory)][string]$Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -cne "CN=EMKE Internal Test" -or
        $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne
            $script:AcceptedSignerThumbprint -or
        $signature.Status -in @(
            [Management.Automation.SignatureStatus]::HashMismatch,
            [Management.Automation.SignatureStatus]::NotSigned
        )
    ) {
        throw "Setup Authenticode signature is invalid."
    }
    return $signature.SignerCertificate
}

if ($PSCmdlet.ParameterSetName -ceq "Inventory") {
    Assert-SetupInventory `
        -Root $InventoryRoot `
        -ManifestPath $InventoryManifestPath
    Write-Output "Setup inventory verified."
    return
}

$resolvedSetup = Assert-NoReparsePathChain -Path $SetupPath
if (
    -not (Test-Path -LiteralPath $resolvedSetup -PathType Leaf) -or
    [IO.Path]::GetFileName($resolvedSetup) -cne
        $script:SetupFileName
) {
    throw "The exact Setup EXE is unavailable."
}
$candidateRoot = [IO.Directory]::GetParent($resolvedSetup).FullName
$signer = Assert-PinnedSetupSignature -Path $resolvedSetup
$actualFiles = @(
    Get-ChildItem -LiteralPath $candidateRoot -File -Force |
        Sort-Object Name |
        ForEach-Object { $_.Name }
)
$expectedFiles = @($script:CandidateFiles | Sort-Object)
if (
    @(Get-ChildItem -LiteralPath $candidateRoot -Directory -Force).Count -ne 0 -or
    $actualFiles.Count -ne $expectedFiles.Count -or
    @(Compare-Object $actualFiles $expectedFiles -CaseSensitive).Count -ne 0
) {
    throw "The Setup candidate inventory is not the exact five-file handoff."
}
$recoveryPath = Assert-NoReparsePathChain -Path (
    Join-Path $candidateRoot $script:RecoveryFileName
)
$recoverySigner = Assert-PinnedSetupSignature -Path $recoveryPath
if ($recoverySigner.Thumbprint -cne $signer.Thumbprint) {
    throw "The Setup recovery helper signer changed."
}

$addedTrust = $false
$store = [Security.Cryptography.X509Certificates.X509Store]::new(
    [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
    [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
)
try {
    $store.Open(
        [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
    )
    $existing = @(
        $store.Certificates |
            Where-Object {
                $_.Thumbprint.ToUpperInvariant() -ceq
                    $script:AcceptedSignerThumbprint
            }
    )
    if ($existing.Count -eq 0) {
        $store.Add($signer)
        $addedTrust = $true
    } elseif ($existing.Count -ne 1) {
        throw "The temporary Setup trust state is ambiguous."
    }
    $signTool = Resolve-SignTool
    foreach ($signedPath in @($resolvedSetup, $recoveryPath)) {
        & $signTool verify /pa /all /v $signedPath | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Setup Authenticode signature is invalid."
        }
        if (
            (Get-AuthenticodeSignature -LiteralPath $signedPath).Status -ne
                [Management.Automation.SignatureStatus]::Valid
        ) {
            throw "Setup Authenticode trust verification failed."
        }
    }
} finally {
    if ($addedTrust) {
        $store.Remove($signer)
    }
    $store.Close()
    $store.Dispose()
}

$setupHash = Get-LowerSha256 -Path $resolvedSetup
$recoveryHash = Get-LowerSha256 -Path $recoveryPath
if ($setupHash -cne $recoveryHash) {
    throw "The recovery helper is not the exact signed Setup executable."
}
$hashPath = Join-Path $candidateRoot "SHA256SUMS.txt"
$provenancePath = Join-Path $candidateRoot "setup-provenance.json"
$zipPath = Join-Path $candidateRoot $script:EngineeringZipFileName
$provenanceHash = Get-LowerSha256 -Path $provenancePath
$zipHash = Get-LowerSha256 -Path $zipPath
$expectedHashText = (
    $setupHash.ToUpperInvariant() + "  " + $script:SetupFileName + "`r`n" +
    $recoveryHash.ToUpperInvariant() + "  " + $script:RecoveryFileName + "`r`n" +
    $provenanceHash.ToUpperInvariant() + "  setup-provenance.json`r`n" +
    $zipHash.ToUpperInvariant() + "  " + $script:EngineeringZipFileName + "`r`n"
)
if ([IO.File]::ReadAllText($hashPath) -cne $expectedHashText) {
    throw "The Setup SHA256SUMS contract is invalid."
}

$provenanceRaw = [IO.File]::ReadAllText($provenancePath)
$provenance = $provenanceRaw | ConvertFrom-Json
Assert-ExactFields `
    -Value $provenance `
    -Expected $script:ProvenanceFields `
    -Label "Setup provenance"
if (
    [int]$provenance.schemaVersion -ne 1 -or
    $provenance.productVersion -cne "0.2.0" -or
    $provenance.channel -cne "internal" -or
    $provenance.architecture -cne "x64" -or
    $provenance.setupSourceCommit -cnotmatch "^[0-9a-f]{40}$" -or
    $provenance.setupWorkflowRun -cnotmatch "^[1-9][0-9]+$" -or
    $provenance.setupSignerSubject -cne "CN=EMKE Internal Test" -or
    $provenance.setupSignerThumbprint -cne
        $script:AcceptedSignerThumbprint -or
    $provenance.setupSha256 -cne $setupHash -or
    $provenance.recoverySha256 -cne $recoveryHash -or
    $provenance.payloadInventorySha256 -cnotmatch "^[0-9a-f]{64}$" -or
    $provenance.unknownPublisherBoundary -ne $true -or
    @($provenance.payloads).Count -ne 5
) {
    throw "The Setup provenance contract is invalid."
}

$selfCheck = @(& $resolvedSetup --verify-self-v1)
if ($LASTEXITCODE -ne 0 -or $selfCheck.Count -ne 1) {
    throw "The exact Setup executable self-check failed."
}
$selfEvidence = $selfCheck[0] | ConvertFrom-Json
if (
    $selfEvidence.status -cne "verified" -or
    [int]$selfEvidence.payloadCount -ne 5 -or
    $selfEvidence.inventorySha256 -cne
        $provenance.payloadInventorySha256
) {
    throw "The embedded Setup inventory evidence changed."
}

Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($zip.Entries | ForEach-Object { $_.FullName })
    if (
        $entryNames.Count -ne 2 -or
        @(Compare-Object `
            ($entryNames | Sort-Object) `
            (@("README.txt", "setup-provenance.json") | Sort-Object) `
            -CaseSensitive).Count -ne 0
    ) {
        throw "The diagnostic engineering ZIP inventory is invalid."
    }
    $provenanceEntry = $zip.GetEntry("setup-provenance.json")
    $reader = [IO.StreamReader]::new(
        $provenanceEntry.Open(),
        [Text.UTF8Encoding]::new($false, $true)
    )
    try {
        if ($reader.ReadToEnd() -cne $provenanceRaw) {
            throw "The engineering ZIP provenance copy changed."
        }
    } finally {
        $reader.Dispose()
    }
} finally {
    $zip.Dispose()
}

Write-Output "Setup Authenticode, hashes, provenance, resources, and handoff verified."
