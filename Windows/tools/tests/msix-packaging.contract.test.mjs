import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  realpath,
  rm,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('../../../', import.meta.url));
const windowsRoot = path.join(repositoryRoot, 'Windows');
const manifestPath = path.join(
  windowsRoot,
  'packaging',
  'App',
  'AppxManifest.internal.xml',
);
const packageScriptPath = path.join(windowsRoot, 'tools', 'package-msix.ps1');
const verifyScriptPath = path.join(windowsRoot, 'tools', 'verify-msix.ps1');
const nativeCmakePath = path.join(windowsRoot, 'native', 'CMakeLists.txt');
const nativeWorkflowPath = path.join(
  repositoryRoot,
  '.github',
  'workflows',
  'windows-audio.yml',
);
const approvedIconPath = path.join(
  repositoryRoot,
  'Packaging',
  'Assets',
  'EMKE-AppIcon-Approved.png',
);
const packagedApprovedIconPath = path.join(
  windowsRoot,
  'packaging',
  'App',
  'Assets',
  'EMKE-AppIcon-Approved.png',
);

function parsePowerShell(scriptPath) {
  const command = `
$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_SCRIPT_PATH,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  $errors | ForEach-Object { Write-Error $_.Message }
  exit 1
}
`;
  return spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: { ...process.env, EMKE_SCRIPT_PATH: scriptPath },
    },
  );
}

function validateStaging(stagingDirectory) {
  return spawnSync(
    'pwsh',
    [
      '-NoLogo',
      '-NoProfile',
      '-File',
      packageScriptPath,
      '-ValidateStagingOnly',
      '-StagingDirectory',
      stagingDirectory,
    ],
    { encoding: 'utf8' },
  );
}

function validateExtracted(extractedDirectory) {
  return spawnSync(
    'pwsh',
    [
      '-NoLogo',
      '-NoProfile',
      '-File',
      verifyScriptPath,
      '-ValidateExtractedOnly',
      '-ExtractedDirectory',
      extractedDirectory,
    ],
    { encoding: 'utf8' },
  );
}

function createPeFixture(machine = 0x8664) {
  const bytes = Buffer.alloc(512);
  bytes.writeUInt16LE(0x5a4d, 0);
  bytes.writeUInt32LE(0x80, 0x3c);
  bytes.writeUInt32LE(0x00004550, 0x80);
  bytes.writeUInt16LE(machine, 0x84);
  return bytes;
}

async function createAllowedStage(root) {
  const assets = path.join(root, 'Assets');
  const zh = path.join(root, 'zh-CN');
  await mkdir(assets, { recursive: true });
  await mkdir(zh, { recursive: true });
  await Promise.all([
    writeFile(path.join(root, 'AppxManifest.xml'), '<Package />'),
    writeFile(path.join(root, 'EMKE.Windows.App.exe'), 'fixture executable'),
    writeFile(path.join(root, 'EMKE.Windows.App.dll'), 'fixture assembly'),
    writeFile(path.join(root, 'EMKE.NativeAudio.dll'), 'fixture native DLL'),
    writeFile(path.join(root, 'System.Private.CoreLib.dll'), 'fixture runtime'),
    writeFile(
      path.join(root, 'EMKE.Windows.App.deps.json'),
      '{"runtimeTarget":{}}',
    ),
    writeFile(
      path.join(root, 'EMKE.Windows.App.runtimeconfig.json'),
      '{"runtimeOptions":{}}',
    ),
    writeFile(
      path.join(root, 'compatibility.json'),
      '{"channel":"internal"}',
    ),
    writeFile(path.join(assets, 'Square44x44Logo.png'), 'fixture PNG'),
    writeFile(
      path.join(zh, 'EMKE.Windows.App.resources.dll'),
      'fixture resources',
    ),
  ]);
}

async function requirePackageScript() {
  await readFile(packageScriptPath, 'utf8');
}

async function requireVerifyScript() {
  await readFile(verifyScriptPath, 'utf8');
}

async function createValidExtractedPackage(root) {
  await mkdir(path.join(root, 'Assets'), { recursive: true });
  await Promise.all([
    copyFile(manifestPath, path.join(root, 'AppxManifest.xml')),
    copyFile(
      path.join(windowsRoot, 'packaging', 'compatibility.internal.json'),
      path.join(root, 'compatibility.json'),
    ),
    writeFile(
      path.join(root, 'EMKE.Windows.App.exe'),
      createPeFixture(),
    ),
    writeFile(
      path.join(root, 'EMKE.NativeAudio.dll'),
      createPeFixture(),
    ),
    copyFile(
      path.join(
        windowsRoot,
        'packaging',
        'App',
        'Assets',
        'Square44x44Logo.png',
      ),
      path.join(root, 'Assets', 'Square44x44Logo.png'),
    ),
  ]);
}

function writeSignerProvenance({
  packagePath,
  certificatePath,
  provenancePath,
  githubOutputPath,
  thumbprint,
}) {
  const command = `
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_PACKAGE_SCRIPT,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  throw 'Package script did not parse.'
}
$function = $ast.Find(
  {
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -ceq 'Write-SignerProvenance'
  },
  $true
)
if ($null -eq $function) {
  throw 'Signer provenance function is unavailable.'
}
Invoke-Expression $function.Extent.Text
Write-SignerProvenance -PackagePath $env:EMKE_FIXTURE_PACKAGE -CertificatePath $env:EMKE_FIXTURE_CERTIFICATE -OutputPath $env:EMKE_FIXTURE_PROVENANCE -Subject 'CN=EMKE Internal Test' -Thumbprint $env:EMKE_FIXTURE_THUMBPRINT
`;
  return spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        EMKE_PACKAGE_SCRIPT: packageScriptPath,
        EMKE_FIXTURE_PACKAGE: packagePath,
        EMKE_FIXTURE_CERTIFICATE: certificatePath,
        EMKE_FIXTURE_PROVENANCE: provenancePath,
        EMKE_FIXTURE_THUMBPRINT: thumbprint,
        GITHUB_OUTPUT: githubOutputPath,
      },
    },
  );
}

function validateSignerProvenance({
  packagePath,
  certificatePath,
  provenancePath,
}) {
  const command = `
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_VERIFY_SCRIPT,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  throw 'Verify script did not parse.'
}
$function = $ast.Find(
  {
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -ceq 'Assert-SignerProvenance'
  },
  $true
)
if ($null -eq $function) {
  throw 'Signer provenance validator is unavailable.'
}
Invoke-Expression $function.Extent.Text
Assert-SignerProvenance -PackagePath $env:EMKE_FIXTURE_PACKAGE -CertificatePath $env:EMKE_FIXTURE_CERTIFICATE -ProvenancePath $env:EMKE_FIXTURE_PROVENANCE
`;
  return spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        EMKE_VERIFY_SCRIPT: verifyScriptPath,
        EMKE_FIXTURE_PACKAGE: packagePath,
        EMKE_FIXTURE_CERTIFICATE: certificatePath,
        EMKE_FIXTURE_PROVENANCE: provenancePath,
      },
    },
  );
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex').toUpperCase();
}

function resolveEphemeralPfx({
  pfxPath,
  temporaryRoot,
  repositoryRoot,
}) {
  const command = `
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_PACKAGE_SCRIPT,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  throw 'Package script did not parse.'
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
foreach ($name in @('Assert-NoReparsePathChain', 'Resolve-EphemeralPfxInput')) {
  $function = $functions | Where-Object { $_.Name -ceq $name }
  if ($null -eq $function) {
    throw "Required PFX function is unavailable: $name"
  }
  Invoke-Expression $function.Extent.Text
}
Resolve-EphemeralPfxInput -PfxPath $env:EMKE_PFX_PATH -TemporaryRoot $env:EMKE_TEMPORARY_ROOT -RepositoryRoot $env:EMKE_REPOSITORY_ROOT
`;
  return spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        EMKE_PACKAGE_SCRIPT: packageScriptPath,
        EMKE_PFX_PATH: pfxPath,
        EMKE_TEMPORARY_ROOT: temporaryRoot,
        EMKE_REPOSITORY_ROOT: repositoryRoot,
      },
    },
  );
}

function selectNewCertificateThumbprints() {
  const command = `
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_PACKAGE_SCRIPT,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  throw 'Package script did not parse.'
}
$function = $ast.Find(
  {
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -ceq 'Select-NewCertificateThumbprints'
  },
  $true
)
if ($null -eq $function) {
  throw 'Temporary certificate selector is unavailable.'
}
Invoke-Expression $function.Extent.Text
$selected = @(
  Select-NewCertificateThumbprints -PreexistingThumbprints @(
    'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
  ) -ImportedThumbprints @(
    'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
    'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB',
    'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'
  )
)
ConvertTo-Json -InputObject $selected -Compress
`;
  return spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        EMKE_PACKAGE_SCRIPT: packageScriptPath,
      },
    },
  );
}

function runCleanupFailureProbe(scriptPath, markerPath) {
  const command = `
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_CLEANUP_SCRIPT,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  throw 'Cleanup script did not parse.'
}
$function = $ast.Find(
  {
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -ceq 'Invoke-CompleteCleanup'
  },
  $true
)
if ($null -eq $function) {
  throw 'Complete cleanup function is unavailable.'
}
Invoke-Expression $function.Extent.Text
Invoke-CompleteCleanup -Actions @(
  [pscustomobject]@{
    Name = 'first'
    Action = {
      Add-Content -LiteralPath $env:EMKE_CLEANUP_MARKER -Value 'first'
      throw 'injected first cleanup failure'
    }
  },
  [pscustomobject]@{
    Name = 'second'
    Action = {
      Add-Content -LiteralPath $env:EMKE_CLEANUP_MARKER -Value 'second'
    }
  }
)
`;
  return spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        EMKE_CLEANUP_SCRIPT: scriptPath,
        EMKE_CLEANUP_MARKER: markerPath,
      },
    },
  );
}

function runEmptyCleanupProbe(scriptPath) {
  const command = `
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
  $env:EMKE_CLEANUP_SCRIPT,
  [ref]$tokens,
  [ref]$errors
)
if ($errors.Count -ne 0) {
  throw 'Cleanup script did not parse.'
}
$function = $ast.Find(
  {
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -ceq 'Invoke-CompleteCleanup'
  },
  $true
)
if ($null -eq $function) {
  throw 'Complete cleanup function is unavailable.'
}
Invoke-Expression $function.Extent.Text
Invoke-CompleteCleanup -Actions @()
`;
  return spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: {
        ...process.env,
        EMKE_CLEANUP_SCRIPT: scriptPath,
      },
    },
  );
}

test('Internal manifest resolves to the exact classic x64 package contract', async () => {
  const command = `
[xml]$manifest = Get-Content -LiteralPath $env:EMKE_MANIFEST -Raw
$manager = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$manager.AddNamespace(
  'f',
  'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
)
$manager.AddNamespace(
  'uap10',
  'http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
)
$manager.AddNamespace(
  'rescap',
  'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities'
)
$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $manager)
$target = $manifest.SelectSingleNode(
  '/f:Package/f:Dependencies/f:TargetDeviceFamily',
  $manager
)
$application = $manifest.SelectSingleNode(
  '/f:Package/f:Applications/f:Application',
  $manager
)
$capability = $manifest.SelectSingleNode(
  '/f:Package/f:Capabilities/rescap:Capability',
  $manager
)
if (
  $null -eq $identity -or
  $null -eq $target -or
  $null -eq $application -or
  $null -eq $capability
) {
  throw 'Required manifest nodes are unavailable.'
}
[ordered]@{
  identityName = $identity.Name
  publisher = $identity.Publisher
  version = $identity.Version
  architecture = $identity.ProcessorArchitecture
  targetName = $target.Name
  minimumVersion = $target.MinVersion
  maximumTestedVersion = $target.MaxVersionTested
  applicationId = $application.Id
  executable = $application.Executable
  entryPoint = $application.EntryPoint
  runtimeBehavior = $application.GetAttribute(
    'RuntimeBehavior',
    'http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
  )
  trustLevel = $application.GetAttribute(
    'TrustLevel',
    'http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
  )
  capability = $capability.Name
} | ConvertTo-Json -Compress
`;
  const result = spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-Command', command],
    {
      encoding: 'utf8',
      env: { ...process.env, EMKE_MANIFEST: manifestPath },
    },
  );
  assert.equal(
    result.status,
    0,
    `manifest contract failed:\n${result.stderr}`,
  );
  assert.deepEqual(JSON.parse(result.stdout.trim()), {
    identityName: 'EMKE.Translation.Internal',
    publisher: 'CN=EMKE Internal Test',
    version: '0.1.0.0',
    architecture: 'x64',
    targetName: 'Windows.Desktop',
    minimumVersion: '10.0.26200.0',
    maximumTestedVersion: '10.0.26200.0',
    applicationId: 'EMKETranslation',
    executable: 'EMKE.Windows.App.exe',
    entryPoint: 'Windows.FullTrustApplication',
    runtimeBehavior: 'packagedClassicApp',
    trustLevel: 'mediumIL',
    capability: 'runFullTrust',
  });
});

test('packaging assets preserve the approved EMKE icon master byte for byte', async () => {
  const [approved, packaged] = await Promise.all([
    readFile(approvedIconPath),
    readFile(packagedApprovedIconPath),
  ]);
  assert.deepEqual(packaged, approved);
});

test('package and verification scripts have valid PowerShell syntax', () => {
  for (const scriptPath of [packageScriptPath, verifyScriptPath]) {
    const result = parsePowerShell(scriptPath);
    assert.equal(
      result.status,
      0,
      `${path.basename(scriptPath)} failed parsing:\n${result.stderr}`,
    );
  }
});

test('verification temporarily trusts and removes only the machine app signer', async () => {
  const source = await readFile(verifyScriptPath, 'utf8');

  assert.match(source, /X509Store\]::new\(/);
  assert.match(source, /StoreName\]::TrustedPeople/);
  assert.match(source, /StoreLocation\]::LocalMachine/);
  assert.match(source, /OpenFlags\]::ReadWrite/);
  assert.match(source, /\.Add\(\$publicCertificate\)/);
  assert.doesNotMatch(source, /Import-Certificate/);
  assert.match(
    source,
    /Cert:\\LocalMachine\\TrustedPeople\\\$trustedThumbprint/,
  );
  assert.doesNotMatch(source, /Cert:\\CurrentUser\\Root/);
  assert.match(source, /\$addedTrust\s*=\s*\$true/);
  assert.match(source, /if\s*\([^]*\$addedTrust[^]*Remove-Item/);
  assert.doesNotMatch(source, /CurrentUser\\TrustedPeople/);
});

test('production package mode requires external PFX and password-environment inputs', async () => {
  await requirePackageScript();
  const result = spawnSync(
    'pwsh',
    [
      '-NoLogo',
      '-NoProfile',
      '-File',
      packageScriptPath,
      '-Configuration',
      'Release',
    ],
    { encoding: 'utf8' },
  );
  assert.notEqual(result.status, 0);
  const output = `${result.stdout}\n${result.stderr}`;
  assert.match(output, /PfxPath/);
  assert.match(output, /PasswordEnvironmentVariable/);
});

test('packaging consumes the exact CMake and native CI x64 Release artifact', async () => {
  const [cmake, nativeWorkflow, packageScript] = await Promise.all([
    readFile(nativeCmakePath, 'utf8'),
    readFile(nativeWorkflowPath, 'utf8'),
    readFile(packageScriptPath, 'utf8'),
  ]);
  const exactRelativePath =
    'Windows/artifacts/native/x64/Release/EMKE.NativeAudio.dll';

  assert.match(
    cmake,
    /EMKE_NATIVE_ARTIFACT_DIRECTORY[^]*artifacts\/native\/x64\/Release/,
  );
  assert.ok(nativeWorkflow.includes(exactRelativePath));
  assert.ok(packageScript.includes(exactRelativePath));
  assert.doesNotMatch(
    packageScript,
    /out\/native\/x64-release\/EMKE\.NativeAudio\/Release/,
  );
});

test('verified signer output writes package-bound provenance and the pinned CI thumbprint', async () => {
  await requirePackageScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-signer-provenance-'),
  );
  try {
    const packagePath = path.join(fixtureRoot, 'fixture.msix');
    const certificatePath = path.join(fixtureRoot, 'fixture.cer');
    const provenancePath = path.join(fixtureRoot, 'signing.json');
    const githubOutputPath = path.join(fixtureRoot, 'github-output.txt');
    const thumbprint = '0123456789ABCDEF0123456789ABCDEF01234567';
    await Promise.all([
      writeFile(packagePath, 'signed package fixture'),
      writeFile(certificatePath, 'public certificate fixture'),
      writeFile(githubOutputPath, ''),
    ]);

    const result = writeSignerProvenance({
      packagePath,
      certificatePath,
      provenancePath,
      githubOutputPath,
      thumbprint,
    });
    assert.equal(
      result.status,
      0,
      `signer provenance failed:\n${result.stdout}\n${result.stderr}`,
    );
    const provenance = JSON.parse(await readFile(provenancePath, 'utf8'));
    assert.deepEqual(
      Object.keys(provenance).sort(),
      [
        'certificateSha256',
        'packageSha256',
        'schemaVersion',
        'subject',
        'thumbprint',
      ],
    );
    assert.equal(provenance.schemaVersion, 1);
    assert.equal(provenance.subject, 'CN=EMKE Internal Test');
    assert.equal(provenance.thumbprint, thumbprint);
    assert.match(provenance.packageSha256, /^[0-9A-F]{64}$/);
    assert.match(provenance.certificateSha256, /^[0-9A-F]{64}$/);
    assert.equal(
      (await readFile(githubOutputPath, 'utf8')).replaceAll('\r\n', '\n'),
      `certificate_thumbprint=${thumbprint}\n`,
    );
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('verification pins package and certificate bytes to verified signer provenance', async () => {
  await requireVerifyScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-signer-verify-'),
  );
  try {
    const packageBytes = Buffer.from('signed package fixture');
    const certificateBytes = Buffer.from('public certificate fixture');
    const packagePath = path.join(fixtureRoot, 'fixture.msix');
    const certificatePath = path.join(fixtureRoot, 'fixture.cer');
    const provenancePath = path.join(fixtureRoot, 'signing.json');
    const thumbprint = '0123456789ABCDEF0123456789ABCDEF01234567';
    await Promise.all([
      writeFile(packagePath, packageBytes),
      writeFile(certificatePath, certificateBytes),
      writeFile(
        provenancePath,
        `${JSON.stringify({
          schemaVersion: 1,
          subject: 'CN=EMKE Internal Test',
          thumbprint,
          packageSha256: sha256(packageBytes),
          certificateSha256: sha256(certificateBytes),
        })}\n`,
      ),
    ]);

    const accepted = validateSignerProvenance({
      packagePath,
      certificatePath,
      provenancePath,
    });
    assert.equal(
      accepted.status,
      0,
      `valid provenance was rejected:\n${accepted.stdout}\n${accepted.stderr}`,
    );
    assert.equal(accepted.stdout.trim(), thumbprint);

    await writeFile(packagePath, 'tampered package fixture');
    const rejected = validateSignerProvenance({
      packagePath,
      certificatePath,
      provenancePath,
    });
    assert.notEqual(rejected.status, 0, 'tampered package provenance passed');
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('PFX cleanup is limited to a validated temporary input outside the repository', async () => {
  await requirePackageScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-pfx-boundary-'),
  );
  try {
    const temporaryRoot = path.join(fixtureRoot, 'runner-temp');
    const repositoryRoot = path.join(fixtureRoot, 'repository');
    const outsideRoot = path.join(fixtureRoot, 'outside');
    await Promise.all([
      mkdir(temporaryRoot),
      mkdir(repositoryRoot),
      mkdir(outsideRoot),
    ]);
    const acceptedPath = path.join(temporaryRoot, 'signing.pfx');
    const repositoryPath = path.join(repositoryRoot, 'signing.pfx');
    const outsidePath = path.join(outsideRoot, 'signing.pfx');
    await Promise.all([
      writeFile(acceptedPath, 'temporary PFX'),
      writeFile(repositoryPath, 'repository PFX'),
      writeFile(outsidePath, 'outside PFX'),
    ]);

    const accepted = resolveEphemeralPfx({
      pfxPath: acceptedPath,
      temporaryRoot,
      repositoryRoot,
    });
    assert.equal(
      accepted.status,
      0,
      `temporary PFX was rejected:\n${accepted.stdout}\n${accepted.stderr}`,
    );
    assert.equal(
      await realpath(accepted.stdout.trim()),
      await realpath(acceptedPath),
    );

    for (const rejectedPath of [repositoryPath, outsidePath]) {
      const rejected = resolveEphemeralPfx({
        pfxPath: rejectedPath,
        temporaryRoot,
        repositoryRoot,
      });
      assert.notEqual(
        rejected.status,
        0,
        `unsafe PFX cleanup target was accepted: ${rejectedPath}`,
      );
    }
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('certificate cleanup selects only unique certificates added by this packaging run', async () => {
  await requirePackageScript();
  const result = selectNewCertificateThumbprints();
  assert.equal(
    result.status,
    0,
    `certificate cleanup selection failed:\n${result.stdout}\n${result.stderr}`,
  );
  assert.deepEqual(
    JSON.parse(result.stdout.trim()),
    ['BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'],
  );
});

test('package and verification cleanup continue after an earlier removal fails', async () => {
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-cleanup-failure-'),
  );
  try {
    for (const scriptPath of [packageScriptPath, verifyScriptPath]) {
      const markerPath = path.join(
        fixtureRoot,
        `${path.basename(scriptPath)}.marker`,
      );
      await writeFile(markerPath, '');
      const result = runCleanupFailureProbe(scriptPath, markerPath);
      assert.notEqual(
        result.status,
        0,
        `${path.basename(scriptPath)} must report aggregate cleanup failure`,
      );
      assert.deepEqual(
        (await readFile(markerPath, 'utf8')).trim().split(/\r?\n/),
        ['first', 'second'],
        `${path.basename(scriptPath)} stopped before later cleanup`,
      );
      assert.match(
        `${result.stdout}\n${result.stderr}`,
        /first/,
        `${path.basename(scriptPath)} lost cleanup failure identity`,
      );
    }
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('package and verification cleanup accept an empty early-failure action set', async () => {
  for (const scriptPath of [packageScriptPath, verifyScriptPath]) {
    const result = runEmptyCleanupProbe(scriptPath);
    assert.equal(
      result.status,
      0,
      `${path.basename(scriptPath)} rejected empty cleanup:\n${result.stderr}`,
    );
  }
});

test('staging validation accepts only the application runtime and package resources', async () => {
  await requirePackageScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-stage-allowed-'),
  );
  try {
    await createAllowedStage(fixtureRoot);
    const result = validateStaging(fixtureRoot);
    assert.equal(
      result.status,
      0,
      `allowed stage was rejected:\n${result.stdout}\n${result.stderr}`,
    );
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

const forbiddenStageNames = [
  'emkevirtualaudio.inf',
  'emkevirtualaudio.cat',
  'emkevirtualaudio.sys',
  'signing.pfx',
  'signing.pem',
  'private.key',
  'password.txt',
  'EMKE.Windows.App.Tests.dll',
  'EMKE.Windows.App.pdb',
  'settings.json',
  'credentials.json',
  'meeting-recording.wav',
  'translation-transcript.txt',
  'raw-endpoint-fixture.json',
];

for (const forbiddenName of forbiddenStageNames) {
  test(`staging validation rejects ${forbiddenName}`, async () => {
    await requirePackageScript();
    const fixtureRoot = await mkdtemp(
      path.join(tmpdir(), 'emke-msix-stage-forbidden-'),
    );
    try {
      await createAllowedStage(fixtureRoot);
      await writeFile(path.join(fixtureRoot, forbiddenName), 'forbidden');
      const result = validateStaging(fixtureRoot);
      assert.notEqual(
        result.status,
        0,
        `forbidden stage item ${forbiddenName} was accepted`,
      );
    } finally {
      await rm(fixtureRoot, { recursive: true, force: true });
    }
  });
}

test('staging validation rejects symlinked content', async () => {
  await requirePackageScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-stage-link-'),
  );
  try {
    await createAllowedStage(fixtureRoot);
    await copyFile(
      path.join(fixtureRoot, 'EMKE.Windows.App.dll'),
      path.join(fixtureRoot, 'outside.dll'),
    );
    await rm(path.join(fixtureRoot, 'System.Private.CoreLib.dll'));
    const linkResult = spawnSync(
      process.platform === 'win32' ? 'cmd.exe' : 'ln',
      process.platform === 'win32'
        ? [
            '/d',
            '/s',
            '/c',
            'mklink',
            path.join(fixtureRoot, 'System.Private.CoreLib.dll'),
            path.join(fixtureRoot, 'outside.dll'),
          ]
        : [
            '-s',
            path.join(fixtureRoot, 'outside.dll'),
            path.join(fixtureRoot, 'System.Private.CoreLib.dll'),
          ],
      { encoding: 'utf8' },
    );
    assert.equal(linkResult.status, 0, 'test fixture symlink setup failed');

    const result = validateStaging(fixtureRoot);
    assert.notEqual(result.status, 0, 'symlinked stage content was accepted');
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('portable extracted-package verification accepts the exact metadata and x64 binaries', async () => {
  await requireVerifyScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-extracted-valid-'),
  );
  try {
    await createValidExtractedPackage(fixtureRoot);
    const result = validateExtracted(fixtureRoot);
    assert.equal(
      result.status,
      0,
      `valid extracted package was rejected:\n${result.stdout}\n${result.stderr}`,
    );
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('portable extracted-package verification rejects manifest drift', async () => {
  await requireVerifyScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-extracted-manifest-'),
  );
  try {
    await createValidExtractedPackage(fixtureRoot);
    const fixtureManifest = path.join(fixtureRoot, 'AppxManifest.xml');
    const source = await readFile(fixtureManifest, 'utf8');
    await writeFile(
      fixtureManifest,
      source.replace('10.0.26200.0', '10.0.26100.0'),
    );
    const result = validateExtracted(fixtureRoot);
    assert.notEqual(result.status, 0, 'manifest drift was accepted');
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('portable extracted-package verification rejects compatibility drift', async () => {
  await requireVerifyScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-extracted-compat-'),
  );
  try {
    await createValidExtractedPackage(fixtureRoot);
    const compatibilityPath = path.join(fixtureRoot, 'compatibility.json');
    const compatibility = JSON.parse(
      await readFile(compatibilityPath, 'utf8'),
    );
    compatibility.driverPackageAvailable = true;
    await writeFile(
      compatibilityPath,
      `${JSON.stringify(compatibility, null, 2)}\n`,
    );
    const result = validateExtracted(fixtureRoot);
    assert.notEqual(result.status, 0, 'compatibility drift was accepted');
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('portable extracted-package verification rejects non-x64 binaries', async () => {
  await requireVerifyScript();
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-msix-extracted-arch-'),
  );
  try {
    await createValidExtractedPackage(fixtureRoot);
    await writeFile(
      path.join(fixtureRoot, 'EMKE.NativeAudio.dll'),
      createPeFixture(0xaa64),
    );
    const result = validateExtracted(fixtureRoot);
    assert.notEqual(result.status, 0, 'ARM64 native DLL was accepted');
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

for (const unexpectedName of ['audio.wav', 'embedded-private-material.snk']) {
  test(`portable extracted-package verification rejects ${unexpectedName}`, async () => {
    await requireVerifyScript();
    const fixtureRoot = await mkdtemp(
      path.join(tmpdir(), 'emke-msix-extracted-unexpected-'),
    );
    try {
      await createValidExtractedPackage(fixtureRoot);
      await writeFile(path.join(fixtureRoot, unexpectedName), 'unexpected');
      const result = validateExtracted(fixtureRoot);
      assert.notEqual(
        result.status,
        0,
        `unexpected extracted item ${unexpectedName} was accepted`,
      );
    } finally {
      await rm(fixtureRoot, { recursive: true, force: true });
    }
  });
}
