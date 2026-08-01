[CmdletBinding(DefaultParameterSetName = "VerifyPackage")]
param(
    [Parameter(Mandatory, ParameterSetName = "VerifyPackage", Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Package,

    [Parameter(Mandatory, ParameterSetName = "ValidateExtracted")]
    [switch]$ValidateExtractedOnly,

    [Parameter(Mandatory, ParameterSetName = "ValidateExtracted")]
    [ValidateNotNullOrEmpty()]
    [string]$ExtractedDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:ExpectedPackageName =
    "EMKE-Translation-Windows-0.1.0-internal-x64.msix"
$script:ExpectedCertificateName =
    "EMKE-Translation-Windows-0.1.0-internal-x64.cer"
$script:ExpectedIdentity = "EMKE.Translation.Internal"
$script:ExpectedPublisher = "CN=EMKE Internal Test"
$script:ExpectedVersion = "0.1.0.0"
$script:ForbiddenExtensionPattern =
    "(?i)\.(?:cat|inf|key|p12|pdb|pem|pfx|sys)$"
$script:ForbiddenNamePattern =
    "(?i)(?:tests?|password|credentials?|settings?|recordings?|transcripts?|" +
    "raw[-_. ]?endpoints?|endpoint[-_. ]?fixtures?)"

function Invoke-CompleteCleanup {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Actions
    )

    $cleanupErrors = [Collections.Generic.List[Exception]]::new()
    foreach ($entry in $Actions) {
        $name = [string]$entry.Name
        $action = $entry.Action
        try {
            if (
                [string]::IsNullOrWhiteSpace($name) -or
                $action -isnot [scriptblock]
            ) {
                throw "Cleanup action definition is invalid."
            }
            & $action
        } catch {
            $cleanupErrors.Add(
                [InvalidOperationException]::new(
                    "Cleanup action '$name' failed.",
                    $_.Exception
                )
            )
        }
    }

    if ($cleanupErrors.Count -ne 0) {
        throw [AggregateException]::new(
            "One or more MSIX cleanup actions failed.",
            $cleanupErrors
        )
    }
}

function Assert-NoReparsePathChain {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $currentPath = $fullPath
    while ($true) {
        if (-not (Test-Path -LiteralPath $currentPath)) {
            throw "MSIX verification path validation failed."
        }
        $item = Get-Item -LiteralPath $currentPath -Force
        $linkProperty = $item.PSObject.Properties["LinkType"]
        $linkType = if ($null -eq $linkProperty) {
            $null
        } else {
            $linkProperty.Value
        }
        if (
            $null -ne $linkType -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "MSIX verification paths must not contain reparse points."
        }
        if (-not $IsWindows) {
            break
        }

        $parent = [IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) {
            break
        }
        $currentPath = $parent.FullName
    }

    return $fullPath
}

function Resolve-WindowsSdkTools {
    $kitsRoot = (
        Get-ItemProperty `
            -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" `
            -ErrorAction Stop
    ).KitsRoot10
    if ([string]::IsNullOrWhiteSpace($kitsRoot)) {
        throw "Windows SDK KitsRoot10 is unavailable."
    }

    $toolPair = @(
        Get-ChildItem -LiteralPath (Join-Path $kitsRoot "bin") -Directory |
            Where-Object {
                [version]$parsedVersion = [version]::new()
                [version]::TryParse($_.Name, [ref]$parsedVersion)
            } |
            Sort-Object {
                [version]$_.Name
            } -Descending |
            ForEach-Object {
                $x64Directory = Join-Path $_.FullName "x64"
                $makeAppx = Join-Path $x64Directory "MakeAppx.exe"
                $signTool = Join-Path $x64Directory "SignTool.exe"
                if (
                    (Test-Path -LiteralPath $makeAppx -PathType Leaf) -and
                    (Test-Path -LiteralPath $signTool -PathType Leaf)
                ) {
                    [pscustomobject]@{
                        MakeAppx = $makeAppx
                        SignTool = $signTool
                    }
                }
            }
    )
    if ($toolPair.Count -eq 0) {
        throw "MakeAppx.exe and SignTool.exe are unavailable in the Windows SDK."
    }

    return $toolPair[0]
}

function Read-RequiredXmlAttribute {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlElement]$Element,

        [Parameter(Mandatory)]
        [string]$Name,

        [string]$NamespaceUri = ""
    )

    $value = $Element.GetAttribute($Name, $NamespaceUri)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required manifest attribute is unavailable: $Name"
    }
    return $value
}

function Assert-ExactCompatibility {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedPath
    )

    $actual = Get-Content -LiteralPath $Path -Raw |
        ConvertFrom-Json -ErrorAction Stop
    $expected = Get-Content -LiteralPath $ExpectedPath -Raw |
        ConvertFrom-Json -ErrorAction Stop
    $expectedNames = @(
        "appVersion",
        "contractVersion",
        "settingsSchemaVersion",
        "driverAbiVersion",
        "minimumDriverVersion",
        "recommendedDriverVersion",
        "driverPackageAvailable",
        "channel"
    )
    $actualNames = @($actual.PSObject.Properties.Name | Sort-Object)
    $sortedExpectedNames = @($expectedNames | Sort-Object)
    $propertyDifference = @(
        Compare-Object `
            -ReferenceObject $sortedExpectedNames `
            -DifferenceObject $actualNames
    )
    if (
        ($actualNames.Count -ne $sortedExpectedNames.Count) -or
        ($propertyDifference.Count -ne 0)
    ) {
        throw "Embedded compatibility property inventory is invalid."
    }

    foreach ($name in $expectedNames) {
        $actualProperty = $actual.PSObject.Properties[$name]
        $expectedProperty = $expected.PSObject.Properties[$name]
        if (
            $null -eq $actualProperty -or
            $null -eq $expectedProperty -or
            $actualProperty.Value.GetType() -ne
                $expectedProperty.Value.GetType() -or
            $actualProperty.Value -cne $expectedProperty.Value
        ) {
            throw "Embedded compatibility metadata differs at $name."
        }
    }
    if ($actual.driverPackageAvailable -ne $false) {
        throw "Internal compatibility must keep driverPackageAvailable=false."
    }
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read
    )
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 0x86 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "PE DOS signature validation failed."
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt ($stream.Length - 6)) {
            throw "PE header offset validation failed."
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "PE signature validation failed."
        }
        return $reader.ReadUInt16()
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Assert-SignerProvenance {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$ProvenancePath
    )

    $provenance = Get-Content -LiteralPath $ProvenancePath -Raw |
        ConvertFrom-Json -ErrorAction Stop
    $expectedNames = @(
        "schemaVersion",
        "subject",
        "thumbprint",
        "packageSha256",
        "certificateSha256"
    )
    $actualNames = @($provenance.PSObject.Properties.Name | Sort-Object)
    $nameDifference = @(
        Compare-Object `
            -ReferenceObject @($expectedNames | Sort-Object) `
            -DifferenceObject $actualNames
    )
    if (
        $actualNames.Count -ne $expectedNames.Count -or
        $nameDifference.Count -ne 0 -or
        [int]$provenance.schemaVersion -ne 1 -or
        $provenance.subject -cne "CN=EMKE Internal Test" -or
        $provenance.thumbprint -notmatch "^[0-9A-F]{40}$" -or
        $provenance.packageSha256 -notmatch "^[0-9A-F]{64}$" -or
        $provenance.certificateSha256 -notmatch "^[0-9A-F]{64}$"
    ) {
        throw "Signer provenance structure is invalid."
    }

    $actualPackageHash = (
        Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256
    ).Hash.ToUpperInvariant()
    $actualCertificateHash = (
        Get-FileHash -LiteralPath $CertificatePath -Algorithm SHA256
    ).Hash.ToUpperInvariant()
    if (
        $actualPackageHash -cne $provenance.packageSha256 -or
        $actualCertificateHash -cne $provenance.certificateSha256
    ) {
        throw "Signer provenance does not match package bytes."
    }

    return [string]$provenance.thumbprint
}

function Assert-ExtractedPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [switch]$RequirePackageInfrastructure
    )

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw "Extracted MSIX path must be absolute."
    }
    $resolvedPath = Assert-NoReparsePathChain -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) {
        throw "Extracted MSIX directory is unavailable."
    }

    foreach ($directory in @(
        Get-ChildItem -LiteralPath $resolvedPath -Directory -Recurse -Force
    )) {
        if (
            $null -ne $directory.LinkType -or
            ($directory.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Extracted MSIX directories must not be reparse points."
        }
    }
    $files = @(
        Get-ChildItem -LiteralPath $resolvedPath -File -Recurse -Force
    )
    foreach ($file in $files) {
        if (
            $null -ne $file.LinkType -or
            ($file.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Extracted MSIX files must not be reparse points."
        }
        $relative = [IO.Path]::GetRelativePath(
            $resolvedPath,
            $file.FullName
        ).Replace("\", "/")
        if (
            $relative -match $script:ForbiddenExtensionPattern -or
            $relative -match $script:ForbiddenNamePattern
        ) {
            throw "Forbidden file in extracted MSIX: $relative"
        }
        $extension = $file.Extension.ToLowerInvariant()
        $allowed = switch ($extension) {
            ".dll" { $true; break }
            ".exe" { $true; break }
            ".png" {
                $relative.StartsWith(
                    "Assets/",
                    [StringComparison]::Ordinal
                )
                break
            }
            ".json" {
                $relative -ceq "compatibility.json" -or
                    $relative -match
                        "^EMKE\.Windows\.App\.(?:deps|runtimeconfig)\.json$"
                break
            }
            ".xml" {
                $relative -in @(
                    "AppxManifest.xml",
                    "AppxBlockMap.xml",
                    "[Content_Types].xml"
                )
                break
            }
            ".p7x" {
                $relative -ceq "AppxSignature.p7x"
                break
            }
            default { $false }
        }
        if (-not $allowed) {
            throw "Unexpected file in extracted MSIX: $relative"
        }
    }

    foreach ($requiredName in @(
        "AppxManifest.xml",
        "compatibility.json",
        "EMKE.Windows.App.exe",
        "EMKE.NativeAudio.dll"
    )) {
        if (-not (
            Test-Path `
                -LiteralPath (Join-Path $resolvedPath $requiredName) `
                -PathType Leaf
        )) {
            throw "Extracted MSIX is missing $requiredName."
        }
    }
    if ($RequirePackageInfrastructure -and -not (
        Test-Path `
            -LiteralPath (Join-Path $resolvedPath "AppxSignature.p7x") `
            -PathType Leaf
    )) {
        throw "Extracted MSIX signature resource is unavailable."
    }

    [xml]$manifest = Get-Content -LiteralPath (
        Join-Path $resolvedPath "AppxManifest.xml"
    ) -Raw
    $manager = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10"
    $uap10 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
    $manager.AddNamespace("f", $foundation)
    $manager.AddNamespace("uap10", $uap10)
    $manager.AddNamespace(
        "rescap",
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
    )
    $identity = $manifest.SelectSingleNode(
        "/f:Package/f:Identity",
        $manager
    )
    $target = $manifest.SelectSingleNode(
        "/f:Package/f:Dependencies/f:TargetDeviceFamily",
        $manager
    )
    $application = $manifest.SelectSingleNode(
        "/f:Package/f:Applications/f:Application",
        $manager
    )
    $capability = $manifest.SelectSingleNode(
        "/f:Package/f:Capabilities/rescap:Capability",
        $manager
    )
    if (
        $null -eq $identity -or
        $null -eq $target -or
        $null -eq $application -or
        $null -eq $capability
    ) {
        throw "Extracted MSIX manifest structure is invalid."
    }

    $expectedAttributes = [ordered]@{
        IdentityName = @(
            $identity,
            "Name",
            "",
            $script:ExpectedIdentity
        )
        Publisher = @(
            $identity,
            "Publisher",
            "",
            $script:ExpectedPublisher
        )
        Version = @(
            $identity,
            "Version",
            "",
            $script:ExpectedVersion
        )
        Architecture = @($identity, "ProcessorArchitecture", "", "x64")
        TargetName = @($target, "Name", "", "Windows.Desktop")
        MinimumVersion = @($target, "MinVersion", "", "10.0.26200.0")
        MaximumVersion = @(
            $target,
            "MaxVersionTested",
            "",
            "10.0.26200.0"
        )
        ApplicationId = @($application, "Id", "", "EMKETranslation")
        Executable = @(
            $application,
            "Executable",
            "",
            "EMKE.Windows.App.exe"
        )
        EntryPoint = @(
            $application,
            "EntryPoint",
            "",
            "Windows.FullTrustApplication"
        )
        RuntimeBehavior = @(
            $application,
            "RuntimeBehavior",
            $uap10,
            "packagedClassicApp"
        )
        TrustLevel = @($application, "TrustLevel", $uap10, "mediumIL")
        Capability = @($capability, "Name", "", "runFullTrust")
    }
    foreach ($entry in $expectedAttributes.GetEnumerator()) {
        $actual = Read-RequiredXmlAttribute `
            -Element $entry.Value[0] `
            -Name $entry.Value[1] `
            -NamespaceUri $entry.Value[2]
        if ($actual -cne $entry.Value[3]) {
            throw "Extracted MSIX manifest differs at $($entry.Key)."
        }
    }

    $repositoryRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../..")
    )
    Assert-ExactCompatibility `
        -Path (Join-Path $resolvedPath "compatibility.json") `
        -ExpectedPath (
            Join-Path (
                $repositoryRoot
            ) "Windows/packaging/compatibility.internal.json"
        )

    foreach ($binaryName in @(
        "EMKE.Windows.App.exe",
        "EMKE.NativeAudio.dll"
    )) {
        $machine = Get-PeMachine -Path (Join-Path $resolvedPath $binaryName)
        if ($machine -ne 0x8664) {
            throw "$binaryName is not an x64 PE image."
        }
    }

    $hashes = @(
        $files |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    path = [IO.Path]::GetRelativePath(
                        $resolvedPath,
                        $_.FullName
                    ).Replace("\", "/")
                    sha256 = (
                        Get-FileHash `
                            -LiteralPath $_.FullName `
                            -Algorithm SHA256
                    ).Hash.ToUpperInvariant()
                }
            }
    )
    return $hashes
}

if ($ValidateExtractedOnly) {
    $validatedHashes = Assert-ExtractedPackage -Path $ExtractedDirectory
    Write-Output (
        [ordered]@{
            status = "verified"
            extractedFiles = $validatedHashes
        } |
            ConvertTo-Json -Depth 5 -Compress
    )
    return
}

if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7) {
    throw "MSIX verification requires PowerShell 7 on Windows."
}
if ($env:PROCESSOR_ARCHITECTURE -cne "AMD64") {
    throw "MSIX verification requires an x64 process."
}
if (
    -not [IO.Path]::IsPathFullyQualified($Package) -and
    -not [IO.Path]::IsPathRooted($Package)
) {
    $Package = Join-Path (Get-Location) $Package
}
$resolvedPackage = Assert-NoReparsePathChain -Path $Package
if (
    -not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf) -or
    [IO.Path]::GetFileName($resolvedPackage) -cne
        $script:ExpectedPackageName
) {
    throw "Exact Internal MSIX input validation failed."
}

$packageDirectory = [IO.Path]::GetDirectoryName($resolvedPackage)
$certificatePath = Join-Path (
    $packageDirectory
) $script:ExpectedCertificateName
$certificatePath = Assert-NoReparsePathChain -Path $certificatePath
if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw "Sibling Internal public certificate is unavailable."
}
$provenancePath = Join-Path (
    $packageDirectory
) "EMKE-Translation-Windows-0.1.0-internal-x64.signing.json"
$provenancePath = Assert-NoReparsePathChain -Path $provenancePath
if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
    throw "Verified signer provenance is unavailable."
}
$provenanceThumbprint = Assert-SignerProvenance `
    -PackagePath $resolvedPackage `
    -CertificatePath $certificatePath `
    -ProvenancePath $provenancePath

$sdkTools = Resolve-WindowsSdkTools
$beforeHash = (
    Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256
).Hash.ToUpperInvariant()
$extractRoot = Join-Path (
    [IO.Path]::GetTempPath()
) ("emke-msix-verify-" + [Guid]::NewGuid().ToString("N"))
$trustedThumbprint = $null
$addedTrust = $false
$publicCertificate = $null

try {
    $publicCertificate =
        [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [IO.File]::ReadAllBytes($certificatePath)
        )
    if (
        $publicCertificate.HasPrivateKey -or
        $publicCertificate.Subject -cne $script:ExpectedPublisher -or
        $publicCertificate.Thumbprint -notmatch "^[0-9A-Fa-f]{40}$" -or
        $publicCertificate.Thumbprint.ToUpperInvariant() -cne
            $provenanceThumbprint
    ) {
        throw "Sibling Internal public certificate validation failed."
    }
    $trustedThumbprint = $provenanceThumbprint
    $trustedPath =
        "Cert:\LocalMachine\TrustedPeople\$trustedThumbprint"
    if (-not (Test-Path -LiteralPath $trustedPath)) {
        $trustedPeopleStore =
            [Security.Cryptography.X509Certificates.X509Store]::new(
                [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
                [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
            )
        try {
            $trustedPeopleStore.Open(
                [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
            )
            $trustedPeopleStore.Add($publicCertificate)
            $addedTrust = $true
        } finally {
            $trustedPeopleStore.Dispose()
        }
        $imported = Get-Item -LiteralPath $trustedPath
        if (
            $null -eq $imported -or
            $imported.Thumbprint.ToUpperInvariant() -cne $trustedThumbprint
        ) {
            throw "Temporary package-verification trust import failed."
        }
    }

    & $sdkTools.SignTool verify /pa /all /v $resolvedPackage
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool /pa verification failed."
    }
    $authenticode = Get-AuthenticodeSignature `
        -LiteralPath $resolvedPackage
    if (
        $authenticode.Status -ne
            [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $authenticode.SignerCertificate -or
        $authenticode.SignerCertificate.Thumbprint.ToUpperInvariant() -cne
            $trustedThumbprint -or
        $authenticode.SignerCertificate.Subject -cne
            $script:ExpectedPublisher
    ) {
        throw "Authenticode signer validation failed."
    }

    [IO.Directory]::CreateDirectory($extractRoot) | Out-Null
    & $sdkTools.MakeAppx unpack `
        /p $resolvedPackage `
        /d $extractRoot `
        /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx package extraction failed."
    }
    $fileHashes = Assert-ExtractedPackage `
        -Path $extractRoot `
        -RequirePackageInfrastructure

    $afterHash = (
        Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256
    ).Hash.ToUpperInvariant()
    if ($afterHash -cne $beforeHash) {
        throw "MSIX bytes changed during verification."
    }

    Write-Output (
        [ordered]@{
            status = "verified"
            package = [IO.Path]::GetFileName($resolvedPackage)
            packageSha256 = $afterHash
            signerThumbprint = $trustedThumbprint
            extractedFiles = $fileHashes
        } |
            ConvertTo-Json -Depth 5
    )
} finally {
    $cleanupActions = [Collections.Generic.List[object]]::new()
    $cleanupExtractRoot = $extractRoot
    $cleanupActions.Add(
        [pscustomobject]@{
            Name = "unpack-directory"
            Action = {
                if (
                    Test-Path `
                        -LiteralPath $cleanupExtractRoot `
                        -PathType Container
                ) {
                    Remove-Item `
                        -LiteralPath $cleanupExtractRoot `
                        -Recurse `
                        -Force
                }
            }.GetNewClosure()
        }
    )
    if (
        $addedTrust -and
        -not [string]::IsNullOrEmpty($trustedThumbprint)
    ) {
        $cleanupTrustedPath =
            "Cert:\LocalMachine\TrustedPeople\$trustedThumbprint"
        $cleanupActions.Add(
            [pscustomobject]@{
                Name = "LocalMachineTrustedPeople:$trustedThumbprint"
                Action = {
                    if (Test-Path -LiteralPath $cleanupTrustedPath) {
                        Remove-Item `
                            -LiteralPath $cleanupTrustedPath `
                            -Force
                    }
                }.GetNewClosure()
            }
        )
    }
    if ($null -ne $publicCertificate) {
        $cleanupPublicCertificate = $publicCertificate
        $cleanupActions.Add(
            [pscustomobject]@{
                Name = "public-certificate"
                Action = {
                    $cleanupPublicCertificate.Dispose()
                }.GetNewClosure()
            }
        )
    }
    Invoke-CompleteCleanup -Actions @($cleanupActions)
}
