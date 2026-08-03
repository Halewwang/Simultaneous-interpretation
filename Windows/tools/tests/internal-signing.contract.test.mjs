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
const deliveryPlanPath = path.join(
  repositoryRoot,
  'docs',
  'superpowers',
  'plans',
  '2026-07-27-emke-windows-internal-msix.md',
);

function assertEnvironmentScopedSecretCommands(document) {
  const commands = [
    ...document.matchAll(/^\s*gh secret (set|list)\b[^\n]*$/gm),
  ].map((match) => match[0].trim());

  for (const command of commands) {
    assert.doesNotMatch(
      command,
      /(?:^|\s)--(?:org|app)(?:\s|=|$)/,
      'GitHub secret commands must not use --org or --app.',
    );
    assert.match(
      command,
      /^gh secret (?:set|list) --env windows-internal-signing(?:\s|$)/,
      'Every GitHub secret command must use exact --env windows-internal-signing scope.',
    );
  }

  return commands;
}

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

test('provisioning guide scopes both secrets to the protected environment', async () => {
  const guide = await readFile(readmePath, 'utf8');
  const secretCommands = assertEnvironmentScopedSecretCommands(guide);
  const secretSetCommands = secretCommands.filter(
    (command) => command.startsWith('gh secret set '),
  );
  const secretListCommands = secretCommands.filter(
    (command) => command.startsWith('gh secret list '),
  );

  assert.deepEqual(secretSetCommands, [
    'gh secret set --env windows-internal-signing WINDOWS_INTERNAL_SIGNING_PFX_BASE64 \\',
    'gh secret set --env windows-internal-signing WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD \\',
  ]);
  assert.deepEqual(secretListCommands, [
    'gh secret list --env windows-internal-signing',
  ]);
  assert.match(guide, /GitHub Environment named `windows-internal-signing`/i);
  assert.match(guide, /required reviewers/i);
  assert.match(
    guide,
    /workflow job[\s\S]*environment:\s*windows-internal-signing/i,
  );
});

test('delivery plan checks only environment-scoped secret names', async () => {
  const plan = await readFile(deliveryPlanPath, 'utf8');
  const provisioningStart = plan.indexOf(
    '- [ ] **Step 2: Provision the persistent Internal certificate secrets**',
  );
  const provisioningEnd = plan.indexOf(
    '- [ ] **Step 3: Push and monitor Windows CI**',
    provisioningStart,
  );
  assert.notEqual(provisioningStart, -1);
  assert.notEqual(provisioningEnd, -1);
  const provisioningSection = plan.slice(provisioningStart, provisioningEnd);

  assertEnvironmentScopedSecretCommands(provisioningSection);
  assert.match(
    provisioningSection,
    /^gh secret list --env windows-internal-signing \| rg \\$/m,
  );
});

test('secret command scanner rejects indented and alternate-scope commands', () => {
  const rejectedCommands = [
    [
      '  gh secret set WINDOWS_INTERNAL_SIGNING_PFX_BASE64',
      /must use exact --env windows-internal-signing scope/,
    ],
    [
      'gh secret list',
      /must use exact --env windows-internal-signing scope/,
    ],
    [
      'gh secret set --org emke WINDOWS_INTERNAL_SIGNING_PFX_BASE64',
      /must not use --org or --app/,
    ],
    [
      'gh secret list --app actions',
      /must not use --org or --app/,
    ],
  ];

  for (const [command, expectedFailure] of rejectedCommands) {
    assert.throws(
      () => assertEnvironmentScopedSecretCommands(command),
      expectedFailure,
    );
  }
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
