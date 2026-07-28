[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".." ".." "..")
)
$installScript = Join-Path `
    $repositoryRoot `
    "Windows/packaging/App/Install-EMKE-Translation-Internal.ps1"
$uninstallScript = Join-Path `
    $repositoryRoot `
    "Windows/packaging/App/Uninstall-EMKE-Translation-Internal.ps1"
$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("emke-internal-msix-lifecycle-" + [guid]::NewGuid().ToString("N"))
$script:failures = [Collections.Generic.List[string]]::new()

function Invoke-Case {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    try {
        & $Action
        Write-Host "PASS: $Name"
    } catch {
        $script:failures.Add("$Name`: $($_.Exception.Message)")
        Write-Host "FAIL: $Name"
    }
}

function Assert-Equal {
    param(
        [AllowNull()]
        $Actual,

        [AllowNull()]
        $Expected,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', received '$Actual'."
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

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
            throw (
                "Expected error '$Pattern'; received " +
                "'$($_.Exception.Message)'."
            )
        }
        return
    }
    throw "Expected action to throw '$Pattern'."
}

function Set-TestFunction {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Body
    )

    Set-Item -LiteralPath "Function:\global:$Name" -Value $Body -Force
}

function Import-LifecycleFunctions {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Lifecycle script is missing: $Path"
    }
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors
    )
    if ($errors.Count -ne 0) {
        throw "Lifecycle script has parser errors: $($errors[0].Message)"
    }
    $definitions = @($ast.FindAll(
        {
            param($candidate)
            $candidate -is
                [Management.Automation.Language.FunctionDefinitionAst]
        },
        $false
    ))
    if ($definitions.Count -eq 0) {
        throw "Lifecycle script exposes no production functions."
    }
    foreach ($definition in $definitions) {
        $bodyText = $definition.Body.Extent.Text
        $bodyText = $bodyText.Substring(1, $bodyText.Length - 2)
        Set-TestFunction `
            -Name $definition.Name `
            -Body ([scriptblock]::Create($bodyText))
    }
}

function New-TestCertificate {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [string]$Subject = "CN=EMKE Internal Test"
    )

    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            [Security.Cryptography.X509Certificates.X500DistinguishedName]::new(
                $Subject
            ),
            $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1
        )
        $certificate = $request.CreateSelfSigned(
            [datetimeoffset]::UtcNow.AddMinutes(-5),
            [datetimeoffset]::UtcNow.AddDays(30)
        )
        try {
            $bytes = $certificate.Export(
                [Security.Cryptography.X509Certificates.X509ContentType]::Cert
            )
            [IO.File]::WriteAllBytes($Path, $bytes)
        } finally {
            $certificate.Dispose()
        }
    } finally {
        $rsa.Dispose()
    }
}

function Write-TestChecksums {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$CertificatePath
    )

    $packageHash = (Get-FileHash `
        -LiteralPath $PackagePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $certificateHash = (Get-FileHash `
        -LiteralPath $CertificatePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $content = (
        "$packageHash  $([IO.Path]::GetFileName($PackagePath))`n" +
        "$certificateHash  $([IO.Path]::GetFileName($CertificatePath))`n"
    )
    [IO.File]::WriteAllText(
        $Path,
        $content,
        [Text.UTF8Encoding]::new($false)
    )
}

function Write-ExactInventoryChecksums {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[]]$Files
    )

    $lines = foreach ($file in $Files) {
        $hash = (Get-FileHash `
            -LiteralPath $file `
            -Algorithm SHA256).Hash.ToUpperInvariant()
        "$hash  $([IO.Path]::GetFileName($file))"
    }
    [IO.File]::WriteAllText(
        $Path,
        (($lines -join "`n") + "`n"),
        [Text.UTF8Encoding]::new($false)
    )
}

function Reset-InstallFunctions {
    Import-LifecycleFunctions -Path $installScript
    $script:PackageName = "EMKE.Translation.Internal"
    $script:ExpectedPublisher = "CN=EMKE Internal Test"
    $script:ExpectedVersion = "0.1.0.0"
    $script:ExpectedArchitecture = "x64"
    $script:ExpectedCertificateSubject = "CN=EMKE Internal Test"
    $script:PackageFileName =
        "EMKE-Translation-Windows-0.1.0-internal-x64.msix"
    $script:CertificateFileName =
        "EMKE-Translation-Windows-0.1.0-internal-x64.cer"
    $script:InstallScriptFileName =
        "Install-EMKE-Translation-Internal.ps1"
    $script:UninstallScriptFileName =
        "Uninstall-EMKE-Translation-Internal.ps1"
}

function Reset-UninstallFunctions {
    Import-LifecycleFunctions -Path $uninstallScript
    $script:PackageName = "EMKE.Translation.Internal"
    $script:ExpectedPublisher = "CN=EMKE Internal Test"
    $script:ExpectedVersion = "0.1.0.0"
    $script:ExpectedArchitecture = "x64"
    $script:ExpectedCertificateSubject = "CN=EMKE Internal Test"
    $script:PackageFileName =
        "EMKE-Translation-Windows-0.1.0-internal-x64.msix"
    $script:CertificateFileName =
        "EMKE-Translation-Windows-0.1.0-internal-x64.cer"
    $script:InstallScriptFileName =
        "Install-EMKE-Translation-Internal.ps1"
    $script:UninstallScriptFileName =
        "Uninstall-EMKE-Translation-Internal.ps1"
}

function Set-SafeInstallOrchestratorDefaults {
    $script:installEvents = [Collections.Generic.List[string]]::new()
    Set-TestFunction -Name Assert-SupportedInstallParent -Body {
        $script:installEvents.Add("parent")
    }
    Set-TestFunction -Name Resolve-ExactBundleInput -Body {
        param($Path, $ExpectedExtension)
        $script:installEvents.Add("resolve:$ExpectedExtension")
        [IO.Path]::GetFullPath($Path)
    }
    Set-TestFunction -Name Get-CurrentLifecycleScriptPath -Body {
        "/tmp/Install-EMKE-Translation-Internal.ps1"
    }
    Set-TestFunction -Name Resolve-ExactBundleInventory -Body {
        param(
            $PackagePath,
            $CertificatePath,
            $ChecksumsPath,
            $CurrentScriptPath
        )
        $script:installEvents.Add("inventory")
        [pscustomobject]@{
            PackagePath = [IO.Path]::GetFullPath($PackagePath)
            CertificatePath = [IO.Path]::GetFullPath($CertificatePath)
            ChecksumsPath = [IO.Path]::GetFullPath($ChecksumsPath)
            InstallScriptPath =
                "/tmp/Install-EMKE-Translation-Internal.ps1"
            UninstallScriptPath =
                "/tmp/Uninstall-EMKE-Translation-Internal.ps1"
            Hashes = [ordered]@{
                Package = "A" * 64
                Certificate = "A" * 64
                InstallScript = "A" * 64
                UninstallScript = "A" * 64
            }
            PackageSha256 = "A" * 64
            CertificateSha256 = "A" * 64
        }
    }
    Set-TestFunction -Name Assert-BundleInventoryUnchanged -Body {
        param($Inventory)
        $script:installEvents.Add("inventory-unchanged")
    }
    Set-TestFunction -Name Read-ExpectedSha256 -Body {
        param($ChecksumsPath, $FilePath)
        $script:installEvents.Add(
            "read-hash:$([IO.Path]::GetExtension($FilePath))"
        )
        "A" * 64
    }
    Set-TestFunction -Name Assert-FileSha256 -Body {
        param($Path, $ExpectedSha256)
        $script:installEvents.Add(
            "hash:$([IO.Path]::GetExtension($Path))"
        )
    }
    Set-TestFunction -Name Get-InternalCertificateEvidence -Body {
        param($Path)
        $script:installEvents.Add("certificate")
        [pscustomobject]@{
            Subject = "CN=EMKE Internal Test"
            Thumbprint = "B" * 40
            Sha256 = "A" * 64
        }
    }
    Set-TestFunction -Name Invoke-ElevatedCertificateImport -Body {
        param(
            $CertificatePath,
            $ExpectedCertificateSha256,
            $ExpectedCertificateThumbprint
        )
        $script:installEvents.Add("elevate-import")
    }
    Set-TestFunction -Name Invoke-AddExactAppxPackage -Body {
        param($PackagePath)
        $script:installEvents.Add("add-appx")
    }
    Set-TestFunction -Name Assert-InternalPackageAbsent -Body {
        $script:installEvents.Add("verify-pre-absent")
    }
    Set-TestFunction -Name Assert-InstalledInternalPackage -Body {
        $script:installEvents.Add("verify-identity")
        [pscustomobject]@{
            Name = "EMKE.Translation.Internal"
            Publisher = "CN=EMKE Internal Test"
            Version = "0.1.0.0"
            Architecture = "x64"
            PackageFullName = "EMKE.Translation.Internal_0.1.0.0_x64__test"
        }
    }
    Set-TestFunction -Name Write-CertificateInstallRecord -Body {
        param($CertificateEvidence)
        $script:installEvents.Add("record")
    }
    Set-TestFunction -Name Invoke-InstallRollback -Body {
        param(
            $CertificateTrustCompleted,
            $CertificatePath,
            $ExpectedCertificateSha256,
            $ExpectedCertificateThumbprint
        )
        $script:installEvents.Add(
            "rollback:cert=$([bool]$CertificateTrustCompleted)"
        )
        [pscustomobject]@{
            Complete = $true
            RecoveryRequired = $false
        }
    }
}

function Set-SafeUninstallOrchestratorDefaults {
    $script:uninstallEvents = [Collections.Generic.List[string]]::new()
    Set-TestFunction -Name Assert-SupportedUninstallParent -Body {
        $script:uninstallEvents.Add("parent")
    }
    Set-TestFunction -Name Resolve-ExactBundleInput -Body {
        param($Path, $ExpectedExtension)
        $script:uninstallEvents.Add("resolve:$ExpectedExtension")
        [IO.Path]::GetFullPath($Path)
    }
    Set-TestFunction -Name Get-CurrentLifecycleScriptPath -Body {
        "/tmp/Uninstall-EMKE-Translation-Internal.ps1"
    }
    Set-TestFunction -Name Resolve-ExactBundleInventory -Body {
        param(
            $PackagePath,
            $CertificatePath,
            $ChecksumsPath,
            $CurrentScriptPath
        )
        $script:uninstallEvents.Add("inventory")
        [pscustomobject]@{
            PackagePath = "/tmp/product.msix"
            CertificatePath = [IO.Path]::GetFullPath($CertificatePath)
            ChecksumsPath = [IO.Path]::GetFullPath($ChecksumsPath)
            InstallScriptPath =
                "/tmp/Install-EMKE-Translation-Internal.ps1"
            UninstallScriptPath =
                "/tmp/Uninstall-EMKE-Translation-Internal.ps1"
            Hashes = [ordered]@{
                Package = "A" * 64
                Certificate = "A" * 64
                InstallScript = "A" * 64
                UninstallScript = "A" * 64
            }
            PackageSha256 = "A" * 64
            CertificateSha256 = "A" * 64
        }
    }
    Set-TestFunction -Name Assert-BundleInventoryUnchanged -Body {
        param($Inventory)
        $script:uninstallEvents.Add("inventory-unchanged")
    }
    Set-TestFunction -Name Read-ExpectedSha256 -Body {
        param($ChecksumsPath, $FilePath)
        $script:uninstallEvents.Add(
            "read-hash:$([IO.Path]::GetExtension($FilePath))"
        )
        "A" * 64
    }
    Set-TestFunction -Name Assert-FileSha256 -Body {
        param($Path, $ExpectedSha256)
        $script:uninstallEvents.Add(
            "hash:$([IO.Path]::GetExtension($Path))"
        )
    }
    Set-TestFunction -Name Get-InternalCertificateEvidence -Body {
        param($Path)
        $script:uninstallEvents.Add("certificate")
        [pscustomobject]@{
            Subject = "CN=EMKE Internal Test"
            Thumbprint = "B" * 40
            Sha256 = "A" * 64
        }
    }
    Set-TestFunction -Name Read-CertificateInstallRecord -Body {
        param([switch]$AllowMissing)
        $script:uninstallEvents.Add("read-record")
        [pscustomobject]@{
            PackageName = "EMKE.Translation.Internal"
            CertificateSubject = "CN=EMKE Internal Test"
            CertificateThumbprint = "B" * 40
            CertificateSha256 = "A" * 64
        }
    }
    Set-TestFunction -Name Get-ExactInstalledInternalPackage -Body {
        $script:uninstallEvents.Add("query-package")
        [pscustomobject]@{
            Name = "EMKE.Translation.Internal"
            Publisher = "CN=EMKE Internal Test"
            Version = "0.1.0.0"
            Architecture = "x64"
            PackageFullName = "EMKE.Translation.Internal_0.1.0.0_x64__test"
        }
    }
    Set-TestFunction -Name Get-OptionalInstalledInternalPackage -Body {
        $script:uninstallEvents.Add("query-package")
        [pscustomobject]@{
            Name = "EMKE.Translation.Internal"
            Publisher = "CN=EMKE Internal Test"
            Version = "0.1.0.0"
            Architecture = "x64"
            PackageFullName = "EMKE.Translation.Internal_0.1.0.0_x64__test"
        }
    }
    Set-TestFunction -Name Invoke-RemoveExactAppxPackage -Body {
        param($PackageFullName)
        $script:uninstallEvents.Add("remove-appx:$PackageFullName")
    }
    Set-TestFunction -Name Assert-InternalPackageAbsent -Body {
        $script:uninstallEvents.Add("verify-absent")
    }
    Set-TestFunction -Name Invoke-ElevatedCertificateRemoval -Body {
        param(
            $CertificatePath,
            $ExpectedCertificateSha256,
            $ExpectedCertificateThumbprint
        )
        $script:uninstallEvents.Add("elevate-remove")
    }
    Set-TestFunction -Name Remove-CertificateInstallRecord -Body {
        $script:uninstallEvents.Add("remove-record")
    }
}

[IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    Invoke-Case "absolute local inputs and SHA256SUMS verify before mutation" {
        Reset-InstallFunctions
        $bundle = Join-Path $testRoot "bundle"
        [IO.Directory]::CreateDirectory($bundle) | Out-Null
        $package = Join-Path `
            $bundle `
            "EMKE-Translation-Windows-0.1.0-internal-x64.msix"
        $certificate = Join-Path `
            $bundle `
            "EMKE-Translation-Windows-0.1.0-internal-x64.cer"
        $checksums = Join-Path $bundle "SHA256SUMS.txt"
        [IO.File]::WriteAllBytes($package, [byte[]](1, 2, 3, 4))
        New-TestCertificate -Path $certificate
        Write-TestChecksums `
            -Path $checksums `
            -PackagePath $package `
            -CertificatePath $certificate

        $resolvedPackage = Resolve-ExactBundleInput `
            -Path $package `
            -ExpectedExtension ".msix"
        $resolvedCertificate = Resolve-ExactBundleInput `
            -Path $certificate `
            -ExpectedExtension ".cer"
        $resolvedChecksums = Resolve-ExactBundleInput `
            -Path $checksums `
            -ExpectedExtension ".txt"
        $packageExpected = Read-ExpectedSha256 `
            -ChecksumsPath $resolvedChecksums `
            -FilePath $resolvedPackage
        $certificateExpected = Read-ExpectedSha256 `
            -ChecksumsPath $resolvedChecksums `
            -FilePath $resolvedCertificate
        Assert-FileSha256 `
            -Path $resolvedPackage `
            -ExpectedSha256 $packageExpected
        Assert-FileSha256 `
            -Path $resolvedCertificate `
            -ExpectedSha256 $certificateExpected
        Assert-Equal `
            $packageExpected.Length `
            64 `
            "Package digest length differs."
        Assert-Equal `
            $certificateExpected.Length `
            64 `
            "Certificate digest length differs."

        Assert-Throws `
            -Pattern "absolute local" `
            -Action {
                Resolve-ExactBundleInput `
                    -Path "relative.msix" `
                    -ExpectedExtension ".msix"
            }
        Assert-Throws `
            -Pattern "extension" `
            -Action {
                Resolve-ExactBundleInput `
                    -Path $certificate `
                    -ExpectedExtension ".msix"
            }
        Assert-Throws `
            -Pattern "digest mismatch" `
            -Action {
                Assert-FileSha256 `
                    -Path $resolvedPackage `
                    -ExpectedSha256 ("0" * 64)
            }

        $originalChecksums = [IO.File]::ReadAllText($checksums)
        [IO.File]::AppendAllText(
            $checksums,
            $originalChecksums.Split("`n")[0] + "`n"
        )
        Assert-Throws `
            -Pattern "exactly one entry" `
            -Action {
                Read-ExpectedSha256 `
                    -ChecksumsPath $resolvedChecksums `
                    -FilePath $resolvedPackage
            }
        [IO.File]::WriteAllText(
            $checksums,
            ("A" * 64) + "  ../product.msix`n"
        )
        Assert-Throws `
            -Pattern "leaf names only" `
            -Action {
                Read-ExpectedSha256 `
                    -ChecksumsPath $resolvedChecksums `
                    -FilePath $resolvedPackage
            }
    }

    Invoke-Case "local input rejects symlink and mismatched bundle directory" {
        Reset-InstallFunctions
        $bundle = Join-Path $testRoot "safe-bundle"
        $other = Join-Path $testRoot "other-bundle"
        [IO.Directory]::CreateDirectory($bundle) | Out-Null
        [IO.Directory]::CreateDirectory($other) | Out-Null
        $target = Join-Path $bundle "target.msix"
        $link = Join-Path $bundle "linked.msix"
        [IO.File]::WriteAllBytes($target, [byte[]](5, 6, 7))
        New-Item `
            -ItemType SymbolicLink `
            -Path $link `
            -Target $target | Out-Null

        Assert-Throws `
            -Pattern "reparse|symbolic" `
            -Action {
                Resolve-ExactBundleInput `
                    -Path $link `
                    -ExpectedExtension ".msix"
            }

        $package = Join-Path $bundle "product.msix"
        $certificate = Join-Path $other "product.cer"
        $checksums = Join-Path $bundle "SHA256SUMS.txt"
        [IO.File]::WriteAllBytes($package, [byte[]](8))
        New-TestCertificate -Path $certificate
        [IO.File]::WriteAllText($checksums, "")
        Assert-Throws `
            -Pattern "same bundle directory" `
            -Action {
                Assert-SameBundleDirectory `
                    -Paths @($package, $certificate, $checksums)
            }
    }

    Invoke-Case "certificate evidence requires exact subject and stable thumbprint" {
        Reset-InstallFunctions
        $validPath = Join-Path $testRoot "valid.cer"
        $wrongPath = Join-Path $testRoot "wrong.cer"
        New-TestCertificate -Path $validPath
        New-TestCertificate -Path $wrongPath -Subject "CN=Other"

        $evidence = Get-InternalCertificateEvidence -Path $validPath
        Assert-Equal `
            $evidence.Subject `
            "CN=EMKE Internal Test" `
            "Certificate subject differs."
        Assert-True `
            ($evidence.Thumbprint -cmatch "^[A-F0-9]{40}$") `
            "Certificate thumbprint is not fixed uppercase hex."
        Assert-True `
            ($evidence.Sha256 -cmatch "^[A-F0-9]{64}$") `
            "Certificate SHA-256 is not fixed uppercase hex."
        Assert-Throws `
            -Pattern "subject" `
            -Action {
                Get-InternalCertificateEvidence -Path $wrongPath
            }
    }

    Invoke-Case "exact bundle inventory rejects extra missing and duplicate entries" {
        Reset-InstallFunctions
        $bundle = Join-Path $testRoot "exact-inventory"
        [IO.Directory]::CreateDirectory($bundle) | Out-Null
        $package = Join-Path `
            $bundle `
            "EMKE-Translation-Windows-0.1.0-internal-x64.msix"
        $certificate = Join-Path `
            $bundle `
            "EMKE-Translation-Windows-0.1.0-internal-x64.cer"
        $install = Join-Path `
            $bundle `
            "Install-EMKE-Translation-Internal.ps1"
        $uninstall = Join-Path `
            $bundle `
            "Uninstall-EMKE-Translation-Internal.ps1"
        $checksums = Join-Path $bundle "SHA256SUMS.txt"
        [IO.File]::WriteAllBytes($package, [byte[]](1, 4, 9))
        New-TestCertificate -Path $certificate
        [IO.File]::Copy($installScript, $install, $true)
        [IO.File]::Copy($uninstallScript, $uninstall, $true)
        $files = @($package, $certificate, $install, $uninstall)
        Write-ExactInventoryChecksums -Path $checksums -Files $files

        $inventory = Resolve-ExactBundleInventory `
            -PackagePath $package `
            -CertificatePath $certificate `
            -ChecksumsPath $checksums `
            -CurrentScriptPath $install
        Assert-Equal `
            $inventory.Hashes.Count `
            4 `
            "Exact bundle inventory hash count differs."

        [IO.File]::AppendAllText(
            $checksums,
            ("A" * 64) + "  unexpected.txt`n"
        )
        Assert-Throws `
            -Pattern "exactly four|unexpected" `
            -Action {
                Resolve-ExactBundleInventory `
                    -PackagePath $package `
                    -CertificatePath $certificate `
                    -ChecksumsPath $checksums `
                    -CurrentScriptPath $install
            }

        Write-ExactInventoryChecksums -Path $checksums -Files $files
        [IO.File]::AppendAllText(
            $checksums,
            [IO.File]::ReadAllLines($checksums)[0] + "`n"
        )
        Assert-Throws `
            -Pattern "exactly four|duplicate" `
            -Action {
                Resolve-ExactBundleInventory `
                    -PackagePath $package `
                    -CertificatePath $certificate `
                    -ChecksumsPath $checksums `
                    -CurrentScriptPath $install
            }
    }

    Invoke-Case "same-subject replacement with updated sums fails fixed thumbprint" {
        Reset-InstallFunctions
        $bundle = Join-Path $testRoot "replacement-certificate"
        [IO.Directory]::CreateDirectory($bundle) | Out-Null
        $package = Join-Path `
            $bundle `
            "EMKE-Translation-Windows-0.1.0-internal-x64.msix"
        $certificate = Join-Path `
            $bundle `
            "EMKE-Translation-Windows-0.1.0-internal-x64.cer"
        $install = Join-Path `
            $bundle `
            "Install-EMKE-Translation-Internal.ps1"
        $uninstall = Join-Path `
            $bundle `
            "Uninstall-EMKE-Translation-Internal.ps1"
        $checksums = Join-Path $bundle "SHA256SUMS.txt"
        [IO.File]::WriteAllBytes($package, [byte[]](2, 3, 5))
        New-TestCertificate -Path $certificate
        $trustedThumbprint =
            (Get-InternalCertificateEvidence -Path $certificate).Thumbprint
        New-TestCertificate -Path $certificate
        $replacementThumbprint =
            (Get-InternalCertificateEvidence -Path $certificate).Thumbprint
        Assert-True `
            ($replacementThumbprint -cne $trustedThumbprint) `
            "Replacement fixture reused the trusted thumbprint."
        [IO.File]::Copy($installScript, $install, $true)
        [IO.File]::Copy($uninstallScript, $uninstall, $true)
        Write-ExactInventoryChecksums `
            -Path $checksums `
            -Files @($package, $certificate, $install, $uninstall)

        Set-TestFunction -Name Assert-SupportedInstallParent -Body {}
        $script:replacementInstallPath = $install
        Set-TestFunction -Name Get-CurrentLifecycleScriptPath -Body {
            $script:replacementInstallPath
        }
        $script:replacementElevated = $false
        Set-TestFunction -Name Invoke-ElevatedCertificateImport -Body {
            $script:replacementElevated = $true
        }
        Set-TestFunction -Name Assert-InternalPackageAbsent -Body {}

        Assert-Throws `
            -Pattern "trusted expected thumbprint|thumbprint" `
            -Action {
                Invoke-InstallInternalMsix `
                    -PackagePath $package `
                    -CertificatePath $certificate `
                    -ChecksumsPath $checksums `
                    -ExpectedCertificateThumbprint $trustedThumbprint `
                    -ConfirmTrust
            }
        Assert-True `
            (-not $script:replacementElevated) `
            "Same-subject replacement reached elevation."
    }

    Invoke-Case "protected request preserves spaced quote path and detects tamper" {
        Reset-InstallFunctions
        $quotedDirectory = Join-Path $testRoot "space ' quote"
        [IO.Directory]::CreateDirectory($quotedDirectory) | Out-Null
        $certificate = Join-Path $quotedDirectory "internal cert.cer"
        New-TestCertificate -Path $certificate
        $evidence = Get-InternalCertificateEvidence -Path $certificate
        $request = New-ProtectedElevatedRequest `
            -Operation "Import" `
            -CertificatePath $certificate `
            -ExpectedCertificateSha256 $evidence.Sha256 `
            -ExpectedCertificateThumbprint $evidence.Thumbprint
        try {
            $payload = ConvertFrom-Json `
                -InputObject ([IO.File]::ReadAllText($request.RequestPath))
            Assert-Equal `
                $payload.certificatePath `
                $certificate `
                "Protected request changed the spaced quote path."
            Assert-ElevatedRequestUnchanged -Request $request
            [IO.File]::SetAttributes(
                $request.RequestPath,
                [IO.FileAttributes]::Normal
            )
            [IO.File]::AppendAllText($request.RequestPath, " ")
            Assert-Throws `
                -Pattern "request.*changed|digest" `
                -Action {
                    Assert-ElevatedRequestUnchanged -Request $request
                }
        } finally {
            Remove-ProtectedElevatedRequest -Request $request
        }
    }

    Invoke-Case "encoded elevation keeps path data out of argv and detects launch tamper" {
        Reset-InstallFunctions
        $quotedDirectory = Join-Path $testRoot "encoded space ' quote"
        [IO.Directory]::CreateDirectory($quotedDirectory) | Out-Null
        $certificate = Join-Path $quotedDirectory "internal cert.cer"
        New-TestCertificate -Path $certificate
        $evidence = Get-InternalCertificateEvidence -Path $certificate
        Set-TestFunction -Name Resolve-TrustedPowerShell -Body {
            "/trusted/pwsh.exe"
        }
        $script:capturedArgumentList = $null
        $script:capturedRequestPath = $null
        Set-TestFunction -Name Start-Process -Body {
            param(
                $FilePath,
                $ArgumentList,
                $Verb,
                [switch]$Wait,
                [switch]$PassThru
            )
            $script:capturedArgumentList = $ArgumentList
            $script:capturedRequestPath = [Environment]::GetEnvironmentVariable(
                "EMKE_ELEVATED_REQUEST_PATH",
                [EnvironmentVariableTarget]::Process
            )
            [pscustomobject]@{ ExitCode = 0 }
        }

        Invoke-ElevatedCertificateOperation `
            -Operation "Import" `
            -CertificatePath $certificate `
            -ExpectedCertificateSha256 $evidence.Sha256 `
            -ExpectedCertificateThumbprint $evidence.Thumbprint
        Assert-True `
            ($script:capturedArgumentList -match "-EncodedCommand") `
            "Elevation did not use EncodedCommand."
        Assert-True `
            (-not $script:capturedArgumentList.Contains(
                $certificate,
                [StringComparison]::Ordinal
            )) `
            "Certificate path leaked into the elevated argv."
        Assert-True `
            (-not [string]::IsNullOrWhiteSpace(
                $script:capturedRequestPath
            )) `
            "Elevation did not pass the protected request path."
        Assert-True `
            (-not (Test-Path -LiteralPath $script:capturedRequestPath)) `
            "Protected request was not cleaned after elevation."

        Set-TestFunction -Name Start-Process -Body {
            param(
                $FilePath,
                $ArgumentList,
                $Verb,
                [switch]$Wait,
                [switch]$PassThru
            )
            $requestPath = [Environment]::GetEnvironmentVariable(
                "EMKE_ELEVATED_REQUEST_PATH",
                [EnvironmentVariableTarget]::Process
            )
            [IO.File]::SetAttributes(
                $requestPath,
                [IO.FileAttributes]::Normal
            )
            [IO.File]::AppendAllText($requestPath, " ")
            [pscustomobject]@{ ExitCode = 0 }
        }
        Assert-Throws `
            -Pattern "request.*changed|digest mismatch" `
            -Action {
                Invoke-ElevatedCertificateOperation `
                    -Operation "Import" `
                    -CertificatePath $certificate `
                    -ExpectedCertificateSha256 $evidence.Sha256 `
                    -ExpectedCertificateThumbprint $evidence.Thumbprint
            }
    }

    Invoke-Case "fixed encoded child source parses independently" {
        Reset-InstallFunctions
        $source = Get-ElevatedCertificateChildSource
        $tokens = $null
        $errors = $null
        [void][Management.Automation.Language.Parser]::ParseInput(
            $source,
            [ref]$tokens,
            [ref]$errors
        )
        Assert-Equal `
            $errors.Count `
            0 `
            "Encoded child source contains parser errors."
        Assert-True `
            (-not $source.Contains(
                '$PSCommandPath',
                [StringComparison]::Ordinal
            )) `
            "Encoded child source depends on a mutable script path."
    }

    Invoke-Case "installer requires trust confirmation before any mutation" {
        Reset-InstallFunctions
        Set-SafeInstallOrchestratorDefaults

        Assert-Throws `
            -Pattern "ConfirmTrust" `
            -Action {
                Invoke-InstallInternalMsix `
                    -PackagePath "/tmp/product.msix" `
                    -CertificatePath "/tmp/product.cer" `
                    -ChecksumsPath "/tmp/SHA256SUMS.txt" `
                    -ExpectedCertificateThumbprint ("B" * 40) `
                    -ConfirmTrust:$false
            }
        Assert-Equal `
            $script:installEvents.Count `
            0 `
            "Missing confirmation performed work."
    }

    Invoke-Case "installer hashes both inputs before exact elevated child and AppX" {
        Reset-InstallFunctions
        Set-SafeInstallOrchestratorDefaults

        Invoke-InstallInternalMsix `
            -PackagePath "/tmp/product.msix" `
            -CertificatePath "/tmp/product.cer" `
            -ChecksumsPath "/tmp/SHA256SUMS.txt" `
            -ExpectedCertificateThumbprint ("B" * 40) `
            -ConfirmTrust
        Assert-Equal `
            ($script:installEvents -join "|") `
            (
                "parent|inventory|certificate|verify-pre-absent|" +
                "elevate-import|inventory-unchanged|certificate|" +
                "add-appx|verify-identity|record"
            ) `
            "Installer mutation sequence differs."
    }

    Invoke-Case "digest failure prevents elevation and AppX mutation" {
        Reset-InstallFunctions
        Set-SafeInstallOrchestratorDefaults
        Set-TestFunction -Name Resolve-ExactBundleInventory -Body {
            throw "File digest mismatch."
        }

        Assert-Throws `
            -Pattern "digest mismatch" `
            -Action {
                Invoke-InstallInternalMsix `
                    -PackagePath "/tmp/product.msix" `
                    -CertificatePath "/tmp/product.cer" `
                    -ChecksumsPath "/tmp/SHA256SUMS.txt" `
                    -ExpectedCertificateThumbprint ("B" * 40) `
                    -ConfirmTrust
            }
        Assert-True `
            (-not $script:installEvents.Contains("elevate-import")) `
            "Digest failure reached elevation."
        Assert-True `
            (-not $script:installEvents.Contains("add-appx")) `
            "Digest failure reached Add-AppxPackage."
    }

    Invoke-Case "UAC cancellation enters exact recoverable rollback path" {
        Reset-InstallFunctions
        Set-SafeInstallOrchestratorDefaults
        Set-TestFunction -Name Invoke-ElevatedCertificateImport -Body {
            $script:installEvents.Add("elevate-import")
            throw "UAC was cancelled."
        }

        Assert-Throws `
            -Pattern "rollback|retry|Recovery required" `
            -Action {
                Invoke-InstallInternalMsix `
                    -PackagePath "/tmp/product.msix" `
                    -CertificatePath "/tmp/product.cer" `
                    -ChecksumsPath "/tmp/SHA256SUMS.txt" `
                    -ExpectedCertificateThumbprint ("B" * 40) `
                    -ConfirmTrust
            }
        Assert-True `
            ($script:installEvents.Contains("rollback:cert=True")) `
            "UAC cancellation did not enter exact certificate recovery."
        Assert-True `
            (-not $script:installEvents.Contains("add-appx")) `
            "UAC cancellation reached Add-AppxPackage."
        Assert-True `
            (-not $script:installEvents.Contains("record")) `
            "UAC cancellation wrote an install record."
    }

    Invoke-Case "Add-Appx failure rolls back certificate without writing record" {
        Reset-InstallFunctions
        Set-SafeInstallOrchestratorDefaults
        Set-TestFunction -Name Invoke-AddExactAppxPackage -Body {
            param($PackagePath)
            $script:installEvents.Add("add-appx")
            throw "Synthetic Add-AppxPackage failure."
        }

        Assert-Throws `
            -Pattern "rollback|retry|Recovery required" `
            -Action {
                Invoke-InstallInternalMsix `
                    -PackagePath "/tmp/product.msix" `
                    -CertificatePath "/tmp/product.cer" `
                    -ChecksumsPath "/tmp/SHA256SUMS.txt" `
                    -ExpectedCertificateThumbprint ("B" * 40) `
                    -ConfirmTrust
            }
        Assert-True `
            ($script:installEvents.Contains("rollback:cert=True")) `
            "Add-AppxPackage failure did not roll back exact certificate."
        Assert-True `
            (-not $script:installEvents.Contains("record")) `
            "Add-AppxPackage failure wrote an install record."
    }

    Invoke-Case "AppX install and identity verification target exact current user package" {
        Reset-InstallFunctions
        $script:addCalls = [Collections.Generic.List[string]]::new()
        Set-TestFunction -Name Add-AppxPackage -Body {
            param($Path, $ErrorAction)
            $script:addCalls.Add($Path)
        }
        Invoke-AddExactAppxPackage -PackagePath "C:\Bundle\Product.msix"
        Assert-Equal `
            ($script:addCalls -join "|") `
            "C:\Bundle\Product.msix" `
            "Add-AppxPackage path differs."

        Set-TestFunction -Name Get-AppxPackage -Body {
            param($Name, $ErrorAction)
            @([pscustomobject]@{
                Name = $Name
                Publisher = "CN=EMKE Internal Test"
                Version = [version]"0.1.0.0"
                Architecture = "X64"
                PackageFullName =
                    "EMKE.Translation.Internal_0.1.0.0_x64__test"
            })
        }
        $package = Assert-InstalledInternalPackage
        Assert-Equal `
            $package.Name `
            "EMKE.Translation.Internal" `
            "Installed package Name differs."

        Set-TestFunction -Name Get-AppxPackage -Body {
            param($Name, $ErrorAction)
            @([pscustomobject]@{
                Name = $Name
                Publisher = "CN=Other"
                Version = [version]"0.1.0.0"
                Architecture = "X64"
                PackageFullName =
                    "EMKE.Translation.Internal_0.1.0.0_x64__test"
            })
        }
        Assert-Throws `
            -Pattern "Publisher" `
            -Action { Assert-InstalledInternalPackage }
    }

    Invoke-Case "uninstaller removes only exact current-user package and retains certificate" {
        Reset-UninstallFunctions
        Set-SafeUninstallOrchestratorDefaults

        Invoke-UninstallInternalMsix `
            -RemoveCertificate:$false `
            -ConfirmRemoveCertificate:$false
        Assert-Equal `
            ($script:uninstallEvents -join "|") `
            (
                "parent|query-package|" +
                "remove-appx:EMKE.Translation.Internal_0.1.0.0_x64__test|" +
                "verify-absent"
            ) `
            "Default uninstall crossed the certificate boundary."
    }

    Invoke-Case "certificate removal requires both explicit switches before mutation" {
        Reset-UninstallFunctions
        Set-SafeUninstallOrchestratorDefaults

        Assert-Throws `
            -Pattern "ConfirmRemoveCertificate" `
            -Action {
                Invoke-UninstallInternalMsix `
                    -RemoveCertificate `
                    -ConfirmRemoveCertificate:$false `
                    -CertificatePath "/tmp/product.cer" `
                    -ChecksumsPath "/tmp/SHA256SUMS.txt" `
                    -ExpectedCertificateThumbprint ("B" * 40)
            }
        Assert-Equal `
            $script:uninstallEvents.Count `
            0 `
            "Missing certificate confirmation performed work."
    }

    Invoke-Case "uninstaller verifies recorded thumbprint then removes package and exact certificate" {
        Reset-UninstallFunctions
        Set-SafeUninstallOrchestratorDefaults

        Invoke-UninstallInternalMsix `
            -RemoveCertificate `
            -ConfirmRemoveCertificate `
            -CertificatePath "/tmp/product.cer" `
            -ChecksumsPath "/tmp/SHA256SUMS.txt" `
            -ExpectedCertificateThumbprint ("B" * 40)
        Assert-Equal `
            ($script:uninstallEvents -join "|") `
            (
                "parent|inventory|certificate|read-record|query-package|" +
                "remove-appx:EMKE.Translation.Internal_0.1.0.0_x64__test|" +
                "verify-absent|inventory-unchanged|" +
                "elevate-remove|remove-record"
            ) `
            "Certificate-removal sequence differs."

        Reset-UninstallFunctions
        Set-SafeUninstallOrchestratorDefaults
        Set-TestFunction -Name Read-CertificateInstallRecord -Body {
            param([switch]$AllowMissing)
            [pscustomobject]@{
                PackageName = "EMKE.Translation.Internal"
                CertificateSubject = "CN=EMKE Internal Test"
                CertificateThumbprint = "C" * 40
                CertificateSha256 = "A" * 64
            }
        }
        Assert-Throws `
            -Pattern "recorded thumbprint" `
            -Action {
                Invoke-UninstallInternalMsix `
                    -RemoveCertificate `
                    -ConfirmRemoveCertificate `
                    -CertificatePath "/tmp/product.cer" `
                    -ChecksumsPath "/tmp/SHA256SUMS.txt" `
                    -ExpectedCertificateThumbprint ("B" * 40)
            }
        Assert-True `
            (-not ($script:uninstallEvents -join "|").Contains("remove-appx")) `
            "Recorded-thumbprint mismatch removed the package."
        Assert-True `
            (-not $script:uninstallEvents.Contains("elevate-remove")) `
            "Recorded-thumbprint mismatch reached elevation."

        Reset-UninstallFunctions
        Set-SafeUninstallOrchestratorDefaults
        Set-TestFunction -Name Read-CertificateInstallRecord -Body {
            param([switch]$AllowMissing)
            [pscustomobject]@{
                PackageName = "EMKE.Translation.Internal"
                CertificateSubject = "CN=EMKE Internal Test"
                CertificateThumbprint = "B" * 40
                CertificateSha256 = "C" * 64
            }
        }
        Assert-Throws `
            -Pattern "recorded bytes" `
            -Action {
                Invoke-UninstallInternalMsix `
                    -RemoveCertificate `
                    -ConfirmRemoveCertificate `
                    -CertificatePath "/tmp/product.cer" `
                    -ChecksumsPath "/tmp/SHA256SUMS.txt" `
                    -ExpectedCertificateThumbprint ("B" * 40)
            }
        Assert-True `
            (-not ($script:uninstallEvents -join "|").Contains("remove-appx")) `
            "Recorded-byte mismatch removed the package."
    }

    Invoke-Case "absent package can still clean exact certificate after failed install" {
        Reset-UninstallFunctions
        Set-SafeUninstallOrchestratorDefaults
        Set-TestFunction -Name Get-OptionalInstalledInternalPackage -Body {
            $script:uninstallEvents.Add("query-package")
            $null
        }
        Set-TestFunction -Name Read-CertificateInstallRecord -Body {
            param([switch]$AllowMissing)
            $script:uninstallEvents.Add("read-record")
            $null
        }

        Invoke-UninstallInternalMsix `
            -RemoveCertificate `
            -ConfirmRemoveCertificate `
            -CertificatePath "/tmp/product.cer" `
            -ChecksumsPath "/tmp/SHA256SUMS.txt" `
            -ExpectedCertificateThumbprint ("B" * 40)
        Assert-True `
            (-not ($script:uninstallEvents -join "|").Contains("remove-appx")) `
            "Absent package path invoked Remove-AppxPackage."
        Assert-True `
            ($script:uninstallEvents.Contains("elevate-remove")) `
            "Absent package blocked exact certificate recovery."
        Assert-True `
            ($script:uninstallEvents.Contains("remove-record")) `
            "Exact certificate recovery did not clean stale record."
    }

    Invoke-Case "AppX uninstall targets one exact full name and verifies absence" {
        Reset-UninstallFunctions
        $script:removeCalls = [Collections.Generic.List[string]]::new()
        Set-TestFunction -Name Get-AppxPackage -Body {
            param($Name, $ErrorAction)
            @([pscustomobject]@{
                Name = "EMKE.Translation.Internal"
                Publisher = "CN=EMKE Internal Test"
                Version = [version]"0.1.0.0"
                Architecture = "X64"
                PackageFullName =
                    "EMKE.Translation.Internal_0.1.0.0_x64__test"
            })
        }
        $package = Get-ExactInstalledInternalPackage
        Set-TestFunction -Name Remove-AppxPackage -Body {
            param($Package, $ErrorAction)
            $script:removeCalls.Add($Package)
        }
        Invoke-RemoveExactAppxPackage `
            -PackageFullName $package.PackageFullName
        Assert-Equal `
            ($script:removeCalls -join "|") `
            "EMKE.Translation.Internal_0.1.0.0_x64__test" `
            "Remove-AppxPackage target differs."

        Set-TestFunction -Name Get-AppxPackage -Body {
            param($Name, $ErrorAction)
            @()
        }
        Assert-InternalPackageAbsent
    }

} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

if ($script:failures.Count -ne 0) {
    foreach ($failure in $script:failures) {
        Write-Error $failure
    }
    throw "$($script:failures.Count) Internal MSIX lifecycle behavior case(s) failed."
}

Write-Host "All Internal MSIX lifecycle behavior cases passed."
