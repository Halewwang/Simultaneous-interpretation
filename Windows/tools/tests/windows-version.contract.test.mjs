import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('../../../', import.meta.url));
const windowsRoot = path.join(repositoryRoot, 'Windows');
const versionFile = path.join(windowsRoot, 'version.json');
const channelsFile = path.join(windowsRoot, 'packaging', 'channels.json');
const compatibilityFile = path.join(
  windowsRoot,
  'packaging',
  'compatibility.internal.json',
);
const resolverFile = path.join(windowsRoot, 'tools', 'resolve-version.ps1');

const pwshProbe = spawnSync(
  'pwsh',
  ['-NoLogo', '-NoProfile', '-Command', '$PSVersionTable.PSVersion.ToString()'],
  { encoding: 'utf8' },
);
const pwshAvailable = pwshProbe.status === 0;

async function readJson(file) {
  return JSON.parse(await readFile(file, 'utf8'));
}

function runResolver(versionPath, requireTag) {
  const command = requireTag === undefined
    ? '& $env:EMKE_RESOLVER -VersionFile $env:EMKE_VERSION_FILE'
    : '& $env:EMKE_RESOLVER -VersionFile $env:EMKE_VERSION_FILE -RequireTag $env:EMKE_REQUIRE_TAG';
  const args = [
    '-NoLogo',
    '-NoProfile',
    '-Command',
    `${command} | ConvertTo-Json -Compress -Depth 5`,
  ];

  return spawnSync('pwsh', args, {
    encoding: 'utf8',
    env: {
      ...process.env,
      EMKE_RESOLVER: resolverFile,
      EMKE_VERSION_FILE: versionPath,
      EMKE_REQUIRE_TAG: requireTag ?? '',
    },
  });
}

test('Windows Internal metadata keeps the version and compatibility contract', async () => {
  const [version, channels, compatibility] = await Promise.all([
    readJson(versionFile),
    readJson(channelsFile),
    readJson(compatibilityFile),
  ]);

  assert.deepEqual(version, {
    productVersion: '0.1.0',
    packageVersion: '0.1.0.0',
    expectedTag: 'windows-v0.1.0',
    contractVersion: 1,
    settingsSchemaVersion: 1,
    driverAbiVersion: 1,
    minimumWindowsBuild: 26200,
    architecture: 'x64',
    channel: 'internal',
  });

  assert.equal(
    channels.channels.internal.packageIdentity,
    'EMKE.Translation.Internal',
  );
  assert.equal(channels.channels.internal.publisher, 'CN=EMKE Internal Test');
  assert.equal(
    channels.channels.beta.packageIdentity,
    'EMKE.Translation.Beta',
  );
  assert.equal(
    channels.channels.stable.packageIdentity,
    'EMKE.Translation',
  );
  assert.equal(
    channels.channels.internal.appId,
    'EMKE.Translation.Internal',
  );
  assert.equal(
    channels.channels.internal.credentialTarget,
    'EMKE.Translation.Internal.ApiKey',
  );
  assert.equal(channels.channels.internal.mutexSuffix, 'Internal');
  assert.equal(channels.channels.internal.pipeSuffix, 'Internal');
  assert.equal(
    channels.channels.internal.appInstallerPath,
    'windows/internal/EMKE.Translation.Internal.appinstaller',
  );
  assert.equal(channels.channels.internal.driverFeedPath, null);

  assert.deepEqual(compatibility, {
    appVersion: '0.1.0',
    contractVersion: 1,
    settingsSchemaVersion: 1,
    driverAbiVersion: 1,
    minimumDriverVersion: '0.1.0',
    recommendedDriverVersion: '0.1.0',
    driverPackageAvailable: false,
    channel: 'internal',
  });
});

test(
  'resolver emits the CI package object and accepts only the exact Windows tag',
  { skip: !pwshAvailable },
  () => {
    const result = runResolver(versionFile);

    assert.equal(result.status, 0, result.stderr || result.stdout);
    assert.deepEqual(JSON.parse(result.stdout), {
      ProductVersion: '0.1.0',
      PackageVersion: '0.1.0.0',
      ExpectedTag: 'windows-v0.1.0',
      PackageIdentity: 'EMKE.Translation.Internal',
      Publisher: 'CN=EMKE Internal Test',
      Channel: 'internal',
      Architecture: 'x64',
      MinimumWindowsBuild: 26200,
      CredentialTarget: 'EMKE.Translation.Internal.ApiKey',
      AppInstallerPath: 'windows/internal/EMKE.Translation.Internal.appinstaller',
      DriverFeedPath: null,
    });

    const windowsTagResult = runResolver(versionFile, 'windows-v0.1.0');
    assert.equal(
      windowsTagResult.status,
      0,
      windowsTagResult.stderr || windowsTagResult.stdout,
    );

    const macTagResult = runResolver(versionFile, 'v0.1.0');
    assert.notEqual(macTagResult.status, 0);
    assert.match(
      `${macTagResult.stdout}\n${macTagResult.stderr}`,
      /Expected tag 'windows-v0\.1\.0', received 'v0\.1\.0'/,
    );
  },
);

for (const forbiddenProperty of [
  'driverPackageUrl',
  'driverPackageSha256',
]) {
  test(
    `resolver rejects ${forbiddenProperty} when the driver package is unavailable`,
    { skip: !pwshAvailable },
    async () => {
      const fixtureRoot = await mkdtemp(
        path.join(tmpdir(), 'emke-version-contract-'),
      );

      try {
        const fixtureWindowsRoot = path.join(fixtureRoot, 'Windows');
        const fixturePackagingRoot = path.join(
          fixtureWindowsRoot,
          'packaging',
        );
        await mkdir(fixturePackagingRoot, { recursive: true });

        const [version, channels, compatibility] = await Promise.all([
          readJson(versionFile),
          readJson(channelsFile),
          readJson(compatibilityFile),
        ]);
        compatibility[forbiddenProperty] =
          forbiddenProperty === 'driverPackageUrl'
            ? 'https://invalid.example/driver.zip'
            : 'a'.repeat(64);

        const fixtureVersionFile = path.join(
          fixtureWindowsRoot,
          'version.json',
        );
        await Promise.all([
          writeFile(
            fixtureVersionFile,
            `${JSON.stringify(version, null, 2)}\n`,
          ),
          writeFile(
            path.join(fixturePackagingRoot, 'channels.json'),
            `${JSON.stringify(channels, null, 2)}\n`,
          ),
          writeFile(
            path.join(
              fixturePackagingRoot,
              'compatibility.internal.json',
            ),
            `${JSON.stringify(compatibility, null, 2)}\n`,
          ),
        ]);

        const result = runResolver(fixtureVersionFile);
        assert.notEqual(result.status, 0);
        const errorOutput = `${result.stdout}\n${result.stderr}`;
        assert.match(errorOutput, /driverPackageAvailable=false/);
        assert.match(errorOutput, /driverPackageUrl/);
        assert.match(errorOutput, /driverPackageSha256/);
      } finally {
        await rm(fixtureRoot, { recursive: true, force: true });
      }
    },
  );
}
