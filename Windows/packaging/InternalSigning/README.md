# Internal MSIX signing certificate

The Internal channel uses a self-signed code-signing certificate with this
fixed contract:

- subject `CN=EMKE Internal Test`;
- RSA key size of at least 3072 bits;
- SHA-256 or stronger certificate signature;
- code-signing EKU `1.3.6.1.5.5.7.3.3`;
- Digital Signature key usage; and
- a currently valid certificate with a private key in the PFX.

The PFX and password are runner inputs only. The exported `.cer` is public and
must never contain a private key. This certificate is for Internal testing; it
does not establish public publisher trust.

## One-time controller provisioning

Run this only from the trusted macOS controller, from a checkout connected to
the intended GitHub repository:

```bash
signing_temp="$(mktemp -d /tmp/emke-msix-signing.XXXXXX)"
openssl rand -base64 48 > "$signing_temp/password"
openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 730 \
  -subj "/CN=EMKE Internal Test" \
  -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=codeSigning" \
  -keyout "$signing_temp/key.pem" \
  -out "$signing_temp/cert.pem"
openssl pkcs12 -export \
  -out "$signing_temp/app.pfx" \
  -inkey "$signing_temp/key.pem" \
  -in "$signing_temp/cert.pem" \
  -passout "file:$signing_temp/password"
base64 < "$signing_temp/app.pfx" > "$signing_temp/app.pfx.base64"
gh secret set WINDOWS_INTERNAL_SIGNING_PFX_BASE64 \
  < "$signing_temp/app.pfx.base64"
gh secret set WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD \
  < "$signing_temp/password"
```

Run `gh secret list` and confirm that both
`WINDOWS_INTERNAL_SIGNING_PFX_BASE64` and
`WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD` are present. The command confirms only
the secret names; it must not reveal either value.

After that confirmation, remove exactly the generated files and then the
now-empty generated directory:

```bash
rm -- "$signing_temp/password" "$signing_temp/key.pem" "$signing_temp/cert.pem" "$signing_temp/app.pfx" "$signing_temp/app.pfx.base64"
rmdir -- "$signing_temp"
```

Never commit the password, private key, PFX, or Base64 PFX. Never upload them
as build artifacts. Never print their contents. Do not recursively remove a
broader temporary directory.

## Runner verification

The Windows runner writes the PFX to a runner-owned temporary directory, keeps
the password only in the named environment variable, and invokes:

```powershell
pwsh Windows/tools/verify-internal-signing-certificate.ps1 `
  -PfxPath $pfxPath `
  -PasswordEnvironmentVariable WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD `
  -ExpectedSubject "CN=EMKE Internal Test" `
  -ExportPublicCertificatePath $cerPath
```

The verifier is read-only with respect to certificate stores. It validates the
PFX, exports only DER public-certificate bytes, and prints only public
certificate metadata. Packaging must clean the runner-owned PFX after signing
and may publish only the `.cer` alongside the signed Internal package.
