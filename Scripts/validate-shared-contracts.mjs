import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const contractsDirectory = path.join(root, "Shared", "Contracts");
const fixturesDirectory = path.join(root, "Shared", "TestVectors");
const failures = [];

const expectedSchemas = new Map([
  ["v1/app-state.schema.json", {
    id: "urn:emke:contracts:v1:app-state",
    title: "EMKE App State v1",
  }],
  ["v1/compatibility.schema.json", {
    id: "urn:emke:contracts:v1:compatibility",
    title: "EMKE Compatibility v1",
  }],
  ["v1/translation-events.schema.json", {
    id: "urn:emke:contracts:v1:translation-events",
    title: "EMKE Translation Events v1",
  }],
]);

const expectedTranslationTypes = [
  "session.update",
  "input_audio_buffer.append",
  "session.close",
  "session.created",
  "session.updated",
  "translation_audio.delta",
  "translation_audio.done",
  "input_audio_transcription.delta",
  "input_audio_transcription.done",
  "error",
  "session.closed",
];

const expectedAppStateEnums = {
  runtimeState: ["stopped", "starting", "running", "stopping", "degraded", "failed"],
  channelState: ["inactive", "connecting", "connected", "reconnecting", "bypassed", "degraded", "failed"],
  inboundRoute: ["stopped", "translated", "originalFailOpen", "originalBypass"],
  outboundRoute: ["stopped", "translated", "mutedFailClosed", "originalBypass"],
  errorCategory: ["configuration", "permission", "driver", "device", "authentication", "endpointModel", "protocol", "network", "backpressure", "closeTimeout"],
  recoveryAction: ["none", "editSettings", "openPrivacySettings", "installDriver", "selectDevice", "updateApiKey", "retry", "reportCompatibility"],
};

function fail(message) {
  failures.push(message);
}

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function regularFile(relativePath) {
  try {
    if (!fs.lstatSync(path.join(root, relativePath)).isFile()) {
      fail(`${relativePath}: must be a regular non-symlink file`);
      return false;
    }
    return true;
  } catch {
    fail(`${relativePath}: must be a regular non-symlink file`);
    return false;
  }
}

function readObjectJson(relativePath) {
  if (!regularFile(relativePath)) return undefined;
  let value;
  try {
    value = JSON.parse(fs.readFileSync(path.join(root, relativePath), "utf8"));
  } catch {
    fail(`${relativePath}: invalid JSON`);
    return undefined;
  }
  if (!isObject(value)) {
    fail(`${relativePath}: root must be a non-null object`);
    return undefined;
  }
  return value;
}

function isSafeRelativePath(relativePath) {
  if (typeof relativePath !== "string" || relativePath.length === 0) return false;
  if (relativePath.includes("\\") || path.isAbsolute(relativePath) || /^[A-Za-z]:[\\/]/.test(relativePath)) return false;
  return relativePath.split("/").every((segment) => segment.length > 0 && segment !== "." && segment !== "..");
}

function collectJsonPaths(directory, excludedFile, label) {
  try {
    return fs.readdirSync(directory, { recursive: true, withFileTypes: true })
      .filter((entry) => entry.name.endsWith(".json"))
      .map((entry) => path.relative(directory, path.join(entry.parentPath, entry.name)).split(path.sep).join("/"))
      .filter((relativePath) => relativePath !== excludedFile)
      .sort();
  } catch {
    fail(`${label}: inventory unreadable`);
    return [];
  }
}

function validateInventory(name, listedPaths, directory, excludedFile, relativeDirectory) {
  if (!Array.isArray(listedPaths)) {
    fail(`${name} manifest: inventory must be an array`);
    return [];
  }

  const listed = new Set();
  for (const relativePath of listedPaths) {
    if (!isSafeRelativePath(relativePath) || !relativePath.endsWith(".json")) {
      fail(`${name} manifest: unsafe inventory path`);
      continue;
    }
    if (listed.has(relativePath)) fail(`${name} manifest: duplicate inventory path`);
    listed.add(relativePath);
  }

  const sortedListed = [...listed].sort();
  const actual = collectJsonPaths(directory, excludedFile, `${relativeDirectory}`);
  const actualSet = new Set(actual);
  for (const relativePath of sortedListed) {
    const contractPath = `${relativeDirectory}/${relativePath}`;
    regularFile(contractPath);
    if (!actualSet.has(relativePath)) fail(`${contractPath}: missing from actual inventory`);
  }
  for (const relativePath of actual) {
    const contractPath = `${relativeDirectory}/${relativePath}`;
    regularFile(contractPath);
    if (!listed.has(relativePath)) fail(`${contractPath}: unlisted inventory file`);
  }
  return sortedListed;
}

function hasClosedRoot(schema) {
  if (schema.additionalProperties === false) return true;
  return Array.isArray(schema.oneOf)
    && schema.oneOf.length > 0
    && schema.oneOf.every((variant) => isObject(variant) && variant.additionalProperties === false);
}

function sameArray(actual, expected) {
  return Array.isArray(actual)
    && actual.length === expected.length
    && actual.every((value, index) => value === expected[index]);
}

function inspectDecodedJson(value, findings) {
  if (typeof value === "string") {
    if (/["']?authorization["']?\s*[:=]/i.test(value)) findings.add("forbidden Authorization key");
    if (/(?:["']?(?:api[_ -]?key|access[_ -]?token|secret|token)["']?)\s*[:=]/i.test(value)) findings.add("forbidden secret-like content");
    if (/sk-[a-z0-9_-]{16,}/i.test(value)) findings.add("forbidden secret-like content");
    if (/(?:^|[^A-Za-z0-9])\/(?:Users|Volumes|private|var|tmp)\//.test(value)) findings.add("forbidden absolute path");
    if (/(?:^|[^A-Za-z0-9])[A-Za-z]:\\(?:Users|Windows|ProgramData)\\/.test(value)) findings.add("forbidden absolute path");
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) inspectDecodedJson(item, findings);
    return;
  }
  if (!isObject(value)) return;

  for (const [key, nestedValue] of Object.entries(value)) {
    const normalizedKey = key.replace(/[ _-]/g, "").toLowerCase();
    if (normalizedKey === "authorization") findings.add("forbidden Authorization key");
    if (["apikey", "accesstoken", "token", "secret"].includes(normalizedKey)) findings.add("forbidden secret-like content");
    inspectDecodedJson(nestedValue, findings);
  }
}

function inspectSharedContent() {
  let entries;
  try {
    entries = fs.readdirSync(path.join(root, "Shared"), { recursive: true, withFileTypes: true });
  } catch {
    fail("Shared: content inventory unreadable");
    return;
  }
  const patterns = [
    ["forbidden Authorization key", /["']?authorization["']?\s*[:=]/i],
    ["forbidden secret-like content", /(?:["']?(?:api[_ -]?key|access[_ -]?token|secret|token)["']?)\s*[:=]\s*["']?[A-Za-z0-9._~+/-]{12,}/i],
    ["forbidden secret-like content", /sk-[a-z0-9_-]{16,}/i],
    ["forbidden absolute path", /(?:^|["'\s])\/(?:Users|Volumes|private|var|tmp)\//m],
    ["forbidden absolute path", /(?:^|["'\s])[A-Za-z]:\\Users\\/m],
    ["forbidden absolute path", /(?:^|["'\s])[A-Za-z]:\\\\Users\\\\/m],
  ];
  for (const entry of entries.filter((candidate) => candidate.isFile()).sort((left, right) => left.parentPath.localeCompare(right.parentPath) || left.name.localeCompare(right.name))) {
    const relativePath = path.relative(root, path.join(entry.parentPath, entry.name)).split(path.sep).join("/");
    let content;
    try {
      content = fs.readFileSync(path.join(entry.parentPath, entry.name), "utf8");
    } catch {
      fail(`${relativePath}: content unreadable`);
      continue;
    }
    for (const [rule, pattern] of patterns) {
      if (pattern.test(content)) fail(`${relativePath}: ${rule}`);
    }
    if (entry.name.endsWith(".json")) {
      try {
        const findings = new Set();
        inspectDecodedJson(JSON.parse(content), findings);
        for (const rule of findings) fail(`${relativePath}: ${rule}`);
      } catch {
        // Invalid JSON is reported by the manifest/inventory validation path.
      }
    }
  }
}

const manifest = readObjectJson("Shared/Contracts/contract-manifest.json");
const fixtureManifest = readObjectJson("Shared/TestVectors/fixture-manifest.json");

if (manifest !== undefined) {
  if (manifest.contractVersion !== 1 || manifest.status !== "frozen") fail("Shared/Contracts/contract-manifest.json: must freeze version 1");
  if (manifest.fixtureManifest !== "../TestVectors/fixture-manifest.json") fail("Shared/Contracts/contract-manifest.json: must reference the canonical fixture manifest");
}
if (fixtureManifest !== undefined && manifest !== undefined && fixtureManifest.contractVersion !== manifest.contractVersion) {
  fail("Shared/TestVectors/fixture-manifest.json: contract version differs from contract manifest");
}

const schemaPaths = validateInventory("schema", manifest?.schemas, contractsDirectory, "contract-manifest.json", "Shared/Contracts");
const fixturePaths = validateInventory("fixture", fixtureManifest?.fixtures, fixturesDirectory, "fixture-manifest.json", "Shared/TestVectors");

for (const relativePath of schemaPaths) {
  const contractPath = `Shared/Contracts/${relativePath}`;
  const schema = readObjectJson(contractPath);
  if (schema === undefined) continue;
  if (schema.$schema !== "https://json-schema.org/draft/2020-12/schema") fail(`${contractPath}: wrong JSON Schema dialect`);
  if (!hasClosedRoot(schema)) fail(`${contractPath}: root must be closed or use closed oneOf variants`);
  if (typeof schema.$id !== "string" || schema.$id.length === 0 || typeof schema.title !== "string" || schema.title.length === 0) {
    fail(`${contractPath}: missing stable schema values`);
  }
  const expectedSchema = expectedSchemas.get(relativePath);
  if (!expectedSchema || expectedSchema.id !== schema.$id || expectedSchema.title !== schema.title) {
    fail(`${contractPath}: unexpected stable schema values`);
  }

  if (relativePath === "v1/translation-events.schema.json") {
    const eventTypes = Array.isArray(schema.oneOf)
      ? schema.oneOf.map((variant) => variant?.properties?.type?.const)
      : undefined;
    if (!sameArray(eventTypes, expectedTranslationTypes)) fail(`${contractPath}: translation event types drifted`);
  }
  if (relativePath === "v1/app-state.schema.json") {
    const enumValues = Object.fromEntries(
      Object.keys(expectedAppStateEnums).map((name) => [name, schema.$defs?.[name]?.enum]),
    );
    if (!Object.entries(expectedAppStateEnums).every(([name, expected]) => sameArray(enumValues[name], expected))) {
      fail(`${contractPath}: app-state enum values drifted`);
    }
  }
  if (relativePath === "v1/compatibility.schema.json" && !sameArray(schema.properties?.channel?.enum, ["internal", "beta", "stable"])) {
    fail(`${contractPath}: compatibility channel values drifted`);
  }
}
for (const relativePath of expectedSchemas.keys()) {
  if (!schemaPaths.includes(relativePath)) fail(`Shared/Contracts/contract-manifest.json: missing stable schema inventory entry`);
}

const fixtureIds = new Set();
for (const relativePath of fixturePaths) {
  const contractPath = `Shared/TestVectors/${relativePath}`;
  const fixture = readObjectJson(contractPath);
  if (fixture === undefined) continue;
  if (fixture.contractVersion !== manifest?.contractVersion) fail(`${contractPath}: contractVersion mismatch`);
  if (typeof fixture.fixtureId !== "string" || fixture.fixtureId.length === 0) {
    fail(`${contractPath}: missing fixtureId`);
  } else if (fixtureIds.has(fixture.fixtureId)) {
    fail(`${contractPath}: duplicate fixtureId`);
  } else {
    fixtureIds.add(fixture.fixtureId);
  }
  if (typeof fixture.category !== "string" || fixture.category.length === 0) fail(`${contractPath}: missing category`);
}

inspectSharedContent();

const outputFailures = [...new Set(failures)].sort();
if (outputFailures.length > 0) {
  process.stderr.write(`${outputFailures.join("\n")}\n`);
  process.exit(1);
}
process.stdout.write(`contract v${manifest.contractVersion}: ${schemaPaths.length} schemas, ${fixturePaths.length} fixtures\n`);
