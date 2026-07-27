import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const toolsDirectory = path.resolve(testDirectory, "..");
const installPath = path.join(toolsDirectory, "install-test-driver.ps1");
const uninstallPath = path.join(toolsDirectory, "uninstall-test-driver.ps1");
const behaviorTestPath = path.join(
  testDirectory,
  "lab-driver-lifecycle.behavior.test.ps1",
);

async function readRequired(filePath) {
  try {
    return await readFile(filePath, "utf8");
  } catch (error) {
    assert.fail(`required lifecycle file is missing: ${filePath}\n${error.message}`);
  }
}

function assertCommonSafetyContract(source, operation) {
  assert.match(source, /Set-StrictMode\s+-Version\s+Latest/);
  assert.match(source, /\$ErrorActionPreference\s*=\s*["']Stop["']/);
  assert.match(source, /\$PSVersionTable\.PSVersion\.Major\s+-ne\s+7/);
  assert.match(source, /\$IsWindows/);
  assert.match(source, /26200/);
  assert.match(source, /WindowsIdentity/);
  assert.match(source, /WindowsPrincipal/);
  assert.match(source, /pnputil\.exe/);
  assert.match(source, /function\s+Resolve-SystemPnpUtil/);
  assert.match(source, /SpecialFolder\]::System/);

  const forbidden = [
    /\bNew-SelfSignedCertificate\b/i,
    /\bImport-(?:Certificate|PfxCertificate)\b/i,
    /\bcertutil(?:\.exe)?\b/i,
    /\bsigntool(?:\.exe)?\b/i,
    /\bbcdedit(?:\.exe)?\b/i,
    /\btestsigning\b/i,
    /\bDeviceInterfacePath\b/i,
    /\bIMMDevice\b/i,
    /\bendpoint[_ ]?id\b/i,
  ];
  for (const pattern of forbidden) {
    assert.doesNotMatch(
      source,
      pattern,
      `${operation} script contains forbidden capability or endpoint identifier text`,
    );
  }
}

test("install lifecycle is fail-closed before the one exact pnputil install", async () => {
  const source = await readRequired(installPath);
  assertCommonSafetyContract(source, "install");

  assert.match(source, /\[string\]\$PackagePath/);
  assert.match(source, /\[string\]\$ExpectedPackageSha256/);
  assert.match(source, /\[string\]\$SmokePath/);
  assert.match(source, /\[switch\]\$ConfirmInstall/);
  assert.match(source, /verify-driver-package\.ps1/);
  assert.match(source, /Get-AuthenticodeSignature/);
  assert.match(source, /\.Status\s+-cne\s+["']Valid["']/);
  assert.match(source, /\.SignerCertificate/);
  assert.match(source, /function\s+Resolve-RequiredFile/);
  assert.match(source, /Resolve-RequiredFile\s+-Path\s+\$SmokePath/);
  assert.match(source, /FixedTimeEquals/);
  assert.match(source, /DriverVer/);
  assert.match(source, /ROOT\\EMKEVIRTUALAUDIO/);
  assert.match(source, /\/add-driver/);
  assert.match(source, /\/install/);
  assert.match(source, /Invoke-PnpUtilInstall\s+-InfPath\s+\$package\.Inf\.FullName/);
  assert.match(source, /--scenario["']?\s*,\s*["']enumerate/);
  assert.match(source, /discovery=ready/);
  assert.match(source, /result=ready/);
  assert.match(source, /driverMissing/);
  assert.match(source, /Win32_PnPEntity/);
  assert.match(source, /ConfigManagerErrorCode/);
});

test("uninstall lifecycle resolves one exact published INF from PnP metadata", async () => {
  const source = await readRequired(uninstallPath);
  assertCommonSafetyContract(source, "uninstall");

  assert.match(source, /\[switch\]\$ConfirmUninstall/);
  assert.match(source, /ROOT\\EMKEVIRTUALAUDIO/);
  assert.match(source, /Win32_PnPEntity/);
  assert.match(source, /Win32_PnPSignedDriver/);
  assert.match(source, /\^oem\[0-9\]\+\\\.inf\$/);
  assert.match(source, /\/delete-driver/);
  assert.match(source, /\/uninstall/);
  assert.match(source, /\/force/);
  assert.match(
    source,
    /Invoke-PnpUtilUninstall\s+-PublishedInf\s+\$publishedInf/,
  );
  assert.doesNotMatch(source, /pnputil[^]*\*/i);
  assert.doesNotMatch(source, /Where-Object[^]*(?:FriendlyName|Description)/i);
});

test("Windows behavior suite covers every destructive gate without real mutation", async () => {
  const source = await readRequired(behaviorTestPath);
  const requiredCases = [
    "missing install confirmation",
    "package digest mismatch",
    "invalid or unsigned catalog",
    "unsupported OS build",
    "non-administrator",
    "space and metacharacter INF path",
    "one exact install command",
    "published INF allow-list",
    "smoke nonzero exit",
    "smoke missing discovery",
    "smoke missing result",
    "smoke driverMissing",
    "missing uninstall confirmation",
    "one exact uninstall command",
  ];
  for (const name of requiredCases) {
    assert.match(source, new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
  assert.match(source, /Invoke-CapturedProcess/);
  assert.match(source, /throw\s+["']REAL PROCESS EXECUTION IS FORBIDDEN/);
  assert.doesNotMatch(source, /Start-Process\s+pnputil/i);
  assert.doesNotMatch(source, /&\s*pnputil/i);
});
