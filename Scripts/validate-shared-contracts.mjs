import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const contractsDirectory = path.join(root, "Shared", "Contracts");
const fixturesDirectory = path.join(root, "Shared", "TestVectors");
const failures = [];

function fail(message) {
  failures.push(message);
}

function readJson(relativePath) {
  try {
    return JSON.parse(fs.readFileSync(path.join(root, relativePath), "utf8"));
  } catch (error) {
    fail(`${relativePath}: invalid or unreadable JSON (${error.message})`);
    return undefined;
  }
}

function isSafeRelativePath(relativePath) {
  if (typeof relativePath !== "string" || relativePath.length === 0) return false;
  if (relativePath.includes("\\")) return false;
  if (path.isAbsolute(relativePath) || /^[A-Za-z]:[\\/]/.test(relativePath)) return false;
  const segments = relativePath.split(/[\\/]/);
  return segments.every((segment) => segment.length > 0 && segment !== "." && segment !== "..");
}

function validateInventory(name, listedPaths, directory, excludedFile) {
  if (!Array.isArray(listedPaths)) {
    fail(`${name} manifest inventory must be an array`);
    return [];
  }

  const listed = new Set();
  for (const relativePath of listedPaths) {
    if (!isSafeRelativePath(relativePath)) {
      fail(`${name} manifest has unsafe path: ${String(relativePath)}`);
      continue;
    }
    if (!relativePath.endsWith(".json")) {
      fail(`${name} manifest path must name JSON: ${relativePath}`);
    }
    if (listed.has(relativePath)) {
      fail(`${name} manifest has duplicate path: ${relativePath}`);
    }
    listed.add(relativePath);
    if (!fs.existsSync(path.join(directory, relativePath))) {
      fail(`${name} manifest missing file: ${relativePath}`);
    }
  }

  const actual = new Set(
    fs.readdirSync(directory, { recursive: true, withFileTypes: true })
      .filter((entry) => entry.isFile() && entry.name.endsWith(".json"))
      .map((entry) => path.relative(directory, path.join(entry.parentPath, entry.name))),
  );
  actual.delete(excludedFile);

  for (const relativePath of actual) {
    if (!listed.has(relativePath)) {
      fail(`${name} manifest has unlisted file: ${relativePath}`);
    }
  }
  for (const relativePath of listed) {
    if (!actual.has(relativePath)) {
      fail(`${name} manifest lists absent file: ${relativePath}`);
    }
  }
  return [...listed];
}

function hasClosedRoot(schema) {
  if (schema.additionalProperties === false) return true;
  return Array.isArray(schema.oneOf)
    && schema.oneOf.length > 0
    && schema.oneOf.every((variant) => variant?.additionalProperties === false);
}

const manifest = readJson("Shared/Contracts/contract-manifest.json");
const fixtureManifest = readJson("Shared/TestVectors/fixture-manifest.json");

if (manifest) {
  if (manifest.contractVersion !== 1 || manifest.status !== "frozen") {
    fail("contract manifest must freeze version 1");
  }
  if (manifest.fixtureManifest !== "../TestVectors/fixture-manifest.json") {
    fail("contract manifest must reference the canonical fixture manifest");
  }
}
if (fixtureManifest && manifest && fixtureManifest.contractVersion !== manifest.contractVersion) {
  fail("fixture manifest version differs from contract version");
}

const schemaPaths = validateInventory(
  "schema",
  manifest?.schemas,
  contractsDirectory,
  "contract-manifest.json",
);
const fixturePaths = validateInventory(
  "fixture",
  fixtureManifest?.fixtures,
  fixturesDirectory,
  "fixture-manifest.json",
);

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
for (const relativePath of schemaPaths) {
  const schema = readJson(path.join("Shared/Contracts", relativePath));
  if (!schema) continue;
  if (schema.$schema !== "https://json-schema.org/draft/2020-12/schema") {
    fail(`${relativePath}: wrong JSON Schema dialect`);
  }
  if (!hasClosedRoot(schema)) {
    fail(`${relativePath}: root must be closed or use closed oneOf variants`);
  }
  if (typeof schema.$id !== "string" || schema.$id.length === 0 || typeof schema.title !== "string" || schema.title.length === 0) {
    fail(`${relativePath}: missing stable schema values`);
  }
  const expectedSchema = expectedSchemas.get(relativePath);
  if (!expectedSchema || expectedSchema.id !== schema.$id || expectedSchema.title !== schema.title) {
    fail(`${relativePath}: unexpected stable schema values`);
  }
}
for (const relativePath of expectedSchemas.keys()) {
  if (!schemaPaths.includes(relativePath)) {
    fail(`schema manifest missing stable schema: ${relativePath}`);
  }
}

const fixtureIds = new Set();
for (const relativePath of fixturePaths) {
  const fixture = readJson(path.join("Shared/TestVectors", relativePath));
  if (!fixture) continue;
  if (fixture.contractVersion !== manifest?.contractVersion) {
    fail(`${relativePath}: contractVersion mismatch`);
  }
  if (typeof fixture.fixtureId !== "string" || fixture.fixtureId.length === 0) {
    fail(`${relativePath}: missing fixtureId`);
  } else if (fixtureIds.has(fixture.fixtureId)) {
    fail(`${relativePath}: duplicate fixtureId: ${fixture.fixtureId}`);
  } else {
    fixtureIds.add(fixture.fixtureId);
  }
  if (typeof fixture.category !== "string" || fixture.category.length === 0) {
    fail(`${relativePath}: missing category`);
  }
}

const sharedText = fs
  .readdirSync(path.join(root, "Shared"), { recursive: true, withFileTypes: true })
  .filter((entry) => entry.isFile())
  .map((entry) => fs.readFileSync(path.join(entry.parentPath, entry.name), "utf8"))
  .join("\n");
for (const pattern of [
  /authorization\s*[:=]/i,
  /(?:["']?(?:api[_ -]?key|access[_ -]?token|secret|token)["']?)\s*[:=]\s*["']?[A-Za-z0-9._~+/-]{12,}/i,
  /sk-[a-z0-9_-]{16,}/i,
  /(?:^|["'\s])\/(?:Users|Volumes|private|var|tmp)\//m,
  /(?:^|["'\s])[A-Za-z]:\\(?:Users|Windows|ProgramData)\\/m,
]) {
  if (pattern.test(sharedText)) fail(`forbidden content: ${pattern}`);
}

if (failures.length > 0) {
  process.stderr.write(`${failures.join("\n")}\n`);
  process.exit(1);
}
process.stdout.write(
  `contract v${manifest.contractVersion}: ${schemaPaths.length} schemas, ${fixturePaths.length} fixtures\n`,
);
