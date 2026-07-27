import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import test from "node:test";

const testDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(testDirectory, "..", "..", "..");
const generator = resolve(
  repositoryRoot,
  "Windows/tools/generate-language-profile.mjs",
);
const fakeFetch = resolve(
  testDirectory,
  "fixtures/language-profile-fake-fetch.mjs",
);

function runWithFakeFetch(mode, extraArguments = []) {
  return spawnSync(
    process.execPath,
    [
      "--import",
      fakeFetch,
      generator,
      "--max-source-bytes",
      "4",
      "--timeout-ms",
      "20",
      ...extraArguments,
    ],
    {
      cwd: repositoryRoot,
      encoding: "utf8",
      env: {
        ...process.env,
        EMKE_FAKE_FETCH_MODE: mode,
      },
      timeout: 2_000,
    },
  );
}

function diagnostic(result) {
  return `${result.stdout ?? ""}${result.stderr ?? ""}`;
}

test("download rejects a redirect outside the fixed Gutenberg HTTPS host", () => {
  const result = runWithFakeFetch("untrusted-redirect");

  assert.notEqual(result.status, 0);
  assert.match(diagnostic(result), /not allowed/i);
});

test("download follows a bounded redirect on the fixed Gutenberg HTTPS host", () => {
  const result = runWithFakeFetch("allowed-redirect");

  assert.notEqual(result.status, 0);
  assert.match(diagnostic(result), /corpus SHA256 mismatch/i);
  assert.doesNotMatch(diagnostic(result), /HTTP 302/i);
});

test("download rejects Content-Length before reading an oversized body", () => {
  const result = runWithFakeFetch("large-content-length");

  assert.notEqual(result.status, 0);
  assert.match(diagnostic(result), /content-length.*exceeds/i);
});

test("download aborts a stream as soon as the hard byte limit is crossed", () => {
  const result = runWithFakeFetch("large-stream");

  assert.notEqual(result.status, 0);
  assert.match(diagnostic(result), /stream.*exceeds/i);
});

test("download has an AbortController deadline", () => {
  const result = runWithFakeFetch("timeout");

  assert.notEqual(result.status, 0);
  assert.equal(result.signal, null);
  assert.match(diagnostic(result), /timed out/i);
});

test("source-dir rejects oversized files before reading them", async () => {
  const sourceDirectory = await mkdtemp(
    resolve(tmpdir(), "emke-profile-sources-"),
  );
  try {
    await writeFile(resolve(sourceDirectory, "zh.txt"), "12345");
    const result = spawnSync(
      process.execPath,
      [
        generator,
        "--source-dir",
        sourceDirectory,
        "--max-source-bytes",
        "4",
      ],
      {
        cwd: repositoryRoot,
        encoding: "utf8",
        timeout: 2_000,
      },
    );

    assert.notEqual(result.status, 0);
    assert.match(diagnostic(result), /source-dir.*exceeds/i);
  } finally {
    await rm(sourceDirectory, { recursive: true, force: true });
  }
});

test("generated model features use Unicode code-point ordinal order", async () => {
  const model = JSON.parse(
    await readFile(
      resolve(
        repositoryRoot,
        "Windows/src/EMKE.Routing/Resources/language-profile-v1.json",
      ),
      "utf8",
    ),
  );

  for (const language of ["zh", "en", "de"]) {
    const features = Object.keys(model.profiles[language]);
    for (let index = 1; index < features.length; index += 1) {
      assert.ok(
        compareCodePoints(features[index - 1], features[index]) <= 0,
        `${language} feature order is not ordinal at ${features[index - 1]} / ${features[index]}`,
      );
    }
  }
});

function compareCodePoints(left, right) {
  const leftPoints = [...left].map((character) => character.codePointAt(0));
  const rightPoints = [...right].map((character) => character.codePointAt(0));
  const count = Math.min(leftPoints.length, rightPoints.length);
  for (let index = 0; index < count; index += 1) {
    if (leftPoints[index] !== rightPoints[index]) {
      return leftPoints[index] - rightPoints[index];
    }
  }

  return leftPoints.length - rightPoints.length;
}
