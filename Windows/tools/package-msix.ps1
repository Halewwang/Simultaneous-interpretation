[CmdletBinding(DefaultParameterSetName = "Package")]
param(
    [Parameter(ParameterSetName = "Package")]
    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [Parameter(ParameterSetName = "Package")]
    [ValidateNotNullOrEmpty()]
    [string]$PfxPath,

    [Parameter(ParameterSetName = "Package")]
    [ValidatePattern("^[A-Za-z_][A-Za-z0-9_]*$")]
    [string]$PasswordEnvironmentVariable,

    [Parameter(Mandatory, ParameterSetName = "ValidateStaging")]
    [switch]$ValidateStagingOnly,

    [Parameter(Mandatory, ParameterSetName = "ValidateStaging")]
    [ValidateNotNullOrEmpty()]
    [string]$StagingDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:ExpectedSubject = "CN=EMKE Internal Test"
$script:AllowedStageExtensions = @(".dll", ".exe", ".json", ".png", ".xml")
$script:ForbiddenStageExtensions = @(
    ".cat",
    ".inf",
    ".key",
    ".p12",
    ".pdb",
    ".pem",
    ".pfx",
    ".sys"
)
$script:ForbiddenStageNamePattern =
    "(?i)(?:tests?|password|credentials?|settings?|recordings?|transcripts?|" +
    "raw[-_. ]?endpoints?|endpoint[-_. ]?fixtures?)"

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
            $item = Get-Item -LiteralPath $currentPath -Force
            $linkProperty = $item.PSObject.Properties["LinkType"]
            $linkType = if ($null -eq $linkProperty) {
                $null
            } else {
                $linkProperty.Value
            }
            if (
                $null -ne $linkType -or
                ($item.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "MSIX paths must not contain reparse points."
            }
        } elseif (-not $AllowMissingLeaf) {
            throw "MSIX path validation failed."
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

function Resolve-EphemeralPfxInput {
    param(
        [Parameter(Mandatory)]
        [string]$PfxPath,

        [Parameter(Mandatory)]
        [string]$TemporaryRoot,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    if (
        -not [IO.Path]::IsPathFullyQualified($PfxPath) -or
        -not [IO.Path]::IsPathFullyQualified($TemporaryRoot) -or
        -not [IO.Path]::IsPathFullyQualified($RepositoryRoot)
    ) {
        throw "PFX cleanup boundaries must use absolute paths."
    }
    $resolvedPfx = Assert-NoReparsePathChain -Path $PfxPath
    $resolvedTemporaryRoot =
        Assert-NoReparsePathChain -Path $TemporaryRoot
    $resolvedRepositoryRoot =
        Assert-NoReparsePathChain -Path $RepositoryRoot
    if (
        -not (Test-Path -LiteralPath $resolvedPfx -PathType Leaf) -or
        [IO.Path]::GetExtension($resolvedPfx) -cne ".pfx"
    ) {
        throw "PFX input validation failed."
    }

    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $temporaryPrefix = $resolvedTemporaryRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $repositoryPrefix = $resolvedRepositoryRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    if (
        -not $resolvedPfx.StartsWith($temporaryPrefix, $comparison) -or
        $resolvedPfx.StartsWith($repositoryPrefix, $comparison)
    ) {
        throw "PFX must be an ephemeral input outside the repository."
    }

    return $resolvedPfx
}

function Select-NewCertificateThumbprints {
    param(
        [Parameter(Mandatory)]
        [string[]]$PreexistingThumbprints,

        [Parameter(Mandatory)]
        [string[]]$ImportedThumbprints
    )

    $preexisting = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($thumbprint in $PreexistingThumbprints) {
        if ($thumbprint -notmatch "^[0-9A-Fa-f]{40}$") {
            throw "Preexisting certificate thumbprint is invalid."
        }
        [void]$preexisting.Add($thumbprint)
    }

    $selected = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($thumbprint in $ImportedThumbprints) {
        if ($thumbprint -notmatch "^[0-9A-Fa-f]{40}$") {
            throw "Imported certificate thumbprint is invalid."
        }
        if (-not $preexisting.Contains($thumbprint)) {
            [void]$selected.Add($thumbprint.ToUpperInvariant())
        }
    }

    return @($selected | Sort-Object)
}

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

function Assert-StagingTree {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw "MSIX staging path must be absolute."
    }
    $resolvedPath = Assert-NoReparsePathChain -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) {
        throw "MSIX staging directory is unavailable."
    }

    $files = @(
        Get-ChildItem -LiteralPath $resolvedPath -File -Recurse -Force
    )
    if ($files.Count -eq 0) {
        throw "MSIX staging directory must not be empty."
    }

    foreach ($directory in @(
        Get-ChildItem -LiteralPath $resolvedPath -Directory -Recurse -Force
    )) {
        if (
            $null -ne $directory.LinkType -or
            ($directory.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "MSIX staging directories must not be reparse points."
        }
    }

    foreach ($file in $files) {
        if (
            $null -ne $file.LinkType -or
            ($file.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "MSIX staging files must not be reparse points."
        }

        $relative = [IO.Path]::GetRelativePath(
            $resolvedPath,
            $file.FullName
        ).Replace("\", "/")
        $extension = $file.Extension.ToLowerInvariant()
        if (
            $extension -in $script:ForbiddenStageExtensions -or
            $relative -match $script:ForbiddenStageNamePattern
        ) {
            throw "Forbidden MSIX staging item: $relative"
        }
        if ($extension -notin $script:AllowedStageExtensions) {
            throw "Unexpected MSIX staging item type: $relative"
        }
        if ($extension -eq ".xml" -and $relative -cne "AppxManifest.xml") {
            throw "Only the package manifest may be staged as XML."
        }
        if ($extension -eq ".png" -and
            -not $relative.StartsWith(
                "Assets/",
                [StringComparison]::Ordinal
            )) {
            throw "PNG resources must be staged under Assets."
        }
        if ($extension -eq ".json") {
            $isCompatibility =
                $relative -ceq "compatibility.json"
            $isApplicationRuntime =
                $relative -match
                    "^EMKE\.Windows\.App\.(?:deps|runtimeconfig)\.json$"
            if (-not $isCompatibility -and -not $isApplicationRuntime) {
                throw "Unexpected JSON file in MSIX staging: $relative"
            }
        }
    }

    foreach ($requiredName in @(
        "AppxManifest.xml",
        "EMKE.Windows.App.exe",
        "EMKE.NativeAudio.dll",
        "compatibility.json"
    )) {
        $matches = @(
            $files |
                Where-Object {
                    [IO.Path]::GetRelativePath(
                        $resolvedPath,
                        $_.FullName
                    ).Replace("\", "/") -ceq $requiredName
                }
        )
        if ($matches.Count -ne 1) {
            throw "MSIX staging requires exactly one $requiredName."
        }
    }

    Write-Output "Validated MSIX staging files: $($files.Count)"
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

function Copy-VerifiedTree {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    $resolvedSource = Assert-NoReparsePathChain -Path $Source
    foreach ($directory in @(
        Get-ChildItem -LiteralPath $resolvedSource -Directory -Recurse -Force
    )) {
        if (
            $null -ne $directory.LinkType -or
            ($directory.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Publish output must not contain reparse points."
        }
    }
    foreach ($file in @(
        Get-ChildItem -LiteralPath $resolvedSource -File -Recurse -Force
    )) {
        if (
            $null -ne $file.LinkType -or
            ($file.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Publish output must not contain reparse points."
        }
        $relative = [IO.Path]::GetRelativePath($resolvedSource, $file.FullName)
        $destinationPath = Join-Path $Destination $relative
        $destinationParent = [IO.Path]::GetDirectoryName($destinationPath)
        [IO.Directory]::CreateDirectory($destinationParent) | Out-Null
        [IO.File]::Copy($file.FullName, $destinationPath, $false)
    }
}

function Write-ValidatedManifest {
    param(
        [Parameter(Mandatory)]
        [string]$TemplatePath,

        [Parameter(Mandatory)]
        [object]$ReleaseMetadata,

        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    [xml]$manifest = Get-Content -LiteralPath $TemplatePath -Raw
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
        throw "Internal MSIX manifest structure is invalid."
    }

    $identity.SetAttribute("Name", [string]$ReleaseMetadata.PackageIdentity)
    $identity.SetAttribute("Publisher", [string]$ReleaseMetadata.Publisher)
    $identity.SetAttribute("Version", [string]$ReleaseMetadata.PackageVersion)
    $identity.SetAttribute(
        "ProcessorArchitecture",
        [string]$ReleaseMetadata.Architecture
    )
    $minimumVersion =
        "10.0.$([int]$ReleaseMetadata.MinimumWindowsBuild).0"
    $maximumVersionTested = [string]$ReleaseMetadata.MaximumVersionTested
    $target.SetAttribute("Name", "Windows.Desktop")
    $target.SetAttribute("MinVersion", $minimumVersion)
    $target.SetAttribute("MaxVersionTested", $maximumVersionTested)
    $application.SetAttribute("Id", "EMKETranslation")
    $application.SetAttribute("Executable", "EMKE.Windows.App.exe")
    $application.SetAttribute(
        "EntryPoint",
        "Windows.FullTrustApplication"
    )
    $application.SetAttribute("RuntimeBehavior", $uap10, "packagedClassicApp")
    $application.SetAttribute("TrustLevel", $uap10, "mediumIL")
    $capability.SetAttribute("Name", "runFullTrust")

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $settings.NewLineHandling = [Xml.NewLineHandling]::Replace
    $writer = [Xml.XmlWriter]::Create($OutputPath, $settings)
    try {
        $manifest.Save($writer)
    } finally {
        $writer.Dispose()
    }
}

function Write-SignerProvenance {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [ValidateSet("CN=EMKE Internal Test")]
        [string]$Subject,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-F]{40}$")]
        [string]$Thumbprint
    )

    $evidence = [ordered]@{
        schemaVersion = 1
        subject = $Subject
        thumbprint = $Thumbprint
        packageSha256 = (
            Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256
        ).Hash.ToUpperInvariant()
        certificateSha256 = (
            Get-FileHash -LiteralPath $CertificatePath -Algorithm SHA256
        ).Hash.ToUpperInvariant()
    }
    [IO.File]::WriteAllText(
        $OutputPath,
        (($evidence | ConvertTo-Json -Depth 3) + "`n"),
        [Text.UTF8Encoding]::new($false)
    )

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        "certificate_thumbprint=$Thumbprint" |
            Out-File `
                -LiteralPath $env:GITHUB_OUTPUT `
                -Encoding utf8 `
                -Append
    }
}

function Resolve-PublishPath {
    param(
        [Parameter(Mandatory)]
        [string]$AppProject,

        [Parameter(Mandatory)]
        [string]$Configuration,

        [Parameter(Mandatory)]
        [string]$WindowsRoot,

        [Parameter(Mandatory)]
        [object]$ReleaseMetadata
    )

    [xml]$buildProps = Get-Content -LiteralPath (
        Join-Path $WindowsRoot "Directory.Build.props"
    ) -Raw
    $targetFramework = [string]$buildProps.Project.PropertyGroup.TargetFramework
    $expectedTargetFramework = "net10.0-windows$($ReleaseMetadata.MinimumWindowsApiContract)"
    if ($targetFramework -cne $expectedTargetFramework) {
        throw "TargetFramework does not match the resolved Windows API contract."
    }
    return Join-Path ([IO.Path]::GetDirectoryName($AppProject)) (
        "bin/$Configuration/$targetFramework/win-$($ReleaseMetadata.Architecture)/publish"
    )
}

if ($ValidateStagingOnly) {
    Assert-StagingTree -Path $StagingDirectory
    return
}

if (
    [string]::IsNullOrWhiteSpace($PfxPath) -or
    [string]::IsNullOrWhiteSpace($PasswordEnvironmentVariable)
) {
    throw (
        "PfxPath and PasswordEnvironmentVariable are required for " +
        "production MSIX packaging."
    )
}
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7) {
    throw "Production MSIX packaging requires PowerShell 7 on Windows."
}
if ($env:PROCESSOR_ARCHITECTURE -cne "AMD64") {
    throw "Production MSIX packaging requires an x64 process."
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "../..")
)
$windowsRoot = Join-Path $repositoryRoot "Windows"
$artifactRoot = Join-Path $windowsRoot "artifacts/msix"
$stagingPath = $null
$resolvedPfxPath = $null
$temporaryStoreThumbprints = @()
$packagePath = $null
$certificatePath = $null
$provenancePath = $null
$packageSucceeded = $false
$deletePfxOnExit = $false
$password = $null
$securePassword = $null

try {
    $temporaryRoot = if (
        [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)
    ) {
        [IO.Path]::GetTempPath()
    } else {
        $env:RUNNER_TEMP
    }
    $resolvedPfxPath = Resolve-EphemeralPfxInput `
        -PfxPath $PfxPath `
        -TemporaryRoot $temporaryRoot `
        -RepositoryRoot $repositoryRoot
    $deletePfxOnExit = $true
    $password = [Environment]::GetEnvironmentVariable(
        $PasswordEnvironmentVariable,
        [EnvironmentVariableTarget]::Process
    )
    if ([string]::IsNullOrEmpty($password)) {
        throw "Signing password environment variable is unavailable."
    }

    [IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
    $artifactRoot = Assert-NoReparsePathChain -Path $artifactRoot
    $stagingPath = Join-Path (
        $artifactRoot
    ) ("staging-" + [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($stagingPath) | Out-Null
    $stagingPath = Assert-NoReparsePathChain -Path $stagingPath

    $versionFile = Join-Path $windowsRoot "version.json"
    $releaseMetadata = & (
        Join-Path $PSScriptRoot "resolve-version.ps1"
    ) -VersionFile $versionFile
    if ($null -eq $releaseMetadata) {
        throw "Windows package metadata resolution failed."
    }
    if (
        $releaseMetadata.Publisher -cne $script:ExpectedSubject -or
        $releaseMetadata.Channel -cne "internal"
    ) {
        throw "Resolved metadata does not match the Internal MSIX contract."
    }
    $packageBaseName = "EMKE-Translation-Windows-$($releaseMetadata.ProductVersion)-internal-$($releaseMetadata.Architecture)"
    $packagePath = Join-Path $artifactRoot "$packageBaseName.msix"
    $certificatePath = Join-Path $artifactRoot "$packageBaseName.cer"
    $provenancePath = Join-Path $artifactRoot "$packageBaseName.signing.json"

    $appProject = Join-Path (
        $windowsRoot
    ) "src/EMKE.Windows.App/EMKE.Windows.App.csproj"
    & dotnet publish $appProject `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "Self-contained WPF publish failed."
    }

    $publishPath = Resolve-PublishPath `
        -AppProject $appProject `
        -Configuration $Configuration `
        -WindowsRoot $windowsRoot `
        -ReleaseMetadata $releaseMetadata
    if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
        throw "Expected self-contained WPF publish output is unavailable."
    }
    Copy-VerifiedTree -Source $publishPath -Destination $stagingPath

    $nativePath = Join-Path (
        $repositoryRoot
    ) "Windows/artifacts/native/x64/Release/EMKE.NativeAudio.dll"
    if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
        throw "Release EMKE.NativeAudio.dll is unavailable."
    }
    $nativePath = Assert-NoReparsePathChain -Path $nativePath
    [IO.File]::Copy(
        $nativePath,
        (Join-Path $stagingPath "EMKE.NativeAudio.dll"),
        $true
    )

    $assetSource = Join-Path $windowsRoot "packaging/App/Assets"
    Copy-VerifiedTree `
        -Source $assetSource `
        -Destination (Join-Path $stagingPath "Assets")
    [IO.File]::Copy(
        (Join-Path $windowsRoot "packaging/compatibility.internal.json"),
        (Join-Path $stagingPath "compatibility.json"),
        $false
    )
    Write-ValidatedManifest `
        -TemplatePath (
            Join-Path $windowsRoot "packaging/App/AppxManifest.internal.xml"
        ) `
        -ReleaseMetadata $releaseMetadata `
        -OutputPath (Join-Path $stagingPath "AppxManifest.xml")
    Assert-StagingTree -Path $stagingPath

    $sdkTools = Resolve-WindowsSdkTools
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }
    & $sdkTools.MakeAppx pack /d $stagingPath /p $packagePath /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx package construction failed."
    }

    $certificateOutput = @(
        & (
            Join-Path $PSScriptRoot "verify-internal-signing-certificate.ps1"
        ) `
            -PfxPath $resolvedPfxPath `
            -PasswordEnvironmentVariable $PasswordEnvironmentVariable `
            -ExpectedSubject $script:ExpectedSubject `
            -ExportPublicCertificatePath $certificatePath
    )
    $thumbprintLines = @(
        $certificateOutput |
            Where-Object {
                $_ -match "^Public thumbprint: (?<thumbprint>[0-9A-F]{40})$"
            }
    )
    if ($thumbprintLines.Count -ne 1) {
        throw "Verified signing certificate thumbprint is unavailable."
    }
    $verifiedThumbprint = (
        [regex]::Match(
            $thumbprintLines[0],
            "^Public thumbprint: (?<thumbprint>[0-9A-F]{40})$"
        ).Groups["thumbprint"].Value
    ).ToUpperInvariant()
    if (
        Test-Path -LiteralPath (
            "Cert:\CurrentUser\My\$verifiedThumbprint"
        )
    ) {
        throw "The verified signing certificate already exists in CurrentUser\My."
    }

    $securePassword = ConvertTo-SecureString `
        -String $password `
        -AsPlainText `
        -Force
    $password = $null
    $preexistingMyThumbprints = @(
        Get-ChildItem -LiteralPath "Cert:\CurrentUser\My" |
            ForEach-Object { $_.Thumbprint }
    )
    $imported = @(
        Import-PfxCertificate `
        -FilePath $resolvedPfxPath `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -Password $securePassword `
        -Exportable:$false
    )
    $temporaryStoreThumbprints = @(
        Select-NewCertificateThumbprints `
            -PreexistingThumbprints $preexistingMyThumbprints `
            -ImportedThumbprints @(
                $imported |
                    ForEach-Object { $_.Thumbprint }
            )
    )
    $importedSigner = @(
        $imported |
            Where-Object {
                $_.Thumbprint.ToUpperInvariant() -ceq $verifiedThumbprint
            }
    )
    if (
        $importedSigner.Count -ne 1 -or
        $verifiedThumbprint -notin $temporaryStoreThumbprints
    ) {
        throw "Temporary signing certificate import validation failed."
    }

    & $sdkTools.SignTool sign `
        /sha1 $verifiedThumbprint `
        /s My `
        /fd SHA256 `
        $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed to sign the Internal MSIX."
    }
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Signed Internal MSIX output is unavailable."
    }
    Write-SignerProvenance `
        -PackagePath $packagePath `
        -CertificatePath $certificatePath `
        -OutputPath $provenancePath `
        -Subject $script:ExpectedSubject `
        -Thumbprint $verifiedThumbprint

    $packageSucceeded = $true
    Write-Output "Package: $packagePath"
    Write-Output "Certificate: $certificatePath"
    Write-Output "Verified signer thumbprint: $verifiedThumbprint"
    Write-Output "Signer provenance: $provenancePath"
} finally {
    $password = $null
    $securePassword = $null
    $cleanupActions = [Collections.Generic.List[object]]::new()
    foreach ($temporaryThumbprint in $temporaryStoreThumbprints) {
        $certificateStorePath =
            "Cert:\CurrentUser\My\$temporaryThumbprint"
        $cleanupCertificatePath = $certificateStorePath
        $cleanupActions.Add(
            [pscustomobject]@{
                Name = "CurrentUserMy:$temporaryThumbprint"
                Action = {
                    if (Test-Path -LiteralPath $cleanupCertificatePath) {
                        Remove-Item `
                            -LiteralPath $cleanupCertificatePath `
                            -Force
                    }
                }.GetNewClosure()
            }
        )
    }
    if ($null -ne $stagingPath) {
        $cleanupStagingPath = $stagingPath
        $cleanupActions.Add(
            [pscustomobject]@{
                Name = "staging-directory"
                Action = {
                    if (
                        Test-Path `
                            -LiteralPath $cleanupStagingPath `
                            -PathType Container
                    ) {
                        Remove-Item `
                            -LiteralPath $cleanupStagingPath `
                            -Recurse `
                            -Force
                    }
                }.GetNewClosure()
            }
        )
    }
    if ($deletePfxOnExit -and $null -ne $resolvedPfxPath) {
        $cleanupPfxPath = $resolvedPfxPath
        $cleanupActions.Add(
            [pscustomobject]@{
                Name = "ephemeral-pfx"
                Action = {
                    if (
                        Test-Path `
                            -LiteralPath $cleanupPfxPath `
                            -PathType Leaf
                    ) {
                        Remove-Item `
                            -LiteralPath $cleanupPfxPath `
                            -Force
                    }
                }.GetNewClosure()
            }
        )
    }
    if (-not $packageSucceeded) {
        foreach ($partialArtifact in @(
            $packagePath,
            $certificatePath,
            $provenancePath
        )) {
            if ([string]::IsNullOrWhiteSpace($partialArtifact)) {
                continue
            }
            $cleanupPartialArtifact = $partialArtifact
            $cleanupActions.Add(
                [pscustomobject]@{
                    Name = "partial-artifact:$(
                        [IO.Path]::GetFileName($partialArtifact)
                    )"
                    Action = {
                        if (
                            Test-Path `
                                -LiteralPath $cleanupPartialArtifact `
                                -PathType Leaf
                        ) {
                            Remove-Item `
                                -LiteralPath $cleanupPartialArtifact `
                                -Force
                        }
                    }.GetNewClosure()
                }
            )
        }
    }
    Invoke-CompleteCleanup -Actions @($cleanupActions)
}
