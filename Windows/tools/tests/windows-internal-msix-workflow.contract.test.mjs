import assert from 'node:assert/strict';
import {
  mkdtemp,
  mkdir,
  readFile,
  readdir,
  rm,
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

const packageBaseName = 'EMKE-Translation-Windows-0.1.0-internal-x64';
const handoffNames = [
  `${packageBaseName}.msix`,
  `${packageBaseName}.cer`,
  'Install-EMKE-Translation-Internal.ps1',
  'Uninstall-EMKE-Translation-Internal.ps1',
  'SHA256SUMS.txt',
];

test('workflow gates the Windows build, exact install smoke, cleanup, and upload', async () => {
  const workflow = await readFile(workflowPath, 'utf8');

  assert.match(workflow, /workflow_dispatch:/);
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
    /name:\s*emke-translation-windows-0\.1\.0-internal-x64-\$\{\{\s*github\.sha\s*\}\}/,
  );

  const signingStep = workflow.match(
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

  const installStepIndex = workflow.indexOf(
    '- name: Install, query, smoke, and remove exact Internal MSIX',
  );
  const bundleStepIndex = workflow.indexOf('- name: Build exact handoff bundle');
  const uploadStepIndex = workflow.indexOf('- name: Upload verified Internal bundle');
  assert.ok(installStepIndex > 0);
  assert.ok(bundleStepIndex > installStepIndex);
  assert.ok(uploadStepIndex > bundleStepIndex);
  const uploadStep = workflow.slice(uploadStepIndex);
  assert.match(uploadStep, /actions\/upload-artifact@v4/);
  assert.doesNotMatch(uploadStep, /if:\s*always\(\)/);
  assert.doesNotMatch(
    workflow,
    /(?:install-test-driver|uninstall-test-driver|pnputil|build-driver)\.ps1/i,
  );
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

test('bundle builder emits an exact five-file ZIP plus hashes and provenance', async (t) => {
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-internal-msix-bundle-'),
  );
  t.after(async () => rm(fixtureRoot, { recursive: true, force: true }));

  const inputRoot = path.join(fixtureRoot, 'input');
  const outputRoot = path.join(fixtureRoot, 'output');
  await mkdir(inputRoot, { recursive: true });

  const fixtureNames = handoffNames.slice(0, 4);
  for (const [index, name] of fixtureNames.entries()) {
    await writeFile(path.join(inputRoot, name), `fixture-${index}\n`);
  }

  const result = spawnSync(
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
