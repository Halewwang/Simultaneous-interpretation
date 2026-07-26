param(
    [switch]$RequireTargetOs,
    [switch]$RequireInstalledWdk
)

$ErrorActionPreference = "Stop"
$requiredBuild = 26200
$build = [Environment]::OSVersion.Version.Build
if ($RequireTargetOs -and $build -lt $requiredBuild) {
    throw "Windows build $build is below required build $requiredBuild"
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found" }
$install = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $install) { throw "Visual C++ x64 tools not found" }

$cmakeText = (& cmake --version | Select-Object -First 1)
$cmakeVersion = [version]($cmakeText -replace '^cmake version\s+', '')
if ($cmakeVersion -lt [version]"4.2") {
    throw "CMake $cmakeVersion does not support Visual Studio 18 2026"
}

$wdkVersion = "NuGet-managed"
if ($RequireInstalledWdk) {
    $kitsRoot = Get-ItemPropertyValue `
        "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" `
        -Name KitsRoot10
    $wdkVersion = Get-ChildItem "$kitsRoot\Include" -Directory |
        Where-Object Name -Match '^10\.0\.28000\.' |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
    if (-not $wdkVersion) { throw "Installed WDK 28000 not found" }
}

[ordered]@{
    windowsBuild = $build
    visualStudio = $install
    cmake = $cmakeVersion.ToString()
    wdk = $wdkVersion
    architecture = $env:PROCESSOR_ARCHITECTURE
    targetOsEligible = ($build -ge $requiredBuild)
} | ConvertTo-Json
