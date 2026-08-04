[CmdletBinding(DefaultParameterSetName = "Package")]
param(
    [Parameter(Mandatory, ParameterSetName = "Package")]
    [string]$MsixPath,

    [Parameter(Mandatory, ParameterSetName = "Package")]
    [string]$DriverDirectory,

    [Parameter(Mandatory, ParameterSetName = "Package")]
    [string]$CertificatePath,

    [Parameter(Mandatory, ParameterSetName = "Package")]
    [string]$CandidateRoot,

    [Parameter(ParameterSetName = "Package")]
    [string]$PfxPath,

    [Parameter(ParameterSetName = "Package")]
    [ValidatePattern("^[A-Za-z_][A-Za-z0-9_]*$")]
    [string]$PasswordEnvironmentVariable,

    [Parameter(Mandatory, ParameterSetName = "Inventory")]
    [switch]$CreateInventoryOnly,

    [Parameter(Mandatory, ParameterSetName = "Inventory")]
    [string]$PayloadRoot,

    [Parameter(Mandatory, ParameterSetName = "Inventory")]
    [string]$InventoryPath,

    [Parameter(Mandatory)]
    [ValidatePattern("^[0-9a-f]{40}$")]
    [string]$SetupSourceCommit,

    [Parameter(Mandatory)]
    [ValidatePattern("^[1-9][0-9]+$")]
    [string]$SetupWorkflowRun,

    [Parameter(Mandatory)]
    [string]$SetupSignerSubject,

    [Parameter(Mandatory)]
    [ValidatePattern("^[0-9a-f]{40}$")]
    [string]$MsixSourceCommit,

    [Parameter(Mandatory)]
    [ValidatePattern("^[1-9][0-9]+$")]
    [string]$MsixWorkflowRun,

    [Parameter(Mandatory)]
    [string]$MsixSignerSubject,

    [Parameter(Mandatory)]
    [ValidatePattern("^[0-9a-f]{40}$")]
    [string]$DriverSourceCommit,

    [Parameter(Mandatory)]
    [ValidatePattern("^[1-9][0-9]+$")]
    [string]$DriverWorkflowRun,

    [Parameter(Mandatory)]
    [string]$DriverSignerSubject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:SetupFileName =
    "EMKE-Translation-Setup-0.2.0-internal-x64.exe"
$script:HashFileName = "SHA256SUMS.txt"
$script:ProvenanceFileName = "setup-provenance.json"
$script:RecoveryFileName =
    "EMKE-Translation-Setup-Recovery-0.2.0-internal-x64.exe"
$script:EngineeringZipFileName =
    "EMKE-Translation-Setup-Engineering-0.2.0-internal-x64.zip"
$script:AcceptedMsixSha256 =
    "6ABB30FF2B22B5A8BA35B14DD51EE3A873BE15DD7D9569E7CBCA9FA4A3F8DCA8"
$script:AcceptedCertificateSha256 =
    "05BE411C8919CFE532E6C27C88C713D49C405D2036BB56400955477189F0CA1C"
$script:AcceptedSignerThumbprint =
    "33E9992B08919BA6522F8A16B95CC2AA5DA6BB98"

$script:PayloadContract = @(
    [ordered]@{
        logicalName = "application-msix"
        fileName = "EMKE-Translation-Windows-0.2.0-internal-x64.msix"
        kind = "msix"
        provenance = "msix"
    },
    [ordered]@{
        logicalName = "application-certificate"
        fileName = "EMKE-Translation-Windows-0.2.0-internal-x64.cer"
        kind = "certificate"
        provenance = "msix"
    },
    [ordered]@{
        logicalName = "driver-inf"
        fileName = "EMKE.VirtualAudio.inf"
        kind = "driverInf"
        provenance = "driver"
    },
    [ordered]@{
        logicalName = "driver-sys"
        fileName = "EMKE.VirtualAudio.sys"
        kind = "driverSys"
        provenance = "driver"
    },
    [ordered]@{
        logicalName = "driver-catalog"
        fileName = "EMKE.VirtualAudio.cat"
        kind = "driverCatalog"
        provenance = "driver"
    }
)

function Assert-NoReparsePathChain {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [switch]$AllowMissingLeaf
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $cursor = $fullPath
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (
                $null -ne $item.LinkType -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "Setup paths must not contain reparse points or links."
            }
        } elseif (-not $AllowMissingLeaf) {
            throw "Setup path validation failed."
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) {
            break
        }
        $cursor = $parent.FullName
    }
    return $fullPath
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

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexStringLower(
        [Security.Cryptography.SHA256]::HashData($bytes)
    )
}

function Assert-Provenance {
    if ($SetupSignerSubject -notin @("CN=EMKE Internal Test", "UNSIGNED")) {
        throw "Setup signer identity is not allowed."
    }
    if ($MsixSignerSubject -cne "CN=EMKE Internal Test") {
        throw "MSIX signer identity is not allowed."
    }
    if (
        $DriverSignerSubject -notmatch
            "(?:^|,\s*)CN=Microsoft Windows Hardware Compatibility Publisher(?:,|$)" -or
        $DriverSignerSubject -notmatch
            "(?:^|,\s*)O=Microsoft Corporation(?:,|$)"
    ) {
        throw "Driver signer identity is not Microsoft Hardware Compatibility."
    }
}

function New-SetupInventory {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    Assert-Provenance
    $resolvedRoot = Assert-NoReparsePathChain -Path $Root
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw "Setup payload root is not a directory."
    }
    $resolvedDestination = Assert-NoReparsePathChain `
        -Path $Destination `
        -AllowMissingLeaf
    if (
        [IO.Directory]::GetParent($resolvedDestination).FullName -cne
            [IO.Directory]::GetParent([IO.Path]::GetFullPath($Destination)).FullName
    ) {
        throw "Setup inventory destination is invalid."
    }

    $payloads = [Collections.Generic.List[object]]::new()
    foreach ($contract in $script:PayloadContract) {
        $path = Join-Path $resolvedRoot $contract.fileName
        $resolved = Assert-NoReparsePathChain -Path $path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Required Setup payload is missing."
        }
        $item = Get-Item -LiteralPath $resolved -Force
        if ($item.Length -le 0) {
            throw "Setup payload length must be positive."
        }
        $provenance = if ($contract.provenance -ceq "msix") {
            [ordered]@{
                sourceCommit = $MsixSourceCommit
                workflowRun = $MsixWorkflowRun
                signerSubject = $MsixSignerSubject
            }
        } else {
            [ordered]@{
                sourceCommit = $DriverSourceCommit
                workflowRun = $DriverWorkflowRun
                signerSubject = $DriverSignerSubject
            }
        }
        $payloads.Add([ordered]@{
            logicalName = $contract.logicalName
            fileName = $contract.fileName
            kind = $contract.kind
            length = [long]$item.Length
            sha256 = Get-LowerSha256 -Path $resolved
            sourceCommit = $provenance.sourceCommit
            workflowRun = $provenance.workflowRun
            signerSubject = $provenance.signerSubject
        })
    }

    $unsigned = [ordered]@{
        schemaVersion = 1
        productVersion = "0.2.0"
        packageVersion = "0.2.0.0"
        channel = "internal"
        architecture = "x64"
        setupSourceCommit = $SetupSourceCommit
        setupWorkflowRun = $SetupWorkflowRun
        setupSignerSubject = $SetupSignerSubject
        payloads = @($payloads)
    }
    $unsignedJson = $unsigned | ConvertTo-Json -Depth 8 -Compress
    $manifest = [ordered]@{}
    foreach ($entry in $unsigned.GetEnumerator()) {
        $manifest[$entry.Key] = $entry.Value
    }
    $manifest.inventorySha256 = Get-TextSha256 -Text $unsignedJson
    $json = $manifest | ConvertTo-Json -Depth 8 -Compress

    $destinationParent = [IO.Directory]::GetParent($resolvedDestination).FullName
    [void][IO.Directory]::CreateDirectory($destinationParent)
    [IO.File]::WriteAllText(
        $resolvedDestination,
        $json,
        [Text.UTF8Encoding]::new($false)
    )
    return $resolvedDestination
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

function Assert-ExactDriverInventory {
    param([Parameter(Mandatory)][string]$Root)

    $expected = @(
        "EMKE.VirtualAudio.cat",
        "EMKE.VirtualAudio.inf",
        "EMKE.VirtualAudio.sys"
    ) | Sort-Object
    $actual = @(
        Get-ChildItem -LiteralPath $Root -File -Force |
            Sort-Object Name |
            ForEach-Object { $_.Name }
    )
    if (
        @(Get-ChildItem -LiteralPath $Root -Directory -Force).Count -ne 0 -or
        $actual.Count -ne $expected.Count -or
        @(Compare-Object $actual $expected -CaseSensitive).Count -ne 0
    ) {
        throw "The driver artifact must be the exact flat INF/SYS/CAT set."
    }
}

function Assert-MicrosoftDriverSignature {
    param([Parameter(Mandatory)][string]$CatalogPath)

    $signature = Get-AuthenticodeSignature -LiteralPath $CatalogPath
    $certificate = $signature.SignerCertificate
    if (
        $signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $certificate -or
        $certificate.Subject -notmatch
            "(?:^|,\s*)CN=Microsoft Windows Hardware Compatibility Publisher(?:,|$)" -or
        $certificate.Subject -notmatch
            "(?:^|,\s*)O=Microsoft Corporation(?:,|$)" -or
        $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow
    ) {
        throw "The driver catalog is not a current Microsoft Hardware Compatibility signature."
    }
}

function Write-DeterministicEngineeringZip {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ProvenancePath
    )

    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None
    )
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true
        )
        try {
            $entries = [ordered]@{
                "README.txt" = (
                    "Diagnostic-only engineering evidence. This ZIP does not " +
                    "install, uninstall, repair, or change machine state.`r`n"
                )
                "setup-provenance.json" = [IO.File]::ReadAllText(
                    $ProvenancePath
                )
            }
            foreach ($entryName in $entries.Keys) {
                $entry = $archive.CreateEntry(
                    $entryName,
                    [IO.Compression.CompressionLevel]::Optimal
                )
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    1980,
                    1,
                    1,
                    0,
                    0,
                    0,
                    [TimeSpan]::Zero
                )
                $writer = [IO.StreamWriter]::new(
                    $entry.Open(),
                    [Text.UTF8Encoding]::new($false)
                )
                try {
                    $writer.Write([string]$entries[$entryName])
                } finally {
                    $writer.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

if ($PSCmdlet.ParameterSetName -ceq "Inventory") {
    $created = New-SetupInventory -Root $PayloadRoot -Destination $InventoryPath
    Write-Output $created
    return
}

if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7) {
    throw "Setup packaging requires PowerShell 7 on Windows."
}

$resolvedMsix = Assert-NoReparsePathChain -Path $MsixPath
$resolvedCertificate = Assert-NoReparsePathChain -Path $CertificatePath
if (-not (Test-Path -LiteralPath $DriverDirectory -PathType Container)) {
    throw "The Microsoft-signed driver input is unavailable."
}
$resolvedDriver = Assert-NoReparsePathChain -Path $DriverDirectory
if (
    -not (Test-Path -LiteralPath $resolvedMsix -PathType Leaf) -or
    [IO.Path]::GetFileName($resolvedMsix) -cne
        "EMKE-Translation-Windows-0.2.0-internal-x64.msix"
) {
    throw "The exact Internal MSIX input is unavailable."
}
if (
    -not (Test-Path -LiteralPath $resolvedCertificate -PathType Leaf) -or
    [IO.Path]::GetFileName($resolvedCertificate) -cne
        "EMKE-Translation-Windows-0.2.0-internal-x64.cer"
) {
    throw "The exact Internal public certificate input is unavailable."
}

try {
    & (Join-Path $PSScriptRoot "verify-msix.ps1") -Package $resolvedMsix |
        Out-Null
} catch {
    throw "MSIX verification failed before Setup publication."
}

try {
    & (Join-Path $PSScriptRoot "verify-driver-package.ps1") `
        -PackageDirectory $resolvedDriver |
        Out-Null
} catch {
    throw "Microsoft-signed driver verification failed before Setup publication."
}

Assert-ExactDriverInventory -Root $resolvedDriver
Assert-MicrosoftDriverSignature -CatalogPath (
    Join-Path $resolvedDriver "EMKE.VirtualAudio.cat"
)
if (
    (Get-FileHash -LiteralPath $resolvedMsix -Algorithm SHA256).Hash -cne
        $script:AcceptedMsixSha256 -or
    (Get-FileHash -LiteralPath $resolvedCertificate -Algorithm SHA256).Hash -cne
        $script:AcceptedCertificateSha256
) {
    throw "The MSIX or certificate bytes do not match the accepted artifact."
}
if (
    [string]::IsNullOrWhiteSpace($PfxPath) -or
    [string]::IsNullOrWhiteSpace($PasswordEnvironmentVariable) -or
    $SetupSignerSubject -cne "CN=EMKE Internal Test"
) {
    throw "The protected Setup signing identity is required."
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$resolvedPfx = Assert-NoReparsePathChain -Path $PfxPath
$temporaryRoot = [IO.Path]::GetFullPath(
    $(if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        [IO.Path]::GetTempPath()
    } else {
        $env:RUNNER_TEMP
    })
).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (
    [IO.Path]::GetExtension($resolvedPfx) -cne ".pfx" -or
    -not $resolvedPfx.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedPfx.StartsWith(
        $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
            [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )
) {
    throw "The PFX must be an ephemeral input outside the repository."
}
try {
$password = [Environment]::GetEnvironmentVariable(
    $PasswordEnvironmentVariable,
    [EnvironmentVariableTarget]::Process
)
if ([string]::IsNullOrEmpty($password)) {
    throw "The Setup signing password environment variable is unavailable."
}

$resolvedCandidate = Assert-NoReparsePathChain `
    -Path $CandidateRoot `
    -AllowMissingLeaf
if (Test-Path -LiteralPath $resolvedCandidate) {
    throw "The Setup candidate root must not already exist."
}
$candidateParent = [IO.Directory]::GetParent($resolvedCandidate).FullName
[void][IO.Directory]::CreateDirectory($candidateParent)
$candidateParent = Assert-NoReparsePathChain -Path $candidateParent
$staging = Join-Path $candidateParent (
    ".emke-setup-staging-" + [Guid]::NewGuid().ToString("N")
)
$publishRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "emke-setup-publish-" + [Guid]::NewGuid().ToString("N")
)
$payloadRoot = Join-Path $publishRoot "payloads"
$inventoryPath = Join-Path $publishRoot "setup-payload-inventory.json"
$exportedSigner = Join-Path $publishRoot "setup-signer.cer"
$temporaryThumbprints = @()
$securePassword = $null
$packageSucceeded = $false

try {
    [void][IO.Directory]::CreateDirectory($payloadRoot)
    [IO.File]::Copy(
        $resolvedMsix,
        (Join-Path $payloadRoot $script:PayloadContract[0].fileName),
        $false
    )
    [IO.File]::Copy(
        $resolvedCertificate,
        (Join-Path $payloadRoot $script:PayloadContract[1].fileName),
        $false
    )
    foreach ($driverName in @(
        "EMKE.VirtualAudio.inf",
        "EMKE.VirtualAudio.sys",
        "EMKE.VirtualAudio.cat"
    )) {
        [IO.File]::Copy(
            (Join-Path $resolvedDriver $driverName),
            (Join-Path $payloadRoot $driverName),
            $false
        )
    }
    New-SetupInventory -Root $payloadRoot -Destination $inventoryPath |
        Out-Null
    & (Join-Path $PSScriptRoot "verify-setup.ps1") `
        -InventoryRoot $payloadRoot `
        -InventoryManifestPath $inventoryPath |
        Out-Null

    $certificateOutput = @(
        & (Join-Path $PSScriptRoot "verify-internal-signing-certificate.ps1") `
            -PfxPath $resolvedPfx `
            -PasswordEnvironmentVariable $PasswordEnvironmentVariable `
            -ExpectedSubject $SetupSignerSubject `
            -ExportPublicCertificatePath $exportedSigner
    )
    $thumbprintLine = @(
        $certificateOutput |
            Where-Object { $_ -match "^Public thumbprint: [0-9A-F]{40}$" }
    )
    if (
        $thumbprintLine.Count -ne 1 -or
        $thumbprintLine[0].Substring($thumbprintLine[0].Length - 40) -cne
            $script:AcceptedSignerThumbprint -or
        (Get-FileHash -LiteralPath $exportedSigner -Algorithm SHA256).Hash -cne
            $script:AcceptedCertificateSha256
    ) {
        throw "The Setup signer does not match the accepted public identity."
    }

    [void][IO.Directory]::CreateDirectory($staging)
    $project = Join-Path $repositoryRoot "Windows/src/EMKE.Setup/EMKE.Setup.csproj"
    & dotnet restore $project --locked-mode --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "The locked Setup runtime restore failed."
    }
    & dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $publishRoot `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:SetupPayloadRoot=$payloadRoot `
        -p:SetupInventoryPath=$inventoryPath
    if ($LASTEXITCODE -ne 0) {
        throw "The deterministic self-contained Setup publish failed."
    }
    $publishedExe = Join-Path $publishRoot "EMKE.Setup.exe"
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        throw "The self-contained Setup executable is unavailable."
    }
    $setupPath = Join-Path $staging $script:SetupFileName
    [IO.File]::Copy($publishedExe, $setupPath, $false)

    $selfCheck = @(& $setupPath --verify-self-v1)
    if (
        $LASTEXITCODE -ne 0 -or
        $selfCheck.Count -ne 1 -or
        ($selfCheck[0] | ConvertFrom-Json).status -cne "verified"
    ) {
        throw "The published Setup embedded-resource self-check failed."
    }

    $securePassword = ConvertTo-SecureString `
        -String $password `
        -AsPlainText `
        -Force
    $password = $null
    $before = @(
        Get-ChildItem -LiteralPath "Cert:\CurrentUser\My" |
            ForEach-Object { $_.Thumbprint.ToUpperInvariant() }
    )
    $imported = @(
        Import-PfxCertificate `
            -FilePath $resolvedPfx `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -Password $securePassword `
            -Exportable:$false
    )
    $temporaryThumbprints = @(
        $imported |
            ForEach-Object { $_.Thumbprint.ToUpperInvariant() } |
            Where-Object { $_ -notin $before }
    )
    if (
        $script:AcceptedSignerThumbprint -notin $temporaryThumbprints -or
        @($imported | Where-Object {
            $_.Thumbprint.ToUpperInvariant() -ceq
                $script:AcceptedSignerThumbprint
        }).Count -ne 1
    ) {
        throw "The temporary Setup signer import is invalid."
    }
    $signTool = Resolve-SignTool
    & $signTool sign `
        /sha1 $script:AcceptedSignerThumbprint `
        /s My `
        /fd SHA256 `
        $setupPath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed to sign the Setup executable."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $setupPath
    if (
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne
            $script:AcceptedSignerThumbprint -or
        $signature.SignerCertificate.Subject -cne $SetupSignerSubject -or
        $signature.Status -in @(
            [Management.Automation.SignatureStatus]::HashMismatch,
            [Management.Automation.SignatureStatus]::NotSigned
        )
    ) {
        throw "The signed Setup executable failed pinned signature inspection."
    }

    $recoveryPath = Join-Path $staging $script:RecoveryFileName
    [IO.File]::Copy($setupPath, $recoveryPath, $false)
    $setupHash = Get-LowerSha256 -Path $setupPath
    $recoveryHash = Get-LowerSha256 -Path $recoveryPath
    $inventory = [IO.File]::ReadAllText($inventoryPath) | ConvertFrom-Json
    $provenance = [ordered]@{
        schemaVersion = 1
        productVersion = "0.2.0"
        channel = "internal"
        architecture = "x64"
        setupSourceCommit = $SetupSourceCommit
        setupWorkflowRun = $SetupWorkflowRun
        setupSignerSubject = $SetupSignerSubject
        setupSignerThumbprint = $script:AcceptedSignerThumbprint
        setupSha256 = $setupHash
        recoverySha256 = $recoveryHash
        payloadInventorySha256 = [string]$inventory.inventorySha256
        unknownPublisherBoundary = $true
        payloads = @($inventory.payloads)
    }
    $provenancePath = Join-Path $staging $script:ProvenanceFileName
    [IO.File]::WriteAllText(
        $provenancePath,
        ($provenance | ConvertTo-Json -Depth 8 -Compress),
        [Text.UTF8Encoding]::new($false)
    )
    $engineeringZipPath = Join-Path `
        $staging `
        $script:EngineeringZipFileName
    Write-DeterministicEngineeringZip `
        -Path $engineeringZipPath `
        -ProvenancePath $provenancePath
    $provenanceHash = Get-LowerSha256 -Path $provenancePath
    $engineeringZipHash = Get-LowerSha256 -Path $engineeringZipPath
    $hashPath = Join-Path $staging $script:HashFileName
    [IO.File]::WriteAllText(
        $hashPath,
        (
            $setupHash.ToUpperInvariant() + "  " + $script:SetupFileName + "`r`n" +
            $recoveryHash.ToUpperInvariant() + "  " + $script:RecoveryFileName + "`r`n" +
            $provenanceHash.ToUpperInvariant() + "  " + $script:ProvenanceFileName + "`r`n" +
            $engineeringZipHash.ToUpperInvariant() + "  " + $script:EngineeringZipFileName + "`r`n"
        ),
        [Text.ASCIIEncoding]::new()
    )

    & (Join-Path $PSScriptRoot "verify-setup.ps1") -SetupPath $setupPath |
        Out-Null
    [IO.Directory]::Move($staging, $resolvedCandidate)
    $packageSucceeded = $true
    Write-Output "Setup: $(Join-Path $resolvedCandidate $script:SetupFileName)"
} finally {
    $password = $null
    $securePassword = $null
    foreach ($thumbprint in $temporaryThumbprints) {
        $storePath = "Cert:\CurrentUser\My\$thumbprint"
        if (Test-Path -LiteralPath $storePath) {
            Remove-Item -LiteralPath $storePath -Force
        }
    }
    if (Test-Path -LiteralPath $resolvedPfx -PathType Leaf) {
        Remove-Item -LiteralPath $resolvedPfx -Force
    }
    if (Test-Path -LiteralPath $publishRoot -PathType Container) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
    if (-not $packageSucceeded -and
        (Test-Path -LiteralPath $staging -PathType Container)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
} finally {
    if (Test-Path -LiteralPath $resolvedPfx -PathType Leaf) {
        Remove-Item -LiteralPath $resolvedPfx -Force
    }
}
