[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PfxPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PasswordEnvironmentVariable,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedSubject,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExportPublicCertificatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$codeSigningOid = "1.3.6.1.5.5.7.3.3"
$strongRsaSignatureOids = @(
    "1.2.840.113549.1.1.11",
    "1.2.840.113549.1.1.12",
    "1.2.840.113549.1.1.13"
)
$certificate = $null
$password = $null
$pfxBytes = $null

try {
    if (-not (Test-Path -LiteralPath $PfxPath -PathType Leaf)) {
        throw "PFX input validation failed."
    }

    $password = [Environment]::GetEnvironmentVariable(
        $PasswordEnvironmentVariable,
        [EnvironmentVariableTarget]::Process
    )
    if ([string]::IsNullOrEmpty($password)) {
        throw "Signing password environment variable is unavailable."
    }

    try {
        $pfxBytes = [IO.File]::ReadAllBytes($PfxPath)
        $keyStorageFlags = if ($IsWindows) {
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        } else {
            # macOS PowerShell does not support EphemeralKeySet. Its default
            # loader uses a process-temporary key file and removes it when the
            # certificate is disposed; production Windows runners stay
            # explicitly ephemeral.
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet
        }
        $certificate =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $pfxBytes,
                $password,
                $keyStorageFlags
            )
    } catch {
        throw "PFX loading failed."
    } finally {
        if ($null -ne $pfxBytes) {
            [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
            $pfxBytes = $null
        }
        $password = $null
    }

    if (-not $certificate.HasPrivateKey) {
        throw "PFX private key validation failed."
    }
    if (-not [string]::Equals(
        $certificate.Subject,
        $ExpectedSubject,
        [StringComparison]::Ordinal
    )) {
        throw "Certificate subject validation failed."
    }

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey(
        $certificate
    )
    if ($null -eq $rsa) {
        throw "Certificate RSA key-size validation failed."
    }
    try {
        $rsaKeySize = $rsa.KeySize
    } finally {
        $rsa.Dispose()
    }
    if ($rsaKeySize -lt 3072) {
        throw "Certificate RSA key-size validation failed."
    }

    if ($certificate.SignatureAlgorithm.Value -notin $strongRsaSignatureOids) {
        throw "Certificate signature algorithm validation failed."
    }

    $ekuOids = @(
        $certificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
            ForEach-Object {
                $extension =
                    [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_
                $extension.EnhancedKeyUsages |
                    ForEach-Object { $_.Value }
            }
    )
    if ($codeSigningOid -notin $ekuOids) {
        throw "Certificate code-signing EKU validation failed."
    }

    $keyUsageExtensions = @(
        $certificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.15" }
    )
    $hasDigitalSignature = $false
    foreach ($extension in $keyUsageExtensions) {
        $keyUsage =
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]$extension
        if (
            ($keyUsage.KeyUsages -band
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -ne
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::None
        ) {
            $hasDigitalSignature = $true
            break
        }
    }
    if (-not $hasDigitalSignature) {
        throw "Certificate digital-signature key usage validation failed."
    }

    $nowUtc = [datetime]::UtcNow
    $notBeforeUtc = $certificate.NotBefore.ToUniversalTime()
    $notAfterUtc = $certificate.NotAfter.ToUniversalTime()
    if ($nowUtc -lt $notBeforeUtc -or $nowUtc -gt $notAfterUtc) {
        throw "Certificate validity validation failed."
    }

    try {
        $publicBytes = $certificate.Export(
            [Security.Cryptography.X509Certificates.X509ContentType]::Cert
        )
        $publicCertificate =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $publicBytes
            )
        try {
            if ($publicCertificate.HasPrivateKey) {
                throw "Exported CER private key validation failed."
            }
        } finally {
            $publicCertificate.Dispose()
        }

        $exportPath = [IO.Path]::GetFullPath(
            $ExportPublicCertificatePath
        )
        $exportDirectory = [IO.Path]::GetDirectoryName($exportPath)
        if (-not [IO.Directory]::Exists($exportDirectory)) {
            throw "Export directory unavailable."
        }
        [IO.File]::WriteAllBytes($exportPath, $publicBytes)
    } catch {
        if ($_.Exception.Message -eq "Exported CER private key validation failed.") {
            throw
        }
        throw "Public certificate export failed."
    }

    $validity = "{0:yyyy-MM-ddTHH:mm:ssZ} - {1:yyyy-MM-ddTHH:mm:ssZ}" -f
        $notBeforeUtc,
        $notAfterUtc
    $ekuDisplay = @($ekuOids | Sort-Object -Unique) -join ","
    $publicThumbprint = $certificate.Thumbprint.ToUpperInvariant()

    Write-Output "Subject: $($certificate.Subject)"
    Write-Output "Validity: $validity"
    Write-Output "EKU: $ekuDisplay"
    Write-Output "RSA key size: $rsaKeySize"
    Write-Output "Public thumbprint: $publicThumbprint"
} finally {
    $password = $null
    if ($null -ne $pfxBytes) {
        [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
    }
    if ($null -ne $certificate) {
        $certificate.Dispose()
    }
}
