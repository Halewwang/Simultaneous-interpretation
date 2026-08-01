import assert from 'node:assert/strict';
import {
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

const packageBaseName = 'EMKE-Translation-Windows-0.2.0-internal-x64';
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

function validateSmokeRecord(json) {
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
Assert-DriverMissingSmokeRecord -Json $env:EMKE_SMOKE_JSON
`;
  return spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-Command', command], {
    encoding: 'utf8',
    env: {
      ...process.env,
      EMKE_HOSTED_INSTALL_SCRIPT: hostedInstallPath,
      EMKE_SMOKE_JSON: json,
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

test('workflow gates the Windows build, exact install smoke, cleanup, and upload', async () => {
  const workflow = await readFile(workflowPath, 'utf8');
  const runtimeWorkflow = await readFile(runtimeWorkflowPath, 'utf8');

  assert.match(workflow, /workflow_dispatch:/);
  assert.match(
    workflow,
    /run_25h2_install_validation:[^]*?type:\s*boolean[^]*?default:\s*false/,
  );
  assert.match(workflow, /runs-on:\s*windows-2025-vs2026/);
  assert.match(workflow, /actions\/setup-dotnet@v4/);
  assert.match(workflow, /dotnet-version:\s*10\.0\.x/);
  assert.match(workflow, /validate-shared-contracts\.mjs/);
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

  const installJob = workflowJob(workflow, 'install-25h2');
  assert.match(
    installJob,
    /if:[^\n]*workflow_dispatch[^\n]*run_25h2_install_validation/,
  );
  assert.match(
    installJob,
    /runs-on:\s*\[\s*self-hosted,\s*Windows,\s*X64,\s*emke-win11-25h2\s*\]/,
  );
  assert.match(installJob, /actions\/download-artifact@v4/);
  assert.match(installJob, /test-hosted-msix-install\.ps1/);
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
  const source = await readFile(hostedInstallPath, 'utf8');

  assert.match(source, /\[string\]\$PackagePath/);
  assert.match(source, /\[string\]\$CertificatePath/);
  assert.match(source, /\[string\]\$ExpectedCertificateThumbprint/);
  assert.match(source, /Cert:\\LocalMachine\\TrustedPeople/);
  assert.match(source, /Add-AppxPackage\s+-Path\s+\$resolvedPackagePath/);
  assert.match(source, /Get-AppxPackage\s+-Name\s+\$ExpectedPackageIdentity/);
  assert.match(source, /PackageFullName/);
  assert.match(source, /Remove-AppxPackage\s+`\s*\n\s+-Package/);
  assert.match(source, /finally\s*\{/);
  assert.match(source, /driverMissing/);
  assert.match(source, /networkOpenCount/);
  assert.match(source, /audioStartCount/);
  assert.match(source, /-ne\s+0/);
  assert.doesNotMatch(
    source,
    /install-test-driver|uninstall-test-driver|pnputil|devcon|sc\.exe/i,
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

test('bundle and hosted install reject reparse ancestors', async (t) => {
  const fixtureRoot = await realpath(
    await mkdtemp(path.join(tmpdir(), 'emke-internal-msix-reparse-')),
  );
  t.after(async () => rm(fixtureRoot, { recursive: true, force: true }));

  const inputRoot = path.join(fixtureRoot, 'input');
  const linkedInputRoot = path.join(fixtureRoot, 'linked-input');
  await mkdir(inputRoot);
  for (const [index, name] of handoffNames.slice(0, 4).entries()) {
    await writeFile(path.join(inputRoot, name), `fixture-${index}\n`);
  }
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
  await mkdir(inputRoot);
  for (const [index, name] of handoffNames.slice(0, 4).entries()) {
    await writeFile(path.join(inputRoot, name), `fixture-${index}\n`);
  }

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
  await mkdir(inputRoot, { recursive: true });

  const fixtureNames = handoffNames.slice(0, 4);
  for (const [index, name] of fixtureNames.entries()) {
    await writeFile(path.join(inputRoot, name), `fixture-${index}\n`);
  }

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
});
