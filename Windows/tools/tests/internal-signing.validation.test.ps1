[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$verifierPath = Join-Path `
    (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path `
    "verify-internal-signing-certificate.ps1"
$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("emke-internal-signing-" + [guid]::NewGuid().ToString("N"))
$passwordVariable = "EMKE_INTERNAL_SIGNING_TEST_PASSWORD"
$passwordCanary = "PASSWORD-CANARY-" + [guid]::NewGuid().ToString("N")
$expectedSubject = "CN=EMKE Internal Test"
$codeSigningOid = "1.3.6.1.5.5.7.3.3"
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

function New-TestPfx {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [string]$Subject = $expectedSubject,

        [int]$KeySize = 3072,

        [string]$HashAlgorithm = "SHA256",

        [bool]$IncludeCodeSigningEku = $true,

        [bool]$IncludeDigitalSignature = $true,

        [datetimeoffset]$NotBefore = [datetimeoffset]::UtcNow.AddMinutes(-5),

        [datetimeoffset]$NotAfter = [datetimeoffset]::UtcNow.AddDays(30),

        [switch]$WithoutPrivateKey
    )

    $rsa = [Security.Cryptography.RSA]::Create($KeySize)
    try {
        $requestHash = if ($HashAlgorithm -eq "SHA1") {
            "SHA256"
        } else {
            $HashAlgorithm
        }
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            [Security.Cryptography.X509Certificates.X500DistinguishedName]::new(
                $Subject
            ),
            $rsa,
            [Security.Cryptography.HashAlgorithmName]::new($requestHash),
            [Security.Cryptography.RSASignaturePadding]::Pkcs1
        )

        $keyUsage = if ($IncludeDigitalSignature) {
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature
        } else {
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
        }
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                $keyUsage,
                $true
            )
        )

        $ekuOids = [Security.Cryptography.OidCollection]::new()
        $ekuValue = if ($IncludeCodeSigningEku) {
            $codeSigningOid
        } else {
            "1.3.6.1.5.5.7.3.1"
        }
        $null = $ekuOids.Add([Security.Cryptography.Oid]::new($ekuValue))
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $ekuOids,
                $false
            )
        )

        $certificate = $request.CreateSelfSigned($NotBefore, $NotAfter)
        if ($HashAlgorithm -eq "SHA1") {
            $certificateBytes = $certificate.Export(
                [Security.Cryptography.X509Certificates.X509ContentType]::Cert
            )
            $sha256WithRsaOid = [byte[]]@(
                0x06, 0x09, 0x2A, 0x86, 0x48, 0x86,
                0xF7, 0x0D, 0x01, 0x01, 0x0B
            )
            $replacementCount = 0
            for (
                $offset = 0;
                $offset -le $certificateBytes.Length - $sha256WithRsaOid.Length;
                $offset += 1
            ) {
                $matchesOid = $true
                for (
                    $index = 0;
                    $index -lt $sha256WithRsaOid.Length;
                    $index += 1
                ) {
                    if (
                        $certificateBytes[$offset + $index] -ne
                            $sha256WithRsaOid[$index]
                    ) {
                        $matchesOid = $false
                        break
                    }
                }
                if ($matchesOid) {
                    $certificateBytes[
                        $offset + $sha256WithRsaOid.Length - 1
                    ] = 0x05
                    $replacementCount += 1
                }
            }
            if ($replacementCount -ne 2) {
                throw "Synthetic weak-signature fixture could not be created."
            }

            $certificate.Dispose()
            $publicWeakCertificate =
                [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $certificateBytes
                )
            try {
                $certificate =
                    [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey(
                        $publicWeakCertificate,
                        $rsa
                    )
            } finally {
                $publicWeakCertificate.Dispose()
            }
        }
        try {
            if ($WithoutPrivateKey) {
                $publicOnly = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $certificate.Export(
                        [Security.Cryptography.X509Certificates.X509ContentType]::Cert
                    )
                )
                try {
                    $bytes = $publicOnly.Export(
                        [Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
                        $passwordCanary
                    )
                } finally {
                    $publicOnly.Dispose()
                }
            } else {
                $bytes = $certificate.Export(
                    [Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
                    $passwordCanary
                )
            }
            [IO.File]::WriteAllBytes($Path, $bytes)
        } finally {
            $certificate.Dispose()
        }
    } finally {
        $rsa.Dispose()
    }
}

function Invoke-Verifier {
    param(
        [Parameter(Mandatory)]
        [string]$PfxPath,

        [Parameter(Mandatory)]
        [string]$ExportPath,

        [string]$Subject = $expectedSubject
    )

    return @(
        & $verifierPath `
            -PfxPath $PfxPath `
            -PasswordEnvironmentVariable $passwordVariable `
            -ExpectedSubject $Subject `
            -ExportPublicCertificatePath $ExportPath
    )
}

function Assert-PrivateFailure {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Pattern
    )

    $caught = $null
    $output = @()
    try {
        $output = @(& $Action *>&1)
    } catch {
        $caught = $_
    }
    if ($null -eq $caught) {
        throw "Expected verification failure matching '$Pattern'."
    }

    $diagnostic = (@($output) + @($caught.Exception.ToString())) -join "`n"
    if ($diagnostic -notmatch $Pattern) {
        throw "Failure did not match '$Pattern'."
    }
    if ($diagnostic.Contains($passwordCanary, [StringComparison]::Ordinal)) {
        throw "Failure disclosed the signing password."
    }
}

[IO.Directory]::CreateDirectory($testRoot) | Out-Null
[Environment]::SetEnvironmentVariable(
    $passwordVariable,
    $passwordCanary,
    [EnvironmentVariableTarget]::Process
)

try {
    Invoke-Case "valid PFX exports public-only DER and safe metadata" {
        $pfx = Join-Path $testRoot "valid.pfx"
        $cer = Join-Path $testRoot "valid.cer"
        New-TestPfx -Path $pfx

        $output = @(Invoke-Verifier -PfxPath $pfx -ExportPath $cer)
        Assert-Equal $output.Count 5 "Verifier must emit exactly five fields."
        Assert-True `
            ($output[0] -eq "Subject: CN=EMKE Internal Test") `
            "Verifier did not emit the expected public subject."
        Assert-True `
            ($output[1] -match "^Validity: .+ - .+$") `
            "Verifier did not emit one validity field."
        Assert-Equal `
            $output[2] `
            "EKU: 1.3.6.1.5.5.7.3.3" `
            "Verifier did not emit the expected public EKU."
        Assert-Equal `
            $output[3] `
            "RSA key size: 3072" `
            "Verifier did not emit the expected RSA key size."
        Assert-True `
            ($output[4] -match "^Public thumbprint: [A-F0-9]{40,}$") `
            "Verifier did not emit one public thumbprint."
        Assert-True `
            (-not (($output -join "`n").Contains(
                $passwordCanary,
                [StringComparison]::Ordinal
            ))) `
            "Verifier disclosed the signing password."

        $publicCertificate =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
        try {
            Assert-True `
                (-not $publicCertificate.HasPrivateKey) `
                "Exported CER retained a private key."
        } finally {
            $publicCertificate.Dispose()
        }
    }

    Invoke-Case "password must come from the named environment variable" {
        $pfx = Join-Path $testRoot "missing-password.pfx"
        $cer = Join-Path $testRoot "missing-password.cer"
        New-TestPfx -Path $pfx
        [Environment]::SetEnvironmentVariable(
            $passwordVariable,
            $null,
            [EnvironmentVariableTarget]::Process
        )
        try {
            Assert-PrivateFailure `
                -Pattern "password environment variable is unavailable" `
                -Action {
                    Invoke-Verifier -PfxPath $pfx -ExportPath $cer
                }
        } finally {
            [Environment]::SetEnvironmentVariable(
                $passwordVariable,
                $passwordCanary,
                [EnvironmentVariableTarget]::Process
            )
        }
    }

    Invoke-Case "PFX must contain a private key" {
        $pfx = Join-Path $testRoot "public-only.pfx"
        $cer = Join-Path $testRoot "public-only.cer"
        New-TestPfx -Path $pfx -WithoutPrivateKey
        Assert-PrivateFailure -Pattern "private key validation failed" -Action {
            Invoke-Verifier -PfxPath $pfx -ExportPath $cer
        }
    }

    Invoke-Case "subject must match exactly" {
        $pfx = Join-Path $testRoot "wrong-subject.pfx"
        $cer = Join-Path $testRoot "wrong-subject.cer"
        New-TestPfx -Path $pfx -Subject "CN=Other Internal Test"
        Assert-PrivateFailure -Pattern "subject validation failed" -Action {
            Invoke-Verifier -PfxPath $pfx -ExportPath $cer
        }
    }

    Invoke-Case "RSA key size must be at least 3072 bits" {
        $pfx = Join-Path $testRoot "weak-key.pfx"
        $cer = Join-Path $testRoot "weak-key.cer"
        New-TestPfx -Path $pfx -KeySize 2048
        Assert-PrivateFailure -Pattern "RSA key-size validation failed" -Action {
            Invoke-Verifier -PfxPath $pfx -ExportPath $cer
        }
    }

    Invoke-Case "certificate signature hash must be SHA-256 or stronger" {
        $pfx = Join-Path $testRoot "weak-hash.pfx"
        $cer = Join-Path $testRoot "weak-hash.cer"
        New-TestPfx -Path $pfx -HashAlgorithm "SHA1"
        Assert-PrivateFailure -Pattern "signature algorithm validation failed" -Action {
            Invoke-Verifier -PfxPath $pfx -ExportPath $cer
        }
    }

    Invoke-Case "EKU must include code signing" {
        $pfx = Join-Path $testRoot "wrong-eku.pfx"
        $cer = Join-Path $testRoot "wrong-eku.cer"
        New-TestPfx -Path $pfx -IncludeCodeSigningEku $false
        Assert-PrivateFailure -Pattern "code-signing EKU validation failed" -Action {
            Invoke-Verifier -PfxPath $pfx -ExportPath $cer
        }
    }

    Invoke-Case "key usage must include digital signature" {
        $pfx = Join-Path $testRoot "wrong-key-usage.pfx"
        $cer = Join-Path $testRoot "wrong-key-usage.cer"
        New-TestPfx -Path $pfx -IncludeDigitalSignature $false
        Assert-PrivateFailure -Pattern "digital-signature key usage validation failed" -Action {
            Invoke-Verifier -PfxPath $pfx -ExportPath $cer
        }
    }

    Invoke-Case "certificate must not be expired" {
        $pfx = Join-Path $testRoot "expired.pfx"
        $cer = Join-Path $testRoot "expired.cer"
        New-TestPfx `
            -Path $pfx `
            -NotBefore ([datetimeoffset]::UtcNow.AddDays(-10)) `
            -NotAfter ([datetimeoffset]::UtcNow.AddDays(-5))
        Assert-PrivateFailure -Pattern "validity validation failed" -Action {
            Invoke-Verifier -PfxPath $pfx -ExportPath $cer
        }
    }

    Invoke-Case "certificate must already be valid" {
        $pfx = Join-Path $testRoot "not-yet-valid.pfx"
        $cer = Join-Path $testRoot "not-yet-valid.cer"
        New-TestPfx `
            -Path $pfx `
            -NotBefore ([datetimeoffset]::UtcNow.AddDays(5)) `
            -NotAfter ([datetimeoffset]::UtcNow.AddDays(10))
        Assert-PrivateFailure -Pattern "validity validation failed" -Action {
            Invoke-Verifier -PfxPath $pfx -ExportPath $cer
        }
    }
} finally {
    [Environment]::SetEnvironmentVariable(
        $passwordVariable,
        $null,
        [EnvironmentVariableTarget]::Process
    )
    [IO.Directory]::Delete($testRoot, $true)
}

if ($script:failures.Count -ne 0) {
    throw (
        "Internal signing validation failed:`n{0}" -f
        ($script:failures -join [Environment]::NewLine)
    )
}

Write-Output "Internal signing validation passed."
