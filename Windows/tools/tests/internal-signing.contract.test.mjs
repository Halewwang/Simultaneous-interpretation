import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('../../../', import.meta.url));
const verifierPath = path.join(
  repositoryRoot,
  'Windows',
  'tools',
  'verify-internal-signing-certificate.ps1',
);
const readmePath = path.join(
  repositoryRoot,
  'Windows',
  'packaging',
  'InternalSigning',
  'README.md',
);

test('verifier exposes only path, environment-variable, subject, and CER inputs', () => {
  const command = [
    '$ErrorActionPreference = "Stop"',
    '$command = Get-Command -Name $env:EMKE_SIGNING_VERIFIER',
    '$common = [System.Management.Automation.PSCmdlet]::CommonParameters',
    '$parameters = @($command.Parameters.Keys |',
    '  Where-Object { $_ -notin $common } |',
    '  Sort-Object)',
    '$parameters | ConvertTo-Json -Compress',
  ].join('\n');
  const result = spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        EMKE_SIGNING_VERIFIER: verifierPath,
      },
    },
  );

  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), [
    'ExpectedSubject',
    'ExportPublicCertificatePath',
    'PasswordEnvironmentVariable',
    'PfxPath',
  ]);
});

test('provisioning guide keeps PFX material in a named temporary directory', async () => {
  const guide = await readFile(readmePath, 'utf8');

  assert.match(
    guide,
    /signing_temp="\$\(mktemp -d \/tmp\/emke-msix-signing\.XXXXXX\)"/,
  );
  assert.match(guide, /openssl rand -base64 48 > "\$signing_temp\/password"/);
  assert.match(guide, /openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 730/);
  assert.match(guide, /-subj "\/CN=EMKE Internal Test"/);
  assert.match(guide, /keyUsage=critical,digitalSignature/);
  assert.match(guide, /extendedKeyUsage=codeSigning/);
  assert.match(guide, /openssl pkcs12 -export/);
  assert.match(guide, /-passout "file:\$signing_temp\/password"/);
});

test('provisioning guide configures only the two named GitHub secrets', async () => {
  const guide = await readFile(readmePath, 'utf8');

  assert.match(
    guide,
    /gh secret set WINDOWS_INTERNAL_SIGNING_PFX_BASE64\s+\\\s*\n\s*< "\$signing_temp\/app\.pfx\.base64"/,
  );
  assert.match(
    guide,
    /gh secret set WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD\s+\\\s*\n\s*< "\$signing_temp\/password"/,
  );
  assert.match(guide, /gh secret list/);
  assert.match(guide, /WINDOWS_INTERNAL_SIGNING_PFX_BASE64/);
  assert.match(guide, /WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD/);
});

test('provisioning guide requires exact cleanup and prohibits disclosure', async () => {
  const guide = await readFile(readmePath, 'utf8');

  assert.match(
    guide,
    /rm -- "\$signing_temp\/password" "\$signing_temp\/key\.pem" "\$signing_temp\/cert\.pem" "\$signing_temp\/app\.pfx" "\$signing_temp\/app\.pfx\.base64"/,
  );
  assert.match(guide, /rmdir -- "\$signing_temp"/);
  assert.match(guide, /Never commit/i);
  assert.match(guide, /Never upload/i);
  assert.match(guide, /Never print/i);
});
