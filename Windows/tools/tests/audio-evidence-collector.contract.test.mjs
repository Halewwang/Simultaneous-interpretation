import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const toolsDirectory = path.resolve(testDirectory, "..");
const repositoryRoot = path.resolve(testDirectory, "..", "..", "..");
const collectorPath = path.join(
  toolsDirectory,
  "collect-audio-evidence.ps1",
);
const workflowPath = path.join(
  repositoryRoot,
  ".github",
  "workflows",
  "windows-audio.yml",
);

async function readRequired(filePath) {
  try {
    const source = await readFile(filePath, "utf8");
    return source.replace(/\r\n?/g, "\n");
  } catch (error) {
    assert.fail(`required collector file is missing\n${error.message}`);
  }
}

test("collector rejects dot-source before changing caller state", async () => {
  const source = await readRequired(collectorPath);
  const guard = source.search(
    /\$MyInvocation\.InvocationName\s+-ceq\s+["']\.["']/,
  );
  const strictMode = source.search(/Set-StrictMode\s+-Version\s+Latest/);
  const firstFunction = source.search(/^function\s+/m);

  assert.ok(guard >= 0, "collector must reject dot-source");
  assert.ok(
    guard < strictMode && guard < firstFunction,
    "dot-source guard must precede strict mode and every function",
  );
  assert.match(source, /\$PSVersionTable\.PSVersion\.Major\s+-ne\s+7/);
  assert.match(source, /\$IsWindows/);
  assert.match(source, /26200/);
  assert.match(source, /OSArchitecture/);
  assert.match(source, /Architecture\]::X64/);

  for (const parameter of [
    "RepositoryPath",
    "ExpectedSourceCommit",
    "PackagePath",
    "ExpectedPackageSha256",
    "ObservationPath",
    "SaltPath",
    "OutputPath",
    "RecordingBundlePath",
    "ConfirmCollect",
  ]) {
    assert.match(source, new RegExp(`\\$${parameter}\\b`));
  }
  assert.match(
    source,
    /ValidatePattern\(["']\^\[0-9A-Fa-f\]\{40\}\$["']\)/,
  );
  assert.match(
    source,
    /ValidatePattern\(["']\^\[0-9A-Fa-f\]\{64\}\$["']\)/,
  );
});

test("collector freezes trusted input and privacy contracts", async () => {
  const source = await readRequired(collectorPath);

  for (const functionName of [
    "Resolve-CollectorInputPath",
    "Resolve-CollectorOutputPath",
    "Get-StrictCollectorPackage",
    "Get-CollectorPackageSha256",
    "Get-CollectorInfMetadata",
    "Get-CollectorCatalogMetadata",
    "Read-CollectorObservation",
    "Get-EndpointRoleSha256",
    "Get-LabAcceptance",
    "New-AudioEvidenceRecord",
    "Write-AtomicEvidenceFile",
    "Invoke-CollectAudioEvidence",
  ]) {
    assert.match(source, new RegExp(`function\\s+${functionName}\\b`));
  }

  assert.match(source, /EMKE-DRIVER-PACKAGE-SHA256-V1/);
  assert.match(source, /EMKE-ENDPOINT-ROLE-HASH-V1/);
  assert.match(source, /FixedTimeEquals/);
  assert.match(source, /Get-AuthenticodeSignature/);
  assert.match(source, /host Authenticode only/);
  assert.match(source, /Microsoft\/WHQL not established/);
  assert.match(source, /ROOT\\EMKEVIRTUALAUDIO/);
  assert.match(source, /DriverAbi/);

  for (const role of [
    "emke.meeting-speaker.render",
    "emke.app-speaker.capture",
    "emke.app-microphone.render",
    "emke.meeting-microphone.capture",
  ]) {
    assert.match(source, new RegExp(role.replaceAll(".", "\\.")));
  }
  for (const scenario of [
    "enumerate",
    "inbound-original",
    "inbound-translated",
    "outbound-translated",
    "outbound-underrun",
    "inbound-failure",
    "outbound-failure",
    "crash-after-mic-open",
  ]) {
    assert.match(source, new RegExp(scenario));
  }

  assert.match(source, /schemaVersion/);
  assert.match(source, /rawObservationSha256/);
  assert.match(source, /recordingBundleSha256/);
  assert.match(source, /collectorValidated/);
  assert.match(source, /driverInstalled/);
  assert.match(source, /notEstablished/);
  assert.match(source, /observationProvided/);
});

test("collector output is new atomic UTF-8 without mutation capability", async () => {
  const source = await readRequired(collectorPath);

  assert.match(source, /FileMode\]::CreateNew/);
  assert.match(source, /UTF8Encoding\]::new\(\$false\)/);
  assert.match(source, /FileOptions\]::WriteThrough/);
  assert.match(source, /Flush\(\$true\)/);
  assert.match(
    source,
    /\[IO\.File\]::Move\([^]*?\$false\s*\)/,
    "final rename must explicitly refuse overwrite",
  );
  assert.match(source, /FileAttributes\]::ReparsePoint/);
  assert.match(source, /GetFullPath/);
  assert.match(source, /IsPathFullyQualified/);

  const forbidden = [
    /\bpnputil(?:\.exe)?\b/i,
    /\bStart-Process\b/i,
    /\bInvoke-Expression\b/i,
    /\bNew-SelfSignedCertificate\b/i,
    /\bImport-(?:Certificate|PfxCertificate)\b/i,
    /\bcertutil(?:\.exe)?\b/i,
    /\bsigntool(?:\.exe)?\b/i,
    /\bbcdedit(?:\.exe)?\b/i,
    /\btestsigning\b/i,
    /\bSetupDi\w*\b/i,
    /\bGet-PnpDevice\b/i,
    /\bGet-CimInstance\b/i,
    /\bIMMDevice\b/i,
    /\bIAudioClient\b/i,
  ];
  for (const pattern of forbidden) {
    assert.doesNotMatch(source, pattern);
  }
});

test("Windows CI runs collector tests only as independent gates", async () => {
  const workflow = await readRequired(workflowPath);
  const validationStep = workflow.match(
    /- name: Validate driver source and package rules(?<body>[^]*?)(?=\n      - name: Build unsigned driver package)/,
  );
  assert.ok(validationStep, "driver validation step is missing");

  const commands = [
    {
      command:
        "node --test Windows/tools/tests/audio-evidence-collector.contract.test.mjs",
      failure: "Audio evidence collector contract tests failed.",
    },
    {
      command:
        "pwsh -NoProfile -File Windows/tools/tests/audio-evidence-collector.validation.test.ps1",
      failure: "Audio evidence collector validation tests failed.",
    },
    {
      command:
        "pwsh -NoProfile -File Windows/tools/tests/audio-evidence-collector.behavior.test.ps1",
      failure: "Audio evidence collector behavior tests failed.",
    },
  ];
  let previousOffset = -1;
  for (const gate of commands) {
    const commandOffset = validationStep.groups.body.indexOf(gate.command);
    assert.ok(
      commandOffset > previousOffset,
      `missing or misordered collector gate: ${gate.command}`,
    );
    const following = validationStep.groups.body.slice(commandOffset);
    assert.match(following, /^\S[^\n]*\n\s*if \(\$LASTEXITCODE -ne 0\)/);
    assert.match(
      following,
      new RegExp(gate.failure.replaceAll(".", "\\.")),
    );
    previousOffset = commandOffset;
  }

  assert.doesNotMatch(validationStep.groups.body, /-ConfirmCollect\b/);
  assert.doesNotMatch(
    validationStep.groups.body,
    /collect-audio-evidence\.ps1\b/,
  );
});
