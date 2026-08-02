import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, symlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";

const repositoryRoot = path.resolve(import.meta.dirname, "..", "..", "..");
const stagingScript = path.join(
  repositoryRoot,
  "Windows",
  "tools",
  "stage-driver-package.mjs",
);
const contractValidator = path.join(
  repositoryRoot,
  "Windows",
  "tools",
  "validate-driver-contract.mjs",
);
const sharedHeader = path.join(
  repositoryRoot,
  "Windows",
  "shared",
  "emke_endpoint_contract.h",
);
const sourceInf = path.join(
  repositoryRoot,
  "Windows",
  "driver",
  "EMKE.VirtualAudio",
  "EMKE.VirtualAudio.inf",
);
const sourceProject = path.join(
  repositoryRoot,
  "Windows",
  "driver",
  "EMKE.VirtualAudio",
  "EMKE.VirtualAudio.vcxproj",
);

function runNode(script, args) {
  return spawnSync(process.execPath, [script, ...args], {
    cwd: repositoryRoot,
    encoding: "utf8",
  });
}

function mutateCompiledGuidComponent(header, componentIndex) {
  const definition = header.match(
    /DEFINE_DEVPROPKEY\(\s*DEVPKEY_EMKE_EndpointRole\s*,([\s\S]*?)\);/,
  );
  assert.notEqual(
    definition,
    null,
    "shared header must compile DEVPKEY_EMKE_EndpointRole",
  );
  const arguments_ = definition[1]
    .split(",")
    .map((value) => value.trim());
  assert.equal(arguments_.length, 12, "property key must have 11 GUID components and one PID");
  const authority = arguments_[componentIndex];
  assert.match(
    authority,
    /^(?:0x[0-9a-f]+|[A-Z][A-Z0-9_]*)$/i,
    `GUID component ${componentIndex} must be a numeric literal or macro`,
  );

  const mutateHex = (value) => {
    const width = value.length - 2;
    const mutated = (Number.parseInt(value.slice(2), 16) ^ 1)
      .toString(16)
      .padStart(width, "0");
    return `0x${mutated}`;
  };

  if (/^0x[0-9a-f]+$/i.test(authority)) {
    const mutatedDefinition = definition[0].replace(
      authority,
      mutateHex(authority),
    );
    return header.replace(definition[0], mutatedDefinition);
  }

  const macro = new RegExp(
    `(^\\s*#define\\s+${authority}\\s+)(0x[0-9a-f]+)(\\s*$)`,
    "im",
  );
  const macroDefinition = header.match(macro);
  assert.notEqual(
    macroDefinition,
    null,
    `GUID authority macro ${authority} must be numeric`,
  );
  return header.replace(
    macro,
    `${macroDefinition[1]}${mutateHex(macroDefinition[2])}${macroDefinition[3]}`,
  );
}

async function makePackageFixture(infText) {
  const root = await mkdtemp(path.join(os.tmpdir(), "emke-driver-package-"));
  const repository = path.join(root, "repository");
  const artifactRoot = path.join(repository, "Windows", "artifacts");
  const source = path.join(root, "wdk-package");
  const artifact = path.join(
    artifactRoot,
    "driver",
    "x64",
    "Release",
  );
  await mkdir(artifactRoot, { recursive: true });
  await mkdir(source, { recursive: true });
  await writeFile(
    path.join(source, "EMKE.VirtualAudio.inf"),
    infText,
    "utf8",
  );
  await writeFile(
    path.join(source, "EMKE.VirtualAudio.sys"),
    Buffer.from([0x45, 0x4d, 0x4b, 0x45, 0x01, 0x00]),
  );
  return { root, repository, artifactRoot, source, artifact };
}

const stampedInf = `[Version]
Signature="$Windows NT$"
DriverVer=08/01/2026,1.0.0.2

[EMKE_Install.NT.Wdf]
KmdfService=EMKEVirtualAudio,EMKE_Wdf

[EMKE_Wdf]
KmdfLibraryVersion=1.31
`;

test("stager copies the exact WDK-stamped INF and SYS bytes", async () => {
  const fixture = await makePackageFixture(stampedInf);
  const result = runNode(stagingScript, [
    "--repository-root",
    fixture.repository,
    "--artifact-root",
    fixture.artifactRoot,
    "--source-package",
    fixture.source,
    "--artifact-directory",
    fixture.artifact,
  ]);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(
    await readFile(path.join(fixture.artifact, "EMKE.VirtualAudio.inf")),
    await readFile(path.join(fixture.source, "EMKE.VirtualAudio.inf")),
  );
  assert.deepEqual(
    await readFile(path.join(fixture.artifact, "EMKE.VirtualAudio.sys")),
    await readFile(path.join(fixture.source, "EMKE.VirtualAudio.sys")),
  );
});

test("stager rejects an unresolved source INF before creating an artifact", async () => {
  const fixture = await makePackageFixture(
    stampedInf.replace("1.31", "$KMDFVERSION$"),
  );
  const result = runNode(stagingScript, [
    "--repository-root",
    fixture.repository,
    "--artifact-root",
    fixture.artifactRoot,
    "--source-package",
    fixture.source,
    "--artifact-directory",
    fixture.artifact,
  ]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /stamped|unresolved|KMDFVERSION/i);
});

test("stager rejects a symlinked artifact target without touching its referent", async () => {
  const fixture = await makePackageFixture(stampedInf);
  const external = path.join(fixture.root, "external");
  const sentinel = path.join(external, "sentinel.txt");
  await mkdir(path.dirname(fixture.artifact), { recursive: true });
  await mkdir(external);
  await writeFile(sentinel, "preserve me", "utf8");
  await symlink(external, fixture.artifact, "dir");

  const result = runNode(stagingScript, [
    "--repository-root",
    fixture.repository,
    "--artifact-root",
    fixture.artifactRoot,
    "--source-package",
    fixture.source,
    "--artifact-directory",
    fixture.artifact,
  ]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /symbolic link|reparse|symlink/i);
  assert.equal(await readFile(sentinel, "utf8"), "preserve me");
});

test("stager rejects an artifact path outside the repository-owned root", async () => {
  const fixture = await makePackageFixture(stampedInf);
  const outside = path.join(fixture.root, "outside-artifact");
  const result = runNode(stagingScript, [
    "--repository-root",
    fixture.repository,
    "--artifact-root",
    fixture.artifactRoot,
    "--source-package",
    fixture.source,
    "--artifact-directory",
    outside,
  ]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /outside|artifact root/i);
});

test("INF validator accepts the real INF only when it matches the shared header", () => {
  const result = runNode(contractValidator, [
    "--header",
    sharedHeader,
    "--inf",
    sourceInf,
  ]);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /driver INF contract validation passed/i);
});

test("resolved package validator rejects version, floor, and KMDF drift", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "emke-package-contract-"));
  const sourceText = await readFile(sourceInf, "utf8");
  const projectText = await readFile(sourceProject, "utf8");
  const desiredInf = sourceText
    .replace("DriverVer=07/26/2026,1.0.0.1", "DriverVer=08/01/2026,1.0.0.2")
    .replaceAll("NTamd64.10.0...26200", "NTamd64.10.0...19045")
    .replace(
      "%DeviceDescription%=EMKE_Install,ROOT\\EMKEVIRTUALAUDIO",
      "%EMKE.VirtualAudio.DeviceDesc%=EMKE.VirtualAudio,ROOT\\EMKEVIRTUALAUDIO",
    )
    .replaceAll("EMKE_Install.NT", "EMKE.VirtualAudio.NT")
    .replace("KmdfLibraryVersion=$KMDFVERSION$", "KmdfLibraryVersion=1.31");
  const desiredProject = projectText
    .replace("<EMKETargetOS>Windows11</EMKETargetOS>", "<EMKETargetOS>Windows10</EMKETargetOS>")
    .replace(
      "<KMDF_VERSION_MAJOR>1</KMDF_VERSION_MAJOR>",
      "<KMDF_VERSION_MAJOR>1</KMDF_VERSION_MAJOR>\n    <KMDF_VERSION_MINOR>31</KMDF_VERSION_MINOR>",
    )
    .replace("<DateStamp>07/26/2026</DateStamp>", "<DateStamp>08/01/2026</DateStamp>")
    .replace("<TimeStamp>1.0.0.1</TimeStamp>", "<TimeStamp>1.0.0.2</TimeStamp>")
    .replace(
      "<TimeStamp>1.0.0.2</TimeStamp>",
      "<TimeStamp>1.0.0.2</TimeStamp>\n      <KmdfVersionNumber>1.31</KmdfVersionNumber>",
    );
  const version = {
    driverPackageVersion: "1.0.0.2",
    minimumWindowsBuild: 19045,
    driverAbiVersion: 1,
    driverHardwareId: "ROOT\\EMKEVIRTUALAUDIO",
    driverKmdfLibraryVersion: "1.31",
    driverEndpointRoles: [
      "emke.meeting-speaker.render",
      "emke.app-speaker.capture",
      "emke.app-microphone.render",
      "emke.meeting-microphone.capture",
    ],
  };
  const compatibility = {
    minimumDriverVersion: "1.0.0.2",
    recommendedDriverVersion: "1.0.0.2",
    minimumWindowsBuild: 19045,
    driverAbiVersion: 1,
    driverHardwareId: "ROOT\\EMKEVIRTUALAUDIO",
    driverKmdfLibraryVersion: "1.31",
    driverEndpointRoles: version.driverEndpointRoles,
  };
  const project = path.join(root, "EMKE.VirtualAudio.vcxproj");
  const versionPath = path.join(root, "version.json");
  const compatibilityPath = path.join(root, "compatibility.internal.json");
  await writeFile(project, desiredProject, "utf8");
  await writeFile(versionPath, JSON.stringify(version), "utf8");
  await writeFile(compatibilityPath, JSON.stringify(compatibility), "utf8");

  const mutations = [
    {
      name: "old driver version",
      inf: desiredInf.replace("1.0.0.2", "1.0.0.1"),
      error: /version|DriverVer/i,
    },
    {
      name: "Windows 11-only floor",
      inf: desiredInf.replaceAll("19045", "26200"),
      error: /19045|minimum Windows|model/i,
    },
    {
      name: "KMDF 1.32",
      inf: desiredInf.replace("KmdfLibraryVersion=1.31", "KmdfLibraryVersion=1.32"),
      error: /KMDF|1\.31/i,
    },
    {
      name: "KMDF 1.33",
      inf: desiredInf.replace("KmdfLibraryVersion=1.31", "KmdfLibraryVersion=1.33"),
      error: /KMDF|1\.31/i,
    },
  ];

  for (const mutation of mutations) {
    const inf = path.join(root, `${mutation.name.replaceAll(" ", "-")}.inf`);
    await writeFile(inf, mutation.inf, "utf8");
    const result = runNode(contractValidator, [
      "--header", sharedHeader,
      "--inf", inf,
      "--project", project,
      "--version", versionPath,
      "--compatibility", compatibilityPath,
    ]);
    assert.notEqual(result.status, 0, `${mutation.name} must be rejected`);
    assert.match(result.stderr, mutation.error);
  }

  const metadataMutations = [
    {
      name: "version package version",
      version: { ...version, driverPackageVersion: "1.0.0.3" },
      compatibility,
      error: /version|DriverVer/i,
    },
    {
      name: "compatibility hardware ID",
      version,
      compatibility: {
        ...compatibility,
        driverHardwareId: "ROOT\\WRONGDEVICE",
      },
      error: /hardware|ROOT/i,
    },
    {
      name: "version endpoint roles",
      version: {
        ...version,
        driverEndpointRoles: version.driverEndpointRoles.slice(0, 3),
      },
      compatibility,
      error: /endpoint|role/i,
    },
  ];

  const desiredInfPath = path.join(root, "desired.inf");
  await writeFile(desiredInfPath, desiredInf, "utf8");
  for (const mutation of metadataMutations) {
    await writeFile(versionPath, JSON.stringify(mutation.version), "utf8");
    await writeFile(
      compatibilityPath,
      JSON.stringify(mutation.compatibility),
      "utf8",
    );
    const result = runNode(contractValidator, [
      "--header", sharedHeader,
      "--inf", desiredInfPath,
      "--project", project,
      "--version", versionPath,
      "--compatibility", compatibilityPath,
    ]);
    assert.notEqual(result.status, 0, `${mutation.name} must be rejected`);
    assert.match(result.stderr, mutation.error);
  }
});

test("driver build validates the resolved staged INF before Inf2Cat", async () => {
  const script = await readFile(
    path.join(repositoryRoot, "Windows", "tools", "build-driver.ps1"),
    "utf8",
  );
  const stagingOffset = script.indexOf('"--artifact-directory", $artifactDirectory');
  const stagedValidationOffset = script.indexOf(
    '"--inf", (Join-Path $artifactDirectory "EMKE.VirtualAudio.inf")',
  );
  const inf2CatOffset = script.indexOf("-Executable $inf2Cat");
  assert.ok(stagingOffset >= 0);
  assert.ok(stagingOffset < stagedValidationOffset);
  assert.ok(stagedValidationOffset < inf2CatOffset);
});

test("INF validator rejects a role string that diverges from the shared header", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "emke-inf-contract-"));
  const mutatedInf = path.join(root, "EMKE.VirtualAudio.inf");
  const text = await readFile(sourceInf, "utf8");
  await writeFile(
    mutatedInf,
    text.replace(
      'RoleAppSpeakerCapture="emke.app-speaker.capture"',
      'RoleAppSpeakerCapture="emke.wrong.capture"',
    ),
    "utf8",
  );
  const result = runNode(contractValidator, [
    "--header",
    sharedHeader,
    "--inf",
    mutatedInf,
  ]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /RoleAppSpeakerCapture|shared header/i);
});

test("INF validator rejects ABI and property-key divergence", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "emke-inf-contract-"));
  const mutatedInf = path.join(root, "EMKE.VirtualAudio.inf");
  const text = await readFile(sourceInf, "utf8");
  await writeFile(
    mutatedInf,
    text
      .replace(
        "HKR,,DriverAbi,0x00010001,0x00000001",
        "HKR,,DriverAbi,0x00010001,0x00000002",
      )
      .replace(
        'PKEY_EMKE_EndpointRole="{3FA64F16-18AF-4E9E-B538-91C1140EC142},2"',
        'PKEY_EMKE_EndpointRole="{00000000-0000-0000-0000-000000000000},9"',
      ),
    "utf8",
  );
  const result = runNode(contractValidator, [
    "--header",
    sharedHeader,
    "--inf",
    mutatedInf,
  ]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /ABI|property key|shared header/i);
});

test("INF validator derives the GUID from every compiled numeric component", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "emke-guid-authority-"));
  const header = await readFile(sharedHeader, "utf8");

  for (let componentIndex = 0; componentIndex < 11; componentIndex += 1) {
    const mutatedHeader = path.join(
      root,
      `emke_endpoint_contract-${componentIndex}.h`,
    );
    await writeFile(
      mutatedHeader,
      mutateCompiledGuidComponent(header, componentIndex),
      "utf8",
    );
    const result = runNode(contractValidator, [
      "--header",
      mutatedHeader,
      "--inf",
      sourceInf,
    ]);
    assert.notEqual(
      result.status,
      0,
      `mutating compiled GUID component ${componentIndex} must invalidate the INF; ` +
        `stdout: ${result.stdout}`,
    );
    assert.match(result.stderr, /property key|shared header|GUID/i);
  }
});
