import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";

const repositoryRoot = path.resolve(import.meta.dirname, "..", "..", "..");
const importerPath = path.join(
  repositoryRoot,
  "Windows",
  "tools",
  "import-microsoft-signed-driver.ps1",
);
const platformPath = path.join(
  repositoryRoot,
  "Windows",
  "src",
  "EMKE.Platform",
  "Driver",
  "WindowsDriverManager.cs",
);
const productionFactoryPath = path.join(
  repositoryRoot,
  "Windows",
  "src",
  "EMKE.Windows.App",
  "Bootstrap",
  "ProductionAppAdapterFactory.cs",
);
const workflowPath = path.join(
  repositoryRoot,
  ".github",
  "workflows",
  "windows-audio.yml",
);

test("Microsoft-signed driver importer exposes a fail-closed promotion gate", async () => {
  assert.equal(existsSync(importerPath), true, "driver importer must exist");
  const source = await readFile(importerPath, "utf8");
  assert.match(source, /SubmissionManifest/);
  assert.match(source, /ReturnedPackageDirectory/);
  assert.match(source, /OutputDirectory/);
  assert.match(source, /EvidencePath/);
  assert.match(source, /PortalSubmissionId/);
  assert.match(source, /PortalStatus/);
  assert.match(source, /Get-AuthenticodeSignature/);
  assert.match(source, /X509Chain/);
  assert.match(source, /signtool(?:\.exe)?/i);
  assert.match(source, /\/kp/);
  assert.match(source, /CatalogMembership/);
  assert.match(source, /\[IO\.FileAttributes\]::ReparsePoint/);
  assert.match(source, /Assert-DisjointPromotionPaths/);
  assert.match(source, /Assert-NoReparsePathComponents/);
  assert.match(source, /driver-snapshot/i);
  assert.match(source, /evidence-staging/i);
  assert.match(source, /snapshotBaselineHashes/);
  assert.match(source, /promotionState/);
  assert.match(source, /"pending"/);
  assert.match(source, /"committed"/);
  assert.match(source, /transactionId/);
  assert.match(source, /EvidencePublished/);
  assert.match(source, /EvidenceCommitted/);
  assert.match(source, /Commit-PromotionEvidence/);
  assert.match(
    source,
    /\[IO\.File\]::Move\([^]*\$StagingPath[^]*\$FinalPath[^]*\$true/,
  );
  assert.doesNotMatch(source, /\[IO\.File\]::Replace/);
  assert.doesNotMatch(source, /expectedEvidenceHash/);
  assert.match(
    source,
    /Assert-CatalogMembership -PackageDirectory \$snapshotPath/,
  );
  assert.match(source, /Get-MicrosoftCatalogTrustEvidence[^]*\$catalogPath/);
  assert.doesNotMatch(source, /\$returnedFiles/);
  assert.match(source, /Flush\(\$true\)/);
  assert.match(source, /X509RevocationMode\]::Online/);
  assert.match(source, /X509RevocationFlag\]::EntireChain/);
  assert.doesNotMatch(
    source,
    /TESTSIGNING|bcdedit|disableintegritychecks|nointegritychecks/i,
  );
});

test("production driver trust is an injected Microsoft-only policy", async () => {
  const [platform, factory] = await Promise.all([
    readFile(platformPath, "utf8"),
    readFile(productionFactoryPath, "utf8"),
  ]);
  assert.match(platform, /interface IDriverCatalogTrustPolicy/);
  assert.match(platform, /record DriverCatalogTrustDecision/);
  assert.match(platform, /class MicrosoftDriverCatalogTrustPolicy/);
  assert.match(platform, /bool kernelPolicyValid/);
  assert.match(platform, /bool catalogMembersValid/);
  assert.match(platform, /X509RevocationMode\.Online/);
  assert.match(platform, /X509RevocationFlag\.EntireChain/);
  assert.match(platform, /WtdRevokeWholeChain/);
  assert.match(platform, /WtdRevocationCheckChain/);
  assert.doesNotMatch(platform, /X509RevocationMode\.NoCheck/);
  assert.doesNotMatch(platform, /WtdRevocationCheckNone/);
  assert.doesNotMatch(platform, /WtdCacheOnlyUrlRetrieval/);
  assert.doesNotMatch(platform, /CN=EMKE Internal Test/);
  assert.match(factory, /new WindowsDriverSnapshotSource\(\)/);
  assert.doesNotMatch(factory, /TestDriverCatalogTrustPolicy/);
});

test("Windows Audio runs importer contract and mutation validation", async () => {
  const workflow = await readFile(workflowPath, "utf8");
  const contract = workflow.indexOf(
    "Windows/tools/tests/microsoft-signed-driver.contract.test.mjs",
  );
  const build = workflow.indexOf("Windows/tools/build-driver.ps1");
  const validation = workflow.indexOf(
    "Windows/tools/tests/microsoft-signed-driver.validation.test.ps1",
  );
  assert.ok(contract >= 0, "importer contract gate is missing");
  assert.ok(build >= 0, "driver build gate is missing");
  assert.ok(validation > build, "importer mutation validation must use built bytes");
});
