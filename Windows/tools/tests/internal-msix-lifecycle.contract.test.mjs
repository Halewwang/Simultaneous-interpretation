import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('../../../', import.meta.url));
const appPackagingDirectory = path.join(
  repositoryRoot,
  'Windows',
  'packaging',
  'App',
);
const installPath = path.join(
  appPackagingDirectory,
  'Install-EMKE-Translation-Internal.ps1',
);
const uninstallPath = path.join(
  appPackagingDirectory,
  'Uninstall-EMKE-Translation-Internal.ps1',
);

async function readRequired(filePath) {
  try {
    return (await readFile(filePath, 'utf8')).replace(/\r\n?/g, '\n');
  } catch (error) {
    assert.fail(`required lifecycle helper is missing: ${filePath}\n${error.message}`);
  }
}

function runPowerShell(command, environment = {}) {
  return spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', command],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        ...environment,
      },
    },
  );
}

test('installer exposes the exact public and constrained child parameter sets', () => {
  const command = [
    '$ErrorActionPreference = "Stop"',
    '$command = Get-Command -Name $env:EMKE_LIFECYCLE_SCRIPT',
    '$common = [System.Management.Automation.PSCmdlet]::CommonParameters',
    '$parameters = @($command.Parameters.Keys |',
    '  Where-Object { $_ -notin $common } |',
    '  Sort-Object)',
    '[pscustomobject]@{',
    '  Parameters = $parameters;',
    '  Sets = @($command.ParameterSets.Name | Sort-Object)',
    '} | ConvertTo-Json -Compress',
  ].join('\n');
  const result = runPowerShell(command, {
    EMKE_LIFECYCLE_SCRIPT: installPath,
  });

  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    Parameters: [
      'CertificatePath',
      'ChecksumsPath',
      'ConfirmTrust',
      'ExpectedCertificateSha256',
      'ExpectedCertificateThumbprint',
      'ImportCertificateChild',
      'PackagePath',
      'VerifiedCertificatePath',
    ],
    Sets: ['ImportCertificateChild', 'Install'],
  });
});

test('uninstaller exposes exact package removal and separately confirmed certificate removal', () => {
  const command = [
    '$ErrorActionPreference = "Stop"',
    '$command = Get-Command -Name $env:EMKE_LIFECYCLE_SCRIPT',
    '$common = [System.Management.Automation.PSCmdlet]::CommonParameters',
    '$parameters = @($command.Parameters.Keys |',
    '  Where-Object { $_ -notin $common } |',
    '  Sort-Object)',
    '[pscustomobject]@{',
    '  Parameters = $parameters;',
    '  Sets = @($command.ParameterSets.Name | Sort-Object)',
    '} | ConvertTo-Json -Compress',
  ].join('\n');
  const result = runPowerShell(command, {
    EMKE_LIFECYCLE_SCRIPT: uninstallPath,
  });

  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    Parameters: [
      'CertificatePath',
      'ChecksumsPath',
      'ConfirmRemoveCertificate',
      'ExpectedCertificateSha256',
      'ExpectedCertificateThumbprint',
      'RemoveCertificate',
      'RemoveCertificateChild',
      'VerifiedCertificatePath',
    ],
    Sets: ['RemoveCertificateChild', 'Uninstall'],
  });
});

for (const [operation, scriptPath] of [
  ['install', installPath],
  ['uninstall', uninstallPath],
]) {
  test(`${operation} helper rejects dot-source before leaking functions`, () => {
    const dotSourceArguments = operation === 'install'
      ? '-PackagePath /tmp/a.msix -CertificatePath /tmp/a.cer -ChecksumsPath /tmp/SHA256SUMS.txt'
      : '';
    const command = [
      '$ErrorActionPreference = "Continue"',
      '$before = @(Get-ChildItem Function: | ForEach-Object Name)',
      '$caught = $null',
      `try { . $env:EMKE_LIFECYCLE_SCRIPT ${dotSourceArguments} } catch { $caught = $_ }`,
      '$after = @(Get-ChildItem Function: | ForEach-Object Name)',
      '$leaked = @($after | Where-Object { $_ -notin $before -and $_ -like "*Internal*" })',
      'if ($null -eq $caught) { throw "dot-source unexpectedly succeeded" }',
      'if ($caught.Exception.Message -notmatch "Dot-source invocation is forbidden") {',
      '  throw $caught',
      '}',
      'if ($leaked.Count -ne 0) { throw "dot-source leaked lifecycle functions" }',
    ].join('\n');
    const result = runPowerShell(command, {
      EMKE_LIFECYCLE_SCRIPT: scriptPath,
    });

    assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
  });
}

test('installer contains only the approved package and certificate mutation boundaries', async () => {
  const source = await readRequired(installPath);

  assert.match(source, /DefaultParameterSetName\s*=\s*["']Install["']/);
  assert.match(source, /EMKE\.Translation\.Internal/);
  assert.match(source, /CN=EMKE Internal Test/);
  assert.match(source, /0\.1\.0\.0/);
  assert.match(source, /LocalMachine/);
  assert.match(source, /TrustedPeople/);
  assert.match(source, /Start-Process[^]*?-Verb\s+RunAs[^]*?-Wait[^]*?-PassThru/);
  assert.match(source, /Add-AppxPackage/);
  assert.doesNotMatch(source, /-AllUsers\b/i);
  assert.doesNotMatch(source, /StoreName\]::Root|Cert:\\LocalMachine\\Root/i);
  assert.doesNotMatch(
    source,
    /\b(?:pnputil|devcon|dism|sc)(?:\.exe)?\b|Install-EMKE-LabDriver|Uninstall-EMKE-LabDriver/i,
  );
});

test('uninstaller contains only exact current-user AppX and certificate mutation boundaries', async () => {
  const source = await readRequired(uninstallPath);

  assert.match(source, /DefaultParameterSetName\s*=\s*["']Uninstall["']/);
  assert.match(source, /EMKE\.Translation\.Internal/);
  assert.match(source, /LocalMachine/);
  assert.match(source, /TrustedPeople/);
  assert.match(source, /Remove-AppxPackage/);
  assert.match(source, /Start-Process[^]*?-Verb\s+RunAs[^]*?-Wait[^]*?-PassThru/);
  assert.doesNotMatch(source, /-AllUsers\b/i);
  assert.doesNotMatch(source, /StoreName\]::Root|Cert:\\LocalMachine\\Root/i);
  assert.doesNotMatch(
    source,
    /\b(?:pnputil|devcon|dism|sc)(?:\.exe)?\b|Install-EMKE-LabDriver|Uninstall-EMKE-LabDriver/i,
  );
});
