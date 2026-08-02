import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";

const repositoryRoot = path.resolve(import.meta.dirname, "..", "..", "..");
const creatorPath = path.join(
  repositoryRoot,
  "Windows",
  "tools",
  "create-driver-submission.ps1",
);
const workflowPath = path.join(
  repositoryRoot,
  ".github",
  "workflows",
  "windows-audio.yml",
);
const evidencePath = path.join(
  repositoryRoot,
  "docs",
  "quality",
  "windows-driver-submission-evidence.md",
);

test("submission creator exposes immutable package and validation entry points", async () => {
  assert.equal(
    existsSync(creatorPath),
    true,
    "create-driver-submission.ps1 must exist",
  );
  const source = await readFile(creatorPath, "utf8");
  assert.match(source, /PackageDirectory/);
  assert.match(source, /OutputDirectory/);
  assert.match(source, /SourceCommit/);
  assert.match(source, /ArchivePath/);
  assert.match(source, /EvidencePath/);
  assert.match(source, /ValidateOnly/);
  assert.match(source, /verify-driver-package\.ps1/);
  assert.match(source, /driver-submission\.json/);
  assert.match(source, /1980-01-01/);
  assert.doesNotMatch(source, /certutil|signtool|\.pfx|private.?key/i);
});

test("Windows Audio builds, verifies, creates, validates, and uploads one submission artifact", async () => {
  const workflow = await readFile(workflowPath, "utf8");
  const job = workflow.match(
    /  driver-build-proof:(?<body>[^]*?)(?:\n  [a-z][a-z0-9-]+:|\s*$)/,
  );
  assert.ok(job, "driver-build-proof job is missing");
  const body = job.groups.body;
  const build = body.indexOf("Windows/tools/build-driver.ps1");
  const verify = body.indexOf("Windows/tools/verify-driver-package.ps1");
  const create = body.indexOf("Windows/tools/create-driver-submission.ps1");
  const validate = body.indexOf(
    "Windows/tools/tests/driver-submission.validation.test.ps1",
  );
  assert.ok(build >= 0, "driver build invocation is missing");
  assert.ok(build < verify, "package verification must follow the build");
  assert.ok(verify < create, "submission creation must follow verification");
  assert.ok(create < validate, "submission validation must follow creation");
  assert.match(body, /artifacts\/windows-driver-submission\.zip/);
  assert.match(body, /artifacts\/driver-submission-evidence\.json/);
  assert.match(body, /uses:\s*actions\/upload-artifact@v4/);
  assert.match(body, /compression-level:\s*0/);
});

test("submission evidence keeps Microsoft signing outside the hosted build", async () => {
  assert.equal(
    existsSync(evidencePath),
    true,
    "windows-driver-submission-evidence.md must exist",
  );
  const evidence = await readFile(evidencePath, "utf8");
  assert.match(evidence, /Hardware Dev Center/i);
  assert.match(evidence, /attestation|WHQL/i);
  assert.match(evidence, /external|outside/i);
  assert.match(evidence, /unsigned/i);
  assert.match(evidence, /driver-submission-evidence\.json/);
  assert.doesNotMatch(evidence, /test certificate included|private key included/i);
});
