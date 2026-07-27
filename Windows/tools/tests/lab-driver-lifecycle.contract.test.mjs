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
const integrationTestPath = path.join(
  testDirectory,
  "lab-driver-lifecycle.integration.test.ps1",
);
const validationTestPath = path.join(
  testDirectory,
  "lab-driver-lifecycle.validation.test.ps1",
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

  const firstFunction = source.search(/^function\s+/m);
  const strictMode = source.search(/Set-StrictMode\s+-Version\s+Latest/);
  const dotSourceGuard = source.search(
    /\$MyInvocation\.InvocationName\s+-ceq\s+["']\.["']/,
  );
  assert.ok(firstFunction > 0, `${operation} script has no functions`);
  assert.ok(
    dotSourceGuard >= 0 &&
      dotSourceGuard < strictMode &&
      dotSourceGuard < firstFunction,
    `${operation} script must reject dot-source before changing caller state or defining functions`,
  );

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
  assert.match(source, /DefaultParameterSetName\s*=\s*["']Install["']/);
  assert.match(source, /ParameterSetName\s*=\s*["']Digest["']/);
  assert.match(source, /\[switch\]\$PrintPackageSha256/);
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
  assert.match(source, /Win32_PnPEntity/);
  assert.match(source, /ConfigManagerErrorCode/);
  assert.match(source, /\$discoveryStatuses\.Count\s+-ne\s+1/);
  assert.match(source, /\$resultStatuses\.Count\s+-ne\s+1/);
  assert.match(
    source,
    /Observed\/Generated package SHA-256[^]*not a trusted expected value/i,
  );
  assert.match(
    source,
    /Observed package SHA-256[^]*Test-FixedSha256Equal/,
  );
  const digestDispatch = source.match(
    /if\s*\(\$PSCmdlet\.ParameterSetName\s+-ceq\s+["']Digest["']\)\s*{(?<body>[^]*?)\n}\s*\nInvoke-InstallTestDriver/,
  );
  assert.ok(digestDispatch, "digest parameter set must have an isolated dispatch");
  assert.match(digestDispatch.groups.body, /Assert-SupportedWindowsHost/);
  assert.match(digestDispatch.groups.body, /Get-StrictDriverPackage/);
  assert.match(digestDispatch.groups.body, /Get-DriverPackageSha256/);
  assert.doesNotMatch(
    digestDispatch.groups.body,
    /ConfirmInstall|Assert-LabMachinePrerequisites|Invoke-DriverPackageVerifier|Get-CatalogSignatureMetadata|Invoke-PnpUtilInstall|Invoke-SmokeEnumeration/,
  );
});

test("uninstall lifecycle resolves one exact published INF from PnP metadata", async () => {
  const source = await readRequired(uninstallPath);
  assertCommonSafetyContract(source, "uninstall");

  assert.match(source, /\[switch\]\$ConfirmUninstall/);
  assert.match(source, /ROOT\\EMKEVIRTUALAUDIO/);
  assert.match(source, /Win32_PnPEntity/);
  assert.match(source, /Win32_PnPSignedDriver/);
  assert.match(source, /\$matching\.Count\s+-ne\s+1/);
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

test("Windows behavior suite declares mutation-free lifecycle safety cases", async () => {
  const source = [
    await readRequired(behaviorTestPath),
    await readRequired(validationTestPath),
  ].join("\n");
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
    "valid catalog without signer",
    "untrusted catalog statuses",
    "install devnode validation",
    "published INF exact CIM mapping",
    "published INF zero and multiple matches",
    "uninstall unsupported OS build",
    "uninstall non-administrator",
    "post-uninstall devnode still present",
    "smoke duplicate status",
    "smoke contradictory status",
    "smoke raw detail remains suppressed",
    "install orchestrator exact process boundary",
    "uninstall orchestrator exact process boundary",
    "digest mismatch reports observed digest",
  ];
  for (const name of requiredCases) {
    assert.match(source, new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
  assert.match(source, /Invoke-CapturedProcess/);
  assert.match(source, /throw\s+["']REAL PROCESS EXECUTION IS FORBIDDEN/);
  assert.doesNotMatch(source, /Start-Process\s+pnputil/i);
  assert.doesNotMatch(source, /&\s*pnputil/i);
  assert.doesNotMatch(source, /every (?:destructive )?gate/i);
});

test("PowerShell integration suite exercises safe real process and invocation seams", async () => {
  const source = await readRequired(integrationTestPath);
  const requiredCases = [
    "install dot-source leaves no lifecycle functions",
    "uninstall dot-source leaves no lifecycle functions",
    "digest mode is reproducible without install prerequisites",
    "digest mode rejects zero or multiple INF files",
    "digest mode requires one flat INF SYS CAT package",
    "ArgumentList preserves hostile-looking arguments",
  ];
  for (const name of requiredCases) {
    assert.match(source, new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
  assert.match(source, /\.\s+\$Path\s+@Parameters/);
  assert.match(source, /"-File"/);
  assert.match(source, /Invoke-CapturedProcess/);
  assert.match(source, /WriteAllText/);
  assert.doesNotMatch(source, /["']\/(?:add-driver|delete-driver)["']/i);
});
