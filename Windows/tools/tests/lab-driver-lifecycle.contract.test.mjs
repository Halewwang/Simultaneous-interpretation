import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const toolsDirectory = path.resolve(testDirectory, "..");
const repositoryRoot = path.resolve(testDirectory, "..", "..", "..");
const installPath = path.join(toolsDirectory, "install-test-driver.ps1");
const uninstallPath = path.join(toolsDirectory, "uninstall-test-driver.ps1");
const toolchainPath = path.join(toolsDirectory, "verify-toolchain.ps1");
const workflowPath = path.join(
  repositoryRoot,
  ".github",
  "workflows",
  "windows-audio.yml",
);
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

async function readRequired(filePath, readText = readFile) {
  try {
    const source = await readText(filePath, "utf8");
    return source.replace(/\r\n?/g, "\n");
  } catch (error) {
    assert.fail(`required lifecycle file is missing: ${filePath}\n${error.message}`);
  }
}

function assertCommonSafetyContract(source, operation) {
  assert.match(source, /Set-StrictMode\s+-Version\s+Latest/);
  assert.match(source, /\$ErrorActionPreference\s*=\s*["']Stop["']/);
  assert.match(source, /\$PSVersionTable\.PSVersion\.Major\s+-ne\s+7/);
  assert.match(source, /\$IsWindows/);
  assert.match(source, /resolve-version\.ps1/);
  assert.match(source, /MinimumWindowsBuild/);
  assert.match(source, /DriverPackageVersion/);
  assert.match(source, /DriverAbiVersion/);
  assert.match(source, /DriverHardwareId/);
  assert.match(source, /Architecture/);
  assert.match(source, /ProductType/);
  assert.doesNotMatch(source, /\b26200\b|\b1\.0\.0\.1\b/);
  assert.doesNotMatch(
    source,
    /\$script:(?:MinimumWindowsBuild|TargetHardwareId)\b/,
  );
  assert.match(source, /WindowsIdentity/);
  assert.match(source, /WindowsPrincipal/);
  assert.match(source, /pnputil\.exe/);
  assert.match(source, /function\s+Resolve-SystemPnpUtil/);
  assert.match(source, /SpecialFolder\]::System/);
  assert.match(source, /\[int\]\$TimeoutSeconds/);
  assert.match(source, /\.Kill\(\$true\)/);
  assert.match(source, /\.WaitForExit\(5000\)/);
  assert.match(source, /function\s+Invoke-BoundedPoll/);
  assert.match(source, /state uncertain/);
  assert.match(source, /read-only inventory/);

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
    /\bDisable-ComputerRestore\b/i,
    /\b(?:Secure\s*Boot|SecureBoot)\b[^\n]*(?:disable|off)/i,
    /\bMemory\s+Integrity\b[^\n]*(?:disable|off)/i,
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

test("toolchain resolves one trusted workstation x64 release boundary", async () => {
  const source = await readRequired(toolchainPath);
  assert.match(source, /resolve-version\.ps1/);
  assert.match(source, /MinimumWindowsBuild/);
  assert.match(source, /Architecture/);
  assert.match(source, /ProductType/);
  assert.doesNotMatch(source, /\b26200\b|\b1\.0\.0\.1\b/);
});

test("install lifecycle input is line-ending stable and fail-closed", async () => {
  assert.equal(
    await readRequired(
      "mixed-endings.ps1",
      async () => "alpha\r\nbeta\rgamma\n",
    ),
    "alpha\nbeta\ngamma\n",
    "required lifecycle sources must normalize CRLF and CR to LF",
  );

  const source = await readRequired(installPath);
  assertCommonSafetyContract(source, "install");

  assert.match(source, /\[string\]\$PackagePath/);
  assert.match(source, /\[string\]\$ExpectedPackageSha256/);
  assert.match(source, /\[string\]\$SmokePath/);
  assert.match(source, /\[string\]\$ExpectedSmokeSha256/);
  assert.match(
    source,
    /ExpectedSmokeSha256[^]*ValidatePattern\(["']\^\[0-9A-Fa-f\]\{64\}\$["']\)|ValidatePattern\(["']\^\[0-9A-Fa-f\]\{64\}\$["']\)[^]*ExpectedSmokeSha256/,
  );
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
  assert.match(source, /DriverHardwareId/);
  assert.match(source, /\/add-driver/);
  assert.match(source, /\/install/);
  assert.match(
    source,
    /Arguments\s+@\(["']\/add-driver["'][^]*?-TimeoutSeconds\s+120/,
  );
  assert.match(
    source,
    /Arguments\s+@\(["']--scenario["'][^]*?-TimeoutSeconds\s+15/,
  );
  assert.match(
    source,
    /Invoke-CreateAndBindRootDevnode[^]*-StagedInf\s+\$package\.Inf/,
  );
  assert.match(source, /SetupDiGetINFClassW/);
  assert.match(source, /SetupDiCreateDeviceInfoList/);
  assert.match(source, /SetupDiCreateDeviceInfoW/);
  assert.match(source, /SetupDiSetDeviceRegistryPropertyW/);
  assert.match(source, /SetupDiSetClassInstallParamsW/);
  assert.match(source, /SetupDiCallClassInstaller/);
  assert.match(source, /DifRegisterDevice/);
  assert.match(source, /DiRemoveDeviceGlobal/);
  assert.match(source, /class\s+RootDevnodeCreateException/);
  assert.match(source, /class\s+RootDevnodeRegistrationTransaction/);
  assert.match(source, /bool\s+StateUncertain\s*{\s*get;/);
  assert.match(source, /bool\s+RollbackCompleted\s*{\s*get;/);
  assert.match(source, /string\s+InstanceId\s*{\s*get;/);
  assert.match(
    source,
    /GetRootDeviceName\s*\(\s*hardwareId\s*\)/,
    "DICD_GENERATE_ID DeviceName must be derived from the hardware ID",
  );
  assert.match(
    source,
    /string\s+deviceName\s*=\s*GetRootDeviceName\s*\(\s*hardwareId\s*\)/,
  );
  assert.doesNotMatch(
    source,
    /SetupDiCreateDeviceInfoW\([^]*?className\.ToString\(\)/,
    "INF class name must never be passed as the root DeviceName",
  );
  const createMethod = source.match(
    /public static string Create\([^]*?(?<body>\{[^]*?)\n        public static void RemoveExact/,
  );
  assert.ok(createMethod, "embedded SetupAPI Create method is missing");
  const createBody = createMethod.groups.body;
  assert.match(createBody, /RootDevnodeRegistrationTransaction\.Complete/);
  assert.match(createBody, /GetDeviceInstanceIdFromInfoElement/);
  assert.match(createBody, /RemoveRegisteredDeviceFromInfoElement/);
  const transactionMethod = source.match(
    /class RootDevnodeRegistrationTransaction[^]*?public static string Complete\([^]*?(?<body>\{[^]*?)\n    }\n\n    public static class RootDevnodeSetupApi/,
  );
  assert.ok(
    transactionMethod,
    "embedded post-registration transaction is missing",
  );
  const transactionBody = transactionMethod.groups.body;
  assert.match(transactionBody, /bool\s+registered\s*=\s*false/);
  assert.match(transactionBody, /registered\s*=\s*true/);
  assert.match(transactionBody, /readRegisteredInstanceId\(\)/);
  assert.match(transactionBody, /catch\s*\(\s*Exception\s+originalFailure\s*\)/);
  assert.match(transactionBody, /rollback\(\)/);
  assert.match(transactionBody, /rollbackCompleted:\s*true/);
  assert.match(transactionBody, /stateUncertain:\s*true/);
  assert.match(source, /function\s+Get-SystemStagingBase/);
  assert.match(source, /function\s+Assert-ProtectedStagingChain/);
  assert.match(
    source,
    /Assert-ProtectedStagingChain\s+-StagingPath\s+\$resolvedRoot/,
  );
  assert.match(source, /Get-WindowsDriver[^]*-Online[^]*-Driver/);
  assert.match(source, /DriverStore[^]*FileRepository/);
  assert.match(source, /Get-InstalledDriverStorePackage/);
  assert.match(
    source,
    /Get-DriverPackageSha256\s+-Package\s+\$installedPackage/,
  );
  assert.match(
    source,
    /Test-FixedSha256Equal[^]*ExpectedPackageSha256[^]*installedPackageSha256/,
  );
  assert.doesNotMatch(
    source,
    /AccessControlSections\]::All/,
    "staging ACL writes must not request absent SACL/audit privileges",
  );
  assert.match(source, /AccessControlSections\]::Owner/);
  assert.match(source, /AccessControlSections\]::Group/);
  assert.match(source, /AccessControlSections\]::Access/);
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
  assert.match(source, /DriverHardwareId/);
  assert.match(source, /Win32_PnPEntity/);
  assert.match(source, /Win32_PnPSignedDriver/);
  assert.match(source, /\$matching\.Count\s+-ne\s+1/);
  assert.match(source, /\^oem\[0-9\]\+\\\.inf\$/);
  assert.match(source, /\/delete-driver/);
  assert.doesNotMatch(source, /["']\/uninstall["']/);
  assert.doesNotMatch(source, /["']\/force["']/);
  assert.match(
    source,
    /Arguments\s+@\(\s*["']\/delete-driver["']\s*,\s*\$PublishedInf\s*\)/,
  );
  assert.match(
    source,
    /package remains|package state is unproven/i,
  );
  assert.match(
    source,
    /Invoke-PnpUtilRemoveDevice[^]*-InstanceId\s+\$devnode\.PNPDeviceID[^]*-HardwareId\s+\$hardwareId/,
  );
  assert.match(
    source,
    /Wait-TargetDevnodeAbsent[^]*-ExpectedInstanceId\s+\$devnode\.PNPDeviceID[^]*-HardwareId\s+\$hardwareId/,
  );
  assert.match(
    source,
    /Invoke-PnpUtilDeleteDriver\s+-PublishedInf\s+\$publishedInf/,
  );
  const removeDevice = source.search(
    /Invoke-PnpUtilRemoveDevice[^]*?-InstanceId\s+\$devnode\.PNPDeviceID/,
  );
  const deleteDriver = source.search(
    /Invoke-PnpUtilDeleteDriver\s+-PublishedInf/,
  );
  assert.ok(
    removeDevice >= 0 && deleteDriver > removeDevice,
    "uninstall must remove the exact devnode before deleting its published INF",
  );
  assert.doesNotMatch(source, /Arguments\s+@\([^)]*\*/i);
  assert.doesNotMatch(source, /\$_\.(?:FriendlyName|Description)/i);
  assert.equal(
    [...source.matchAll(/-TimeoutSeconds\s+120/g)].length,
    2,
    "remove-device and delete-driver must each use the bounded PnP timeout",
  );
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
    "Windows 10 workstation x64 floor matrix",
    "non-workstation and non-x64 hosts",
    "non-administrator",
    "space and metacharacter INF path",
    "one exact install command",
    "published INF allow-list",
    "smoke nonzero exit",
    "smoke missing discovery",
    "smoke missing result",
    "smoke driverMissing",
    "missing uninstall confirmation",
    "one exact remove-device command",
    "one exact delete-driver command",
    "valid catalog without signer",
    "untrusted catalog statuses",
    "install devnode validation",
    "published INF exact CIM mapping",
    "published INF zero and multiple matches",
    "uninstall unsupported OS build",
    "uninstall non-administrator",
    "post-uninstall devnode still present",
    "shared published INF blocks every deletion",
    "smoke duplicate status",
    "smoke contradictory status",
    "smoke raw detail remains suppressed",
    "install orchestrator exact process boundary",
    "uninstall orchestrator exact process boundary",
    "digest mismatch reports observed digest",
    "local input rejects UNC and reparse paths",
    "protected staging ACL contract and cleanup guard",
    "staged inputs detect package and smoke replacement",
    "expected Smoke digest blocks ready-looking replacement",
    "install orchestrator uses only protected staged copies",
    "actual INF Models parser rejects inactive-section bait",
    "catalog certificate digest is exactly SHA256",
    "installed package identity matches exact devnode",
    "embedded SetupAPI helper compiles without mutation",
    "nested typed create exception exposes machine state",
    "pre-register instance ID failure performs no mutation",
    "post-register failure reports exact rollback completed",
    "post-register rollback failure permits only read-only inventory",
    "root create bind package identity state machine",
    "preexisting target blocks root creation",
    "bind failure reports partial state and exact cleanup",
    "captured process timeout kills and reports uncertain state",
    "bounded polling reaches completion and timeout",
    "devnode and published INF polling is exact and bounded",
    "process timeout permits only read-only inventory",
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

test("Windows CI runs all lifecycle suites as independent non-mutating gates", async () => {
  const source = await readRequired(workflowPath);
  for (const pattern of [
    /\bbcdedit(?:\.exe)?\b/i,
    /\btestsigning\b/i,
    /\bDisable-ComputerRestore\b/i,
    /\b(?:Secure\s*Boot|SecureBoot)\b[^\n]*(?:disable|off)/i,
    /\bMemory\s+Integrity\b[^\n]*(?:disable|off)/i,
  ]) {
    assert.doesNotMatch(source, pattern);
  }
  const gates = [
    {
      command:
        "node --test Windows/tools/tests/lab-driver-lifecycle.contract.test.mjs",
      failure: "Lifecycle contract tests failed.",
    },
    {
      command:
        "pwsh -NoProfile -File Windows/tools/tests/lab-driver-lifecycle.validation.test.ps1",
      failure: "Lifecycle validation tests failed.",
    },
    {
      command:
        "pwsh -NoProfile -File Windows/tools/tests/lab-driver-lifecycle.behavior.test.ps1",
      failure: "Lifecycle behavior tests failed.",
    },
    {
      command:
        "pwsh -NoProfile -File Windows/tools/tests/lab-driver-lifecycle.integration.test.ps1",
      failure: "Lifecycle integration tests failed.",
    },
  ];
  let previousOffset = -1;
  for (const gate of gates) {
    const commandOffset = source.indexOf(gate.command);
    assert.ok(
      commandOffset > previousOffset,
      `missing or misordered independent CI command: ${gate.command}`,
    );
    const following = source.slice(commandOffset);
    assert.match(following, /^\S[^\n]*\n\s*if \(\$LASTEXITCODE -ne 0\)/);
    assert.match(following, new RegExp(gate.failure.replaceAll(".", "\\.")));
    previousOffset = commandOffset;
  }
  assert.doesNotMatch(source, /-Confirm(?:Install|Uninstall)\b/);
});
