import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  copyFile,
  mkdtemp,
  mkdir,
  readFile,
  readdir,
  realpath,
  rm,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('../../../', import.meta.url));
const workflowPath = path.join(
  repositoryRoot,
  '.github',
  'workflows',
  'windows-internal-msix.yml',
);
const runtimeWorkflowPath = path.join(
  repositoryRoot,
  '.github',
  'workflows',
  'windows-runtime.yml',
);
const bundleBuilderPath = path.join(
  repositoryRoot,
  'Windows',
  'tools',
  'build-internal-msix-bundle.ps1',
);
const hostedInstallPath = path.join(
  repositoryRoot,
  'Windows',
  'tools',
  'test-hosted-msix-install.ps1',
);
const lifecycleBehaviorPath = path.join(
  repositoryRoot,
  'Windows',
  'tools',
  'tests',
  'internal-msix-lifecycle.behavior.test.ps1',
);
const installLifecyclePath = path.join(
  repositoryRoot,
  'Windows',
  'packaging',
  'App',
  'Install-EMKE-Translation-Internal.ps1',
);
const uninstallLifecyclePath = path.join(
  repositoryRoot,
  'Windows',
  'packaging',
  'App',
  'Uninstall-EMKE-Translation-Internal.ps1',
);
const versionMetadata = JSON.parse(
  await readFile(path.join(repositoryRoot, 'Windows', 'version.json'), 'utf8'),
);

const packageBaseName = `EMKE-Translation-Windows-${versionMetadata.productVersion}-internal-${versionMetadata.architecture}`;
const handoffNames = [
  `${packageBaseName}.msix`,
  `${packageBaseName}.cer`,
  'Install-EMKE-Translation-Internal.ps1',
  'Uninstall-EMKE-Translation-Internal.ps1',
  'SHA256SUMS.txt',
];

function workflowJob(source, name) {
  const normalizedSource = source.replaceAll('\r\n', '\n');
  const marker = `\n  ${name}:\n`;
  const start = normalizedSource.indexOf(marker);
  assert.notEqual(start, -1, `workflow job ${name} must exist`);
  const remainder = normalizedSource.slice(start + marker.length);
  const nextJob = remainder.search(/\n  [a-zA-Z0-9_-]+:\n/);
  return nextJob === -1 ? remainder : remainder.slice(0, nextJob);
}

function validateSmokeRecord(json, expectedStatus = 'driverMissing') {
  const command = `
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_HOSTED_INSTALL_SCRIPT,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  throw 'Hosted install script did not parse.'
}
$function = $ast.Find(
  {
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -ceq 'Assert-DriverMissingSmokeRecord'
  },
  $true
)
if ($null -eq $function) {
  throw 'Smoke record validator function is unavailable.'
}
Invoke-Expression $function.Extent.Text
Assert-DriverMissingSmokeRecord -Json $env:EMKE_SMOKE_JSON -ExpectedStatus $env:EMKE_SMOKE_STATUS
`;
  return spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-Command', command], {
    encoding: 'utf8',
    env: {
      ...process.env,
      EMKE_HOSTED_INSTALL_SCRIPT: hostedInstallPath,
      EMKE_SMOKE_JSON: json,
      EMKE_SMOKE_STATUS: expectedStatus,
    },
  });
}

function resolveHostedInputPath(inputPath, expectedExtension) {
  const command = `
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_HOSTED_INSTALL_SCRIPT,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  throw 'Hosted install script did not parse.'
}
$functions = @(
  $ast.FindAll(
    {
      param($node)
      $node -is [Management.Automation.Language.FunctionDefinitionAst]
    },
    $true
  )
)
foreach ($name in @('Assert-NoReparsePathChain', 'Resolve-ExactLeafFile')) {
  $function = $functions | Where-Object { $_.Name -ceq $name }
  if ($null -ne $function) {
    Invoke-Expression $function.Extent.Text
  }
}
if ($null -eq (Get-Command Resolve-ExactLeafFile -ErrorAction SilentlyContinue)) {
  throw 'Hosted input resolver function is unavailable.'
}
Resolve-ExactLeafFile -Path $env:EMKE_HOSTED_INPUT -ExpectedExtension $env:EMKE_HOSTED_EXTENSION
`;
  return spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-Command', command], {
    encoding: 'utf8',
    env: {
      ...process.env,
      EMKE_HOSTED_INSTALL_SCRIPT: hostedInstallPath,
      EMKE_HOSTED_INPUT: inputPath,
      EMKE_HOSTED_EXTENSION: expectedExtension,
    },
  });
}

function runBundleBuilder({
  inputRoot,
  outputRoot,
  allowedOutputRoot,
}) {
  const fixtureNames = handoffNames.slice(0, 4);
  return spawnSync(
    'pwsh',
    [
      '-NoLogo',
      '-NoProfile',
      '-File',
      bundleBuilderPath,
      '-PackagePath',
      path.join(inputRoot, fixtureNames[0]),
      '-CertificatePath',
      path.join(inputRoot, fixtureNames[1]),
      '-InstallScriptPath',
      path.join(inputRoot, fixtureNames[2]),
      '-UninstallScriptPath',
      path.join(inputRoot, fixtureNames[3]),
      '-AllowedOutputRoot',
      allowedOutputRoot,
      '-OutputDirectory',
      outputRoot,
      '-SourceCommit',
      '0123456789abcdef0123456789abcdef01234567',
      '-WorkflowRunId',
      '123456789',
      '-PackageIdentity',
      'EMKE.Translation.Internal',
      '-CertificateThumbprint',
      '0123456789ABCDEF0123456789ABCDEF01234567',
    ],
    { encoding: 'utf8' },
  );
}

async function createBundleInputs(inputRoot) {
  await mkdir(inputRoot, { recursive: true });
  await Promise.all([
    writeFile(path.join(inputRoot, handoffNames[0]), 'fixture-msix\n'),
    writeFile(path.join(inputRoot, handoffNames[1]), 'fixture-certificate\n'),
    copyFile(installLifecyclePath, path.join(inputRoot, handoffNames[2])),
    copyFile(uninstallLifecyclePath, path.join(inputRoot, handoffNames[3])),
  ]);
}

function workflowRunBlocks(source) {
  const lines = source.split(/\r?\n/);
  const blocks = [];
  for (let index = 0; index < lines.length; index += 1) {
    if (!/^ {8}run: \|$/.test(lines[index])) {
      continue;
    }
    const block = [];
    for (index += 1; index < lines.length; index += 1) {
      const line = lines[index];
      if (line.length > 0 && !/^ {10}/.test(line)) {
        index -= 1;
        break;
      }
      block.push(line.length === 0 ? '' : line.slice(10));
    }
    blocks.push(block.join('\n'));
  }
  return blocks;
}

function onlyRunBlockContaining(source, marker, label) {
  const matches = workflowRunBlocks(source).filter((block) =>
    block.includes(marker),
  );
  assert.equal(
    matches.length,
    1,
    `${label} must appear in exactly one PowerShell run block`,
  );
  return matches[0];
}

function assertCompleteTrxEvidence(block, label) {
  assert.match(
    block,
    /\[int\]\$counters\.total\s+-le\s+0/,
    `${label} must reject an empty selection`,
  );
  assert.match(
    block,
    /\[int\]\$counters\.executed\s+-ne\s+\[int\]\$counters\.total|\[int\]\$counters\.total\s+-ne\s+\[int\]\$counters\.executed/,
    `${label} must require every selected test to execute`,
  );
  assert.match(
    block,
    /\[int\]\$counters\.passed\s+-ne\s+\[int\]\$counters\.total|\[int\]\$counters\.total\s+-ne\s+\[int\]\$counters\.passed/,
    `${label} must require every selected test to pass`,
  );
  assert.match(
    block,
    /\[int\]\$counters\.failed\s+-ne\s+0/,
    `${label} must reject failed tests`,
  );
  assert.match(
    block,
    /\[int\]\$counters\.notExecuted\s+-ne\s+0/,
    `${label} must reject skipped or otherwise unexecuted tests`,
  );
}

function assertClearedInFinally(block, variableNames, label) {
  const finallyIndex = block.lastIndexOf('finally {');
  assert.notEqual(finallyIndex, -1, `${label} must clear fixtures in finally`);
  const cleanup = block.slice(finallyIndex);
  for (const variableName of variableNames) {
    const escapedName = variableName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    assert.match(
      cleanup,
      new RegExp(
        `(?:Remove-Item\\s+Env:${escapedName}|\\$env:${escapedName}\\s*=\\s*\\$null)`,
      ),
      `${label} must clear ${variableName} in finally`,
    );
  }
}

test('workflow gates the Windows build, exact install smoke, cleanup, and upload', async () => {
  const workflow = await readFile(workflowPath, 'utf8');
  const runtimeWorkflow = await readFile(runtimeWorkflowPath, 'utf8');
  const lifecycleBehavior = await readFile(lifecycleBehaviorPath, 'utf8');

  assert.match(workflow, /workflow_dispatch:/);
  assert.match(
    workflow,
    /run_hosted_install_validation:[^]*?type:\s*boolean[^]*?default:\s*false/,
  );
  assert.match(workflow, /runs-on:\s*windows-2025-vs2026/);
  assert.match(workflow, /actions\/setup-dotnet@v4/);
  assert.match(workflow, /dotnet-version:\s*10\.0\.x/);
  assert.match(workflow, /validate-shared-contracts\.mjs/);
  assert.match(
    workflow,
    /pwsh[^\r\n]*internal-msix-lifecycle\.behavior\.test\.ps1/,
    'the portable gate must execute lifecycle behavior against rendered scripts',
  );
  assert.match(workflow, /dotnet restore Windows\/EMKE\.Windows\.slnx --locked-mode/);
  assert.match(workflow, /dotnet build Windows\/EMKE\.Windows\.slnx[^]*--no-restore/);
  assert.match(workflow, /dotnet test Windows\/EMKE\.Windows\.slnx[^]*--no-build/);
  assert.match(workflow, /cmake --preset windows-x64-release/);
  assert.match(workflow, /cmake --build --preset windows-x64-release/);
  assert.match(workflow, /ctest --preset windows-x64-release/);
  assert.match(workflow, /package-msix\.ps1/);
  assert.match(workflow, /verify-msix\.ps1/);
  assert.match(workflow, /test-hosted-msix-install\.ps1/);
  assert.match(workflow, /build-internal-msix-bundle\.ps1/);
  assert.match(
    workflow,
    /id:\s*release[^]*?package_base_name=\$packageBaseName[^]*?artifact_name=emke-translation-windows-\$\(\$release\.ProductVersion\)-\$\(\$release\.Channel\)-\$\(\$release\.Architecture\)-\$\{\{\s*github\.sha\s*\}\}/,
  );
  assert.match(
    workflow,
    /name:\s*\$\{\{\s*steps\.release\.outputs\.artifact_name\s*\}\}/,
    'the MSIX artifact name must be derived from resolved release metadata',
  );
  assert.match(
    workflow,
    /Windows\/tools\/resolve-version\.ps1/,
    'the MSIX workflow must resolve checked-in Windows metadata',
  );
  assert.match(
    runtimeWorkflow,
    /Windows\/tools\/resolve-version\.ps1/,
    'the runtime workflow must resolve checked-in Windows metadata',
  );
  assert.doesNotMatch(
    workflow,
    /EMKE-Translation-Windows-0\.1\.0-internal-x64/,
    'the MSIX workflow must not retain stale package or artifact names',
  );
  assert.match(lifecycleBehavior, /build-internal-msix-bundle\.ps1/);
  assert.match(lifecycleBehavior, /resolve-version\.ps1/);
  assert.doesNotMatch(lifecycleBehavior, /0\.1\.0(?:\.0)?/);
  assert.doesNotMatch(
    workflow,
    /\b26200\b/,
    'the MSIX workflow must not retain an independent Windows 25H2 floor',
  );
  assert.doesNotMatch(
    runtimeWorkflow,
    /\b26200\b/,
    'the runtime workflow must not retain an independent Windows 25H2 floor',
  );

  const buildJob = workflowJob(workflow, 'build-test');
  const crlfWorkflow = workflow
    .replaceAll('\r\n', '\n')
    .replaceAll('\n', '\r\n');
  assert.match(
    workflowJob(crlfWorkflow, 'build-test'),
    /runs-on:\s*windows-2025-vs2026/,
  );
  assert.doesNotMatch(buildJob, /secrets\.|WINDOWS_INTERNAL_SIGNING_PFX/);
  assert.doesNotMatch(
    buildJob,
    /package-msix\.ps1|Add-AppxPackage|test-hosted-msix-install\.ps1/,
  );

  const signingJob = workflowJob(workflow, 'sign-package-bundle');
  assert.match(signingJob, /needs:\s*build-test/);
  assert.match(signingJob, /environment:\s*windows-internal-signing/);
  assert.match(
    signingJob,
    /if:[^\n]*(?:workflow_dispatch)[^\n]*(?:refs\/heads\/main)/,
  );
  assert.doesNotMatch(
    signingJob,
    /Add-AppxPackage|test-hosted-msix-install\.ps1/,
  );

  const signingStep = signingJob.match(
    /- name:\s*Reconstruct, sign, and verify Internal MSIX[^]*?(?=\n\s{6}- name:)/,
  );
  assert.ok(signingStep, 'workflow must have one bounded signing step');
  assert.match(signingStep[0], /WINDOWS_INTERNAL_SIGNING_PFX_BASE64:/);
  assert.match(signingStep[0], /WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD:/);
  assert.equal(
    (workflow.match(/WINDOWS_INTERNAL_SIGNING_PFX_BASE64:/g) ?? []).length,
    1,
    'PFX secret must be scoped only to the signing step',
  );
  assert.equal(
    (workflow.match(/WINDOWS_INTERNAL_SIGNING_PFX_PASSWORD:/g) ?? []).length,
    1,
    'password secret must be scoped only to the signing step',
  );
  assert.match(signingStep[0], /::FromBase64String/);
  assert.match(signingStep[0], /finally\s*\{/);
  assert.match(signingStep[0], /Remove-Item[^]*\$pfxPath/);
  assert.doesNotMatch(
    signingStep[0],
    /GetCertHashString|Thumbprint\.ToUpperInvariant|certificate_thumbprint\s*=/,
    'workflow must consume package-msix verified-PFX output without CER-derived overwrite',
  );

  const bundleStep = signingJob.match(
    /- name:\s*Build exact handoff bundle[^]*?(?=\n\s{6}- name:)/,
  );
  assert.ok(bundleStep, 'workflow must have one exact bundle step');
  assert.match(
    bundleStep[0],
    /-CertificateThumbprint\s+`\s*\n\s*"\$\{\{\s*steps\.package\.outputs\.certificate_thumbprint\s*\}\}"/,
  );

  const installJob = workflowJob(workflow, 'install-hosted-preview');
  assert.match(
    installJob,
    /if:[^\n]*workflow_dispatch[^\n]*run_hosted_install_validation/,
  );
  assert.match(installJob, /runs-on:\s*windows-2025-vs2026/);
  assert.doesNotMatch(installJob, /self-hosted|emke-win11-25h2/);
  assert.doesNotMatch(
    installJob,
    /25H2\s+runner/i,
    'hosted preview validation must not imply a 25H2 runner',
  );
  assert.match(installJob, /actions\/download-artifact@v4/);
  assert.match(installJob, /test-hosted-msix-install\.ps1/);
  assert.match(installJob, /Get-CimInstance\s+-ClassName\s+Win32_OperatingSystem/);
  assert.match(installJob, /ExpectedSmokeStatus/);
  assert.match(
    installJob,
    /needs\.sign-package-bundle\.outputs\.certificate_thumbprint/,
  );
  assert.match(installJob, /if:\s*always\(\)/);

  const bundleStepIndex = signingJob.indexOf('- name: Build exact handoff bundle');
  const uploadStepIndex = signingJob.indexOf(
    '- name: Upload verified Internal bundle',
  );
  assert.ok(bundleStepIndex > 0);
  assert.ok(uploadStepIndex > bundleStepIndex);
  const uploadStep = signingJob.slice(uploadStepIndex);
  assert.match(uploadStep, /actions\/upload-artifact@v4/);
  assert.doesNotMatch(uploadStep, /if:\s*always\(\)/);
  assert.doesNotMatch(
    workflow,
    /(?:install-test-driver|uninstall-test-driver|pnputil|build-driver)\.ps1/i,
  );
});

test('workflow emits isolated complete Task 2R managed, inbox, and signed evidence', async () => {
  const workflow = await readFile(workflowPath, 'utf8');
  const buildJob = workflowJob(workflow, 'build-test');
  const signingJob = workflowJob(workflow, 'sign-package-bundle');
  const ordinaryFilter =
    'TestCategory!=WindowsSetupSignedPayload&TestCategory!=WindowsSetupUnsignedEmkeCatalog';
  const inboxFilter =
    'FullyQualifiedName~WindowsHandleCatalogTrustTests&TestCategory!=WindowsSetupUnsignedEmkeCatalog';
  const signedFilter = 'TestCategory=WindowsSetupSignedPayload';
  const signedVariables = [
    'EMKE_SETUP_SIGNED_MSIX_FIXTURE',
    'EMKE_SETUP_SIGNING_CER_FIXTURE',
  ];

  const solutionBlock = onlyRunBlockContaining(
    buildJob,
    'dotnet test Windows/EMKE.Windows.slnx',
    'ordinary solution regression gate',
  );
  assert.ok(
    solutionBlock.includes(ordinaryFilter),
    'the solution regression run must exclude both environment fixture categories',
  );

  const setupBlock = onlyRunBlockContaining(
    buildJob,
    'task2r-setup-managed.trx',
    'ordinary Setup gate',
  );
  assert.match(
    setupBlock,
    /dotnet test Windows\/tests\/EMKE\.Setup\.Tests\/EMKE\.Setup\.Tests\.csproj/,
  );
  assert.ok(
    setupBlock.includes(`--filter "${ordinaryFilter}"`),
    'ordinary Setup must exclude both strict fixture categories',
  );
  assert.ok(
    setupBlock.includes(
      '--logger "trx;LogFileName=task2r-setup-managed.trx"',
    ),
  );
  assertCompleteTrxEvidence(setupBlock, 'ordinary Setup gate');

  const inboxBlock = onlyRunBlockContaining(
    buildJob,
    'task2r-inbox-catalog.trx',
    'inbox catalog gate',
  );
  assert.match(
    inboxBlock,
    /dotnet test Windows\/tests\/EMKE\.Integration\.Tests\/EMKE\.Integration\.Tests\.csproj/,
  );
  assert.ok(
    inboxBlock.includes(`--filter "${inboxFilter}"`),
    'inbox evidence must exclude the environment-backed unsigned EMKE case',
  );
  assert.ok(
    inboxBlock.includes(
      '--logger "trx;LogFileName=task2r-inbox-catalog.trx"',
    ),
  );
  assertCompleteTrxEvidence(inboxBlock, 'inbox catalog gate');

  for (const variableName of signedVariables) {
    assert.doesNotMatch(
      buildJob,
      new RegExp(variableName),
      `${variableName} must not leak into the ordinary build job`,
    );
  }

  const signedBlock = onlyRunBlockContaining(
    signingJob,
    'task2r-signed-payload.trx',
    'signed payload gate',
  );
  assert.ok(
    signingJob.indexOf('verify-msix.ps1') <
      signingJob.indexOf('task2r-signed-payload.trx'),
    'signed payload evidence must run after exact MSIX verification',
  );
  assert.match(
    signedBlock,
    /\$env:EMKE_SETUP_SIGNED_MSIX_FIXTURE\s*=\s*\$packagePath/,
  );
  assert.match(
    signedBlock,
    /\$env:EMKE_SETUP_SIGNING_CER_FIXTURE\s*=\s*\$certificatePath/,
  );
  assert.ok(
    signedBlock.includes(`--filter "${signedFilter}"`),
    'the signed job must run only the strict signed fixture category',
  );
  assert.ok(
    signedBlock.includes(
      '--logger "trx;LogFileName=task2r-signed-payload.trx"',
    ),
  );
  assertCompleteTrxEvidence(signedBlock, 'signed payload gate');
  assertClearedInFinally(signedBlock, signedVariables, 'signed payload gate');
});

test('every PowerShell workflow block parses after expression substitution', async () => {
  const workflow = await readFile(workflowPath, 'utf8');
  const blocks = workflowRunBlocks(workflow);
  assert.ok(blocks.length >= 8, 'expected all PowerShell workflow blocks');
  for (const [index, block] of blocks.entries()) {
    const substituted = block.replace(/\$\{\{[^}]+\}\}/g, '0123456789');
    const command = `
$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseInput(
  $env:EMKE_WORKFLOW_RUN_BLOCK,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  $errors | ForEach-Object { Write-Error $_.Message }
  exit 1
}
`;
    const result = spawnSync(
      'pwsh',
      ['-NoLogo', '-NoProfile', '-Command', command],
      {
        encoding: 'utf8',
        env: {
          ...process.env,
          EMKE_WORKFLOW_RUN_BLOCK: substituted,
        },
      },
    );
    assert.equal(
      result.status,
      0,
      `workflow PowerShell block ${index + 1} failed parsing:\n${result.stderr}`,
    );
  }
});

test('hosted install validator targets only the exact package and certificate', async () => {
  const [source, workflow] = await Promise.all([
    readFile(hostedInstallPath, 'utf8'),
    readFile(workflowPath, 'utf8'),
  ]);

  assert.match(source, /\[string\]\$PackagePath/);
  assert.match(source, /\[string\]\$CertificatePath/);
  assert.match(source, /\[string\]\$ExpectedCertificateThumbprint/);
  assert.match(source, /Cert:\\LocalMachine\\TrustedPeople/);
  assert.match(source, /function Assert-TrustedPackageSignature/);
  assert.match(source, /Get-AuthenticodeSignature\s+-FilePath\s+\$PackagePath/);
  assert.match(source, /signaturePostTrustStatus=/);
  assert.match(source, /Add-AppxPackage\s+-Path\s+\$resolvedPackagePath/);
  assert.match(source, /Get-AppxPackage\s+-Name\s+\$ExpectedPackageIdentity/);
  assert.match(source, /PackageFullName/);
  assert.match(source, /Remove-AppxPackage\s+`\s*\n\s+-Package/);
  assert.match(source, /finally\s*\{/);
  assert.match(source, /driverMissing/);
  assert.match(source, /networkOpenCount/);
  assert.match(source, /audioStartCount/);
  assert.match(source, /ExpectedSmokeStatus/);
  assert.match(source, /unsupportedWindowsProductType/);
  assert.doesNotMatch(source, /ValidateSet\('0\.1\.0\.0'\)/);
  assert.match(source, /-ne\s+0/);
  assert.doesNotMatch(
    source,
    /install-test-driver|uninstall-test-driver|pnputil|devcon|sc\.exe/i,
  );

  const trustedSignatureCall = source.lastIndexOf('Assert-TrustedPackageSignature');
  assert.ok(
    source.indexOf('$trustedPeopleStore.Add($certificate)') < trustedSignatureCall,
    'the exact certificate must be trusted before Valid Authenticode is required',
  );
  assert.ok(
    trustedSignatureCall <
      source.indexOf('Add-AppxPackage -Path $resolvedPackagePath'),
    'the trusted signature check must precede installation',
  );
  assert.doesNotMatch(
    workflowJob(workflow, 'install-hosted-preview'),
    /signature\.Status\s+-ne\s+\[Management\.Automation\.SignatureStatus\]::Valid/,
    'workflow must not require Valid Authenticode before temporarily trusting the exact certificate',
  );
});

test('hosted smoke requires four explicitly typed fields', () => {
  const exactRecord = validateSmokeRecord(
    JSON.stringify({
      status: 'driverMissing',
      translationStartAllowed: false,
      networkOpenCount: 0,
      audioStartCount: 0,
    }),
  );
  assert.equal(exactRecord.status, 0, exactRecord.stderr);

  const invalidRecords = [
    {
      status: 'driverMissing',
      translationStartAllowed: false,
      audioStartCount: 0,
    },
    {
      status: 'driverMissing',
      translationStartAllowed: false,
      networkOpenCount: 0,
    },
    {
      status: 'driverMissing',
      translationStartAllowed: false,
      networkOpenCount: '0',
      audioStartCount: 0,
    },
    {
      status: 'driverMissing',
      translationStartAllowed: false,
      networkOpenCount: 0,
      audioStartCount: '0',
    },
  ];
  for (const record of invalidRecords) {
    const result = validateSmokeRecord(JSON.stringify(record));
    assert.notEqual(
      result.status,
      0,
      `smoke validator accepted ${JSON.stringify(record)}`,
    );
  }
});

test('hosted smoke accepts the explicit Windows Server compatibility result', () => {
  const serverRecord = validateSmokeRecord(
    JSON.stringify({
      status: 'unsupportedWindowsProductType',
      translationStartAllowed: false,
      networkOpenCount: 0,
      audioStartCount: 0,
    }),
    'unsupportedWindowsProductType',
  );
  assert.equal(serverRecord.status, 0, serverRecord.stderr);
});

test('bundle and hosted install reject reparse ancestors', async (t) => {
  const fixtureRoot = await realpath(
    await mkdtemp(path.join(tmpdir(), 'emke-internal-msix-reparse-')),
  );
  t.after(async () => rm(fixtureRoot, { recursive: true, force: true }));

  const inputRoot = path.join(fixtureRoot, 'input');
  const linkedInputRoot = path.join(fixtureRoot, 'linked-input');
  await createBundleInputs(inputRoot);
  await symlink(
    inputRoot,
    linkedInputRoot,
    process.platform === 'win32' ? 'junction' : 'dir',
  );

  const linkedInputResult = runBundleBuilder({
    inputRoot: linkedInputRoot,
    outputRoot: path.join(fixtureRoot, 'linked-input-output'),
    allowedOutputRoot: fixtureRoot,
  });
  assert.notEqual(
    linkedInputResult.status,
    0,
    'bundle builder accepted an input through a reparse ancestor',
  );

  const realOutputParent = path.join(fixtureRoot, 'real-output-parent');
  const linkedOutputParent = path.join(fixtureRoot, 'linked-output-parent');
  await mkdir(realOutputParent);
  await symlink(
    realOutputParent,
    linkedOutputParent,
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const linkedOutputResult = runBundleBuilder({
    inputRoot,
    outputRoot: path.join(linkedOutputParent, 'bundle'),
    allowedOutputRoot: fixtureRoot,
  });
  assert.notEqual(
    linkedOutputResult.status,
    0,
    'bundle builder accepted an output through a reparse ancestor',
  );

  const hostedResult = resolveHostedInputPath(
    path.join(linkedInputRoot, handoffNames[0]),
    '.msix',
  );
  assert.notEqual(
    hostedResult.status,
    0,
    'hosted install validator accepted an input through a reparse ancestor',
  );
});

test('bundle builder rejects output outside its controlled root', async (t) => {
  const fixtureRoot = await realpath(
    await mkdtemp(path.join(tmpdir(), 'emke-internal-msix-root-')),
  );
  const outsideRoot = await realpath(
    await mkdtemp(path.join(tmpdir(), 'emke-internal-msix-outside-')),
  );
  t.after(async () => {
    await rm(fixtureRoot, { recursive: true, force: true });
    await rm(outsideRoot, { recursive: true, force: true });
  });

  const inputRoot = path.join(fixtureRoot, 'input');
  await createBundleInputs(inputRoot);

  const result = runBundleBuilder({
    inputRoot,
    outputRoot: path.join(outsideRoot, 'bundle'),
    allowedOutputRoot: fixtureRoot,
  });
  assert.notEqual(
    result.status,
    0,
    'bundle builder wrote outside its controlled output root',
  );
  assert.match(
    result.stderr,
    /inside the allowed output root/,
    `unexpected rejection reason: ${result.stderr}`,
  );
});

test('bundle builder emits an exact five-file ZIP plus hashes and provenance', async (t) => {
  const fixtureRoot = await realpath(
    await mkdtemp(path.join(tmpdir(), 'emke-internal-msix-bundle-')),
  );
  t.after(async () => rm(fixtureRoot, { recursive: true, force: true }));

  const inputRoot = path.join(fixtureRoot, 'input');
  const outputRoot = path.join(fixtureRoot, 'output');
  const fixtureNames = handoffNames.slice(0, 4);
  await createBundleInputs(inputRoot);

  const result = runBundleBuilder({
    inputRoot,
    outputRoot,
    allowedOutputRoot: fixtureRoot,
  });
  assert.equal(result.status, 0, result.stderr);

  const outputNames = (await readdir(outputRoot)).sort();
  assert.deepEqual(outputNames, [
    ...handoffNames,
    `${packageBaseName}.provenance.json`,
    `${packageBaseName}.zip`,
  ].sort());

  const extractionRoot = path.join(fixtureRoot, 'expanded');
  const expand = spawnSync(
    'pwsh',
    [
      '-NoLogo',
      '-NoProfile',
      '-Command',
      'Expand-Archive -LiteralPath $env:EMKE_ZIP -DestinationPath $env:EMKE_EXPANDED',
    ],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        EMKE_ZIP: path.join(outputRoot, `${packageBaseName}.zip`),
        EMKE_EXPANDED: extractionRoot,
      },
    },
  );
  assert.equal(expand.status, 0, expand.stderr);
  assert.deepEqual((await readdir(extractionRoot)).sort(), [...handoffNames].sort());

  for (const lifecycleName of handoffNames.slice(2, 4)) {
    const rendered = await readFile(path.join(outputRoot, lifecycleName), 'utf8');
    const expanded = await readFile(path.join(extractionRoot, lifecycleName), 'utf8');
    assert.equal(expanded, rendered, `${lifecycleName} ZIP bytes differ`);
    assert.match(rendered, new RegExp(`ExpectedVersion = "${versionMetadata.packageVersion.replaceAll('.', '\\.')}"`));
    assert.match(rendered, new RegExp(`ExpectedArchitecture = "${versionMetadata.architecture}"`));
    assert.match(rendered, new RegExp(`${packageBaseName.replaceAll('.', '\\.')}\\.msix`));
    assert.match(rendered, new RegExp(`${packageBaseName.replaceAll('.', '\\.')}\\.cer`));
    assert.doesNotMatch(rendered, /__EMKE_[A-Z0-9_]+__/);
    assert.doesNotMatch(rendered, /0\.1\.0(?:\.0)?/);
  }

  const sums = await readFile(
    path.join(outputRoot, 'SHA256SUMS.txt'),
    'utf8',
  );
  for (const name of fixtureNames) {
    const escapedName = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    assert.match(
      sums,
      new RegExp(`^[0-9A-F]{64}  ${escapedName}$`, 'm'),
    );
  }
  assert.doesNotMatch(sums, /SHA256SUMS\.txt|\.zip|provenance/i);

  const provenance = JSON.parse(
    await readFile(
      path.join(outputRoot, `${packageBaseName}.provenance.json`),
      'utf8',
    ),
  );
  assert.equal(
    provenance.sourceCommit,
    '0123456789abcdef0123456789abcdef01234567',
  );
  assert.equal(provenance.workflowRunId, '123456789');
  assert.equal(provenance.packageIdentity, 'EMKE.Translation.Internal');
  assert.equal(
    provenance.certificateThumbprint,
    '0123456789ABCDEF0123456789ABCDEF01234567',
  );
  assert.deepEqual(
    provenance.handoffFiles.map(({ name }) => name).sort(),
    [...handoffNames].sort(),
  );
  assert.equal(provenance.zip.name, `${packageBaseName}.zip`);
  assert.match(provenance.zip.sha256, /^[0-9A-F]{64}$/);
  assert.ok(provenance.zip.size > 0);
  for (const lifecycleName of handoffNames.slice(2, 4)) {
    const bytes = await readFile(path.join(outputRoot, lifecycleName));
    const expectedHash = createHash('sha256').update(bytes).digest('hex').toUpperCase();
    const evidence = provenance.handoffFiles.find(({ name }) => name === lifecycleName);
    assert.equal(evidence.sha256, expectedHash);
    assert.match(sums, new RegExp(`^${expectedHash}  ${lifecycleName}$`, 'm'));
  }
});

for (const [label, mutate] of [
  ['missing placeholder', (source) => source.replace('__EMKE_PACKAGE_VERSION__', 'missing')],
  ['duplicate placeholder', (source) => source.replace('__EMKE_PACKAGE_VERSION__', '__EMKE_PACKAGE_VERSION____EMKE_PACKAGE_VERSION__')],
  ['unexpected placeholder', (source) => `${source}\n__EMKE_UNEXPECTED__\n`],
]) {
  test(`bundle builder rejects lifecycle template with ${label}`, async (t) => {
    const fixtureRoot = await realpath(
      await mkdtemp(path.join(tmpdir(), 'emke-lifecycle-template-tamper-')),
    );
    t.after(async () => rm(fixtureRoot, { recursive: true, force: true }));
    const inputRoot = path.join(fixtureRoot, 'input');
    await createBundleInputs(inputRoot);
    const installFixture = path.join(inputRoot, handoffNames[2]);
    await writeFile(installFixture, mutate(await readFile(installFixture, 'utf8')));
    const result = runBundleBuilder({
      inputRoot,
      outputRoot: path.join(fixtureRoot, 'output'),
      allowedOutputRoot: fixtureRoot,
    });
    assert.notEqual(result.status, 0, `${label} was accepted`);
    assert.match(`${result.stdout}\n${result.stderr}`, /lifecycle template/i);
  });
}
