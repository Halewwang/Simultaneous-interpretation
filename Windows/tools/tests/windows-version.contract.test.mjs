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
    const metadata = { version, channels, compatibility };
    mutate(metadata);

    const compatibilityChannel =
      metadata.version !== null
      && typeof metadata.version === 'object'
      && !Array.isArray(metadata.version)
      && typeof metadata.version.channel === 'string'
      && /^[A-Za-z0-9!-]+$/.test(metadata.version.channel)
        ? metadata.version.channel
        : 'internal';
    const fixtureVersionFile = path.join(
      fixtureWindowsRoot,
      'version.json',
    );
    await Promise.all([
      writeFile(
        fixtureVersionFile,
        `${JSON.stringify(metadata.version, null, 2)}\n`,
      ),
      writeFile(
        path.join(fixturePackagingRoot, 'channels.json'),
        `${JSON.stringify(metadata.channels, null, 2)}\n`,
      ),
      writeFile(
        path.join(
          fixturePackagingRoot,
          `compatibility.${compatibilityChannel}.json`,
        ),
        `${JSON.stringify(metadata.compatibility, null, 2)}\n`,
      ),
    ]);

    return runResolver(fixtureVersionFile);
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
}

const invalidRootMutations = [
  ['array', (value) => [value]],
  ['null', () => null],
  ['primitive', () => 42],
].flatMap(([shape, createValue]) =>
  ['version', 'channels', 'compatibility'].map((section) => ({
    name: `${section} root ${shape}`,
    mutate: (metadata) => {
      metadata[section] = createValue(metadata[section]);
    },
  })));

const invalidSchemaMutations = [
  ...invalidRootMutations,
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
      version.productVersion = '0.2';
      version.expectedTag = 'windows-v0.2';
      compatibility.appVersion = '0.2';
    },
  },
  {
    name: 'missing packageVersion',
    mutate: ({ version }) => delete version.packageVersion,
  },
  {
    name: 'three-segment packageVersion',
    mutate: ({ version }) => {
      version.packageVersion = '0.2.0';
    },
  },
  {
    name: 'packageVersion component above 65535',
    mutate: ({ version }) => {
      version.packageVersion = '0.2.0.65536';
    },
  },
  {
    name: 'packageVersion not aligned with productVersion',
    mutate: ({ version }) => {
      version.packageVersion = '0.3.0.0';
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
    name: 'zero contractVersion',
    mutate: ({ version, compatibility }) => {
      version.contractVersion = 0;
      compatibility.contractVersion = 0;
    },
  },
  {
    name: 'negative settingsSchemaVersion',
    mutate: ({ version, compatibility }) => {
      version.settingsSchemaVersion = -1;
      compatibility.settingsSchemaVersion = -1;
    },
  },
  {
    name: 'zero driverAbiVersion',
    mutate: ({ version, compatibility }) => {
      version.driverAbiVersion = 0;
      compatibility.driverAbiVersion = 0;
    },
  },
  {
    name: 'non-canonical productVersion',
    mutate: ({ version, compatibility }) => {
      version.productVersion = '00.01.000';
      version.packageVersion = '00.01.000.0';
      version.expectedTag = 'windows-v00.01.000';
      compatibility.appVersion = '00.01.000';
    },
  },
  {
    name: 'non-canonical packageVersion',
    mutate: ({ version }) => {
      version.packageVersion = '0.1.0.00';
    },
  },
  {
    name: 'missing minimumWindowsBuild',
    mutate: ({ version }) => delete version.minimumWindowsBuild,
  },
  {
    name: 'string minimumWindowsBuild',
    mutate: ({ version }) => {
      version.minimumWindowsBuild = '19045';
    },
  },
  {
    name: 'fractional minimumWindowsBuild',
    mutate: ({ version }) => {
      version.minimumWindowsBuild = 19045.5;
    },
  },
  {
    name: 'minimumWindowsBuild below Windows 10 floor',
    mutate: ({ version }) => {
      version.minimumWindowsBuild = 19044;
    },
  },
  {
    name: 'minimumWindowsBuild above Windows 10 floor',
    mutate: ({ version }) => {
      version.minimumWindowsBuild = 19046;
    },
  },
  {
    name: 'missing minimumWindowsApiContract',
    mutate: ({ version }) => delete version.minimumWindowsApiContract,
  },
  {
    name: 'incorrect minimumWindowsApiContract',
    mutate: ({ version }) => {
      version.minimumWindowsApiContract = '10.0.19045.0';
    },
  },
  {
    name: 'missing maximumVersionTested',
    mutate: ({ version }) => delete version.maximumVersionTested,
  },
  {
    name: 'incorrect maximumVersionTested',
    mutate: ({ version }) => {
      version.maximumVersionTested = '10.0.26100.0';
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
    name: 'incorrect minimumDriverVersion',
    mutate: ({ compatibility }) => {
      compatibility.minimumDriverVersion = '1.0.0.3';
    },
  },
  {
    name: 'blank recommendedDriverVersion',
    mutate: ({ compatibility }) => {
      compatibility.recommendedDriverVersion = ' ';
    },
  },
  {
    name: 'incorrect recommendedDriverVersion',
    mutate: ({ compatibility }) => {
      compatibility.recommendedDriverVersion = '1.0.0.3';
    },
  },
  {
    name: 'non-canonical minimumDriverVersion',
    mutate: ({ compatibility }) => {
      compatibility.minimumDriverVersion = '00.01.000';
    },
  },
  {
    name: 'non-canonical recommendedDriverVersion',
    mutate: ({ compatibility }) => {
      compatibility.recommendedDriverVersion = '00.01.000';
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
    name: 'false with case-variant DriverPackageUrl',
    mutate: ({ compatibility }) => {
      compatibility.DriverPackageUrl = null;
    },
    expectsLocationError: true,
  },
  {
    name: 'false with case-variant driverPackageSHA256',
    mutate: ({ compatibility }) => {
      compatibility.driverPackageSHA256 = '';
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
    productVersion: '0.2.0',
    packageVersion: '0.2.0.0',
    expectedTag: 'windows-v0.2.0',
    contractVersion: 1,
    settingsSchemaVersion: 1,
    driverPackageVersion: '1.0.0.2',
    driverAbiVersion: 1,
    driverHardwareId: 'ROOT\\EMKEVIRTUALAUDIO',
    driverKmdfLibraryVersion: '1.31',
    driverEndpointRoles: [
      'emke.meeting-speaker.render',
      'emke.app-speaker.capture',
      'emke.app-microphone.render',
      'emke.meeting-microphone.capture',
    ],
    minimumWindowsBuild: 19045,
    minimumWindowsApiContract: '10.0.19041.0',
    maximumVersionTested: '10.0.26200.0',
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
    appVersion: '0.2.0',
    contractVersion: 1,
    settingsSchemaVersion: 1,
    driverAbiVersion: 1,
    minimumDriverVersion: '1.0.0.2',
    recommendedDriverVersion: '1.0.0.2',
    driverPackageAvailable: false,
    channel: 'internal',
    minimumWindowsBuild: 19045,
  });
});

test(
  'resolver emits the CI package object and accepts only the exact Windows tag',
  { skip: !pwshAvailable },
  () => {
    const result = runResolver(versionFile);

    assert.equal(result.status, 0, result.stderr || result.stdout);
    assert.deepEqual(JSON.parse(result.stdout), {
      ProductVersion: '0.2.0',
      PackageVersion: '0.2.0.0',
      ExpectedTag: 'windows-v0.2.0',
      PackageIdentity: 'EMKE.Translation.Internal',
      Publisher: 'CN=EMKE Internal Test',
      Channel: 'internal',
      Architecture: 'x64',
      MinimumWindowsBuild: 19045,
      DriverPackageVersion: '1.0.0.2',
      DriverAbiVersion: 1,
      DriverHardwareId: 'ROOT\\EMKEVIRTUALAUDIO',
      DriverKmdfLibraryVersion: '1.31',
      DriverModelSection: 'EMKE.NTamd64.10.0...19045',
      DriverEndpointRoles: [
        'emke.meeting-speaker.render',
        'emke.app-speaker.capture',
        'emke.app-microphone.render',
        'emke.meeting-microphone.capture',
      ],
      MinimumWindowsApiContract: '10.0.19041.0',
      MaximumVersionTested: '10.0.26200.0',
      CredentialTarget: 'EMKE.Translation.Internal.ApiKey',
      AppInstallerPath: 'windows/internal/EMKE.Translation.Internal.appinstaller',
      DriverFeedPath: null,
    });

    const windowsTagResult = runResolver(versionFile, 'windows-v0.2.0');
    assert.equal(
      windowsTagResult.status,
      0,
      windowsTagResult.stderr || windowsTagResult.stdout,
    );

    const macTagResult = runResolver(versionFile, 'v0.2.0');
    assert.notEqual(macTagResult.status, 0);
    assert.match(
      `${macTagResult.stdout}\n${macTagResult.stderr}`,
      /Expected tag 'windows-v0\.2\.0', received 'v0\.2\.0'/,
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
