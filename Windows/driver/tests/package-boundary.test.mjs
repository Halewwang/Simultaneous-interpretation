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

function runNode(script, args) {
  return spawnSync(process.execPath, [script, ...args], {
    cwd: repositoryRoot,
    encoding: "utf8",
  });
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
DriverVer=07/26/2026,1.0.0.1

[EMKE_Install.NT.Wdf]
KmdfService=EMKEVirtualAudio,EMKE_Wdf

[EMKE_Wdf]
KmdfLibraryVersion=1.33
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
    stampedInf.replace("1.33", "$KMDFVERSION$"),
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
