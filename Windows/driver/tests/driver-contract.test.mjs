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

test("project is a pinned x64 Windows 11 KMDF driver built by MSBuild", async () => {
  const project = await text("EMKE.VirtualAudio.vcxproj");
  assert.match(project, /<PlatformToolset>WindowsKernelModeDriver10\.0<\/PlatformToolset>/);
  assert.match(project, /<ConfigurationType>Driver<\/ConfigurationType>/);
  assert.match(project, /<DriverType>KMDF<\/DriverType>/);
  assert.match(project, /<EMKETargetOS>Windows11<\/EMKETargetOS>/);
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
  assert.doesNotMatch(project, /Debug\|/);

  const lock = JSON.parse(await text("packages.lock.json"));
  assert.equal(lock.version, 1);
  assert.equal(
    lock.dependencies["native,Version=v0.0"]["Microsoft.Windows.WDK.x64"].resolved,
    "10.0.28000.2526",
  );
});

test("INF freezes the root identity, driver ABI, roles, and endpoint names", async () => {
  const inf = await text("EMKE.VirtualAudio.inf");
  assert.match(inf, /ROOT\\EMKEVIRTUALAUDIO/i);
  assert.match(inf, /NTamd64\.10\.0\.\.\.26200/);
  assert.match(inf, /DriverAbi[^\\r\\n]*0x00000001/i);
  assert.match(inf, /EMKE Virtual Speaker/);
  assert.match(inf, /EMKE Virtual Microphone/);
  assert.match(inf, /EMKE Internal [^"\\r\\n]+/);
  for (const role of roles) {
    assert.equal(inf.split(role).length - 1, 1, `${role} must occur exactly once`);
  }
});

test("driver and native host share the endpoint property key and role strings", async () => {
  const roleHeader = await text("include/emke_endpoint_roles.h");
  const nativeHeader = await readFile(
    path.resolve(driverDirectory, "..", "native", "include", "emke_endpoint_properties.h"),
    "utf8",
  );
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
  assert.match(roleHeader, /,\s*2\s*\)/);
  for (const role of roles) {
    assert.match(roleHeader, new RegExp(role.replaceAll(".", "\\.")));
  }
  assert.match(nativeHeader, /3fa64f16/i);
  assert.match(nativeHeader, /,\s*2\s*\)/);
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

test("build script is Release x64 only, uses MSBuild and Inf2Cat, and never installs", async () => {
  const script = await readFile(path.join(toolsDirectory, "build-driver.ps1"), "utf8");
  assert.match(script, /MSBuild\.exe/);
  assert.match(script, /Inf2Cat\.exe/i);
  assert.match(script, /\/driver:/i);
  assert.match(script, /\/os:10_X64/i);
  assert.match(script, /Configuration\s*=\s*"Release"/);
  assert.match(script, /Platform\s*=\s*"x64"/);
  assert.doesNotMatch(script, /dotnet\s+build/i);
  assert.doesNotMatch(script, /\b(?:pnputil|devcon|bcdedit)\b/i);
});

test("package verifier is fail-closed and checks catalog membership", async () => {
  const script = await readFile(
    path.join(toolsDirectory, "verify-driver-package.ps1"),
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
  assert.match(script, /Get-FileCatalog/i);
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
  assert.match(workflow, /pwsh Windows\/tools\/build-driver\.ps1/);
  assert.match(workflow, /pwsh Windows\/tools\/verify-driver-package\.ps1/);
  assert.match(workflow, /actions\/upload-artifact@v4/);
  assert.match(workflow, /Windows\/artifacts\/driver\/x64\/Release/);
  assert.doesNotMatch(workflow, /\b(?:pnputil|devcon|bcdedit)\b/i);
});
