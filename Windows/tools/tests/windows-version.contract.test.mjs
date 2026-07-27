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

async function runMutatedResolver(mutate) {
  const fixtureRoot = await mkdtemp(
    path.join(tmpdir(), 'emke-version-contract-'),
  );

  try {
    const fixtureWindowsRoot = path.join(fixtureRoot, 'Windows');
    const fixturePackagingRoot = path.join(fixtureWindowsRoot, 'packaging');
    await mkdir(fixturePackagingRoot, { recursive: true });

    const [version, channels, compatibility] = await Promise.all([
      readJson(versionFile),
      readJson(channelsFile),
      readJson(compatibilityFile),
    ]);
    mutate({ version, channels, compatibility });

    const compatibilityChannel =
      typeof version.channel === 'string'
      && /^[A-Za-z0-9!-]+$/.test(version.channel)
        ? version.channel
        : 'internal';
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
          `compatibility.${compatibilityChannel}.json`,
        ),
        `${JSON.stringify(compatibility, null, 2)}\n`,
      ),
    ]);

    return runResolver(fixtureVersionFile);
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
}

const invalidSchemaMutations = [
  {
    name: 'missing productVersion',
    mutate: ({ version }) => delete version.productVersion,
  },
  {
    name: 'non-string productVersion',
    mutate: ({ version, compatibility }) => {
      version.productVersion = 1;
      version.expectedTag = 'windows-v1';
      compatibility.appVersion = 1;
    },
  },
  {
    name: 'two-segment productVersion',
    mutate: ({ version, compatibility }) => {
      version.productVersion = '0.1';
      version.expectedTag = 'windows-v0.1';
      compatibility.appVersion = '0.1';
    },
  },
  {
    name: 'missing packageVersion',
    mutate: ({ version }) => delete version.packageVersion,
  },
  {
    name: 'three-segment packageVersion',
    mutate: ({ version }) => {
      version.packageVersion = '0.1.0';
    },
  },
  {
    name: 'packageVersion component above 65535',
    mutate: ({ version }) => {
      version.packageVersion = '0.1.0.65536';
    },
  },
  {
    name: 'packageVersion not aligned with productVersion',
    mutate: ({ version }) => {
      version.packageVersion = '0.2.0.0';
    },
  },
  {
    name: 'missing expectedTag',
    mutate: ({ version }) => delete version.expectedTag,
  },
  {
    name: 'expectedTag not derived from productVersion',
    mutate: ({ version }) => {
      version.expectedTag = 'windows-v9.9.9';
    },
  },
  {
    name: 'string contractVersion',
    mutate: ({ version, compatibility }) => {
      version.contractVersion = '1';
      compatibility.contractVersion = '1';
    },
  },
  {
    name: 'null settingsSchemaVersion',
    mutate: ({ version, compatibility }) => {
      version.settingsSchemaVersion = null;
      compatibility.settingsSchemaVersion = null;
    },
  },
  {
    name: 'Boolean driverAbiVersion',
    mutate: ({ version, compatibility }) => {
      version.driverAbiVersion = true;
      compatibility.driverAbiVersion = true;
    },
  },
  {
    name: 'missing minimumWindowsBuild',
    mutate: ({ version }) => delete version.minimumWindowsBuild,
  },
  {
    name: 'string minimumWindowsBuild',
    mutate: ({ version }) => {
      version.minimumWindowsBuild = '26200';
    },
  },
  {
    name: 'fractional minimumWindowsBuild',
    mutate: ({ version }) => {
      version.minimumWindowsBuild = 26200.5;
    },
  },
  {
    name: 'minimumWindowsBuild below 26200',
    mutate: ({ version }) => {
      version.minimumWindowsBuild = 26199;
    },
  },
  {
    name: 'blank architecture',
    mutate: ({ version }) => {
      version.architecture = ' ';
    },
  },
  {
    name: 'unsupported architecture',
    mutate: ({ version }) => {
      version.architecture = 'arm64';
    },
  },
  {
    name: 'case-mismatched channel',
    mutate: ({ version, compatibility }) => {
      version.channel = 'Internal';
      compatibility.channel = 'Internal';
    },
  },
  {
    name: 'case-mismatched channels key',
    mutate: ({ channels }) => {
      channels.channels.Internal = {
        ...channels.channels.internal,
      };
      delete channels.channels.internal;
    },
  },
  {
    name: 'unsafe channel identifier',
    mutate: ({ version, channels, compatibility }) => {
      version.channel = 'Internal!';
      compatibility.channel = 'Internal!';
      channels.channels['Internal!'] = {
        ...channels.channels.internal,
      };
    },
  },
  {
    name: 'blank packageIdentity',
    mutate: ({ channels }) => {
      channels.channels.internal.packageIdentity = ' ';
    },
  },
  {
    name: 'missing publisher',
    mutate: ({ channels }) => {
      delete channels.channels.internal.publisher;
    },
  },
  {
    name: 'missing credentialTarget',
    mutate: ({ channels }) => {
      delete channels.channels.internal.credentialTarget;
    },
  },
  {
    name: 'non-string appInstallerPath',
    mutate: ({ channels }) => {
      channels.channels.internal.appInstallerPath = 42;
    },
  },
  {
    name: 'missing compatibility appVersion',
    mutate: ({ compatibility }) => delete compatibility.appVersion,
  },
  {
    name: 'missing compatibility channel',
    mutate: ({ compatibility }) => delete compatibility.channel,
  },
  {
    name: 'mismatched compatibility contractVersion',
    mutate: ({ compatibility }) => {
      compatibility.contractVersion = 999;
    },
  },
  {
    name: 'mismatched compatibility settingsSchemaVersion',
    mutate: ({ compatibility }) => {
      compatibility.settingsSchemaVersion = 999;
    },
  },
  {
    name: 'mismatched compatibility driverAbiVersion',
    mutate: ({ compatibility }) => {
      compatibility.driverAbiVersion = 999;
    },
  },
  {
    name: 'string compatibility contractVersion',
    mutate: ({ compatibility }) => {
      compatibility.contractVersion = '1';
    },
  },
  {
    name: 'null compatibility settingsSchemaVersion',
    mutate: ({ compatibility }) => {
      compatibility.settingsSchemaVersion = null;
    },
  },
  {
    name: 'Boolean compatibility driverAbiVersion',
    mutate: ({ compatibility }) => {
      compatibility.driverAbiVersion = true;
    },
  },
  {
    name: 'missing minimumDriverVersion',
    mutate: ({ compatibility }) => {
      delete compatibility.minimumDriverVersion;
    },
  },
  {
    name: 'invalid minimumDriverVersion',
    mutate: ({ compatibility }) => {
      compatibility.minimumDriverVersion = '0.1';
    },
  },
  {
    name: 'blank recommendedDriverVersion',
    mutate: ({ compatibility }) => {
      compatibility.recommendedDriverVersion = ' ';
    },
  },
];

const invalidDriverPackageMutations = [
  {
    name: 'false with driverPackageUrl',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageUrl =
        'https://invalid.example/driver.zip';
    },
    expectsLocationError: true,
  },
  {
    name: 'false with driverPackageSha256',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageSha256 = 'a'.repeat(64);
    },
    expectsLocationError: true,
  },
  {
    name: 'false with null driverPackageUrl',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageUrl = null;
    },
    expectsLocationError: true,
  },
  {
    name: 'false with empty driverPackageSha256',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageSha256 = '';
    },
    expectsLocationError: true,
  },
  {
    name: 'driverPackageAvailable true',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageAvailable = true;
    },
  },
  {
    name: 'missing driverPackageAvailable',
    mutate: ({ compatibility }) => {
      delete compatibility.driverPackageAvailable;
    },
  },
  {
    name: 'null driverPackageAvailable',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageAvailable = null;
    },
  },
  {
    name: 'string driverPackageAvailable',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageAvailable = 'false';
    },
  },
  {
    name: 'numeric driverPackageAvailable',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageAvailable = 0;
    },
  },
];

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

test(
  'resolver rejects malformed or inconsistent metadata',
  { skip: !pwshAvailable },
  async () => {
    const unexpectedSuccesses = [];

    for (const { name, mutate } of invalidSchemaMutations) {
      const result = await runMutatedResolver(mutate);
      if (result.status === 0) {
        unexpectedSuccesses.push(name);
      }
    }

    assert.deepEqual(unexpectedSuccesses, []);
  },
);

test(
  'resolver rejects unsupported driver package metadata',
  { skip: !pwshAvailable },
  async () => {
    const unexpectedSuccesses = [];
    const incompleteLocationErrors = [];

    for (
      const {
        name,
        mutate,
        expectsLocationError,
      } of invalidDriverPackageMutations
    ) {
      const result = await runMutatedResolver(mutate);
      if (result.status === 0) {
        unexpectedSuccesses.push(name);
      }

      if (expectsLocationError) {
        const errorOutput = `${result.stdout}\n${result.stderr}`;
        for (const expectedText of [
          /driverPackageAvailable=false/,
          /driverPackageUrl/,
          /driverPackageSha256/,
        ]) {
          if (!expectedText.test(errorOutput)) {
            incompleteLocationErrors.push(name);
          }
        }
      }
    }

    assert.deepEqual(unexpectedSuccesses, []);
    assert.deepEqual(incompleteLocationErrors, []);
  },
);
