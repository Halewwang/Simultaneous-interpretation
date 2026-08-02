import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const driverDirectory = path.resolve(testDirectory, "..");
const projectDirectory = path.join(driverDirectory, "EMKE.VirtualAudio");
const toolsDirectory = path.resolve(driverDirectory, "..", "tools");

const roles = [
  "emke.meeting-speaker.render",
  "emke.app-speaker.capture",
  "emke.app-microphone.render",
  "emke.meeting-microphone.capture",
];

async function text(relativePath) {
  return readFile(path.join(projectDirectory, relativePath), "utf8");
}

test("provenance is immutable and names the Microsoft sample license", async () => {
  const notice = await readFile(
    path.join(driverDirectory, "THIRD_PARTY_NOTICES.md"),
    "utf8",
  );
  assert.match(
    notice,
    /https:\/\/github\.com\/microsoft\/Windows-driver-samples\.git/,
  );
  assert.match(notice, /\b[0-9a-f]{40}\b/);
  assert.match(notice, /Microsoft Public License \(MS-PL\)/);
  assert.match(notice, /audio\/simpleaudiosample\/Source/);
  assert.match(notice, /Local modifications/i);
});

test("project is a pinned x64 Windows 10-11 KMDF 1.31 driver built by MSBuild", async () => {
  const project = await text("EMKE.VirtualAudio.vcxproj");
  assert.match(project, /<PlatformToolset>WindowsKernelModeDriver10\.0<\/PlatformToolset>/);
  assert.match(project, /<ConfigurationType>Driver<\/ConfigurationType>/);
  assert.match(project, /<DriverType>KMDF<\/DriverType>/);
  assert.match(project, /<EMKETargetOS>Windows10<\/EMKETargetOS>/);
  assert.match(project, /<KMDF_VERSION_MAJOR>1<\/KMDF_VERSION_MAJOR>/);
  assert.match(project, /<KMDF_VERSION_MINOR>31<\/KMDF_VERSION_MINOR>/);
  assert.match(project, /<WindowsTargetPlatformVersion>10\.0\.28000\.0<\/WindowsTargetPlatformVersion>/);
  assert.match(project, /<TargetVersion>Windows10<\/TargetVersion>/);
  assert.doesNotMatch(project, /<TargetVersion>Windows11<\/TargetVersion>/);
  const platformVersionOffset = project.indexOf(
    "<WindowsTargetPlatformVersion>10.0.28000.0</WindowsTargetPlatformVersion>",
  );
  const defaultPropsOffset = project.indexOf(
    '<Import Project="$(VCTargetsPath)\\Microsoft.Cpp.Default.props" />',
  );
  assert.ok(platformVersionOffset >= 0 && platformVersionOffset < defaultPropsOffset);
  assert.match(project, /<Import Project="\$\(VCTargetsPath\)\\Microsoft\.Cpp\.props" \/>/);
  assert.match(project, /<Import Project="\$\(VCTargetsPath\)\\Microsoft\.Cpp\.targets" \/>/);
  assert.match(project, /<MinimumVisualStudioVersion>18\.0<\/MinimumVisualStudioVersion>/);
  assert.match(project, /<Platform>x64<\/Platform>/);
  assert.match(project, /Include="Microsoft\.Windows\.WDK\.x64"/);
  assert.match(project, /Version="10\.0\.28000\.2526"/);
  assert.match(project, /GeneratePathProperty="true"/);
  assert.match(
    project,
    /<FilesToPackage Include="\$\(TargetPath\)" Condition="'\$\(ConfigurationType\)'=='Driver' or '\$\(ConfigurationType\)'=='DynamicLibrary'" \/>/,
  );
  assert.doesNotMatch(project, /Debug\|/);

  const lock = JSON.parse(await text("packages.lock.json"));
  assert.equal(lock.version, 1);
  assert.equal(
    lock.dependencies["native,Version=v0.0"]["Microsoft.Windows.WDK.x64"].resolved,
    "10.0.28000.2526",
  );
});

test("WDK StampInf metadata freezes the packaged INF version and date", async () => {
  const project = await text("EMKE.VirtualAudio.vcxproj");
  const inf = await text("EMKE.VirtualAudio.inf");
  const resource = await text("src/SimpleAudioSample.rc");
  const infItem = project.match(
    /<Inf Include="EMKE\.VirtualAudio\.inf">(?<metadata>[\s\S]*?)<\/Inf>/,
  );

  assert.ok(infItem, "the real driver INF item must declare StampInf metadata");
  assert.match(
    infItem.groups.metadata,
    /<SpecifyDriverVerDirectiveDate>true<\/SpecifyDriverVerDirectiveDate>/,
  );
  assert.match(infItem.groups.metadata, /<DateStamp>08\/01\/2026<\/DateStamp>/);
  assert.match(
    infItem.groups.metadata,
    /<SpecifyDriverVerDirectiveVersion>true<\/SpecifyDriverVerDirectiveVersion>/,
  );
  assert.match(infItem.groups.metadata, /<TimeStamp>1\.0\.0\.2<\/TimeStamp>/);
  assert.match(
    infItem.groups.metadata,
    /<KmdfVersionNumber>1\.31<\/KmdfVersionNumber>/,
  );
  assert.doesNotMatch(infItem.groups.metadata, /<SpecifyDriverDirectiveVersion>/);
  assert.doesNotMatch(infItem.groups.metadata, />\s*\*\s*</);
  assert.match(inf, /^DriverVer=08\/01\/2026,1\.0\.0\.2$/m);
  assert.match(resource, /^\s*FILEVERSION 1,0,0,2$/m);
  assert.match(resource, /^\s*PRODUCTVERSION 1,0,0,2$/m);
  assert.match(resource, /VALUE "FileVersion", "1\.0\.0\.2\\0"/);
  assert.match(resource, /VALUE "ProductVersion", "1\.0\.0\.2\\0"/);
});

test("INF freezes the root identity, driver ABI, roles, and endpoint names", async () => {
  const inf = await text("EMKE.VirtualAudio.inf");
  assert.match(inf, /ROOT\\EMKEVIRTUALAUDIO/i);
  assert.match(
    inf,
    /^%ManufacturerName%=EMKE,NTamd64\.10\.0\.\.\.19045$/m,
  );
  assert.match(inf, /^\[EMKE\.NTamd64\.10\.0\.\.\.19045\]$/m);
  assert.match(
    inf,
    /^%DeviceDescription%=EMKE_Install,ROOT\\EMKEVIRTUALAUDIO$/m,
  );
  assert.match(inf, /^\[EMKE_Install\.NT\]$/m);
  assert.doesNotMatch(inf, /NTamd64\.10\.0\.\.\.26200/);
  assert.match(inf, /DriverAbi[^\\r\\n]*0x00000001/i);
  assert.match(inf, /EMKE Virtual Speaker/);
  assert.match(inf, /EMKE Virtual Microphone/);
  assert.match(inf, /EMKE Internal [^"\\r\\n]+/);
  for (const role of roles) {
    assert.equal(inf.split(role).length - 1, 1, `${role} must occur exactly once`);
  }
});

test("driver and native host share the endpoint property key and role strings", async () => {
  const roleHeader = await readFile(
    path.resolve(driverDirectory, "..", "shared", "emke_endpoint_contract.h"),
    "utf8",
  );
  const nativeCatalog = await readFile(
    path.resolve(
      driverDirectory,
      "..",
      "native",
      "EMKE.NativeAudio",
      "src",
      "device_catalog.hpp",
    ),
    "utf8",
  );
  const miniports = await text("src/minipairs.h");
  assert.match(roleHeader, /3fa64f16/i);
  assert.match(roleHeader, /0x18af/i);
  assert.match(roleHeader, /0x4e9e/i);
  assert.match(roleHeader, /0xb5/);
  assert.match(roleHeader, /0x38/);
  assert.match(roleHeader, /0x91/);
  assert.match(roleHeader, /0xc1/);
  assert.match(roleHeader, /0x14/);
  assert.match(roleHeader, /0x0e/);
  assert.match(roleHeader, /0x42/);
  assert.match(roleHeader, /EMKE_ENDPOINT_ROLE_PROPERTY_PID\s+2u/);
  for (const role of roles) {
    assert.match(roleHeader, new RegExp(role.replaceAll(".", "\\.")));
  }
  assert.match(nativeCatalog, /#include "emke_endpoint_contract\.h"/);
  assert.match(miniports, /#include "emke_endpoint_contract\.h"/);
});

test("exactly four endpoint miniports are declared with onboarding-safe names", async () => {
  const miniports = await text("src/minipairs.h");
  for (const role of roles) {
    assert.match(miniports, new RegExp(role.replaceAll(".", "\\.")));
  }
  assert.match(miniports, /EMKE Virtual Speaker/);
  assert.match(miniports, /EMKE Virtual Microphone/);
  assert.match(miniports, /EMKE Internal Speaker Capture/);
  assert.match(miniports, /EMKE Internal Microphone Render/);
  assert.match(miniports, /g_cRenderEndpoints\s+2/);
  assert.match(miniports, /g_cCaptureEndpoints\s+2/);
});

test("WaveRT tables and stream movement use the compiled Float32 bridge contract", async () => {
  const speakerFormats = await text("src/speakerwavtable.h");
  const captureFormats = await text("src/micarraywavtable.h");
  const stream = await text("src/minwavertstream.cpp");
  const miniport = await text("src/minwavert.h");
  const routing = await text("src/emke_bridge_routing.cpp");
  for (const formats of [speakerFormats, captureFormats]) {
    assert.match(formats, /KSDATAFORMAT_SUBTYPE_IEEE_FLOAT/);
    assert.match(formats, /EMKE_AUDIO_SAMPLE_RATE/);
    assert.match(formats, /EMKE_AUDIO_CHANNEL_COUNT/);
    assert.match(formats, /EMKE_AUDIO_BITS_PER_SAMPLE/);
    assert.doesNotMatch(formats, /KSDATAFORMAT_SUBTYPE_PCM/);
  }
  assert.match(stream, /EmkeBridgeTransferDma/);
  assert.match(stream, /EmkeAudioBridgeReset/);
  assert.match(miniport, /EmkeBridgeEndpointForDeviceType/);
  assert.doesNotMatch(miniport, /switch\s*\(\s*m_DeviceType\s*\)/);
  assert.match(routing, /EmkeAudioBridgeWrite/);
  assert.match(routing, /EmkeAudioBridgeRead/);
  assert.doesNotMatch(stream, /GenerateSine|SaveData|WriteData/);
});

test("kernel bridge boundary excludes user-mode C++ headers and is declared at adapter use", async () => {
  const bridgeHeader = await text("src/emke_audio_bridge.h");
  const bridgeSource = await text("src/emke_audio_bridge.cpp");
  const adapter = await text("src/adapter.cpp");
  const kernelBranch = bridgeHeader.match(
    /#if defined\(_KERNEL_MODE\)(?<body>[\s\S]*?)#else/,
  )?.groups?.body;

  assert.ok(kernelBranch, "bridge header must define an explicit kernel branch");
  assert.doesNotMatch(kernelBranch, /#include\s*</);
  assert.doesNotMatch(kernelBranch, /\bstd::/);
  assert.match(kernelBranch, /using EmkeSize = SIZE_T;/);
  assert.doesNotMatch(bridgeSource, /\bstd::/);
  assert.match(adapter, /#include "emke_audio_bridge\.h"/);
});

test("build script is Release x64 only, uses MSBuild and Inf2Cat, and never installs", async () => {
  const script = await readFile(path.join(toolsDirectory, "build-driver.ps1"), "utf8");
  assert.match(script, /MSBuild\.exe/);
  assert.match(script, /MSBuild\\\*\*\\Bin\\amd64\\MSBuild\.exe/);
  assert.doesNotMatch(script, /MSBuild\\\*\*\\Bin\\MSBuild\.exe/);
  assert.match(script, /Inf2Cat\.exe/i);
  assert.match(script, /\/driver:/i);
  assert.match(script, /\/os:10_X64/i);
  assert.match(script, /Configuration\s*=\s*"Release"/);
  assert.match(script, /Platform\s*=\s*"x64"/);
  assert.match(script, /\$wdkPackageVersion\s*=\s*"10\.0\.28000\.2526"/);
  assert.match(script, /\$wdkPlatformVersion\s*=\s*"10\.0\.28000\.0"/);
  assert.match(script, /c\\bin\\\$wdkPlatformVersion\\x64\\stampinf\.exe/i);
  assert.match(script, /c\\bin\\\$wdkPlatformVersion\\x86\\Inf2Cat\.exe/i);
  assert.match(script, /c\\bin\\\$wdkPlatformVersion\\x64\\drvcat\.exe/i);
  assert.match(script, /c\\bin\\\$wdkPlatformVersion\\x64\\ApiValidator\.exe/i);
  assert.match(script, /c\\bin\\\$wdkPlatformVersion\\x64\\aitstatic\.exe/i);
  assert.match(script, /c\\build\\\$wdkPlatformVersion\\bin\\x64\\InfVerif\.dll/i);
  assert.match(script, /PackageVerifier\.18\.0\.dll/);
  assert.match(script, /"\/p:WDKBinRoot=\$wdkBinRoot"/);
  assert.match(script, /"\/p:InfToolPath=\$wdkX64Bin"/);
  assert.match(script, /"\/p:Inf2CatToolPath=\$wdkX86Bin"/);
  assert.match(script, /"\/p:DrvCatToolPath=\$wdkX64Bin"/);
  assert.match(script, /"\/p:PROCESSOR_ARCHITECTURE=AMD64"/);
  assert.match(script, /"\/p:ApiValidator_ApiExtractorExePath=\$wdkX64Bin"/);
  assert.match(script, /"\/p:ApiValidatorAdditionalOptions=-AitCmdLogEverything:true"/);
  assert.match(script, /-WorkingDirectory \$wdkBuildTaskRoot/);
  assert.match(script, /\$wdkPackageOutput\s*=\s*Join-Path \$buildOutput "EMKE\.VirtualAudio"/);
  assert.match(script, /stage-driver-package\.mjs/);
  assert.match(script, /validate-driver-contract\.mjs/);
  assert.doesNotMatch(
    script,
    /Join-Path \$projectDirectory "EMKE\.VirtualAudio\.inf"\)\s*`\s*\n\s*-Destination \$artifactDirectory/,
  );
  assert.doesNotMatch(
    script,
    /Remove-Item -LiteralPath \$artifactDirectory -Recurse/,
  );
  const restoreOffset = script.indexOf('"/t:Restore"');
  const pinnedToolOffset = script.indexOf("$stampInf = Resolve-PinnedTool");
  const validationRuntimeOffset = script.indexOf("$packageVerifier = Resolve-PinnedTool");
  const rebuildOffset = script.indexOf('"/t:Rebuild"');
  assert.ok(restoreOffset >= 0 && restoreOffset < pinnedToolOffset);
  assert.ok(pinnedToolOffset < validationRuntimeOffset);
  assert.ok(validationRuntimeOffset < rebuildOffset);
  assert.doesNotMatch(script, /dotnet\s+build/i);
  assert.doesNotMatch(script, /SkipPackageVerification\s*=\s*true/i);
  assert.doesNotMatch(script, /ApiValidator_Enable\s*=\s*false/i);
  assert.doesNotMatch(script, /Get-Command\s+(?:stampinf|inf2cat|drvcat)/i);
  assert.doesNotMatch(script, /Windows Kits[\\/]/i);
  assert.doesNotMatch(script, /Get-ChildItem[\s\S]*-Filter\s+"(?:stampinf|Inf2Cat|drvcat)\.exe"/i);
  assert.doesNotMatch(script, /\b(?:pnputil|devcon|bcdedit)\b/i);
});

test("package verifier is fail-closed and checks catalog membership", async () => {
  const script = await readFile(
    path.join(toolsDirectory, "verify-driver-package.ps1"),
    "utf8",
  );
  const referenceSet = await readFile(
    path.join(toolsDirectory, "catalog-reference-set.ps1"),
    "utf8",
  );
  assert.match(script, /-Extension "\.inf" -Description "INF"/i);
  assert.match(script, /-Extension "\.sys" -Description "SYS"/i);
  assert.match(script, /-Extension "\.cat" -Description "CAT"/i);
  assert.match(script, /ROOT\\\\EMKEVIRTUALAUDIO/i);
  assert.match(script, /DriverVer/i);
  assert.match(script, /FileVersion/i);
  assert.match(script, /DriverAbi/i);
  assert.match(script, /Get-AuthenticodeSignature/);
  assert.match(script, /CryptCATOpen/);
  assert.match(script, /CryptCATEnumerateMember/);
  assert.match(script, /CryptCATAdminAcquireContext2/);
  assert.match(script, /CryptCATAdminCalcHashFromFileHandle2/);
  assert.match(script, /foreach \(\$hashAlgorithm in @\("SHA1", "SHA256"\)\)/);
  assert.match(script, /CalculateCatalogHash/);
  assert.match(script, /Assert-ExactCatalogMemberReferenceTags/);
  assert.match(script, /Catalog enumeration diagnostic/);
  assert.match(script, /filename=.*referenceTag=/s);
  assert.match(referenceSet, /function Assert-ExactCatalogMemberReferenceTags/);
  assert.match(referenceSet, /\$actualTags \| Sort-Object/);
  assert.match(referenceSet, /\$expectedTags \| Sort-Object/);
  assert.match(referenceSet, /-cne/);
  assert.doesNotMatch(script, /\$catalogMembers\.Count -ne 2/);
  assert.doesNotMatch(script, /GetFileName\(\$_\.FileName\)/);
  assert.doesNotMatch(script, /Test-FileCatalog/);
  assert.doesNotMatch(script, /certutil/i);
  assert.doesNotMatch(script, /Get-FileHash/i);
  assert.doesNotMatch(script, /catalogHex|SHA-256 digest/i);
  assert.match(script, /PDB/i);
  assert.match(script, /Debug/i);
});

test("source import is bounded to the files listed by the project", async () => {
  const project = await text("EMKE.VirtualAudio.vcxproj");
  const sourceDirectory = path.join(projectDirectory, "src");
  const files = (await readdir(sourceDirectory))
    .filter((name) => /\.(?:cpp|h|rc)$/i.test(name))
    .sort();
  assert.ok(files.length >= 20, "WaveRT derivative source import is unexpectedly incomplete");
  for (const file of files) {
    const escaped = file.replaceAll(".", "\\.");
    assert.match(project, new RegExp(`(?:ClCompile|ClInclude|ResourceCompile) Include="src\\\\${escaped}"`));
  }
});

test("authorized hosted workflow builds, verifies, and uploads only the package", async () => {
  const workflow = await readFile(
    path.resolve(driverDirectory, "..", "..", ".github", "workflows", "windows-audio.yml"),
    "utf8",
  );
  assert.match(workflow, /node --test Windows\/driver\/tests\/driver-contract\.test\.mjs/);
  assert.match(workflow, /node --test Windows\/driver\/tests\/package-boundary\.test\.mjs/);
  assert.match(workflow, /catalog-reference-set\.test\.ps1/);
  assert.match(workflow, /pwsh Windows\/tools\/build-driver\.ps1/);
  assert.match(workflow, /pwsh Windows\/tools\/verify-driver-package\.ps1/);
  assert.match(workflow, /package-verifier\.integration\.ps1/);
  assert.match(workflow, /actions\/upload-artifact@v4/);
  assert.match(workflow, /Windows\/artifacts\/driver\/x64\/Release/);
  assert.doesNotMatch(workflow, /\b(?:pnputil|devcon|bcdedit)\b/i);
});

test("driver release metadata agrees on version, floor, ABI, identity, KMDF, and roles", async () => {
  const version = JSON.parse(
    await readFile(path.resolve(driverDirectory, "..", "version.json"), "utf8"),
  );
  const compatibility = JSON.parse(
    await readFile(
      path.resolve(
        driverDirectory,
        "..",
        "packaging",
        "compatibility.internal.json",
      ),
      "utf8",
    ),
  );

  assert.equal(version.driverPackageVersion, "1.0.0.2");
  assert.equal(version.minimumWindowsBuild, 19045);
  assert.equal(version.driverAbiVersion, 1);
  assert.equal(version.driverHardwareId, "ROOT\\EMKEVIRTUALAUDIO");
  assert.equal(version.driverKmdfLibraryVersion, "1.31");
  assert.deepEqual(version.driverEndpointRoles, roles);

  assert.equal(compatibility.minimumDriverVersion, version.driverPackageVersion);
  assert.equal(compatibility.recommendedDriverVersion, version.driverPackageVersion);
  assert.equal(compatibility.minimumWindowsBuild, version.minimumWindowsBuild);
  assert.equal(compatibility.driverAbiVersion, version.driverAbiVersion);
  assert.equal(compatibility.driverHardwareId, version.driverHardwareId);
  assert.equal(
    compatibility.driverKmdfLibraryVersion,
    version.driverKmdfLibraryVersion,
  );
  assert.deepEqual(compatibility.driverEndpointRoles, version.driverEndpointRoles);
});
