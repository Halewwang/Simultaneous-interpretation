import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  mkdtemp,
  mkdir,
  readFile,
  rm,
  writeFile,
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('../../../', import.meta.url));
const packageScript = path.join(
  repositoryRoot,
  'Windows',
  'tools',
  'package-setup.ps1',
);
const verifyScript = path.join(
  repositoryRoot,
  'Windows',
  'tools',
  'verify-setup.ps1',
);
const nodePath = process.execPath;

const payloadNames = [
  'EMKE-Translation-Windows-0.2.0-internal-x64.msix',
  'EMKE-Translation-Windows-0.2.0-internal-x64.cer',
  'EMKE.VirtualAudio.inf',
  'EMKE.VirtualAudio.sys',
  'EMKE.VirtualAudio.cat',
];

function run(command, args, options = {}) {
  return spawnSync(command, args, {
    cwd: repositoryRoot,
    encoding: 'utf8',
    ...options,
  });
}

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

async function createInventoryFixture() {
  const root = await mkdtemp(path.join(os.tmpdir(), 'emke-setup-contract-'));
  const payloadRoot = path.join(root, 'payloads');
  await mkdir(payloadRoot);
  const expected = new Map();
  for (const [index, fileName] of payloadNames.entries()) {
    const bytes = Buffer.alloc(index + 17, index + 1);
    await writeFile(path.join(payloadRoot, fileName), bytes);
    expected.set(fileName, {
      length: bytes.length,
      sha256: sha256(bytes),
    });
  }
  return {
    root,
    payloadRoot,
    inventoryPath: path.join(root, 'setup-payload-inventory.json'),
    expected,
  };
}

test('Setup packaging artifacts are ignored by Git', () => {
  const result = run('git', [
    'check-ignore',
    '--quiet',
    'Windows/artifacts/setup-contract-probe.bin',
  ]);
  assert.equal(
    result.status,
    0,
    'Windows/artifacts must be ignored before Setup packaging can run.',
  );
});

test('packager exposes only exact artifact, provenance, and signing inputs', () => {
  const command = [
    '$command = Get-Command -Name $env:EMKE_SETUP_PACKAGER',
    '$common = [System.Management.Automation.PSCmdlet]::CommonParameters',
    '$parameters = @($command.Parameters.Keys |',
    '  Where-Object { $_ -notin $common } | Sort-Object)',
    '$parameters | ConvertTo-Json -Compress',
  ].join('\n');
  const result = run('pwsh', ['-NoLogo', '-NoProfile', '-Command', command], {
    env: {
      ...process.env,
      EMKE_SETUP_PACKAGER: packageScript,
    },
  });

  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), [
    'CandidateRoot',
    'CertificatePath',
    'CreateInventoryOnly',
    'DriverDirectory',
    'DriverSignerSubject',
    'DriverSourceCommit',
    'DriverWorkflowRun',
    'InventoryPath',
    'MsixPath',
    'MsixSignerSubject',
    'MsixSourceCommit',
    'MsixWorkflowRun',
    'PasswordEnvironmentVariable',
    'PayloadRoot',
    'PfxPath',
    'SetupSignerSubject',
    'SetupSourceCommit',
    'SetupWorkflowRun',
  ]);
});

test('inventory generation and verification execute against exact payload bytes', async () => {
  const fixture = await createInventoryFixture();
  try {
    const packageResult = run('pwsh', [
      '-NoLogo',
      '-NoProfile',
      '-File',
      packageScript,
      '-CreateInventoryOnly',
      '-PayloadRoot',
      fixture.payloadRoot,
      '-InventoryPath',
      fixture.inventoryPath,
      '-SetupSourceCommit',
      'be5ce00cfd4d10b3fbcf29d21c2f5d65013a0062',
      '-SetupWorkflowRun',
      '30890000001',
      '-SetupSignerSubject',
      'CN=EMKE Internal Test',
      '-MsixSourceCommit',
      '44c7f8770f11e211301301338135e9ca2c6f9c20',
      '-MsixWorkflowRun',
      '30800829927',
      '-MsixSignerSubject',
      'CN=EMKE Internal Test',
      '-DriverSourceCommit',
      '1111111111111111111111111111111111111111',
      '-DriverWorkflowRun',
      '30880000001',
      '-DriverSignerSubject',
      'CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation',
    ]);
    assert.equal(packageResult.status, 0, packageResult.stderr);

    const inventory = JSON.parse(await readFile(fixture.inventoryPath, 'utf8'));
    assert.equal(inventory.schemaVersion, 1);
    assert.equal(inventory.productVersion, '0.2.0');
    assert.equal(inventory.packageVersion, '0.2.0.0');
    assert.equal(inventory.channel, 'internal');
    assert.equal(inventory.architecture, 'x64');
    assert.deepEqual(
      inventory.payloads.map((payload) => payload.fileName),
      payloadNames,
    );
    for (const payload of inventory.payloads) {
      const expected = fixture.expected.get(payload.fileName);
      assert.equal(payload.length, expected.length);
      assert.equal(payload.sha256, expected.sha256);
      assert.match(payload.sourceCommit, /^[0-9a-f]{40}$/);
      assert.match(payload.workflowRun, /^[1-9][0-9]+$/);
      assert.ok(payload.signerSubject.length > 0);
    }

    const verifyResult = run('pwsh', [
      '-NoLogo',
      '-NoProfile',
      '-File',
      verifyScript,
      '-InventoryRoot',
      fixture.payloadRoot,
      '-InventoryManifestPath',
      fixture.inventoryPath,
    ]);
    assert.equal(verifyResult.status, 0, verifyResult.stderr);
    assert.match(verifyResult.stdout, /Setup inventory verified\./);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test('contract test itself runs on the repository Node 24 major', () => {
  const result = run(nodePath, ['--version']);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout.trim(), /^v24\./);
});

test('package mode rejects untrusted payloads before publishing a candidate', async () => {
  const fixture = await createInventoryFixture();
  const candidateRoot = path.join(fixture.root, 'candidate');
  try {
    const result = run('pwsh', [
      '-NoLogo',
      '-NoProfile',
      '-File',
      packageScript,
      '-MsixPath',
      path.join(fixture.payloadRoot, payloadNames[0]),
      '-CertificatePath',
      path.join(fixture.payloadRoot, payloadNames[1]),
      '-DriverDirectory',
      fixture.payloadRoot,
      '-CandidateRoot',
      candidateRoot,
      '-SetupSourceCommit',
      'be5ce00cfd4d10b3fbcf29d21c2f5d65013a0062',
      '-SetupWorkflowRun',
      '30890000001',
      '-SetupSignerSubject',
      'CN=EMKE Internal Test',
      '-MsixSourceCommit',
      '44c7f8770f11e211301301338135e9ca2c6f9c20',
      '-MsixWorkflowRun',
      '30800829927',
      '-MsixSignerSubject',
      'CN=EMKE Internal Test',
      '-DriverSourceCommit',
      '1111111111111111111111111111111111111111',
      '-DriverWorkflowRun',
      '30880000001',
      '-DriverSignerSubject',
      'CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation',
    ]);
    assert.notEqual(result.status, 0);
    assert.match(`${result.stdout}\n${result.stderr}`, /MSIX verification failed/);
    const probe = run('pwsh', [
      '-NoLogo',
      '-NoProfile',
      '-Command',
      `if (Test-Path -LiteralPath '${candidateRoot.replaceAll("'", "''")}') { exit 1 }`,
    ]);
    assert.equal(probe.status, 0, 'a rejected package must publish no candidate tree');
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test('independent verifier rejects an unsigned exact-name executable', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'emke-setup-verifier-'));
  const setupPath = path.join(
    root,
    'EMKE-Translation-Setup-0.2.0-internal-x64.exe',
  );
  try {
    await writeFile(setupPath, Buffer.from('not-a-signed-setup'));
    const result = run('pwsh', [
      '-NoLogo',
      '-NoProfile',
      '-File',
      verifyScript,
      '-SetupPath',
      setupPath,
    ]);
    assert.notEqual(result.status, 0);
    assert.match(`${result.stdout}\n${result.stderr}`, /Authenticode signature is invalid/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('packager declares the exact five-file candidate handoff', async () => {
  const source = await readFile(packageScript, 'utf8');
  for (const fileName of [
    'EMKE-Translation-Setup-0.2.0-internal-x64.exe',
    'SHA256SUMS.txt',
    'setup-provenance.json',
    'EMKE-Translation-Setup-Recovery-0.2.0-internal-x64.exe',
    'EMKE-Translation-Setup-Engineering-0.2.0-internal-x64.zip',
  ]) {
    assert.match(source, new RegExp(fileName.replaceAll('.', '\\.')));
  }
  assert.doesNotMatch(source, /testsigning|nointegritychecks|Add-AppxPackage/i);
});

test('Setup workflow and evidence ledger are checked in', async () => {
  const workflow = await readFile(
    path.join(repositoryRoot, '.github', 'workflows', 'windows-setup.yml'),
    'utf8',
  );
  const evidence = await readFile(
    path.join(repositoryRoot, 'docs', 'quality', 'windows-setup-evidence.md'),
    'utf8',
  );
  assert.match(workflow, /windows-setup-signing/);
  assert.match(workflow, /package-setup\.ps1/);
  assert.match(workflow, /verify-setup\.ps1/);
  assert.match(evidence, /Evidence level A/);
  assert.match(evidence, /Evidence level E/);
  assert.match(evidence, /PENDING/);
});

test('self-contained single-file publish verifies its embedded inventory', async () => {
  const fixture = await createInventoryFixture();
  const publishRoot = path.join(fixture.root, 'publish');
  try {
    const inventoryResult = run('pwsh', [
      '-NoLogo',
      '-NoProfile',
      '-File',
      packageScript,
      '-CreateInventoryOnly',
      '-PayloadRoot',
      fixture.payloadRoot,
      '-InventoryPath',
      fixture.inventoryPath,
      '-SetupSourceCommit',
      'be5ce00cfd4d10b3fbcf29d21c2f5d65013a0062',
      '-SetupWorkflowRun',
      '30890000001',
      '-SetupSignerSubject',
      'CN=EMKE Internal Test',
      '-MsixSourceCommit',
      '44c7f8770f11e211301301338135e9ca2c6f9c20',
      '-MsixWorkflowRun',
      '30800829927',
      '-MsixSignerSubject',
      'CN=EMKE Internal Test',
      '-DriverSourceCommit',
      '1111111111111111111111111111111111111111',
      '-DriverWorkflowRun',
      '30880000001',
      '-DriverSignerSubject',
      'CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation',
    ]);
    assert.equal(inventoryResult.status, 0, inventoryResult.stderr);
    const project = path.join(
      repositoryRoot,
      'Windows',
      'src',
      'EMKE.Setup',
      'EMKE.Setup.csproj',
    );
    const restore = run('dotnet', [
      'restore',
      project,
      '--locked-mode',
      '--runtime',
      'win-x64',
    ]);
    assert.equal(restore.status, 0, `${restore.stdout}\n${restore.stderr}`);
    const publish = run('dotnet', [
      'publish',
      project,
      '--configuration',
      'Release',
      '--runtime',
      'win-x64',
      '--self-contained',
      'true',
      '--no-restore',
      '--output',
      publishRoot,
      '-p:PublishSingleFile=true',
      '-p:EnableCompressionInSingleFile=true',
      '-p:IncludeNativeLibrariesForSelfExtract=true',
      '-p:DebugType=None',
      '-p:DebugSymbols=false',
      `-p:SetupPayloadRoot=${fixture.payloadRoot}`,
      `-p:SetupInventoryPath=${fixture.inventoryPath}`,
    ]);
    assert.equal(publish.status, 0, `${publish.stdout}\n${publish.stderr}`);
    const selfCheck = run(path.join(publishRoot, 'EMKE.Setup.exe'), [
      '--verify-self-v1',
    ]);
    assert.equal(selfCheck.status, 0, selfCheck.stderr);
    const evidence = JSON.parse(selfCheck.stdout);
    assert.equal(evidence.status, 'verified');
    assert.equal(evidence.payloadCount, 5);
    assert.match(evidence.inventorySha256, /^[0-9a-f]{64}$/);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});
