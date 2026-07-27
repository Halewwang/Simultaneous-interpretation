[CmdletBinding(DefaultParameterSetName = "Install")]
param(
    [Parameter(Mandatory, ParameterSetName = "Install")]
    [Parameter(Mandatory, ParameterSetName = "Digest")]
    [string]$PackagePath,

    [Parameter(Mandatory, ParameterSetName = "Install")]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedPackageSha256,

    [Parameter(Mandatory, ParameterSetName = "Install")]
    [string]$SmokePath,

    [Parameter(Mandatory, ParameterSetName = "Install")]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedSmokeSha256,

    [Parameter(ParameterSetName = "Install")]
    [switch]$ConfirmInstall,

    [Parameter(Mandatory, ParameterSetName = "Digest")]
    [switch]$PrintPackageSha256
)

if ($MyInvocation.InvocationName -ceq ".") {
    throw "Dot-source invocation is forbidden for this lifecycle script."
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:TargetHardwareId = "ROOT\EMKEVIRTUALAUDIO"
$script:MinimumWindowsBuild = 26200

function Assert-SupportedWindowsHost {
    if ($PSVersionTable.PSVersion.Major -ne 7) {
        throw "This lab tool requires PowerShell 7."
    }
    if (-not $IsWindows) {
        throw "This lab tool can only run on Windows."
    }
}

function Get-WindowsBuildNumber {
    return [Environment]::OSVersion.Version.Build
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Assert-LabMachinePrerequisites {
    $build = Get-WindowsBuildNumber
    if ($build -lt $script:MinimumWindowsBuild) {
        throw (
            "Windows build $build is unsupported; build " +
            "$($script:MinimumWindowsBuild) or newer is required."
        )
    }
    if (-not (Test-IsAdministrator)) {
        throw "An elevated PowerShell 7 administrator session is required."
    }
}

function Assert-LocalNonReparsePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet("Container", "Leaf")]
        [string]$ExpectedType
    )

    if ($Path -match "^[\\/]{2}") {
        throw "UNC and device paths are forbidden for lifecycle inputs."
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path `
        -LiteralPath $fullPath `
        -PathType $ExpectedType)) {
        throw "Required $ExpectedType path does not exist: $Path"
    }

    $current = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne
            0) {
            throw "Lifecycle paths must not contain a reparse point: $Path"
        }
        $current = if ($current -is [IO.FileInfo]) {
            $current.Directory
        } else {
            $current.Parent
        }
    }
    return $fullPath
}

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return Assert-LocalNonReparsePath -Path $Path -ExpectedType Leaf
}

function Resolve-SystemPnpUtil {
    $systemDirectory = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::System
    )
    if ([string]::IsNullOrWhiteSpace($systemDirectory)) {
        throw "Windows system directory could not be resolved."
    }
    $pnpUtil = Join-Path $systemDirectory "pnputil.exe"
    if (-not (Test-Path -LiteralPath $pnpUtil -PathType Leaf)) {
        throw "System pnputil.exe is missing: $pnpUtil"
    }
    return $pnpUtil
}

function Get-SinglePackageFile {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Files,

        [Parameter(Mandatory)]
        [string]$Extension
    )

    $matches = @($Files | Where-Object { $_.Extension -ieq $Extension })
    if ($matches.Count -ne 1) {
        throw (
            "Driver package must contain exactly one $Extension file; " +
            "found $($matches.Count)."
        )
    }
    return $matches[0]
}

function Get-StrictDriverPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    $resolved = Assert-LocalNonReparsePath `
        -Path $Directory `
        -ExpectedType Container
    $nested = @(Get-ChildItem -LiteralPath $resolved -Directory -Force)
    if ($nested.Count -ne 0) {
        throw "Driver package must be flat."
    }
    $files = @(Get-ChildItem -LiteralPath $resolved -File -Force)
    if ($files.Count -ne 3) {
        throw "Driver package must contain only one INF, one SYS, and one CAT."
    }
    foreach ($file in $files) {
        [void](Assert-LocalNonReparsePath `
            -Path $file.FullName `
            -ExpectedType Leaf)
    }

    return [pscustomobject]@{
        Directory = $resolved
        Inf = Get-SinglePackageFile -Files $files -Extension ".inf"
        Sys = Get-SinglePackageFile -Files $files -Extension ".sys"
        Cat = Get-SinglePackageFile -Files $files -Extension ".cat"
    }
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolved = Resolve-RequiredFile -Path $Path
    return (Get-FileHash `
        -LiteralPath $resolved `
        -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Invoke-DriverPackageVerifier {
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory
    )

    $verifier = Join-Path $PSScriptRoot "verify-driver-package.ps1"
    if (-not (Test-Path -LiteralPath $verifier -PathType Leaf)) {
        throw "Required driver package verifier is missing: $verifier"
    }
    & $verifier -PackageDirectory $PackageDirectory
}

function Get-DriverPackageSha256 {
    param(
        [Parameter(Mandatory)]
        [psobject]$Package
    )

    $infHash = (Get-FileHash `
        -LiteralPath $Package.Inf.FullName `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    $sysHash = (Get-FileHash `
        -LiteralPath $Package.Sys.FullName `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    $catHash = (Get-FileHash `
        -LiteralPath $Package.Cat.FullName `
        -Algorithm SHA256).Hash.ToUpperInvariant()
    $manifest = (
        "EMKE-DRIVER-PACKAGE-SHA256-V1`n" +
        "INF=$infHash`n" +
        "SYS=$sysHash`n" +
        "CAT=$catHash`n"
    )
    $bytes = [Text.Encoding]::UTF8.GetBytes($manifest)
    $digest = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($digest)
}

function Test-FixedSha256Equal {
    param(
        [Parameter(Mandatory)]
        [string]$Expected,

        [Parameter(Mandatory)]
        [string]$Actual
    )

    if ($Expected -notmatch "^[0-9A-Fa-f]{64}$" -or
        $Actual -notmatch "^[0-9A-Fa-f]{64}$") {
        return $false
    }
    $expectedBytes = [Convert]::FromHexString($Expected)
    $actualBytes = [Convert]::FromHexString($Actual)
    return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
        $expectedBytes,
        $actualBytes
    )
}

function Get-ProtectedStagingSddl {
    return "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"
}

function Assert-ProtectedStagingSecurityDescriptor {
    param(
        [Parameter(Mandatory)]
        [string]$Sddl
    )

    $descriptor =
        [Security.AccessControl.RawSecurityDescriptor]::new($Sddl)
    if ($descriptor.Owner.Value -cne "S-1-5-32-544") {
        throw "Protected staging owner must be BUILTIN\Administrators."
    }
    if (($descriptor.ControlFlags -band
        [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -eq
        0) {
        throw "Protected staging DACL must not inherit access rules."
    }
    if ($descriptor.DiscretionaryAcl.Count -ne 2) {
        throw "Protected staging DACL must contain exactly two access rules."
    }
    $expectedSids = @("S-1-5-18", "S-1-5-32-544")
    foreach ($ace in $descriptor.DiscretionaryAcl) {
        if ($ace.AceQualifier -ne
            [Security.AccessControl.AceQualifier]::AccessAllowed -or
            $ace.AccessMask -ne 0x1F01FF -or
            ($ace.AceFlags -band
                [Security.AccessControl.AceFlags]::ContainerInherit) -eq 0 -or
            ($ace.AceFlags -band
                [Security.AccessControl.AceFlags]::ObjectInherit) -eq 0 -or
            $ace.SecurityIdentifier.Value -notin $expectedSids) {
            throw (
                "Protected staging DACL may grant inherited FullControl " +
                "only to SYSTEM and BUILTIN\Administrators."
            )
        }
    }
    $actualSids = @($descriptor.DiscretionaryAcl |
        ForEach-Object { $_.SecurityIdentifier.Value } |
        Sort-Object -Unique)
    if ($actualSids.Count -ne 2) {
        throw "Protected staging DACL has duplicate or missing principals."
    }
}

function Get-SystemStagingBase {
    $programData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData
    )
    if ([string]::IsNullOrWhiteSpace($programData)) {
        throw "The local system staging base could not be resolved."
    }
    return [IO.Path]::GetFullPath(
        (Join-Path $programData "EMKE")
    )
}

function Get-SystemStagingRoot {
    return [IO.Path]::GetFullPath(
        (Join-Path (Get-SystemStagingBase) "DriverLabStaging")
    )
}

function Set-ProtectedStagingAcl {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $sections =
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor
        [Security.AccessControl.AccessControlSections]::Access
    $security.SetSecurityDescriptorSddlForm(
        (Get-ProtectedStagingSddl),
        $sections
    )
    Set-Acl -LiteralPath $Path -AclObject $security -ErrorAction Stop
}

function Assert-ProtectedStagingAcl {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolved = Assert-LocalNonReparsePath `
        -Path $Path `
        -ExpectedType Container
    $acl = Get-Acl -LiteralPath $resolved -ErrorAction Stop
    Assert-ProtectedStagingSecurityDescriptor -Sddl $acl.Sddl
}

function Assert-ProtectedStagingChain {
    param(
        [Parameter(Mandatory)]
        [string]$StagingPath
    )

    $base = [IO.Path]::GetFullPath((Get-SystemStagingBase))
    $root = [IO.Path]::GetFullPath((Get-SystemStagingRoot))
    $actual = [IO.Path]::GetFullPath($StagingPath)
    $expectedParent = [IO.DirectoryInfo]::new($actual).Parent.FullName
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
        $expectedParent,
        $root
    )) {
        throw "Protected staging path is outside the exact GUID child root."
    }
    foreach ($path in @($base, $root, $actual)) {
        $resolved = Assert-LocalNonReparsePath `
            -Path $path `
            -ExpectedType Container
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            ([IO.Path]::GetFullPath($resolved)),
            $path
        )) {
            throw "Protected staging chain resolved to an unexpected path."
        }
        Assert-ProtectedStagingAcl -Path $path
    }
    return $actual
}

function New-ProtectedStagingDirectory {
    $base = Get-SystemStagingBase
    [IO.Directory]::CreateDirectory($base) | Out-Null
    [void](Assert-LocalNonReparsePath `
        -Path $base `
        -ExpectedType Container)
    Set-ProtectedStagingAcl -Path $base
    Assert-ProtectedStagingAcl -Path $base

    $root = Get-SystemStagingRoot
    [IO.Directory]::CreateDirectory($root) | Out-Null
    [void](Assert-LocalNonReparsePath `
        -Path $root `
        -ExpectedType Container)
    Set-ProtectedStagingAcl -Path $root
    Assert-ProtectedStagingAcl -Path $root

    $token = [guid]::NewGuid().ToString("N")
    $path = Join-Path $root $token
    [IO.Directory]::CreateDirectory($path) | Out-Null
    [void](Assert-LocalNonReparsePath `
        -Path $path `
        -ExpectedType Container)
    Set-ProtectedStagingAcl -Path $path
    Assert-ProtectedStagingAcl -Path $path
    [void](Assert-ProtectedStagingChain -StagingPath $path)
    return [pscustomobject]@{
        Path = $path
        Token = $token
    }
}

function Remove-ProtectedStagingDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Token
    )

    $parsedToken = [guid]::Empty
    if (-not [guid]::TryParseExact($Token, "N", [ref]$parsedToken)) {
        throw "Owned staging cleanup requires an exact GUID token."
    }
    $root = [IO.Path]::GetFullPath((Get-SystemStagingRoot))
    $expected = [IO.Path]::GetFullPath((Join-Path $root $Token))
    $actual = [IO.Path]::GetFullPath($Path)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($actual, $expected)) {
        throw "Refusing cleanup outside the exact owned staging directory."
    }
    if (-not (Test-Path -LiteralPath $actual)) {
        return
    }
    [void](Assert-ProtectedStagingChain -StagingPath $actual)
    Remove-Item -LiteralPath $actual -Recurse -Force -ErrorAction Stop
}

function Copy-InstallInputsToStaging {
    param(
        [Parameter(Mandatory)]
        [psobject]$Package,

        [Parameter(Mandatory)]
        [psobject]$SmokeFile,

        [Parameter(Mandatory)]
        [string]$StagingRoot
    )

    $resolvedRoot = Assert-LocalNonReparsePath `
        -Path $StagingRoot `
        -ExpectedType Container
    [void](Assert-ProtectedStagingChain -StagingPath $resolvedRoot)
    if (@(Get-ChildItem -LiteralPath $resolvedRoot -Force).Count -ne 0) {
        throw "Owned staging directory must be empty before copying inputs."
    }
    $packageDirectory = Join-Path $resolvedRoot "package"
    $smokeDirectory = Join-Path $resolvedRoot "smoke"
    [IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($smokeDirectory) | Out-Null
    foreach ($source in @($Package.Inf, $Package.Sys, $Package.Cat)) {
        $destination = Join-Path $packageDirectory $source.Name
        [IO.File]::Copy($source.FullName, $destination, $false)
    }
    $smokeDestination = Join-Path $smokeDirectory $SmokeFile.Name
    [IO.File]::Copy($SmokeFile.FullName, $smokeDestination, $false)

    $stagedPackage = Get-StrictDriverPackage -Directory $packageDirectory
    $stagedSmokePath = Resolve-RequiredFile -Path $smokeDestination
    $stagedSmoke = Get-Item -LiteralPath $stagedSmokePath -Force
    return [pscustomobject]@{
        Package = $stagedPackage
        Smoke = $stagedSmoke
        StagingRoot = $resolvedRoot
        PackageSha256 = Get-DriverPackageSha256 -Package $stagedPackage
        SmokeSha256 = Get-FileSha256 -Path $stagedSmoke.FullName
    }
}

function Assert-StagedInputsUnchanged {
    param(
        [Parameter(Mandatory)]
        [psobject]$StagedInputs
    )

    [void](Assert-ProtectedStagingChain `
        -StagingPath $StagedInputs.StagingRoot)
    $package = Get-StrictDriverPackage `
        -Directory $StagedInputs.Package.Directory
    $packageSha256 = Get-DriverPackageSha256 -Package $package
    if (-not (Test-FixedSha256Equal `
        -Expected $StagedInputs.PackageSha256 `
        -Actual $packageSha256)) {
        throw "Protected staged driver package changed after validation."
    }
    $smokeSha256 = Get-FileSha256 -Path $StagedInputs.Smoke.FullName
    if (-not (Test-FixedSha256Equal `
        -Expected $StagedInputs.SmokeSha256 `
        -Actual $smokeSha256)) {
        throw "Protected staged Smoke SHA-256 changed after validation."
    }
}

function Get-CatalogSignatureMetadata {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$Catalog
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Catalog.FullName
    $certificate = $signature.SignerCertificate
    $summary = if ($null -eq $certificate) {
        ""
    } else {
        $certificate.GetCertHashString(
            [Security.Cryptography.HashAlgorithmName]::SHA256
        )
    }
    return [pscustomobject]@{
        Status = [string]$signature.Status
        Certificate = $certificate
        SummarySha256 = $summary
    }
}

function Assert-CatalogSignatureValid {
    param(
        [Parameter(Mandatory)]
        [psobject]$Metadata
    )

    if ($Metadata.Status -cne "Valid") {
        throw (
            "Driver catalog signature status must be Valid; received " +
            "'$($Metadata.Status)'."
        )
    }
    if ($null -eq $Metadata.Certificate) {
        throw "Driver catalog signature has no signing certificate."
    }
    if ([string]::IsNullOrWhiteSpace($Metadata.SummarySha256) -or
        $Metadata.SummarySha256 -notmatch "^[0-9A-Fa-f]{64}$") {
        throw "Driver catalog signing certificate has no usable SHA-256 summary."
    }
}

function ConvertTo-InfSectionMap {
    param(
        [Parameter(Mandatory)]
        [string]$Text
    )

    $sections =
        [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )
    $currentSection = $null
    foreach ($rawLine in ($Text -split "\r?\n")) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or
            $line.StartsWith(";", [StringComparison]::Ordinal)) {
            continue
        }
        if ($line -match "^\[(?<name>[^\[\]]+)\]$") {
            $name = $Matches["name"].Trim()
            if ($sections.ContainsKey($name)) {
                throw "INF contains duplicate section [$name]."
            }
            $currentSection =
                [Collections.Generic.List[string]]::new()
            $sections.Add($name, $currentSection)
            continue
        }
        if ($null -eq $currentSection) {
            throw "INF content appeared before the first section."
        }
        $currentSection.Add($line)
    }
    return $sections
}

function ConvertTo-InfKeyValueMap {
    param(
        [Parameter(Mandatory)]
        [object]$Lines,

        [Parameter(Mandatory)]
        [string]$SectionName
    )

    $values =
        [Collections.Generic.Dictionary[string, string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )
    foreach ($line in $Lines) {
        $separator = $line.IndexOf("=")
        if ($separator -le 0) {
            throw "INF section [$SectionName] contains a malformed entry."
        }
        $key = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if ([string]::IsNullOrWhiteSpace($key) -or
            [string]::IsNullOrWhiteSpace($value) -or
            $values.ContainsKey($key)) {
            throw "INF section [$SectionName] has a duplicate or empty key."
        }
        $values.Add($key, $value)
    }
    return $values
}

function ConvertFrom-QuotedInfString {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    if ($Value -match '^"(?<value>[^"]*)"$') {
        return $Matches["value"]
    }
    return $Value
}

function Get-DriverInfMetadata {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$Inf,

        [Parameter(Mandatory)]
        [int]$WindowsBuild
    )

    $text = Get-Content -LiteralPath $Inf.FullName -Raw
    $sections = ConvertTo-InfSectionMap -Text $text
    foreach ($requiredSection in @("Version", "Manufacturer", "Strings")) {
        if (-not $sections.ContainsKey($requiredSection)) {
            throw "INF is missing required section [$requiredSection]."
        }
    }

    $strings = ConvertTo-InfKeyValueMap `
        -Lines $sections["Strings"] `
        -SectionName "Strings"
    if (-not $strings.ContainsKey("ProviderName") -or
        -not $strings.ContainsKey("ManufacturerName")) {
        throw "INF [Strings] must define provider and manufacturer names."
    }
    $providerName =
        ConvertFrom-QuotedInfString -Value $strings["ProviderName"]
    $manufacturerName =
        ConvertFrom-QuotedInfString -Value $strings["ManufacturerName"]
    if ($providerName -cne "EMKE" -or $manufacturerName -cne "EMKE") {
        throw "INF provider and manufacturer must both resolve to EMKE."
    }

    $version = ConvertTo-InfKeyValueMap `
        -Lines $sections["Version"] `
        -SectionName "Version"
    if (-not $version.ContainsKey("Provider") -or
        $version["Provider"] -cne "%ProviderName%" -or
        -not $version.ContainsKey("DriverVer")) {
        throw "INF [Version] provider or DriverVer is invalid."
    }
    $driverVer = $version["DriverVer"]
    if ($driverVer -notmatch (
        "^(?<date>[0-9]{2}/[0-9]{2}/[0-9]{4})," +
        "(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)$"
    )) {
        throw "INF DriverVer must contain one date and four-part version."
    }
    $driverVersion = ([version]$Matches["version"]).ToString()

    if ($WindowsBuild -lt $script:MinimumWindowsBuild) {
        throw "INF Models selection requires Windows build 26200 or newer."
    }
    $manufacturerLines = $sections["Manufacturer"]
    if ($manufacturerLines.Count -ne 1) {
        throw "INF [Manufacturer] must contain exactly one Models mapping."
    }
    $manufacturer = ConvertTo-InfKeyValueMap `
        -Lines $manufacturerLines `
        -SectionName "Manufacturer"
    if ($manufacturer.Count -ne 1 -or
        -not $manufacturer.ContainsKey("%ManufacturerName%")) {
        throw "INF [Manufacturer] mapping is not the frozen EMKE mapping."
    }
    $manufacturerParts = @($manufacturer["%ManufacturerName%"].Split(",") |
        ForEach-Object { $_.Trim() })
    if ($manufacturerParts.Count -ne 2 -or
        $manufacturerParts[0] -cne "EMKE" -or
        $manufacturerParts[1] -cne "NTamd64.10.0...26200") {
        throw "INF [Manufacturer] has an unsupported Models decoration."
    }
    $modelSection = "EMKE.NTamd64.10.0...26200"
    if (-not $sections.ContainsKey($modelSection)) {
        throw "INF is missing the active x64 Models section [$modelSection]."
    }
    foreach ($sectionName in $sections.Keys) {
        if ($sectionName -imatch "^EMKE\.NT" -and
            $sectionName -cne $modelSection) {
            throw "INF contains an unexpected inactive Models section."
        }
    }
    $modelLines = $sections[$modelSection]
    if ($modelLines.Count -ne 1) {
        throw "Active INF Models section must contain exactly one model entry."
    }
    $modelSeparator = $modelLines[0].IndexOf("=")
    if ($modelSeparator -le 0) {
        throw "Active INF Models entry is malformed."
    }
    $modelParts = @(
        $modelLines[0].Substring($modelSeparator + 1).Split(",") |
        ForEach-Object { $_.Trim() }
    )
    if ($modelParts.Count -ne 2 -or
        $modelParts[0] -cne "EMKE_Install" -or
        $modelParts[1] -cne $script:TargetHardwareId) {
        throw (
            "Active INF model must use EMKE_Install and contain only the " +
            "exact target hardware ID with no compatible IDs."
        )
    }
    $installSection = "EMKE_Install"
    if (-not $sections.ContainsKey("$installSection.NT")) {
        throw "INF active model references a missing install section."
    }

    return [pscustomobject]@{
        DriverVer = $driverVer
        DriverVersion = $driverVersion
        ProviderName = $providerName
        ModelSection = $modelSection
        InstallSection = $installSection
        HardwareId = $modelParts[1]
    }
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [ValidateRange(1, 900)]
        [int]$TimeoutSeconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start required process: $Executable"
        }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $terminationDetail = "process-tree termination was attempted"
            try {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) {
                    $terminationDetail = (
                        "process-tree termination was attempted but bounded " +
                        "reaping did not complete"
                    )
                }
            } catch {
                $terminationDetail = (
                    "process-tree termination failed: " +
                    $_.Exception.Message
                )
            }
            $exception = [TimeoutException]::new(
                "Process timed out after $TimeoutSeconds seconds; " +
                "state uncertain; perform read-only inventory before any " +
                "further mutation; $terminationDetail."
            )
            $exception.Data["StateUncertain"] = $true
            throw $exception
        }
        $process.WaitForExit()
        $stdout = $standardOutput.GetAwaiter().GetResult()
        $stderr = $standardError.GetAwaiter().GetResult()
        $combined = $stdout + [Environment]::NewLine + $stderr
        $lines = @($combined -split "\r?\n" | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        })
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            OutputLines = $lines
        }
    } finally {
        $process.Dispose()
    }
}

function Invoke-PnpUtilInstall {
    param(
        [Parameter(Mandatory)]
        [string]$InfPath
    )

    $pnpUtil = Resolve-SystemPnpUtil
    $result = Invoke-CapturedProcess `
        -Executable $pnpUtil `
        -Arguments @("/add-driver", $InfPath, "/install") `
        -TimeoutSeconds 120
    if ($result.ExitCode -ne 0) {
        throw "pnputil driver installation failed with exit code $($result.ExitCode)."
    }
}

function Get-RootDevnodeSetupApiSource {
    return @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Emke.DriverLab
{
    public sealed class RootDevnodeCreateException : Exception
    {
        public bool StateUncertain { get; }
        public bool RollbackCompleted { get; }
        public string InstanceId { get; }
        public Exception CleanupFailure { get; }

        public RootDevnodeCreateException(
            string message,
            string instanceId,
            bool stateUncertain,
            bool rollbackCompleted,
            Exception originalFailure,
            Exception cleanupFailure)
            : base(message, originalFailure)
        {
            StateUncertain = stateUncertain;
            RollbackCompleted = rollbackCompleted;
            InstanceId = instanceId;
            CleanupFailure = cleanupFailure;
            Data["StateUncertain"] = stateUncertain;
            Data["RollbackCompleted"] = rollbackCompleted;
            Data["InstanceId"] = instanceId;
        }
    }

    public static class RootDevnodeRegistrationTransaction
    {
        public static string Complete(
            string expectedInstanceId,
            Action register,
            Func<string> readRegisteredInstanceId,
            Action rollback)
        {
            if (String.IsNullOrWhiteSpace(expectedInstanceId))
            {
                throw new ArgumentException(
                    "Expected instance ID is required.",
                    nameof(expectedInstanceId));
            }
            if (register == null)
            {
                throw new ArgumentNullException(nameof(register));
            }
            if (readRegisteredInstanceId == null)
            {
                throw new ArgumentNullException(nameof(readRegisteredInstanceId));
            }
            if (rollback == null)
            {
                throw new ArgumentNullException(nameof(rollback));
            }

            bool registered = false;
            try
            {
                register();
                registered = true;
                string observedInstanceId = readRegisteredInstanceId();
                if (!String.Equals(
                    observedInstanceId,
                    expectedInstanceId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Post-registration instance ID changed unexpectedly.");
                }
                return observedInstanceId;
            }
            catch (Exception originalFailure)
            {
                if (!registered)
                {
                    throw;
                }
                try
                {
                    rollback();
                }
                catch (Exception cleanupFailure)
                {
                    throw new RootDevnodeCreateException(
                        message:
                            "Root device creation failed after registration; " +
                            "exact same-handle rollback failed; " +
                            "state uncertain.",
                        instanceId: expectedInstanceId,
                        stateUncertain: true,
                        rollbackCompleted: false,
                        originalFailure: originalFailure,
                        cleanupFailure: cleanupFailure);
                }
                throw new RootDevnodeCreateException(
                    message:
                        "Root device creation failed after registration; " +
                        "exact same-handle rollback completed.",
                    instanceId: expectedInstanceId,
                    stateUncertain: false,
                    rollbackCompleted: true,
                    originalFailure: originalFailure,
                    cleanupFailure: null);
            }
        }
    }

    public static class RootDevnodeSetupApi
    {
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        private const uint DicdGenerateId = 0x00000001;
        private const uint SpdrpHardwareId = 0x00000001;
        private const uint DifRemove = 0x00000005;
        private const uint DifRegisterDevice = 0x00000019;
        private const uint DiRemoveDeviceGlobal = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpClassInstallHeader
        {
            public uint Size;
            public uint InstallFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpRemoveDeviceParams
        {
            public SpClassInstallHeader ClassInstallHeader;
            public uint Scope;
            public uint HwProfile;
        }

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetINFClassW(
            string infName,
            out Guid classGuid,
            StringBuilder className,
            uint classNameSize,
            out uint requiredSize);

        [DllImport(
            "setupapi.dll",
            EntryPoint = "SetupDiCreateDeviceInfoList",
            SetLastError = true)]
        private static extern IntPtr CreateDeviceInfoListForClass(
            ref Guid classGuid,
            IntPtr parentWindow);

        [DllImport(
            "setupapi.dll",
            EntryPoint = "SetupDiCreateDeviceInfoList",
            SetLastError = true)]
        private static extern IntPtr CreateEmptyDeviceInfoList(
            IntPtr classGuid,
            IntPtr parentWindow);

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiCreateDeviceInfoW(
            IntPtr deviceInfoSet,
            string deviceName,
            ref Guid classGuid,
            string deviceDescription,
            IntPtr parentWindow,
            uint creationFlags,
            ref SpDevinfoData deviceInfoData);

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiSetDeviceRegistryPropertyW(
            IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData,
            uint property,
            byte[] propertyBuffer,
            uint propertyBufferSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiCallClassInstaller(
            uint installFunction,
            IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData);

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiSetClassInstallParamsW(
            IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData,
            ref SpRemoveDeviceParams classInstallParams,
            uint classInstallParamsSize);

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData,
            StringBuilder deviceInstanceId,
            uint deviceInstanceIdSize,
            out uint requiredSize);

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiOpenDeviceInfoW(
            IntPtr deviceInfoSet,
            string deviceInstanceId,
            IntPtr parentWindow,
            uint openFlags,
            ref SpDevinfoData deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(
            IntPtr deviceInfoSet);

        private static void ThrowLastError(string operation)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                operation + " failed.");
        }

        private static SpDevinfoData NewDeviceInfoData()
        {
            return new SpDevinfoData
            {
                Size = (uint)Marshal.SizeOf<SpDevinfoData>()
            };
        }

        private static void RemoveRegisteredDeviceFromInfoElement(
            IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData)
        {
            var removeParams = new SpRemoveDeviceParams
            {
                ClassInstallHeader = new SpClassInstallHeader
                {
                    Size = (uint)Marshal.SizeOf<SpClassInstallHeader>(),
                    InstallFunction = DifRemove
                },
                Scope = DiRemoveDeviceGlobal,
                HwProfile = 0
            };
            if (!SetupDiSetClassInstallParamsW(
                deviceInfoSet,
                ref deviceInfoData,
                ref removeParams,
                (uint)Marshal.SizeOf<SpRemoveDeviceParams>()))
            {
                ThrowLastError("SetupDiSetClassInstallParams(DIF_REMOVE)");
            }
            if (!SetupDiCallClassInstaller(
                DifRemove,
                deviceInfoSet,
                ref deviceInfoData))
            {
                ThrowLastError("SetupDiCallClassInstaller(DIF_REMOVE)");
            }
        }

        private static void ValidateExactInstanceId(string instanceId)
        {
            const string prefix = "ROOT\\EMKEVIRTUALAUDIO\\";
            if (String.IsNullOrWhiteSpace(instanceId) ||
                !instanceId.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Instance ID is outside the exact EMKE root target.",
                    nameof(instanceId));
            }
            string suffix = instanceId.Substring(prefix.Length);
            if (suffix.Length == 0 || suffix.Contains("\\"))
            {
                throw new ArgumentException(
                    "Instance ID is not one exact EMKE root instance.",
                    nameof(instanceId));
            }
        }

        public static string GetRootDeviceName(string hardwareId)
        {
            const string prefix = "ROOT\\";
            if (!String.Equals(
                    hardwareId,
                    "ROOT\\EMKEVIRTUALAUDIO",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Only the exact EMKE root hardware ID is allowed.",
                    nameof(hardwareId));
            }
            string deviceName = hardwareId.Substring(prefix.Length);
            if (deviceName.Length == 0 || deviceName.Contains("\\"))
            {
                throw new ArgumentException(
                    "Root hardware ID has an invalid device name.",
                    nameof(hardwareId));
            }
            return deviceName;
        }

        private static string GetDeviceInstanceIdFromInfoElement(
            IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData)
        {
            uint requiredSize;
            var instanceId = new StringBuilder(512);
            if (!SetupDiGetDeviceInstanceIdW(
                deviceInfoSet,
                ref deviceInfoData,
                instanceId,
                (uint)instanceId.Capacity,
                out requiredSize))
            {
                ThrowLastError("SetupDiGetDeviceInstanceId");
            }
            string result = instanceId.ToString();
            ValidateExactInstanceId(result);
            return result;
        }

        public static string Create(string infPath, string hardwareId)
        {
            string deviceName = GetRootDeviceName(hardwareId);

            Guid classGuid;
            uint requiredSize;
            var className = new StringBuilder(256);
            if (!SetupDiGetINFClassW(
                infPath,
                out classGuid,
                className,
                (uint)className.Capacity,
                out requiredSize))
            {
                ThrowLastError("SetupDiGetINFClass");
            }

            IntPtr deviceInfoSet = CreateDeviceInfoListForClass(
                ref classGuid,
                IntPtr.Zero);
            if (deviceInfoSet == InvalidHandleValue)
            {
                ThrowLastError("SetupDiCreateDeviceInfoList");
            }
            try
            {
                SpDevinfoData deviceInfoData = NewDeviceInfoData();
                if (!SetupDiCreateDeviceInfoW(
                    deviceInfoSet,
                    deviceName,
                    ref classGuid,
                    null,
                    IntPtr.Zero,
                    DicdGenerateId,
                    ref deviceInfoData))
                {
                    ThrowLastError("SetupDiCreateDeviceInfo");
                }

                byte[] multiString = Encoding.Unicode.GetBytes(
                    hardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryPropertyW(
                    deviceInfoSet,
                    ref deviceInfoData,
                    SpdrpHardwareId,
                    multiString,
                    (uint)multiString.Length))
                {
                    ThrowLastError(
                        "SetupDiSetDeviceRegistryProperty" +
                        "(SPDRP_HARDWAREID)");
                }

                string result = GetDeviceInstanceIdFromInfoElement(
                    deviceInfoSet,
                    ref deviceInfoData);
                return RootDevnodeRegistrationTransaction.Complete(
                    result,
                    delegate
                    {
                    if (!SetupDiCallClassInstaller(
                        DifRegisterDevice,
                        deviceInfoSet,
                        ref deviceInfoData))
                    {
                        ThrowLastError(
                            "SetupDiCallClassInstaller(DIF_REGISTERDEVICE)");
                    }
                    },
                    delegate
                    {
                        return GetDeviceInstanceIdFromInfoElement(
                            deviceInfoSet,
                            ref deviceInfoData);
                    },
                    delegate
                    {
                        RemoveRegisteredDeviceFromInfoElement(
                            deviceInfoSet,
                            ref deviceInfoData);
                    });
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        public static void RemoveExact(string instanceId)
        {
            ValidateExactInstanceId(instanceId);
            IntPtr deviceInfoSet = CreateEmptyDeviceInfoList(
                IntPtr.Zero,
                IntPtr.Zero);
            if (deviceInfoSet == InvalidHandleValue)
            {
                ThrowLastError("SetupDiCreateDeviceInfoList");
            }
            try
            {
                SpDevinfoData deviceInfoData = NewDeviceInfoData();
                if (!SetupDiOpenDeviceInfoW(
                    deviceInfoSet,
                    instanceId,
                    IntPtr.Zero,
                    0,
                    ref deviceInfoData))
                {
                    ThrowLastError("SetupDiOpenDeviceInfo");
                }
                RemoveRegisteredDeviceFromInfoElement(
                    deviceInfoSet,
                    ref deviceInfoData);
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }
    }
}
'@
}

function Initialize-RootDevnodeSetupApi {
    if ($null -eq ("Emke.DriverLab.RootDevnodeSetupApi" -as [type])) {
        Add-Type `
            -Language CSharp `
            -TypeDefinition (Get-RootDevnodeSetupApiSource)
    }
}

function Get-RootDevnodeFailureMetadata {
    param(
        [Parameter(Mandatory)]
        [Exception]$Exception
    )

    $current = $Exception
    while ($null -ne $current) {
        if ($current.Data.Contains("StateUncertain") -or
            $null -ne $current.PSObject.Properties["StateUncertain"]) {
            $stateUncertain = if (
                $current.Data.Contains("StateUncertain")
            ) {
                [bool]$current.Data["StateUncertain"]
            } else {
                [bool]$current.StateUncertain
            }
            $rollbackCompleted = if (
                $current.Data.Contains("RollbackCompleted")
            ) {
                [bool]$current.Data["RollbackCompleted"]
            } else {
                [bool]$current.RollbackCompleted
            }
            $instanceId = if ($current.Data.Contains("InstanceId")) {
                [string]$current.Data["InstanceId"]
            } else {
                [string]$current.InstanceId
            }
            return [pscustomobject]@{
                Failure = $current
                StateUncertain = $stateUncertain
                RollbackCompleted = $rollbackCompleted
                InstanceId = $instanceId
            }
        }
        $current = $current.InnerException
    }
    return $null
}

function New-RootDevnodeFromInf {
    param(
        [Parameter(Mandatory)]
        [string]$InfPath,

        [Parameter(Mandatory)]
        [string]$HardwareId
    )

    if ($HardwareId -cne $script:TargetHardwareId) {
        throw "Only the exact EMKE root hardware ID may be created."
    }
    Initialize-RootDevnodeSetupApi
    try {
        return [Emke.DriverLab.RootDevnodeSetupApi]::Create(
            $InfPath,
            $HardwareId
        )
    } catch {
        $outerFailure = $_.Exception
        $metadata = Get-RootDevnodeFailureMetadata `
            -Exception $outerFailure
        if ($null -eq $metadata) {
            throw
        }
        $normalized = [InvalidOperationException]::new(
            $metadata.Failure.Message,
            $outerFailure
        )
        $normalized.Data["StateUncertain"] =
            $metadata.StateUncertain
        $normalized.Data["RollbackCompleted"] =
            $metadata.RollbackCompleted
        $normalized.Data["InstanceId"] = $metadata.InstanceId
        throw $normalized
    }
}

function Remove-ExactCreatedRootDevnode {
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId
    )

    if ($InstanceId -notmatch "^ROOT\\EMKEVIRTUALAUDIO\\[^\\]+$") {
        throw "Refusing cleanup outside one exact created EMKE instance."
    }
    Initialize-RootDevnodeSetupApi
    [Emke.DriverLab.RootDevnodeSetupApi]::RemoveExact($InstanceId)
}

function Get-TargetDevnodes {
    $rootDevices = @(Get-CimInstance `
        -ClassName Win32_PnPEntity `
        -Filter "PNPDeviceID LIKE 'ROOT%'")
    return @($rootDevices | Where-Object {
        @($_.HardwareID) -icontains $script:TargetHardwareId
    })
}

function Invoke-PollDelay {
    param(
        [Parameter(Mandatory)]
        [ValidateRange(1, 60000)]
        [int]$DelayMilliseconds
    )

    Start-Sleep -Milliseconds $DelayMilliseconds
}

function Invoke-BoundedPoll {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [scriptblock]$IsComplete,

        [Parameter(Mandatory)]
        [string]$Description,

        [ValidateRange(1, 300)]
        [int]$MaxAttempts = 30,

        [ValidateRange(1, 60000)]
        [int]$DelayMilliseconds = 1000
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt += 1) {
        $value = & $Action
        if (& $IsComplete $value) {
            return $value
        }
        if ($attempt -lt $MaxAttempts) {
            Invoke-PollDelay -DelayMilliseconds $DelayMilliseconds
        }
    }
    $exception = [TimeoutException]::new(
        "Timed out waiting for $Description after $MaxAttempts attempts; " +
        "state uncertain; perform read-only inventory before any further " +
        "mutation."
    )
    $exception.Data["StateUncertain"] = $true
    throw $exception
}

function Wait-TargetDevnode {
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedInstanceId,

        [ValidateRange(1, 300)]
        [int]$MaxAttempts = 30,

        [ValidateRange(1, 60000)]
        [int]$DelayMilliseconds = 1000
    )

    $state = Invoke-BoundedPoll `
        -Action {
            $devnodes = @(Get-TargetDevnodes)
            [pscustomobject]@{
                All = $devnodes
                Exact = @($devnodes | Where-Object {
                    [string]$_.PNPDeviceID -ceq $ExpectedInstanceId
                })
            }
        } `
        -IsComplete {
            param($Value)
            $Value.All.Count -eq 1 -and
            $Value.Exact.Count -eq 1 -and
            $Value.Exact[0].Present -eq $true
        } `
        -Description "one exact present target devnode '$ExpectedInstanceId'" `
        -MaxAttempts $MaxAttempts `
        -DelayMilliseconds $DelayMilliseconds
    return $state.Exact[0]
}

function Wait-TargetDevnodeAbsent {
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedInstanceId,

        [ValidateRange(1, 300)]
        [int]$MaxAttempts = 30,

        [ValidateRange(1, 60000)]
        [int]$DelayMilliseconds = 1000
    )

    [void](Invoke-BoundedPoll `
        -Action {
            @(Get-TargetDevnodes)
        } `
        -IsComplete {
            param($Value)
            $null -eq $Value -or @($Value).Count -eq 0
        } `
        -Description "absence of exact target devnode '$ExpectedInstanceId'" `
        -MaxAttempts $MaxAttempts `
        -DelayMilliseconds $DelayMilliseconds)
}

function Assert-InstalledDevnodeHealthy {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Devnodes
    )

    if ($Devnodes.Count -ne 1) {
        throw (
            "Expected exactly one $script:TargetHardwareId devnode after install; " +
            "found $($Devnodes.Count)."
        )
    }
    $devnode = $Devnodes[0]
    if ($devnode.Present -ne $true) {
        throw "The target driver devnode is not present after installation."
    }
    $errorCode = 0
    if ($null -eq $devnode.ConfigManagerErrorCode -or
        -not [int]::TryParse(
            [string]$devnode.ConfigManagerErrorCode,
            [ref]$errorCode
        )) {
        throw "ConfigManagerErrorCode must be an integer."
    }
    if ($errorCode -ne 0) {
        throw (
            "The target driver devnode is not healthy; ConfigManagerErrorCode=" +
            "$($devnode.ConfigManagerErrorCode)."
        )
    }
}

function Get-DriverStoreFileRepositoryRoot {
    $systemDirectory = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::System
    )
    if ([string]::IsNullOrWhiteSpace($systemDirectory)) {
        throw "Windows system directory could not be resolved."
    }
    return Assert-LocalNonReparsePath `
        -Path (Join-Path $systemDirectory "DriverStore\FileRepository") `
        -ExpectedType Container
}

function Get-InstalledDriverStorePackage {
    param(
        [Parameter(Mandatory)]
        [string]$PublishedInf,

        [Parameter(Mandatory)]
        [psobject]$TrustedPackage
    )

    if ($PublishedInf -notmatch "^oem[0-9]+\.inf$") {
        throw "Installed package has a non-allow-listed published INF."
    }
    $driverRecords = @(Get-WindowsDriver `
        -Online `
        -Driver $PublishedInf `
        -ErrorAction Stop)
    if ($driverRecords.Count -ne 1) {
        throw (
            "Expected one Driver Store package for '$PublishedInf'; " +
            "found $($driverRecords.Count)."
        )
    }
    $driverRecord = $driverRecords[0]
    $recordPublishedName = if (
        $null -ne $driverRecord.PSObject.Properties["Driver"]
    ) {
        [string]$driverRecord.Driver
    } elseif (
        $null -ne $driverRecord.PSObject.Properties["PublishedName"]
    ) {
        [string]$driverRecord.PublishedName
    } else {
        ""
    }
    if ([string]::IsNullOrWhiteSpace($recordPublishedName)) {
        throw "Driver Store inventory has no published INF identity."
    }
    if ($recordPublishedName -ine $PublishedInf) {
        throw "Driver Store inventory returned the wrong published INF."
    }

    $originalInf = [string]$driverRecord.OriginalFileName
    if ([string]::IsNullOrWhiteSpace($originalInf)) {
        throw "Driver Store inventory has no original INF path."
    }
    $installedInfPath = Resolve-RequiredFile -Path $originalInf
    $repositoryRoot = [IO.Path]::GetFullPath(
        (Get-DriverStoreFileRepositoryRoot)
    ).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $packageDirectory = [IO.Path]::GetFullPath(
        ([IO.FileInfo]::new($installedInfPath).DirectoryName)
    )
    $repositoryPrefix =
        $repositoryRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $packageDirectory.StartsWith(
        $repositoryPrefix,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Installed package INF is outside Driver Store FileRepository."
    }

    foreach ($file in @(
        $TrustedPackage.Inf,
        $TrustedPackage.Sys,
        $TrustedPackage.Cat
    )) {
        if ([string]::IsNullOrWhiteSpace([string]$file.Name) -or
            [IO.Path]::GetFileName([string]$file.Name) -cne
            [string]$file.Name) {
            throw "Trusted package contains an invalid exact file name."
        }
    }
    if ([IO.Path]::GetFileName($installedInfPath) -ine
        [string]$TrustedPackage.Inf.Name) {
        throw "Driver Store original INF name differs from the trusted package."
    }

    $installedInf = Get-Item `
        -LiteralPath (Resolve-RequiredFile -Path $installedInfPath) `
        -Force
    $installedSys = Get-Item `
        -LiteralPath (Resolve-RequiredFile -Path (
            Join-Path $packageDirectory $TrustedPackage.Sys.Name
        )) `
        -Force
    $installedCat = Get-Item `
        -LiteralPath (Resolve-RequiredFile -Path (
            Join-Path $packageDirectory $TrustedPackage.Cat.Name
        )) `
        -Force
    return [pscustomobject]@{
        Directory = $packageDirectory
        Inf = $installedInf
        Sys = $installedSys
        Cat = $installedCat
    }
}

function Assert-InstalledDriverPackageIdentity {
    param(
        [Parameter(Mandatory)]
        [psobject]$Devnode,

        [Parameter(Mandatory)]
        [psobject]$InfMetadata,

        [Parameter(Mandatory)]
        [psobject]$TrustedPackage,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{64}$")]
        [string]$ExpectedPackageSha256
    )

    $signedDrivers = @(Get-CimInstance -ClassName Win32_PnPSignedDriver)
    $matching = @($signedDrivers | Where-Object {
        $_.DeviceID -ieq $Devnode.PNPDeviceID
    })
    if ($matching.Count -ne 1) {
        throw (
            "Expected one installed package identity for the exact devnode; " +
            "found $($matching.Count)."
        )
    }
    $identity = $matching[0]
    if ([string]$identity.InfName -notmatch "^oem[0-9]+\.inf$") {
        throw "Installed package has a non-allow-listed published INF."
    }
    if ([string]$identity.DriverVersion -cne
        [string]$InfMetadata.DriverVersion) {
        throw "Installed package version does not match trusted INF DriverVer."
    }
    if ([string]$identity.ProviderName -cne "EMKE" -or
        [string]$InfMetadata.ProviderName -cne "EMKE") {
        throw "Installed package provider does not match trusted EMKE INF."
    }
    $installedPackage = Get-InstalledDriverStorePackage `
        -PublishedInf ([string]$identity.InfName) `
        -TrustedPackage $TrustedPackage
    $installedPackageSha256 =
        Get-DriverPackageSha256 -Package $installedPackage
    if (-not (Test-FixedSha256Equal `
        -Expected $ExpectedPackageSha256 `
        -Actual $installedPackageSha256)) {
        throw (
            "Installed Driver Store package content SHA-256 does not match " +
            "the trusted staged package."
        )
    }
    return [pscustomobject]@{
        InfName = [string]$identity.InfName
        DriverVersion = [string]$identity.DriverVersion
        ProviderName = [string]$identity.ProviderName
        PackageSha256 = $installedPackageSha256
    }
}

function Invoke-CreateAndBindRootDevnode {
    param(
        [Parameter(Mandatory)]
        [psobject]$StagedInf,

        [Parameter(Mandatory)]
        [psobject]$InfMetadata,

        [Parameter(Mandatory)]
        [psobject]$TrustedPackage,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{64}$")]
        [string]$ExpectedPackageSha256
    )

    $existing = @(Get-TargetDevnodes)
    if ($existing.Count -ne 0) {
        throw (
            "A pre-existing target devnode already exists; refusing an " +
            "implicit driver update."
        )
    }

    $createdInstanceId = $null
    try {
        $createdInstanceId = New-RootDevnodeFromInf `
            -InfPath $StagedInf.FullName `
            -HardwareId $script:TargetHardwareId
        $createdDevnode = Wait-TargetDevnode `
            -ExpectedInstanceId $createdInstanceId
        if ($createdDevnode.PNPDeviceID -cne $createdInstanceId) {
            throw "Created devnode discovery returned the wrong instance ID."
        }

        Invoke-PnpUtilInstall -InfPath $StagedInf.FullName
        $installedDevnode = Wait-TargetDevnode `
            -ExpectedInstanceId $createdInstanceId
        Assert-InstalledDevnodeHealthy -Devnodes @($installedDevnode)
        $identity = Assert-InstalledDriverPackageIdentity `
            -Devnode $installedDevnode `
            -InfMetadata $InfMetadata `
            -TrustedPackage $TrustedPackage `
            -ExpectedPackageSha256 $ExpectedPackageSha256
        return [pscustomobject]@{
            CreatedInstanceId = $createdInstanceId
            Devnode = $installedDevnode
            Identity = $identity
        }
    } catch {
        $originalFailure = $_
        $internalInstanceId =
            [string]$originalFailure.Exception.Data["InstanceId"]
        $reportedInstanceId = if (
            -not [string]::IsNullOrWhiteSpace($internalInstanceId)
        ) {
            $internalInstanceId
        } else {
            $createdInstanceId
        }
        if ($originalFailure.Exception.Data["RollbackCompleted"] -eq $true) {
            throw (
                "Root devnode creation failed for '$reportedInstanceId'; " +
                "exact same-handle rollback completed; state recovered. " +
                "Original error: " +
                $originalFailure.Exception.Message
            )
        }
        if ($originalFailure.Exception.Data["StateUncertain"] -eq $true) {
            $inventorySummary = "read-only inventory failed"
            try {
                $inventory = @(Get-TargetDevnodes)
                $inventorySummary = (
                    "read-only inventory observed $($inventory.Count) target " +
                    "devnode(s)"
                )
            } catch {
                $inventorySummary = (
                    "read-only inventory failed: " +
                    $_.Exception.Message
                )
            }
            throw (
                "Root devnode creation or bind failed for " +
                "'$reportedInstanceId'. Partial state: state uncertain; " +
                "$inventorySummary; no cleanup mutation was attempted. " +
                "Original error: " +
                $originalFailure.Exception.Message
            )
        }
        if ($null -eq $createdInstanceId) {
            throw
        }
        $cleanupStatus = "state uncertain"
        try {
            Remove-ExactCreatedRootDevnode `
                -InstanceId $createdInstanceId
            Wait-TargetDevnodeAbsent `
                -ExpectedInstanceId $createdInstanceId
            $cleanupStatus = "exact created instance confirmed absent"
        } catch {
            $cleanupStatus = (
                "state uncertain; exact-instance cleanup failed: " +
                $_.Exception.Message
            )
        }
        throw (
            "Root devnode bind failed after creating '$createdInstanceId'. " +
            "Partial state: driver binding was not proven; cleanup=" +
            "$cleanupStatus. Original error: " +
            $originalFailure.Exception.Message
        )
    }
}

function Invoke-SmokeEnumeration {
    param(
        [Parameter(Mandatory)]
        [string]$SmokePath,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{64}$")]
        [string]$ExpectedSmokeSha256
    )

    $resolvedSmoke = Resolve-RequiredFile -Path $SmokePath
    $actualSmokeSha256 = Get-FileSha256 -Path $resolvedSmoke
    if (-not (Test-FixedSha256Equal `
        -Expected $ExpectedSmokeSha256 `
        -Actual $actualSmokeSha256)) {
        throw (
            "Smoke SHA-256 does not match the trusted expected value. " +
            "Refusing execution."
        )
    }
    $result = Invoke-CapturedProcess `
        -Executable $resolvedSmoke `
        -Arguments @("--scenario", "enumerate") `
        -TimeoutSeconds 15
    if ($result.ExitCode -ne 0) {
        throw "Audio smoke enumeration failed with exit code $($result.ExitCode)."
    }
    $discoveryStatuses = @($result.OutputLines | Where-Object {
        [string]$_ -cmatch "^discovery="
    })
    $resultStatuses = @($result.OutputLines | Where-Object {
        [string]$_ -cmatch "^result="
    })
    if ($discoveryStatuses.Count -ne 1 -or
        $discoveryStatuses[0] -cne "discovery=ready") {
        throw (
            "Audio smoke enumeration must report exactly one " +
            "discovery=ready status."
        )
    }
    if ($resultStatuses.Count -ne 1 -or
        $resultStatuses[0] -cne "result=ready") {
        throw (
            "Audio smoke enumeration must report exactly one " +
            "result=ready status."
        )
    }
}

function Invoke-InstallTestDriver {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$ExpectedPackageSha256,

        [Parameter(Mandatory)]
        [string]$SmokePath,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{64}$")]
        [string]$ExpectedSmokeSha256,

        [switch]$ConfirmInstall
    )

    if (-not $ConfirmInstall) {
        throw "Installation requires the explicit -ConfirmInstall switch."
    }
    Assert-SupportedWindowsHost
    Assert-LabMachinePrerequisites

    $resolvedSmoke = Resolve-RequiredFile -Path $SmokePath
    $inputSmoke = [pscustomobject]@{
        FullName = $resolvedSmoke
        Name = [IO.Path]::GetFileName($resolvedSmoke)
    }
    $inputPackage = Get-StrictDriverPackage -Directory $PackagePath
    $staging = $null
    try {
        $staging = New-ProtectedStagingDirectory
        $staged = Copy-InstallInputsToStaging `
            -Package $inputPackage `
            -SmokeFile $inputSmoke `
            -StagingRoot $staging.Path
        $package = $staged.Package
        Invoke-DriverPackageVerifier -PackageDirectory $package.Directory

        $actualPackageSha256 =
            Get-DriverPackageSha256 -Package $package
        Write-Host "Observed package SHA-256: $actualPackageSha256"
        if (-not (Test-FixedSha256Equal `
            -Expected $ExpectedPackageSha256 `
            -Actual $actualPackageSha256)) {
            throw (
                "Driver package SHA-256 does not match the trusted " +
                "expected value. Refusing installation."
            )
        }
        if (-not (Test-FixedSha256Equal `
            -Expected $ExpectedSmokeSha256 `
            -Actual $staged.SmokeSha256)) {
            throw (
                "Smoke SHA-256 does not match the trusted expected value. " +
                "Refusing installation."
            )
        }

        $signature = Get-CatalogSignatureMetadata -Catalog $package.Cat
        Assert-CatalogSignatureValid -Metadata $signature
        $infMetadata = Get-DriverInfMetadata `
            -Inf $package.Inf `
            -WindowsBuild (Get-WindowsBuildNumber)
        if ($infMetadata.HardwareId -cne $script:TargetHardwareId) {
            throw "Driver INF hardware ID is not the exact target."
        }

        Write-Host "DriverVer: $($infMetadata.DriverVer)"
        Write-Host "Hardware ID: $($infMetadata.HardwareId)"
        Write-Host "Verified package SHA-256: $actualPackageSha256"
        Write-Host (
            "Catalog signature: Valid; signing certificate SHA-256: " +
            "$($signature.SummarySha256); host Authenticode validation only; " +
            "Microsoft/WHQL not established"
        )

        Assert-StagedInputsUnchanged -StagedInputs $staged
        $binding = Invoke-CreateAndBindRootDevnode `
            -StagedInf $package.Inf `
            -InfMetadata $infMetadata `
            -TrustedPackage $package `
            -ExpectedPackageSha256 $actualPackageSha256
        $installedIdentity = $binding.Identity
        Write-Host (
            "Installed package: $($installedIdentity.InfName); " +
            "version=$($installedIdentity.DriverVersion); " +
            "provider=$($installedIdentity.ProviderName); " +
            "package SHA-256=$($installedIdentity.PackageSha256)"
        )
        Assert-StagedInputsUnchanged -StagedInputs $staged
        Invoke-SmokeEnumeration `
            -SmokePath $staged.Smoke.FullName `
            -ExpectedSmokeSha256 $ExpectedSmokeSha256
        Write-Host (
            "Audio smoke: discovery=ready; result=ready; " +
            "public ABI four-role contract passed."
        )
    } finally {
        if ($null -ne $staging) {
            Remove-ProtectedStagingDirectory `
                -Path $staging.Path `
                -Token $staging.Token
        }
    }
}

if ($PSCmdlet.ParameterSetName -ceq "Digest") {
    Assert-SupportedWindowsHost
    $observedPackage = Get-StrictDriverPackage -Directory $PackagePath
    $observedDigest = Get-DriverPackageSha256 -Package $observedPackage
    Write-Output (
        "Observed/Generated package SHA-256 " +
        "(not a trusted expected value): $observedDigest"
    )
    return
}

Invoke-InstallTestDriver @PSBoundParameters
